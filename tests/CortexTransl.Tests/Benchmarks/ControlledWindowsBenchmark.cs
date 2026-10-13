using CortexTransl.App.Data;
using CortexTransl.App.Models;
using CortexTransl.App.Services.Cache;
using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Diagnostics;
using CortexTransl.App.Services.Ocr;
using CortexTransl.App.Services.Translation;
using CortexTransl.App.Utils;
using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;
using Baseline = CortexTransl.Tests.Benchmarks.Update17;

namespace CortexTransl.Tests.Benchmarks;

public sealed class ControlledWindowFactAttribute : FactAttribute
{
    public ControlledWindowFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CORTEX_TRANSL_RUN_WINDOWS_BENCHMARK") != "1")
            Skip = "Opt-in real WGC benchmark: set CORTEX_TRANSL_RUN_WINDOWS_BENCHMARK=1 on an interactive Windows desktop.";
        else if (BergamotFactAttribute.FindModels() is null) Skip = "The controlled benchmark needs installed en-ar models.";
    }
}

[CollectionDefinition("Windows latency benchmark", DisableParallelization = true)]
public sealed class WindowsBenchmarkCollection;

[Collection("Windows latency benchmark")]
public sealed class ControlledWindowsBenchmark(ITestOutputHelper output)
{
    [ControlledWindowFact]
    public async Task RealCaptureExcludesOverlayReusesStagingAndRetainsStaticDialogue()
    {
        await using var fixture = new AnimatedSubtitleFixture();
        await fixture.ReadyAsync(); await fixture.PresentAsync("I can't help you.");
        using var capture = WindowsGraphicsWindowCapturer.TryCreate(fixture.Handle)
            ?? throw new InvalidOperationException("WGC unavailable");
        var ocr = new WindowsOcrEngine();
        for (var sample = 0; sample < 2; sample++)
        {
            await Task.Delay(120);
            using var image = capture.CaptureRegion(fixture.Region, fixture.Bounds, TimeSpan.FromMilliseconds(200));
            Assert.NotNull(image);
            Assert.Equal(fixture.Region.Width, image.Width);
            Assert.Equal("I can't help you.", (await ocr.RecognizeAsync(image, "en")).Text, ignoreCase: true);
            using var metrics = new PerformanceDiagnostics();
            await fixture.PublishAsync("هذه طبقة عربية تغطي النص الإنجليزي", metrics);
        }
        Assert.Equal(1, capture.StagingAllocations);
        await fixture.SetAnimationAsync(false);
        // Drain any frames already in transit before verifying no-frame reuse.
        for (var sample = 0; sample < 6; sample++)
        {
            await Task.Delay(120);
            using var image = capture.CaptureRegion(fixture.Region, fixture.Bounds, TimeSpan.FromMilliseconds(200));
            Assert.NotNull(image);
        }
        var copies = capture.ReadbackCount;
        using var retained = capture.CaptureRegion(fixture.Region, fixture.Bounds, TimeSpan.Zero);
        Assert.NotNull(retained); Assert.Equal(copies, capture.ReadbackCount);
        // A changed ROI cannot reuse pixels from the previous physical location.
        var smaller = fixture.Region with { Width = fixture.Region.Width - 10 };
        using var invalidated = capture.CaptureRegion(smaller, fixture.Bounds, TimeSpan.Zero);
        Assert.Null(invalidated);
        await fixture.SetAnimationAsync(true); await Task.Delay(150);
        using var resized = capture.CaptureRegion(smaller, fixture.Bounds, TimeSpan.FromMilliseconds(200));
        Assert.NotNull(resized); Assert.Equal(smaller.Width, resized.Width);
        Assert.Equal(2, capture.StagingAllocations);
    }

    private static readonly string[] Corpus =
    ["Cr-cr-cry since the day we parted", "Run!", "No!", "Help!", "I can't help you.",
     "We must reach the bridge before the enemy catches us!", "I will never leave you", "He is alive."];

