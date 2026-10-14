using CortexTransl.App.Models;
using CortexTransl.App.Services.Cache;
using CortexTransl.App.Services.Ocr;
using CortexTransl.App.Services.Translation;
using CortexTransl.App.Utils;
using System.Threading.Channels;
using System.Diagnostics;
using CortexTransl.App.Services.Diagnostics;

namespace CortexTransl.App.Services.Capture;

public sealed class TranslationPipeline : IDisposable
{
    private sealed record Work(long Sequence, OcrResult Ocr, string Text, string Source, string Target,
        bool ListMode, StoryTranslationContext Context, string ProviderId, string CacheId, CancellationToken Token,
        long ObservedAt, long QueuedAt);

    private readonly IScreenCaptureService _capture;
    private readonly IOcrEngine _ocr;
    private readonly ITranslationProvider _provider;
    private readonly ITranslationCacheRepository _cache;
    private readonly MenuTranslationProcessor _menus;
    private readonly TimeProvider _clock;
    private readonly DialogueTracker _tracker;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _detectionLock = new(1, 1);
    private readonly Channel<Work> _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(1)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _worker;
    private CancellationTokenSource? _current;
    private long _queuedSequence = -1, _retryAt, _lastOcrAt, _lastNonemptyAt;
    private string? _fingerprint, _sessionKey;
    private OcrResult? _successfulOcr;
    private (string Text, CaptureRegion Bounds)[] _menuLayout = [];
    private string _menuSignature = "";
    private PipelineResult _result = Empty("Waiting for text.");
    private StoryTranslationContext _context = new();
    private readonly Queue<StoryDialogue> _history = new();
    private string? _historyText;
    private StoryTranslationContext? _requestContext;
    private int _consecutiveFailures;
    private string? _lastOcrError;
    private long _lastOcrErrorAt;
    private bool _disposed;
    private ulong? _visualSignature;
    private long _observedAt;
    private long _recognizedAt;
    public PerformanceDiagnostics Diagnostics { get; }
    internal Task Completion => _worker;
    public event Action<PipelineResult>? ResultAvailable;
    public PipelineResult LatestResult { get { lock (_gate) return _result; } }

    public TranslationPipeline(IScreenCaptureService screenCaptureService, IOcrEngine ocrEngine,
        ITranslationProvider translationProvider, ITranslationCacheRepository cacheRepository, TimeProvider? timeProvider = null,
        PerformanceDiagnostics? diagnostics = null)
    {
        _capture = screenCaptureService;
        _ocr = ocrEngine;
        _provider = translationProvider;
        _cache = cacheRepository;
        _menus = new(translationProvider, cacheRepository);
        _clock = timeProvider ?? TimeProvider.System;
        Diagnostics = diagnostics ?? new();
        _tracker = new(_clock);
        _worker = Task.Run(ProcessQueueAsync);
    }

    public StorySettings Settings { get; private set; } = new();
    public long CurrentSequence { get { lock (_gate) return _tracker.Sequence; } }

    public void BindCapture(CaptureRegion region, bool listMode, bool onlyIfUnbound = false)
    {
        _capture.BindTarget(region, listMode, onlyIfUnbound);
    }

    public void StopCapture()
    {
        Reset();
        _capture.ReleaseSession();
    }

    public void Configure(string gameKey, StorySettings settings)
    {
        var snapshot = settings.Clone();
        var context = new StoryTranslationContext
        {
            GameKey = gameKey, Speaker = NullIfEmpty(snapshot.Speaker), SceneState = NullIfEmpty(snapshot.SceneState),
            Terminology = snapshot.Terminology, SpeakerNames = snapshot.SpeakerNames,
            DeepLGlossaryId = NullIfEmpty(snapshot.DeepLGlossaryId)
        };
        lock (_gate)
        {
            if (_context.CacheScope != context.CacheScope)
            {
                ResetState();
                _context = context;
            }
            Settings = snapshot;
            _tracker.Settings = snapshot;
            Diagnostics.Enabled = snapshot.PerformanceDiagnostics;
        }
    }

