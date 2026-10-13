using CortexTransl.App.Data;
using CortexTransl.App.Models;
using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Settings;
using CortexTransl.App.Services.Translation;
using CortexTransl.App.Utils;
using Xunit;

namespace CortexTransl.Tests;

public sealed class CleanupRegressionTests
{
    [Fact]
    public void ApiKeyEncryptionRoundTripsAndCorruptSavedKeyFailsClosed()
    {
        var service = new AppSettingsService(Path.GetTempPath());
        var encrypted = service.EncryptApiKey("synthetic-key");
        Assert.NotEmpty(encrypted);
        Assert.NotEqual("synthetic-key", encrypted);
        Assert.Equal("synthetic-key", service.DecryptApiKey(encrypted));
        Assert.Empty(service.DecryptApiKey("invalid-base64"));
    }

    [Fact]
    public void FailingResourceCleanupStillAllowsRemainingResourcesToDispose()
    {
        var failed = new TrackedResource { Failure = new InvalidOperationException("synthetic disposal failure") };
        var next = new TrackedResource();
        ResourceCleanup.Dispose(failed, "cleanup-test");
        ResourceCleanup.Dispose(next, "cleanup-test");
        Assert.True(failed.Disposed); Assert.True(next.Disposed);
    }

    private sealed class TrackedResource : IDisposable
    {
        public bool Disposed { get; private set; }
        public Exception? Failure { get; init; }
        public void Dispose() { Disposed = true; if (Failure is not null) throw Failure; }
    }

    [Theory]
    [InlineData("cover", 500)]
    [InlineData("below", 608)]
    [InlineData("above", 392)]
    public void OverlayPlacementRetainsAllSupportedChoices(string placement, int top)
    {
        Assert.Equal(new CaptureRegion(300, top, 600, 100),
            OverlaySettings.CalculatePlacement(new(300, 500, 600, 100), placement, new(0, 0, 1920, 1080)));
    }

    [Theory]
    [InlineData("above", -100, -100)]
    [InlineData("below", 820, 820)]
    [InlineData("below", 200, 308)]
    public void OverlayPlacementKeepsScreenEdgesAndNegativeMonitorOrigins(string placement, int y, int expectedY)
    {
        Assert.Equal(new CaptureRegion(-600, expectedY, 600, 100),
            OverlaySettings.CalculatePlacement(new(-400, y, 600, 100), placement, new(-1920, -100, 1920, 1080)));
    }

    [Fact]
    public async Task StopCaptureReleasesSessionAndRejectsLateResults()
    {
        var delayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new FakeCapture(); var cache = new FakeCache();
        using var pipeline = new TranslationPipeline(capture, new FakeOcr(), new FakeProvider
        {
            Translate = (_, _) => { started.TrySetResult(); return delayed.Task; }
        }, cache);
        await pipeline.RunAsync(new(0, 0, 64, 32), "en", "ar", false);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        pipeline.StopCapture();
        Assert.Equal(1, capture.ReleasedSessions);
        delayed.SetResult("obsolete translation");
        // Synchronize with the worker by queuing a new translation.
        pipeline.BindCapture(new(0, 0, 64, 32), false, onlyIfUnbound: true);
        var ready = new TaskCompletionSource<PipelineResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        pipeline.ResultAvailable += result => ready.TrySetResult(result);
        await pipeline.RunAsync(new(0, 0, 64, 32), "en", "ar", false);
        var result = await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cache.Saved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(pipeline.CurrentSequence, result.Sequence);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task SqliteFactoryEscapesSemicolonsInDatabasePathsAndOpensConcurrently()
    {
        var path = Path.Combine(Path.GetTempPath(), "CortexTransl-semi;colon-" + Guid.NewGuid().ToString("N"), "fixture.db");
        var factory = new SqliteConnectionFactory(path);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await using var connection = factory.CreateConnection();
            await connection.OpenAsync();
            Assert.Equal(path, connection.DataSource);
        })));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task SettingsSaveAndLoadRetainsPlacementTermsAndFastProfile()
    {
        var service = new AppSettingsService(Path.Combine(Path.GetTempPath(), "CortexTransl-settings-" + Guid.NewGuid().ToString("N")));
        await service.SaveAsync(new() { OverlayPlacement = "below", TranslationEngine = "deepl",
            OverlayTextColor = "gold", RegionWidth = 640, Story = new() { FastSubtitles = true,
                Terminology = new() { ["Keep"] = "قلعة" } } });
        var settings = await service.LoadAsync();
        Assert.Equal("below", settings.OverlayPlacement);
        Assert.Equal("deepl", settings.TranslationEngine);
        Assert.Equal("gold", settings.OverlayTextColor);
        Assert.Equal(640, settings.RegionWidth);
        Assert.True(settings.Story.FastSubtitles);
        Assert.Equal("قلعة", settings.Story.Terminology["Keep"]);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync(new(), canceled.Token));
        Assert.Equal("below", (await service.LoadAsync()).OverlayPlacement);
    }

    [Fact]
    public async Task TranslationTestRunnerPreservesChunkOrderAndFindsMissingResults()
    {
        var provider = new BatchProvider(["ترجمة أولى"]);
        var result = await new TranslationTestRunner(provider).RunAsync("First line.\nSecond line.", "en", "ar");
        Assert.Equal(TranslationContentKind.GameDialogue, provider.Kind);
        Assert.Equal("ترجمة أولى", result.Text);
        Assert.Equal(2, result.Chunks.Count);
        Assert.Equal(2, result.Chunks[1].Index);
        Assert.Equal("Second line.", result.Chunks[1].OriginalText);
        Assert.True(result.Chunks[1].IsEmpty);
        Assert.Contains("1/2 chunks returned empty", result.Status);
    }

    [Fact]
    public async Task TranslationTestRunnerReportsExtraResultsAndHonorsCancellation()
    {
        var runner = new TranslationTestRunner(new BatchProvider(["ترجمة صحيحة", "extra"]));
        var result = await runner.RunAsync("Run!", "en", "ar");
        Assert.Equal("ترجمة صحيحة", result.Text);
        Assert.Contains("extra translation chunks", result.Status);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync("Run!", "en", "ar", canceled.Token));
    }

    private sealed class BatchProvider(IReadOnlyList<string> results) : ITranslationProvider
    {
        public string Id => "test";
        public bool SupportsContextualLongText => false;
        public TranslationContentKind Kind { get; private set; }
        public Task<IReadOnlyList<string>> TranslateManyAsync(IReadOnlyList<string> texts, string sourceLanguage,
            string targetLanguage, CancellationToken cancellationToken = default, TranslationContentKind contentKind = TranslationContentKind.General)
        { cancellationToken.ThrowIfCancellationRequested(); Kind = contentKind; return Task.FromResult(results); }
        public Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage,
            CancellationToken cancellationToken = default, TranslationContentKind contentKind = TranslationContentKind.General)
            => throw new NotSupportedException();
    }
}