    [ControlledWindowFact]
    public async Task MeasureUpdate17AndFastSubtitlesWithRealWindowCaptureOcrBergamotAndOverlay()
    {
        await using var fixture = new AnimatedSubtitleFixture();
        await fixture.ReadyAsync();
        var directory = Environment.GetEnvironmentVariable("CORTEX_TRANSL_BENCHMARK_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "CortexTransl-latency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var baseline = await MeasureAsync(fixture, directory, legacy: true);
        GC.Collect(); GC.WaitForPendingFinalizers();
        var upgraded = await MeasureAsync(fixture, directory, legacy: false);
        var comparison = new { BaselineCommit = "e99fd23 (Update-17)", Scope = "controlled WPF animated source, real WGC/OCR/Bergamot/OverlayWindow; not a browser/game or physical display scanout measurement",
            baseline, upgraded };
        File.WriteAllText(Path.Combine(directory, "comparison.json"), JsonSerializer.Serialize(comparison, new JsonSerializerOptions { WriteIndented = true }));
        output.WriteLine(JsonSerializer.Serialize(comparison));
        Assert.All(baseline.Readings, x => Assert.True(x.CorrectOcr, $"Update-17 fixture {x.Index} was not correctly recognized."));
        Assert.All(upgraded.Readings, x => Assert.True(x.CorrectOcr, $"Upgraded fixture {x.Index} was not correctly recognized."));
        Assert.Equal(baseline.Readings.Select(x => x.Arabic), upgraded.Readings.Select(x => x.Arabic));
        Assert.True(upgraded.ReadbackBytes < baseline.ReadbackBytes);
        Assert.True(upgraded.StagingAllocations < baseline.StagingAllocations);
    }

    private sealed record Reading(int Index, double Milliseconds, bool CorrectOcr, string Arabic);
    private sealed record Measurements(IReadOnlyDictionary<string, StageStatistics> Stages, List<Reading> Readings,
        long Readbacks, long ReadbackBytes, long StagingAllocations, double WallSeconds, double CpuSeconds);

    private async Task<Measurements> MeasureAsync(AnimatedSubtitleFixture fixture, string directory, bool legacy)
    {
        using var diagnostics = new PerformanceDiagnostics { Enabled = true };
        var label = legacy ? "update17" : "fast-subtitles";
        // A fresh fixture DB on every run keeps cold-cache / repeated-line
        // samples reproducible without touching any real user cache.
        var factory = new SqliteConnectionFactory(Path.Combine(directory, label + "-" + Guid.NewGuid().ToString("N") + ".db"));
        await new DatabaseMigrator(factory).InitializeAsync();
        var cache = new SqliteTranslationCacheRepository(factory);
        using var networkForbidden = new HttpClient(new DelegateHttpHandler((_, _) => throw new InvalidOperationException("Benchmark must stay offline")));
        var installer = new OfflineModelInstaller(Path.Combine(directory, label + "-models"), BergamotFactAttribute.FindModels(), networkForbidden);
        using var capture = new FixtureCapture(fixture, diagnostics, legacy);
        var readings = new List<Reading>();
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime; var wall = Stopwatch.StartNew();
        Func<Task<PipelineResult>> tick;
        IDisposable pipeline;
        Action<PipelineResult>? available = null;
        TaskCompletionSource<PipelineResult>? pending = null;
        string expectedLine = string.Empty;
        IDisposable provider;
        if (legacy)
        {
            var offline = new Baseline.OfflineBergamotTranslationProvider(installer) { Diagnostics = diagnostics };
            provider = offline;
            offline.BenchmarkWarmup = true;
            await offline.TranslateAsync("Hello.", "en", "ar");
            offline.BenchmarkWarmup = false;
            var original = new Baseline.TranslationPipeline(capture, new Baseline.WindowsOcrEngine { Diagnostics = diagnostics }, offline, cache) { Diagnostics = diagnostics };
            original.Configure("controlled", new StorySettings());
            pipeline = original;
            tick = () => original.RunAsync(fixture.Region, "en", "ar", false);
        }
        else
        {
            var offline = new OfflineBergamotTranslationProvider(installer, diagnostics);
            provider = offline;
            await offline.WarmAsync("en");
            var current = new TranslationPipeline(capture, new WindowsOcrEngine(diagnostics), offline, cache, diagnostics: diagnostics);
            current.Configure("controlled", new StorySettings { FastSubtitles = true, PerformanceDiagnostics = true });
            available = result =>
            {
                if (OcrTextCanonicalizer.Normalize(result.OriginalText).Equals(expectedLine, StringComparison.OrdinalIgnoreCase))
                    pending?.TrySetResult(result);
            };
            current.ResultAvailable += available;
            pipeline = current;
            tick = () => current.RunAsync(fixture.Region, "en", "ar", false);
        }
        try
        {
            for (var index = 0; index < 24; index++)
            {
                var expected = Corpus[index % Corpus.Length];
                expectedLine = expected;
                pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var appeared = await fixture.PresentAsync(expected);
                var timeout = Stopwatch.StartNew();
                var correct = false; var arabic = ""; var duration = 0.0;
                while (timeout.ElapsedMilliseconds < 3500)
                {
                    diagnostics.SampleResources();
                    var turn = Stopwatch.GetTimestamp();
                    var result = await tick();
                    if (pending.Task.IsCompletedSuccessfully) result = pending.Task.Result;
                    if (!string.IsNullOrWhiteSpace(result.TranslatedText) && OcrTextCanonicalizer.Normalize(result.OriginalText).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    {
                        var publication = Stopwatch.GetTimestamp();
                        if (result.CompletedTimestamp > 0) diagnostics.Elapsed(LatencyStage.OverlayDispatch, result.CompletedTimestamp, publication);
                        long rendered;
                        rendered = await fixture.PublishAsync(result.TranslatedText, diagnostics);
                        diagnostics.Elapsed(LatencyStage.OverlayRender, publication, rendered);
                        diagnostics.Elapsed(LatencyStage.ControlledAppearanceToOverlayRender, appeared, rendered);
                        duration = Stopwatch.GetElapsedTime(appeared, rendered).TotalMilliseconds;
                        correct = true; arabic = result.TranslatedText;
                        break;
                    }
                    var delay = legacy ? 100 : Math.Max(5, 80 - (int)Stopwatch.GetElapsedTime(turn).TotalMilliseconds);
                    if (!legacy) await Task.WhenAny(pending.Task, Task.Delay(delay));
                    else await Task.Delay(delay);
                }
                if (!correct) duration = timeout.Elapsed.TotalMilliseconds;
                readings.Add(new(index, duration, correct, arabic));
                File.WriteAllText(Path.Combine(directory, label + "-readings.json"), JsonSerializer.Serialize(readings));
                output.WriteLine($"{label} fixture {index}: {duration:F1} ms, exact OCR={correct}");
                // Keep unchanged animated text on screen briefly to measure OCR
                // scheduling/readback and CPU at a meaningful steady workload.
                for (var stable = 0; stable < 3; stable++) { await tick(); await Task.Delay(80); }
            }
            diagnostics.SampleResources();
            diagnostics.Export(Path.Combine(directory, label + "-stages.json"));
            return new(diagnostics.Snapshot(), readings, capture.Readbacks, capture.ReadbackBytes, capture.StagingAllocations,
                wall.Elapsed.TotalSeconds, (process.TotalProcessorTime - cpuStart).TotalSeconds);
        }
        finally
        {
            pipeline.Dispose(); provider.Dispose();
        }
    }

    private sealed class FixtureCapture : IScreenCaptureService
    {
        private readonly AnimatedSubtitleFixture _fixture;
        private readonly WindowsGraphicsWindowCapturer? _current;
        private readonly Baseline.WindowsGraphicsWindowCapturer? _baseline;
        public FixtureCapture(AnimatedSubtitleFixture fixture, PerformanceDiagnostics diagnostics, bool legacy)
        {
            _fixture = fixture;
            if (legacy) { _baseline = Baseline.WindowsGraphicsWindowCapturer.TryCreate(fixture.Handle) ?? throw new InvalidOperationException("Update-17 WGC unavailable"); _baseline.Diagnostics = diagnostics; }
            else _current = WindowsGraphicsWindowCapturer.TryCreate(fixture.Handle, diagnostics) ?? throw new InvalidOperationException("WGC unavailable");
        }
        public long Readbacks => _baseline?.ReadbackCount ?? _current!.ReadbackCount;
        public long ReadbackBytes => _baseline?.ReadbackBytes ?? _current!.ReadbackBytes;
        public long StagingAllocations => _baseline?.StagingAllocations ?? _current!.StagingAllocations;
        public Task<Bitmap> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bitmap = _baseline is not null ? _baseline.CaptureRegion(region, _fixture.Bounds, TimeSpan.FromMilliseconds(80))
                : _current!.CaptureRegion(region, _fixture.Bounds, TimeSpan.FromMilliseconds(80));
            return Task.FromResult(bitmap ?? throw new CaptureUnavailableException("Controlled fixture frame unavailable"));
        }
        public void Dispose() { _baseline?.Dispose(); _current?.Dispose(); }
    }
}
