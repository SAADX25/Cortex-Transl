using CortexTransl.App.Models;
using CortexTransl.App.Services.Cache;
using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Diagnostics;
using CortexTransl.App.Services.Ocr;
using CortexTransl.App.Services.Translation;
using CortexTransl.App.Utils;
using System.Drawing;
using System.Drawing.Imaging;
using Xunit;

namespace CortexTransl.Tests;

public sealed class LatencyRegressionTests
{
    [Fact]
    public async Task AnimatedBackgroundReusesConfirmedOcrButPeriodicallyRevalidatesText()
    {
        var clock = new ManualClock(); var capture = new AnimatedCapture(); var ocr = new FakeOcr();
        using var pipeline = new TranslationPipeline(capture, ocr, new FakeProvider(), new FakeCache(), clock);
        pipeline.Configure("fixture", new() { FastSubtitles = true });
        for (var frame = 0; frame < 10; frame++)
        { await pipeline.RunAsync(new(0, 0, 64, 32), "en", "ar", false); clock.Advance(80); }
        Assert.InRange(ocr.Calls, 5, 6);
        Assert.Equal(10, capture.Captures);
        capture.EditGlyph = true; ocr.Result = new("No!");
        var previous = pipeline.CurrentSequence;
        await pipeline.RunAsync(new(0, 0, 64, 32), "en", "ar", false);
        Assert.True(pipeline.CurrentSequence > previous);
    }