    // Capture/OCR is serialized; network and offline inference run on the worker.
    public async Task<PipelineResult> RunAsync(CaptureRegion region, string sourceLanguage, string targetLanguage,
        bool listMode, CancellationToken cancellationToken = default)
    {
        await _detectionLock.WaitAsync(cancellationToken);
        try
        {
            Diagnostics.SampleResources();
            var captureStarted = Stopwatch.GetTimestamp();
            long generation;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var key = $"{region}:{sourceLanguage}:{targetLanguage}:{listMode}:{_provider.Id}";
                if (_sessionKey != key) { ResetState(); _sessionKey = key; }
                generation = _tracker.Sequence;
            }
            OcrResult? ocr = null;
            var failed = false;
            string? error = null;
            var freshOcr = false;
            ulong? observedVisual = null;
            try
            {
                System.Drawing.Bitmap bitmap;
                using (Diagnostics.Measure(LatencyStage.Capture)) bitmap = await _capture.CaptureAsync(region, cancellationToken);
                using var captured = bitmap;
                string? fingerprint = null;
                ulong? visual = null;
                if (listMode)
                { using (Diagnostics.Measure(LatencyStage.Fingerprint)) fingerprint = ImageFingerprint.Compute(bitmap); }
                else
                { using (Diagnostics.Measure(LatencyStage.TextChangeDetection)) visual = SubtitleVisualSignature.Compute(bitmap); }
                observedVisual = visual;
                var now = _clock.GetTimestamp();
                lock (_gate)
                {
                    var maximumReuse = listMode ? 400 : Settings.FastSubtitles ? 160 : 200;
                    var unchangedVisual = listMode ? _fingerprint == fingerprint : _visualSignature == visual;
                    // Masks schedule work; OCR still confirms candidate text and
                    // periodically revalidates it, including weak/dark glyphs.
                    if (unchangedVisual && _successfulOcr is not null && !_tracker.RequiresOcrConfirmation
                        && _clock.GetElapsedTime(_lastOcrAt, now).TotalMilliseconds < maximumReuse)
                        ocr = _successfulOcr;
                }
                if (ocr is null)
                {
                    ocr = await _ocr.RecognizeAsync(bitmap, sourceLanguage, cancellationToken, listMode ? "list" : "dialogue");
                    freshOcr = true;
                    lock (_gate)
                    {
                        if (generation == _tracker.Sequence)
                        {
                            _lastOcrAt = now;
                            _successfulOcr = string.IsNullOrWhiteSpace(ocr.Text) ? null : ocr;
                            _fingerprint = _successfulOcr is null ? null : fingerprint;
                            _visualSignature = _successfulOcr is null ? null : visual;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (CaptureUnavailableException ex)
            {
                // Losing the bound window is a privacy boundary, not OCR noise.
                lock (_gate) { ResetState(); return Empty(ex.Message) with { Sequence = _tracker.Sequence }; }
            }
            catch (Exception ex)
            {
                var now = _clock.GetTimestamp();
                if (_lastOcrError != ex.Message || _clock.GetElapsedTime(_lastOcrErrorAt, now).TotalSeconds >= 5)
                { AppLog.Write("capture-ocr", ex); _lastOcrError = ex.Message; _lastOcrErrorAt = now; }
                failed = true;
                error = ex is InvalidOperationException ? ex.Message : "Could not read the text. Retrying OCR.";
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (generation != _tracker.Sequence) return Empty("Capture settings changed.") with { Sequence = _tracker.Sequence };
                var text = ocr is null ? "" : DialogueTextAssembler.Assemble(ocr, sourceLanguage);
                if (listMode && ocr is not null)
                    text = GetMenuSignature(ocr);
                var now = _clock.GetTimestamp();
                if (text.Length > 0) _lastNonemptyAt = now;
                else if (_clock.GetElapsedTime(_lastNonemptyAt, now).TotalMilliseconds >= Math.Clamp(Settings.SceneResetMilliseconds, 1000, 60000))
                { _history.Clear(); _historyText = null; }

                var observation = _tracker.Observe(text, failed, freshOcr, observedVisual);
                text = observation.Text;
                if (observation.Changed || (observation.Clear && _queuedSequence != -1))
                {
                    CancelCurrent();
                    _queuedSequence = -1;
                    _retryAt = 0;
                    _consecutiveFailures = 0;
                    _requestContext = null;
                    _observedAt = captureStarted;
                    _recognizedAt = Stopwatch.GetTimestamp();
                    _result = Empty(error ?? "Waiting for stable dialogue.") with { Sequence = observation.Sequence };
                }
                if (_current?.IsCancellationRequested == true)
                { CancelCurrent(); _queuedSequence = -1; }
                if (observation.Ready && _queuedSequence != observation.Sequence && now >= _retryAt && ocr is not null)
                {
                    CancelCurrent();
                    _current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                    var context = _requestContext ??= _context with { GameKey = _context.GameKey + ":" + _capture.CacheIdentity, RecentDialogue = _history.ToArray() };
                    var kind = listMode ? TranslationContentKind.UiLabel : TranslationContentKind.GameDialogue;
                    var cacheId = _provider.UsesHistoricalContext
                        ? $"{_provider.Id}:{kind}:story-v4:{context.CacheScope}"
                        : $"{_provider.Id}:{kind}:offline-v5:{context.OfflineCacheScope}";
                    var queuedAt = Stopwatch.GetTimestamp();
                    Diagnostics.Elapsed(LatencyStage.Stabilization, _recognizedAt, queuedAt);
                    _queue.Writer.TryWrite(new(observation.Sequence, ocr, text, sourceLanguage, targetLanguage,
                        listMode, context, _provider.Id, cacheId, _current.Token, _observedAt, queuedAt));
                    _queuedSequence = observation.Sequence;
                    if (!listMode && _historyText != text)
                    {
                        _history.Enqueue(new(text, context.Speaker));
                        while (_history.Count > 6) _history.Dequeue();
                        _historyText = text;
                    }
                    _result = Empty("Translating dialogue.") with { OriginalText = listMode ? ocr.Text : text, Sequence = observation.Sequence };
                }
                if (error is not null) _result = _result with { Status = error };
                return _result with { Skipped = _result.TranslatedText.Length == 0, ClearOverlay = observation.Clear || _result.ClearOverlay };
            }
        }
        finally { _detectionLock.Release(); }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(_lifetime.Token))
            {
                try
                {
                    work.Token.ThrowIfCancellationRequested();
                    Diagnostics.Elapsed(LatencyStage.QueueWait, work.QueuedAt);
                    if (_provider.Id != work.ProviderId) continue;
                    PipelineResult result;
                    string? cacheValue = null;
                    if (work.ListMode)
                        result = await _menus.TranslateAsync(work.CacheId, work.Ocr, work.Source, work.Target, work.Context, work.Token);
                    else
                    {
                        string? cached;
                        using (Diagnostics.Measure(LatencyStage.CacheLookup))
                            cached = await _cache.GetAsync(work.Text, work.Source, work.Target, work.CacheId, work.Token);
                        var translated = cached ?? await _provider.TranslateStoryAsync(work.Text, work.Source, work.Target, work.Context, work.Token);
                        if (string.IsNullOrWhiteSpace(translated))
                            throw new TranslationProviderException("Translation returned empty text. Retrying.", "empty translation");
                        work.Token.ThrowIfCancellationRequested();
                        if (cached is null) cacheValue = translated;
                        result = new(work.Text, translated, cached is null ? "Translated." : "Loaded from cache.", cached is not null, false);
                    }
                    lock (_gate)
                    {
                        if (_disposed || work.Token.IsCancellationRequested || work.Sequence != _tracker.Sequence || _provider.Id != work.ProviderId) continue;
                        result = _result = result with { Sequence = work.Sequence, ClearOverlay = result.TranslatedText.Length == 0,
                            ObservedTimestamp = work.ObservedAt, CompletedTimestamp = Stopwatch.GetTimestamp() };
                        if (result.TranslatedText.Length == 0) { ScheduleRetry(); continue; }
                        _consecutiveFailures = 0;
                    }
                    // Publish immediately instead of waiting for the next OCR
                    // turn or SQLite write. Persistence remains bounded/serial.
                    NotifyResult(result);
                    if (cacheValue is not null)
                        await _cache.SaveAsync(work.Text, work.Source, work.Target, work.CacheId, cacheValue, work.Token);
                }
                catch (OperationCanceledException) when (work.Token.IsCancellationRequested || _lifetime.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    AppLog.Write("translation-worker", ex);
                    lock (_gate)
                    {
                        if (work.Sequence != _tracker.Sequence || work.Token.IsCancellationRequested) continue;
                        _result = Empty(ex is TranslationProviderException ? ex.Message : "Translation failed. Retrying.") with { Sequence = work.Sequence };
                        ScheduleRetry();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void NotifyResult(PipelineResult result)
    {
        foreach (var handler in ResultAvailable?.GetInvocationList() ?? [])
        {
            try { ((Action<PipelineResult>)handler)(result); }
            catch (Exception ex) { AppLog.Write("overlay-publication", ex); }
        }
    }

    private string GetMenuSignature(OcrResult ocr)
    {
        var layout = ocr.Blocks.Where(block => !block.Bounds.IsEmpty)
            .Select(block => (Text: OcrTextCanonicalizer.Normalize(block.Text), block.Bounds))
            .Where(block => block.Text.Length > 0)
            .OrderBy(block => block.Text, StringComparer.Ordinal)
            .ThenBy(block => block.Bounds.Y).ThenBy(block => block.Bounds.X).ToArray();
        if (layout.Length == 0) return "";
        var same = layout.Length == _menuLayout.Length;
        for (var index = 0; same && index < layout.Length; index++)
        {
            var previous = _menuLayout[index];
            var current = layout[index];
            same = previous.Text == current.Text
                && Math.Abs(previous.Bounds.X - current.Bounds.X) <= 4
                && Math.Abs(previous.Bounds.Y - current.Bounds.Y) <= 4
                && Math.Abs(previous.Bounds.Width - current.Bounds.Width) <= 4
                && Math.Abs(previous.Bounds.Height - current.Bounds.Height) <= 4;
        }
        if (same) return _menuSignature;
        // Anchor tolerance to the last accepted layout, so real scrolling still
        // moves the labels while one-pixel OCR jitter cannot starve translation.
        _menuLayout = layout;
        return _menuSignature = string.Join('\n', layout.Select(block => $"{block.Text}@{block.Bounds}"));
    }

    private void ScheduleRetry()
    {
        _queuedSequence = -1;
        _consecutiveFailures++;
        var delaySeconds = 1 << Math.Min(_consecutiveFailures - 1, 3);
        _retryAt = _clock.GetTimestamp() + _clock.TimestampFrequency * delaySeconds;
    }

    public void Reset() { lock (_gate) ResetState(); }
    public void BeginScene(string sceneState)
    {
        lock (_gate) { ResetState(); _context = _context with { SceneState = sceneState }; }
    }

    private void ResetState()
    {
        CancelCurrent();
        _tracker.Reset();
        _queuedSequence = -1;
        _retryAt = 0;
        _fingerprint = null;
        _successfulOcr = null;
        _menuLayout = [];
        _menuSignature = "";
        _visualSignature = null;
        _history.Clear();
        _historyText = null;
        _requestContext = null;
        _consecutiveFailures = 0;
        _result = Empty("Waiting for text.") with { Sequence = _tracker.Sequence };
    }

    private void CancelCurrent()
    {
        _current?.Cancel();
        _current?.Dispose();
        _current = null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static PipelineResult Empty(string status) => new("", "", status, false, true) { ClearOverlay = true };

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ResetState();
            _queue.Writer.TryComplete();
            _lifetime.Cancel();
        }
        // Do not block WPF shutdown on an uninterruptible native inference call.
        _ = _worker.ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
