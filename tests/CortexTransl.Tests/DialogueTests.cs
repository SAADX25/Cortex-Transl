using CortexTransl.App.Models;
using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Translation;
using CortexTransl.App.Utils;
using Xunit;

namespace CortexTransl.Tests;

public sealed class DialogueTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialHelloFramesRequireTheFinalSentenceToSettle(bool fast)
    {
        var clock = new ManualClock();
        var tracker = new DialogueTracker(clock) { Settings = new() { FastSubtitles = fast } };
        foreach (var frame in new[] { "Hel", "Hello ", "Hello world!" })
        {
            Assert.False(tracker.Observe(frame).Ready);
            clock.Advance(60);
        }
        Assert.False(tracker.Observe("Hello world!").Ready);
        clock.Advance(250);
        var final = tracker.Observe("Hello world!");
        Assert.True(final.Ready);
        Assert.Equal("Hello world!", final.Text);
    }

    [Theory]
    [InlineData("[[ z x q 0 q x z ]]، مرحباً ] ] Z X Q 1 Q X Z [ [ !")]
    [InlineData("\u202E]]ZXQ0QXZ[[، مرحباً [[ZXQ١QXZ]] !")]
    public void ArabicNamesAndVariablesSurviveSpacedOrMirroredPlaceholders(string output)
    {
        var context = new StoryTranslationContext { SpeakerNames = new Dictionary<string, string> { ["Alice"] = "أليس" } };
        var protectedText = ProtectedStoryText.Create("Alice, greet {player_name}!", "en", "ar", context, useXml: false)!;
        var restored = ArabicTranslationCleaner.Clean(protectedText.ReadTranslation(output), "ar");
        Assert.Equal("أليس، مرحباً {player_name}!", restored);
        Assert.DoesNotContain("ZXQ", restored, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("\u202Eقالت Alice !", "قالت Alice!")]
    [InlineData("هل تسمعني Bob ?", "هل تسمعني Bob?")]
    [InlineData("انتظر Alice . . .", "انتظر Alice...")]
    public void BidiCleanupPreservesMixedWordsAndTerminalPunctuation(string source, string expected)
        => Assert.Equal(expected, ArabicTranslationCleaner.Clean(source, "ar"));

    [Theory]
    [InlineData("Run!")]
    [InlineData("No!")]
    [InlineData("Help!")]
    public void ShortDialogueAfterLongDialogueRequiresIndependentConfirmation(string shortLine)
    {
        const string longLine = "We must make our way through the forest before the enemy reaches us.";
        Assert.False(DialogueTextAssembler.LooksLikeOcrGlitch(longLine, shortLine));
        var clock = new ManualClock();
        var tracker = new DialogueTracker(clock);
        var before = tracker.Observe(longLine);
        var after = tracker.Observe(shortLine);
        Assert.False(after.Ready);
        clock.Advance(100);
        Assert.True(tracker.Observe(shortLine).Ready);
        Assert.True(after.Changed);
        Assert.True(after.Sequence > before.Sequence);
    }

    [Theory]
    [InlineData("I can go.", "I can't go.")]
    [InlineData("He is alive.", "He is a live.")]
    [InlineData("Now!", "No!")]
    [InlineData("I hate you.", "I have you.")]
    [InlineData("Go?", "Go!")]
    [InlineData("I will help her.", "I will help him.")]
    public void SimilarSentencesAreNeverClassifiedAsUnchanged(string first, string second)
        => Assert.NotEqual(DialogueStability.Unchanged, TextSimilarity.Classify(first, second));

    [Fact]
    public void ProgressiveTextWaitsForQuietPeriod()
    {
        var clock = new ManualClock();
        var tracker = new DialogueTracker(clock);
        foreach (var text in new[] { "We", "We have", "We have to", "We have to leave" })
        { Assert.False(tracker.Observe(text).Ready); clock.Advance(100); }
        Assert.False(tracker.Observe("We have to leave").Ready);
        clock.Advance(300);
        Assert.True(tracker.Observe("We have to leave").Ready);
        Assert.False(tracker.Observe("We have to leave!").Ready);
        clock.Advance(250);
        Assert.True(tracker.Observe("We have to leave!").Ready);
    }

    [Fact]
    public void DisappearanceHasGraceThenInvalidatesSequence()
    {
        var clock = new ManualClock();
        var tracker = new DialogueTracker(clock);
        var line = tracker.Observe("Help!");
        clock.Advance(300);
        Assert.False(tracker.Observe("").Clear);
        clock.Advance(400);
        var gone = tracker.Observe("");
        Assert.True(gone.Clear);
        Assert.True(gone.Sequence > line.Sequence);
        Assert.False(tracker.Observe("Help!").Ready);
        clock.Advance(100);
        Assert.True(tracker.Observe("Help!").Ready);
    }

    [Fact]
    public void OcrFailureHasBoundedGraceAndRecovers()
    {
        var clock = new ManualClock();
        var tracker = new DialogueTracker(clock);
        tracker.Observe("No!");
        clock.Advance(700);
        Assert.False(tracker.Observe("", ocrFailed: true).Clear);
        Assert.False(tracker.Observe("No!").Changed);
        clock.Advance(1600);
        Assert.True(tracker.Observe("", ocrFailed: true).Clear);
        Assert.False(tracker.Observe("Run!").Ready);
        clock.Advance(100);
        Assert.True(tracker.Observe("Run!").Ready);
    }

    [Fact]
    public void ConstantFrameCannotKeepSubtitleForever()
    {
        var clock = new ManualClock();
        var tracker = new DialogueTracker(clock) { Settings = new StorySettings { MaximumVisibilityMilliseconds = 1000 } };
        tracker.Observe("Stop!");
        clock.Advance(1100);
        var expired = tracker.Observe("Stop!");
        Assert.True(expired.Clear);
        Assert.False(expired.Ready);
        clock.Advance(1100);
        Assert.False(tracker.Observe("Stop!").Ready);
    }

    [Fact]
    public void TruncatedPrefixIsOnlyAHintAndDoesNotSuppressNextUtterance()
    {
        var clock = new ManualClock();
        var tracker = new DialogueTracker(clock);
        tracker.Observe("Run to the bridge!");
        Assert.False(tracker.Observe("Run to").Changed);
        Assert.True(tracker.Observe("Run!").Changed);
    }

    [Fact]
    public void LongerCompleteLinesConfirmBrieflyWithoutIgnoringSemanticEdits()
    {
        var clock = new ManualClock();
        var tracker = new DialogueTracker(clock);
        Assert.False(tracker.Observe("I will never trust you again!").Ready);
        clock.Advance(100);
        Assert.True(tracker.Observe("I will never trust you again!").Ready);
        var changed = tracker.Observe("I will ever trust you again!");
        Assert.True(changed.Changed);
        Assert.False(changed.Ready);
        clock.Advance(100);
        Assert.True(tracker.Observe("I will ever trust you again!").Ready);
    }
}