    private sealed class AnimatedCapture : IScreenCaptureService
    {
        public int Captures { get; private set; }
        public bool EditGlyph { get; set; }
        public Task<Bitmap> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken = default)
        {
            var bitmap = new Bitmap(64, 32, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(70 + ++Captures, 90, 135));
            graphics.FillRectangle(Brushes.White, 10, 10, EditGlyph ? 24 : 20, 10);
            return Task.FromResult(bitmap);
        }
        public void Dispose() { }
    }

    [Fact]
    public void FastProfileHasExplicitInitialTimings()
    {
        var settings = new StorySettings { FastSubtitles = true };
        Assert.Equal(80, settings.EffectiveCaptureInterval);
        Assert.Equal(150, settings.EffectiveStability);
        Assert.Equal(50, settings.EffectiveCompletedStability);
        settings.FastSubtitles = false;
        Assert.Equal(350, settings.EffectiveStability);
    }

    [Fact]
    public void LyricLineWithoutPunctuationBecomesReadyWithinFastStabilityBudget()
    {
        const string lyric = "Cr-cr-cry since the day we parted";
        var clock = new ManualClock(); var tracker = new DialogueTracker(clock) { Settings = new() { FastSubtitles = true } };
        Assert.False(tracker.Observe(lyric).Ready);
        clock.Advance(80); Assert.False(tracker.Observe(lyric).Ready);
        clock.Advance(80); var settled = tracker.Observe(lyric);
        Assert.True(settled.Ready); Assert.Equal(lyric, settled.Text);
    }

    [Fact]
    public void ReusedOcrDoesNotFabricateIndependentConfirmation()
    {
        var clock = new ManualClock(); var tracker = new DialogueTracker(clock) { Settings = new() { FastSubtitles = true } };
        tracker.Observe("Please come back to me"); clock.Advance(200);
        Assert.False(tracker.Observe("Please come back to me", freshObservation: false).Ready);
        Assert.True(tracker.Observe("Please come back to me").Ready);
    }

    [Fact]
    public void HarmlessTypographicJitterDoesNotResetStabilityButApostropheRemovalDoes()
    {
        var clock = new ManualClock(); var tracker = new DialogueTracker(clock) { Settings = new() { FastSubtitles = true } };
        var first = tracker.Observe("I can't leave you alone"); clock.Advance(80);
        var equivalent = tracker.Observe(" I can’t  leave you alone ");
        Assert.False(equivalent.Changed); Assert.Equal(first.Sequence, equivalent.Sequence);
        clock.Advance(80); Assert.True(tracker.Observe("I can't leave you alone").Ready);
        var meaningful = tracker.Observe("I cant leave you alone");
        Assert.True(meaningful.Changed); Assert.False(meaningful.Ready);
    }

    [Theory]
    [InlineData("I can help you.", "I can't help you.")]
    [InlineData("She needs you now", "He needs you now")]
    [InlineData("Don't go!", "Don't go?")]
    [InlineData("Cr-cr-cry since the day we parted", "Cr-cr-cry since the day we started")]
    public void FastProfileStillInvalidatesMeaningfulChanges(string first, string second)
    {
        var tracker = new DialogueTracker(new ManualClock()) { Settings = new() { FastSubtitles = true } };
        var before = tracker.Observe(first); var changed = tracker.Observe(second);
        Assert.True(changed.Changed); Assert.True(changed.Sequence > before.Sequence);
    }

    [Fact]
    public void GenuineTypewriterGrowthNeverInheritsStabilityFromItsPrefix()
    {
        var clock = new ManualClock(); var tracker = new DialogueTracker(clock) { Settings = new() { FastSubtitles = true } };
        foreach (var text in new[] { "Cr", "Cr-cr", "Cr-cr-cry", "Cr-cr-cry since", "Cr-cr-cry since the day", "Cr-cr-cry since the day we parted" })
        { Assert.False(tracker.Observe(text).Ready); clock.Advance(80); }
        Assert.False(tracker.Observe("Cr-cr-cry since the day we parted").Ready);
        clock.Advance(110); Assert.True(tracker.Observe("Cr-cr-cry since the day we parted").Ready);
    }

    [Fact]
    public void MovingMidToneBackgroundDoesNotChangeCaptionSignatureButGlyphEditsDo()
    {
        using var first = new Bitmap(320, 100, PixelFormat.Format32bppArgb);
        using var second = new Bitmap(320, 100, PixelFormat.Format32bppArgb);
        using (var a = Graphics.FromImage(first)) { a.Clear(Color.FromArgb(75, 90, 135)); a.FillRectangle(Brushes.White, 40, 40, 50, 20); }
        using (var b = Graphics.FromImage(second)) { b.Clear(Color.FromArgb(125, 90, 135)); b.FillRectangle(Brushes.White, 40, 40, 50, 20); }
        Assert.NotEqual(ImageFingerprint.Compute(first), ImageFingerprint.Compute(second));
        Assert.Equal(SubtitleVisualSignature.Compute(first), SubtitleVisualSignature.Compute(second));
        second.SetPixel(92, 55, Color.White);
        Assert.NotEqual(SubtitleVisualSignature.Compute(first), SubtitleVisualSignature.Compute(second));
    }

    [Theory]
    [InlineData(-1920, -100, -1800, -50, 120, 50)]
    [InlineData(200, 300, 250, 330, 50, 30)]
    public void GpuCropPreservesPhysicalMultiMonitorCoordinates(int wx, int wy, int x, int y, int expectedX, int expectedY)
    {
        var crop = CaptureCrop.Calculate(new(x, y, 300, 100), new(wx, wy, 1920, 1080), 1920, 1080);
        Assert.Equal(new Rectangle(expectedX, expectedY, 300, 100), crop);
        Assert.Null(CaptureCrop.Calculate(new(wx - 1, wy, 100, 100), new(wx, wy, 1920, 1080), 1920, 1080));
        Assert.Null(CaptureCrop.Calculate(new(wx + 1800, wy, 300, 100), new(wx, wy, 1920, 1080), 1920, 1080));
    }

    [Fact]
    public void OfflineCacheIgnoresHistoryAndSpeakerFactsButRetainsGameAndTerms()
    {
        var context = new StoryTranslationContext { GameKey = "game-A" };
        var other = context with { RecentDialogue = [new("Do not trust him")], Speaker = "A", SceneState = "Battle", DeepLGlossaryId = "unrelated" };
        Assert.Equal(context.OfflineCacheScope, other.OfflineCacheScope);
        Assert.NotEqual(context.CacheScope, other.CacheScope);
        Assert.NotEqual(context.OfflineCacheScope, (context with { GameKey = "game-B" }).OfflineCacheScope);
        Assert.NotEqual(context.OfflineCacheScope, (context with { Terminology = new Dictionary<string, string> { ["Keep"] = "قلعة" } }).OfflineCacheScope);
    }

    [Fact]
    public void DiagnosticsAreOptionalBoundedNumericAndHaveP50P95()
    {
        using var diagnostics = new PerformanceDiagnostics();
        diagnostics.Record(LatencyStage.NativeInference, 4); Assert.Empty(diagnostics.Snapshot());
        diagnostics.Enabled = true;
        for (var index = 0; index < 1000; index++) diagnostics.Record(LatencyStage.NativeInference, index);
        var stats = diagnostics.Snapshot()[nameof(LatencyStage.NativeInference)];
        Assert.Equal(512, stats.Count); Assert.Equal(743, stats.P50); Assert.Equal(974, stats.P95);
        var file = Path.Combine(Path.GetTempPath(), "CortexTransl-metrics-" + Guid.NewGuid().ToString("N") + ".json");
        diagnostics.Export(file);
        var exported = File.ReadAllText(file);
        Assert.DoesNotContain("Cr-cr-cry", exported); Assert.True(exported.Length < 20000);
        Assert.Contains("P95", exported);
    }

    [Fact]
    public async Task ConfirmedTranslationIsPublishedBeforeCachePersistenceOrAnotherCapture()
    {
        var cache = new BlockedWriteCache();
        using var pipeline = new TranslationPipeline(new FakeCapture(), new FakeOcr(), new FakeProvider(), cache);
        var published = new TaskCompletionSource<PipelineResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.ResultAvailable += result => published.TrySetResult(result);
        await PipelineTestDriver.ConfirmAsync(pipeline);
        var result = await published.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Arabic:Hello!", result.TranslatedText);
        Assert.True(result.CompletedTimestamp > 0);
        cache.Release.TrySetResult();
    }

    [Fact]
    public async Task MissingOfflineModelsNeverInvokeNetworkDuringPlaybackOrWarmup()
    {
        var called = false;
        using var client = new HttpClient(new DelegateHttpHandler((_, _) => { called = true; throw new InvalidOperationException("network forbidden"); }));
        var folder = Path.Combine(Path.GetTempPath(), "CortexTransl-no-network-" + Guid.NewGuid().ToString("N"));
        using var offline = new OfflineBergamotTranslationProvider(new OfflineModelInstaller(folder, folder, client));
        await Assert.ThrowsAsync<TranslationProviderException>(() => offline.TranslateAsync("Run!", "en", "ar"));
        await Assert.ThrowsAsync<TranslationProviderException>(() => offline.WarmAsync("en"));
        Assert.False(called);
    }

    [BergamotFact]
    public async Task WarmServiceIsInitializedOnceAndPreservesLyricTranslation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "CortexTransl-warm-" + Guid.NewGuid().ToString("N"));
        using var client = new HttpClient(new DelegateHttpHandler((_, _) => throw new InvalidOperationException("network forbidden")));
        var installer = new OfflineModelInstaller(folder, BergamotFactAttribute.FindModels(), client);
        using var diagnostics = new PerformanceDiagnostics { Enabled = true };
        using var offline = new OfflineBergamotTranslationProvider(installer, diagnostics);
        await offline.WarmAsync("en"); await offline.WarmAsync("en");
        const string lyric = "Cr-cr-cry since the day we parted";
        var actual = await offline.TranslateStoryAsync(lyric, "en", "ar", new());
        using var previous = new OfflineBergamotTranslationProvider(installer);
        Assert.Equal(await previous.TranslateStoryAsync(lyric, "en", "ar", new()), actual);
        Assert.Equal(1, diagnostics.Snapshot()[nameof(LatencyStage.ModelInitialization)].Count);
    }

    [BergamotFact]
    public async Task CancellationDuringNativeInferenceRejectsItsResultAndResidentServiceRecovers()
    {
        var folder = Path.Combine(Path.GetTempPath(), "CortexTransl-cancel-" + Guid.NewGuid().ToString("N"));
        using var client = new HttpClient(new DelegateHttpHandler((_, _) => throw new InvalidOperationException("network forbidden")));
        using var diagnostics = new PerformanceDiagnostics { Enabled = true };
        using var offline = new OfflineBergamotTranslationProvider(new OfflineModelInstaller(folder, BergamotFactAttribute.FindModels(), client), diagnostics);
        await offline.WarmAsync("en");
        using var cancellation = new CancellationTokenSource();
        var slow = offline.TranslateAsync(string.Join(' ', Enumerable.Repeat("We must reach the bridge before the enemy catches us.", 70)), "en", "ar", cancellation.Token);
        await Task.Delay(40);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow);
        // A completed native stage proves cancellation occurred after inference
        // started, rather than simply preventing a queued Task from starting.
        Assert.True(diagnostics.Snapshot()[nameof(LatencyStage.NativeInference)].Count >= 1);
        Assert.False(string.IsNullOrWhiteSpace(await offline.TranslateAsync("Run!", "en", "ar")));
        Assert.Equal(1, diagnostics.Snapshot()[nameof(LatencyStage.ModelInitialization)].Count);
    }

    private sealed class BlockedWriteCache : ITranslationCacheRepository
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> GetAsync(string text, string source, string target, string provider, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SaveAsync(string text, string source, string target, string provider, string translated, CancellationToken cancellationToken = default) => Release.Task.WaitAsync(cancellationToken);
    }
}

