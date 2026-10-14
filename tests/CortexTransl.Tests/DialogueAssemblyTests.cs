using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Ocr;
using CortexTransl.App.Utils;
using Xunit;

namespace CortexTransl.Tests;

public sealed class DialogueAssemblyTests
{
    [Fact]
    public void WrappedBlocksAndSameRowFragmentsFormOneSentence()
    {
        var result = new OcrResult("ignored", [new("We must", new(0, 0, 70, 12)),
            new("reach the bridge", new(80, 0, 90, 12)), new("before dawn!", new(0, 40, 90, 12))]);
        Assert.Equal("We must reach the bridge before dawn!", DialogueTextAssembler.Assemble(result, "en"));
    }

    [Theory]
    [InlineData("We must\nleave now!", "We must leave now!")]
    [InlineData("Stop!\nWait for me.", "Stop!\nWait for me.")]
    [InlineData("\"Stop!\"\nWait for me.", "\"Stop!\"\nWait for me.")]
    public void FallbackAndBlockTextUseTheSameSentenceBoundaries(string text, string expected)
    {
        Assert.Equal(expected, DialogueTextAssembler.Assemble(new(text), "en"));
        Assert.Equal(expected, DialogueTextAssembler.Assemble(new(text, [new(text, new(0, 0, 100, 30))]), "en"));
    }

    [Fact]
    public void ShortPunctuatedTypewriterFramesNeverBypassDebounce()
    {
        var clock = new ManualClock();
        var tracker = new DialogueTracker(clock);
        foreach (var text in new[] { "Wait.", "Wait..", "Wait...", "Wait... come back!" })
        {
            Assert.False(tracker.Observe(text).Ready);
            clock.Advance(60);
        }
        Assert.False(tracker.Observe("Wait... come back!").Ready);
        clock.Advance(250);
        Assert.True(tracker.Observe("Wait... come back!").Ready);
    }

    [Fact]
    public void VisualGrowthAndMissingFramesRestartConfirmation()
    {
        var clock = new ManualClock();
        var tracker = new DialogueTracker(clock);
        tracker.Observe("Wait!", visualSignature: 1);
        clock.Advance(100);
        Assert.False(tracker.Observe("Wait!", visualSignature: 2).Ready);
        clock.Advance(100);
        Assert.True(tracker.Observe("Wait!", visualSignature: 2).Ready);
        tracker.Observe("", ocrFailed: true);
        clock.Advance(100);
        Assert.False(tracker.Observe("Wait!", visualSignature: 2).Ready);
        clock.Advance(100);
        Assert.True(tracker.Observe("Wait!", visualSignature: 2).Ready);
    }

    [Fact]
    public void RepeatedFreshOcrCanConfirmTextOverAnAnimatedBackground()
    {
        var clock = new ManualClock();
        var tracker = new DialogueTracker(clock);
        Assert.False(tracker.Observe("Wait!", visualSignature: 1).Ready);
        clock.Advance(180);
        Assert.False(tracker.Observe("Wait!", visualSignature: 2).Ready);
        clock.Advance(180);
        Assert.True(tracker.Observe("Wait!", visualSignature: 3).Ready);
    }

    [Fact]
    public void OcrNoiseDoesNotCreateANewDialogueSequence()
    {
        var clock = new ManualClock();
        var tracker = new DialogueTracker(clock);
        var initial = tracker.Observe("I can't leave!");
        clock.Advance(100);
        var jitter = tracker.Observe("| I can’t leave! _ x");
        Assert.True(jitter.Ready);
        Assert.False(jitter.Changed);
        Assert.Equal(initial.Sequence, jitter.Sequence);
        Assert.Equal("I can't leave!", jitter.Text);
    }
}
