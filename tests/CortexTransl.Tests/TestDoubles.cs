using CortexTransl.App.Models;
using CortexTransl.App.Services.Cache;
using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Ocr;
using CortexTransl.App.Services.Translation;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;

namespace CortexTransl.Tests;

internal sealed class ManualClock : TimeProvider
{
    private long _milliseconds;
    public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => Interlocked.Read(ref _milliseconds);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(GetTimestamp());
    public void Advance(int milliseconds) => Interlocked.Add(ref _milliseconds, milliseconds);
}

internal sealed class FakeCapture : IScreenCaptureService
{
    private int _frame;
    public bool SameFrame { get; set; }
    public int Captures { get; private set; }
    public int ReleasedSessions { get; private set; }
    public void ReleaseSession() => ReleasedSessions++;
    public Exception? Failure { get; set; }
    public Task<Bitmap> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Captures++;
        if (Failure is not null) return Task.FromException<Bitmap>(Failure);
        var bitmap = new Bitmap(64, 32, PixelFormat.Format32bppArgb);
        if (!SameFrame) _frame++;
        bitmap.SetPixel(3, 4, Color.FromArgb(_frame & 255, (_frame >> 8) & 255, 100));
        for (var bit = 0; bit < 16; bit++)
            if ((_frame & (1 << bit)) != 0) bitmap.SetPixel(8 + bit, 8, Color.White);
        return Task.FromResult(bitmap);
    }
    public void Dispose() { }
}

internal sealed class FakeOcr : IOcrEngine
{
    public string Id => "fake";
    public OcrResult Result { get; set; } = new("Hello!");
    public Exception? Failure { get; set; }
    public int Calls { get; private set; }
    public Task<OcrResult> RecognizeAsync(Bitmap bitmap, string sourceLanguage, CancellationToken cancellationToken = default, string captureKind = "dialogue")
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        if (Failure is not null) return Task.FromException<OcrResult>(Failure);
        return Task.FromResult(Result);
    }
}

internal sealed class FakeCache : ITranslationCacheRepository
{
    public TaskCompletionSource Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<(string, string, string, string), string> _values = new();
    public int Count => _values.Count;
    public Task<string?> GetAsync(string text, string source, string target, string provider, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_values.TryGetValue((text, source, target, provider), out var value) ? value : null);
    }
    public Task SaveAsync(string text, string source, string target, string provider, string translated, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _values[(text, source, target, provider)] = translated;
        Saved.TrySetResult();
        return Task.CompletedTask;
    }
}

internal sealed class FakeProvider : ITranslationProvider
{
    public string Id { get; set; } = "fake-deepl";
    public bool SupportsContextualLongText => true;
    public Func<string, CancellationToken, Task<string>> Translate { get; set; } = (text, _) => Task.FromResult("Arabic:" + text);
    public ConcurrentQueue<string> Calls { get; } = new();
    public ConcurrentQueue<StoryTranslationContext> Contexts { get; } = new();
    public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default,
        TranslationContentKind contentKind = TranslationContentKind.General)
    { Calls.Enqueue(text); return Translate(text, cancellationToken); }
    public Task<string> TranslateStoryAsync(string text, string sourceLanguage, string targetLanguage, StoryTranslationContext context, CancellationToken cancellationToken = default)
    { Contexts.Enqueue(context); return TranslateAsync(text, sourceLanguage, targetLanguage, cancellationToken, TranslationContentKind.GameDialogue); }
    public async Task<IReadOnlyList<string>> TranslateManyAsync(IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage,
        CancellationToken cancellationToken = default, TranslationContentKind contentKind = TranslationContentKind.General)
    {
        var result = new List<string>();
        foreach (var text in texts) result.Add(await TranslateAsync(text, sourceLanguage, targetLanguage, cancellationToken, contentKind));
        return result;
    }
}
