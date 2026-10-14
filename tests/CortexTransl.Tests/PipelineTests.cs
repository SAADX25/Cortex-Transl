using CortexTransl.App.Models;
using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Ocr;
using CortexTransl.App.Services.Translation;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace CortexTransl.Tests;

public sealed class PipelineTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HelloTypewriterCallsProviderExactlyOnceWithTheFinalSentence(bool fast)
    {
        var clock = new ManualClock(); var ocr = new FakeOcr();
        var provider = new FakeProvider { Translate = (_, _) => Task.FromResult("مرحباً بالعالم!") };
        using var pipeline = new TranslationPipeline(new FakeCapture(), ocr, provider, new FakeCache(), clock);
        pipeline.Configure("typewriter", new() { FastSubtitles = fast });
        foreach (var frame in new[] { "Hel", "Hello ", "Hello world!" })
        {
            ocr.Result = new(frame);
            await pipeline.RunAsync(Region, "en", "ar", false);
            clock.Advance(60);
            Assert.Empty(provider.Calls);
        }
        await pipeline.RunAsync(Region, "en", "ar", false);
        Assert.Empty(provider.Calls);
        clock.Advance(250);
        var translated = await AwaitTranslation(pipeline, "مرحباً بالعالم!", clock: clock);
        for (var frame = 0; frame < 5; frame++)
        { clock.Advance(80); await pipeline.RunAsync(Region, "en", "ar", false); }
        Assert.Equal("Hello world!", Assert.Single(provider.Calls));
        Assert.Equal("Hello world!", translated.OriginalText);
    }

    [Fact]
    public async Task TwoLineSubtitleProducesOneCoherentArabicTranslation()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider { Translate = (text, _) => Task.FromResult(text == "We must leave before dawn!"
            ? "يجب أن نغادر قبل الفجر!" : "ترجمة مجزأة") };
        var ocr = new FakeOcr { Result = new("We must leave\nbefore dawn!",
            [new("before dawn!", new(0, 30, 100, 12)), new("We must leave", new(0, 0, 100, 12))]) };
        using var pipeline = new TranslationPipeline(new FakeCapture(), ocr, provider, new FakeCache(), clock);
        var result = await AwaitTranslation(pipeline, "يجب أن نغادر قبل الفجر!", clock: clock);
        Assert.Equal("We must leave before dawn!", Assert.Single(provider.Calls));
        Assert.DoesNotContain('\n', result.TranslatedText);
    }

    [Theory]
    [InlineData("Run!")]
    [InlineData("No!")]
    [InlineData("Help!")]
    public async Task FullPipelineConfirmsShortLineAfterLongLine(string shortLine)
    {
        const string longLine = "We must reach the bridge before the enemy catches us!";
        var clock = new ManualClock(); var ocr = new FakeOcr { Result = new(longLine) };
        using var pipeline = new TranslationPipeline(new FakeCapture(), ocr, new FakeProvider(), new FakeCache(), clock);
        await pipeline.RunAsync(Region, "en", "ar", false); clock.Advance(100);
        await AwaitTranslation(pipeline, "Arabic:" + longLine, clock: clock);
        ocr.Result = new(shortLine);
        clock.Advance(250);
        var changed = await pipeline.RunAsync(Region, "en", "ar", false);
        Assert.True(changed.ClearOverlay);
        var translated = await AwaitTranslation(pipeline, "Arabic:" + shortLine, clock: clock);
        Assert.Equal(shortLine, translated.OriginalText);
    }

    [Fact]
    public async Task FullPipelineWaitsForTypewriterTextToSettle()
    {
        var clock = new ManualClock(); var ocr = new FakeOcr(); var provider = new FakeProvider();
        using var pipeline = new TranslationPipeline(new FakeCapture(), ocr, provider, new FakeCache(), clock);
        foreach (var text in new[] { "We", "We must", "We must leave" })
        {
            ocr.Result = new(text);
            await pipeline.RunAsync(Region, "en", "ar", false); clock.Advance(100);
        }
        Assert.Empty(provider.Calls);
        clock.Advance(250);
        await AwaitTranslation(pipeline, "Arabic:We must leave", clock: clock);
        Assert.Equal("We must leave", Assert.Single(provider.Calls));
    }
    [Fact]
    public async Task LosingSelectedWindowClearsImmediatelyAndCancelsLateTranslation()
    {
        var delayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider { Translate = (_, _) => delayed.Task };
        var capture = new FakeCapture(); var cache = new FakeCache();
        using var pipeline = new TranslationPipeline(capture, new FakeOcr(), provider, cache);
        await PipelineTestDriver.ConfirmAsync(pipeline);
        await WaitFor(() => provider.Calls.Count == 1);
        capture.Failure = new CaptureUnavailableException("Bound game minimized");
        var lost = await pipeline.RunAsync(Region, "en", "ar", false);
        Assert.True(lost.ClearOverlay);
        Assert.Empty(lost.TranslatedText);
        Assert.Equal("Bound game minimized", lost.Status);
        delayed.SetResult("obsolete");
        await Task.Delay(20);
        Assert.Empty((await pipeline.RunAsync(Region, "en", "ar", false)).TranslatedText);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task SameTextCanRetryAfterCallerCancellationWithANewToken()
    {
        var delayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = 0;
        var provider = new FakeProvider { Translate = (_, ct) => Interlocked.Increment(ref call) == 1
            ? delayed.Task.WaitAsync(ct) : Task.FromResult("recovered") };
        using var pipeline = new TranslationPipeline(new FakeCapture(), new FakeOcr(), provider, new FakeCache());
        using var cancellation = new CancellationTokenSource();
        await PipelineTestDriver.ConfirmAsync(pipeline, cancellationToken: cancellation.Token);
        await WaitFor(() => provider.Calls.Count == 1);
        cancellation.Cancel();
        await AwaitTranslation(pipeline, "recovered");
        Assert.Equal(2, provider.Calls.Count);
    }

    [Fact]
    public async Task MenuGlossaryUsesPerGameArabicTermsWithoutProviderCalls()
    {
        var clock = new ManualClock(); var provider = new FakeProvider();
        var ocr = new FakeOcr { Result = new("Silver Keep", [new("Silver Keep", new(3, 4, 32, 12))]) };
        using var pipeline = new TranslationPipeline(new FakeCapture(), ocr, provider, new FakeCache(), clock);
        pipeline.Configure("Test game", new StorySettings { Terminology = new() { ["Silver Keep"] = "قلعة الفضة" } });
        await pipeline.RunAsync(Region, "en", "ar", true); clock.Advance(400);
        await AwaitTranslation(pipeline, "قلعة الفضة", listMode: true, clock: clock);
        Assert.Empty(provider.Calls);
    }
    private static readonly CaptureRegion Region = new(0, 0, 64, 32);

    [Fact]
    public async Task MenuOcrRectangleJitterDoesNotContinuouslyCancelTranslation()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider();
        var ocr = new FakeOcr();
        using var pipeline = new TranslationPipeline(new FakeCapture(), ocr, provider, new FakeCache(), clock);
        for (var frame = 0; frame < 10; frame++)
        {
            var offset = frame % 2;
            ocr.Result = new("Continue", [new("Continue", new(3 + offset, 4 + offset, 32 + offset, 12))]);
            await pipeline.RunAsync(Region, "en", "ar", true);
            clock.Advance(100);
            await Task.Delay(10);
        }
        Assert.Equal("Continue", Assert.Single(provider.Calls));
        Assert.Equal("Arabic:Continue", pipeline.LatestResult.TranslatedText);
        var previousSequence = pipeline.CurrentSequence;
        var moved = new CaptureRegion(20, 20, 32, 12);
        ocr.Result = new("Continue", [new("Continue", moved)]);
        await pipeline.RunAsync(Region, "en", "ar", true);
        var result = await AwaitTranslation(pipeline, "Arabic:Continue", listMode: true, clock: clock);
        Assert.True(result.Sequence > previousSequence);
        Assert.Equal(moved, Assert.Single(result.Blocks).Bounds);
        Assert.Single(provider.Calls);
    }

    [Fact]
    public async Task WrappedSubtitleBlocksAreTranslatedAsOneUtterance()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider();
        var ocr = new FakeOcr { Result = new("We must\nleave before dawn!",
            [new("We must", new(0, 0, 80, 12)), new("leave before dawn!", new(0, 40, 100, 12))]) };
        using var pipeline = new TranslationPipeline(new FakeCapture(), ocr, provider, new FakeCache(), clock);
        await AwaitTranslation(pipeline, "Arabic:We must leave before dawn!", clock: clock);
        Assert.Equal("We must leave before dawn!", Assert.Single(provider.Calls));
    }

    [Fact]
    public async Task NoisyOcrDoesNotRetranslateAnAlreadyPublishedSubtitle()
    {
        var clock = new ManualClock();
        var provider = new FakeProvider();
        var ocr = new FakeOcr { Result = new("I can't go!") };
        using var pipeline = new TranslationPipeline(new FakeCapture(), ocr, provider, new FakeCache(), clock);
        var first = await AwaitTranslation(pipeline, "Arabic:I can't go!", clock: clock);
        ocr.Result = new("| I can’t go! _ x");
        clock.Advance(250);
        var jitter = await pipeline.RunAsync(Region, "en", "ar", false);
        Assert.Equal(first.Sequence, jitter.Sequence);
        Assert.Equal(first.TranslatedText, jitter.TranslatedText);
        Assert.Single(provider.Calls);
    }

    private static async Task<PipelineResult> AwaitTranslation(TranslationPipeline pipeline, string expected, bool listMode = false,
        ManualClock? clock = null)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            var result = await pipeline.RunAsync(Region, "en", "ar", listMode);
            if (result.TranslatedText == expected) return result;
            clock?.Advance(100);
            await Task.Delay(5);
        }
        throw new TimeoutException("Translation was not published: " + expected);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition() && timeout.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(5);
        Assert.True(condition());
    }

    [Fact]
    public async Task SlowNetworkDoesNotBlockCaptureAndBoundedQueueKeepsLatestDialogue()
    {
        var clock = new ManualClock();
        var capture = new FakeCapture(); var ocr = new FakeOcr(); var cache = new FakeCache();
        var delayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider { Translate = (text, _) => text == "Hello!" ? delayed.Task : Task.FromResult("Arabic:" + text) };
        using var pipeline = new TranslationPipeline(capture, ocr, provider, cache, clock);
        await PipelineTestDriver.ConfirmAsync(pipeline, clock);
        await WaitFor(() => provider.Calls.Count == 1);
        var times = new List<double>();
        foreach (var text in new[] { "No!", "Help!", "Run!" })
        {
            ocr.Result = new(text);
            clock.Advance(250);
            var timer = Stopwatch.StartNew();
            var detection = pipeline.RunAsync(Region, "en", "ar", false);
            Assert.Same(detection, await Task.WhenAny(detection, Task.Delay(2000)));
            var result = await detection;
            times.Add(timer.Elapsed.TotalMilliseconds);
            Assert.True(result.ClearOverlay);
            Assert.Empty(result.TranslatedText);
        }
        Assert.Equal(5, capture.Captures);
        Assert.Single(provider.Calls);
        // Deliberately ignores cancellation, simulating a late native/network response.
        delayed.SetResult("obsolete Arabic");
        var latest = await AwaitTranslation(pipeline, "Arabic:Run!", clock: clock);
        Assert.Equal("Run!", latest.OriginalText);
        Assert.Equal(new[] { "Hello!", "Run!" }, provider.Calls.ToArray());
        Assert.Equal(1, cache.Count);
        output.WriteLine($"Synthetic capture/detection while network stalled: mean={times.Average():F3} ms, max={times.Max():F3} ms; active requests=1, pending capacity=1.");
    }

    [Fact]
    public async Task CooperativeCancellationAllowsFastConsecutiveSubtitles()
    {
        var ocr = new FakeOcr();
        var delayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider { Translate = (text, ct) => text == "Hello!" ? delayed.Task.WaitAsync(ct) : Task.FromResult("Arabic:" + text) };
        using var pipeline = new TranslationPipeline(new FakeCapture(), ocr, provider, new FakeCache());
        await PipelineTestDriver.ConfirmAsync(pipeline);
        await WaitFor(() => provider.Calls.Count == 1);
        ocr.Result = new("Help!");
        await pipeline.RunAsync(Region, "en", "ar", false);
        await AwaitTranslation(pipeline, "Arabic:Help!");
        ocr.Result = new("No!");
        await pipeline.RunAsync(Region, "en", "ar", false);
        await AwaitTranslation(pipeline, "Arabic:No!");
        Assert.Equal(3, provider.Calls.Count);
    }

    [Fact]
    public async Task UnchangedFailedFrameIsRetriedInsteadOfFingerprintPoisoning()
    {
        var clock = new ManualClock(); var capture = new FakeCapture { SameFrame = true }; var ocr = new FakeOcr();
        using var pipeline = new TranslationPipeline(capture, ocr, new FakeProvider(), new FakeCache(), clock);
        await AwaitTranslation(pipeline, "Arabic:Hello!", clock: clock);
        clock.Advance(500);
        ocr.Failure = new InvalidOperationException("temporary OCR failure");
        var failed = await pipeline.RunAsync(Region, "en", "ar", false);
        Assert.Equal("Arabic:Hello!", failed.TranslatedText);
        var calls = ocr.Calls;
        ocr.Failure = null; ocr.Result = new("Run!");
        await pipeline.RunAsync(Region, "en", "ar", false);
        Assert.True(ocr.Calls > calls);
        await AwaitTranslation(pipeline, "Arabic:Run!", clock: clock);
    }

    [Fact]
    public async Task EmptyFrameIsRetriedAndDisappearanceClearsOverlay()
    {
        var clock = new ManualClock(); var capture = new FakeCapture { SameFrame = true }; var ocr = new FakeOcr();
        using var pipeline = new TranslationPipeline(capture, ocr, new FakeProvider(), new FakeCache(), clock);
        await AwaitTranslation(pipeline, "Arabic:Hello!", clock: clock);
        clock.Advance(500); ocr.Result = new("");
        Assert.False((await pipeline.RunAsync(Region, "en", "ar", false)).ClearOverlay);
        clock.Advance(200);
        var gone = await pipeline.RunAsync(Region, "en", "ar", false);
        Assert.True(gone.ClearOverlay); Assert.Empty(gone.TranslatedText);
        var calls = ocr.Calls;
        ocr.Result = new("No!");
        await pipeline.RunAsync(Region, "en", "ar", false);
        Assert.True(ocr.Calls > calls);
        await AwaitTranslation(pipeline, "Arabic:No!", clock: clock);
    }

    [Fact]
    public async Task ResetRejectsLateResultEvenWhenNewSceneHasSameText()
    {
        var delayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var provider = new FakeProvider { Translate = (_, _) => Interlocked.Increment(ref calls) == 1 ? delayed.Task : Task.FromResult("new scene") };
        using var pipeline = new TranslationPipeline(new FakeCapture(), new FakeOcr(), provider, new FakeCache());
        await PipelineTestDriver.ConfirmAsync(pipeline);
        await WaitFor(() => provider.Calls.Count == 1);
        var oldSequence = pipeline.CurrentSequence;
        pipeline.BeginScene("A new scene supplied by the player");
        await pipeline.RunAsync(Region, "en", "ar", false);
        Assert.True(pipeline.CurrentSequence > oldSequence);
        delayed.SetResult("old scene");
        var result = await AwaitTranslation(pipeline, "new scene");
        Assert.Equal(pipeline.CurrentSequence, result.Sequence);
        Assert.Empty(provider.Contexts.Last().RecentDialogue);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("deepl")]
    public async Task ProviderFailuresRetryAndRecover(string engine)
    {
        var clock = new ManualClock(); var fail = true;
        var provider = new FakeProvider { Id = engine, Translate = (text, _) => fail
            ? Task.FromException<string>(new TranslationProviderException("temporary failure", "test"))
            : Task.FromResult("recovered") };
        using var pipeline = new TranslationPipeline(new FakeCapture(), new FakeOcr(), provider, new FakeCache(), clock);
        await PipelineTestDriver.ConfirmAsync(pipeline, clock);
        await WaitFor(() => provider.Calls.Count == 1);
        await Task.Delay(30);
        for (var index = 0; index < 5; index++) await pipeline.RunAsync(Region, "en", "ar", false);
        Assert.Single(provider.Calls); // Cooldown prevents request storms.
        fail = false; clock.Advance(1100);
        await AwaitTranslation(pipeline, "recovered", clock: clock);
        Assert.Equal(2, provider.Calls.Count);
    }

    [Fact]
    public async Task ContextContainsRecentObservedDialogueAndClearsAfterSceneGap()
    {
        var clock = new ManualClock(); var ocr = new FakeOcr(); var provider = new FakeProvider();
        using var pipeline = new TranslationPipeline(new FakeCapture(), ocr, provider, new FakeCache(), clock);
        pipeline.Configure("Game A", new StorySettings { Speaker = "Configured speaker", SceneState = "Known scene" });
        await AwaitTranslation(pipeline, "Arabic:Hello!", clock: clock);
        ocr.Result = new("Run!");
        await AwaitTranslation(pipeline, "Arabic:Run!", clock: clock);
        var context = provider.Contexts.Last();
        Assert.Equal("Hello!", Assert.Single(context.RecentDialogue).Text);
        Assert.Equal("Configured speaker", context.Speaker);
        ocr.Result = new(""); clock.Advance(5000);
        await pipeline.RunAsync(Region, "en", "ar", false);
        ocr.Result = new("Help!");
        await AwaitTranslation(pipeline, "Arabic:Help!", clock: clock);
        Assert.Empty(provider.Contexts.Last().RecentDialogue);
    }

    [Fact]
    public async Task MenuModePreservesTranslatedBoundsAndUsesUiLabelRoute()
    {
        var clock = new ManualClock(); var bounds = new CaptureRegion(3, 4, 32, 12);
        var ocr = new FakeOcr { Result = new("Continue", [new("Continue", bounds)]) };
        using var pipeline = new TranslationPipeline(new FakeCapture(), ocr, new FakeProvider(), new FakeCache(), clock);
        await pipeline.RunAsync(Region, "en", "ar", true); clock.Advance(400);
        var result = await AwaitTranslation(pipeline, "Arabic:Continue", listMode: true, clock: clock);
        Assert.Equal(bounds, Assert.Single(result.Blocks).Bounds);
    }
}
