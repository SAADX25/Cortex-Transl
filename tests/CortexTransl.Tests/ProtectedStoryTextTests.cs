using CortexTransl.App.Services.Translation;
using CortexTransl.App.Utils;
using Xunit;

namespace CortexTransl.Tests;

public sealed class ProtectedStoryTextTests
{
    private static readonly StoryTranslationContext Context = new()
    {
        SpeakerNames = new Dictionary<string, string> { ["Alice"] = "أليس" },
        Terminology = new Dictionary<string, string> { ["Silver Keep"] = "قلعة الفضة" }
    };

    [Fact]
    public void XmlRestoresReorderedNamesWhitespaceCaseAndMirroredTagBrackets()
    {
        var text = ProtectedStoryText.Create("Alice went to Silver Keep.", "en", "ar", Context)!;
        var translated = "> CORTEX - DIALOGUE <إلى > CORTEX - TERM < قلعة  الفضة >/ CORTEX - TERM < ذهبت " +
            "<CORTEX-TERM> أليس ! </CORTEX-TERM>>/ CORTEX - DIALOGUE <";
        Assert.Equal("إلى قلعة الفضة ذهبت أليس !", text.ReadTranslation(translated));
    }

    [Fact]
    public void VariablesAndMarkupAreProtectedWithoutConfiguredTerms()
    {
        var text = ProtectedStoryText.Create("Hello {player_name}, <b>go</b>!", "en", "ar", new())!;
        Assert.Contains("{player_name}", text.Text);
        Assert.Contains("&lt;b&gt;", text.Text);
        Assert.Equal("مرحباً {player_name}، <b>اذهب</b>!", text.ReadTranslation(
            "<cortex-dialogue>مرحباً <cortex-term>{player_name}</cortex-term>، " +
            "<cortex-term>&lt;b&gt;</cortex-term>اذهب<cortex-term>&lt;/b&gt;</cortex-term>!</cortex-dialogue>"));
    }

    [Theory]
    [InlineData("اذهبي ] ] z x q ١ q x z [ [ يا \u200F[[ z x q ٠ q x z ]]!", "اذهبي قلعة الفضة يا أليس!")]
    [InlineData("اذهبي ZXQ1QXZ يا ZXQ0QXZ!", "اذهبي قلعة الفضة يا أليس!")]
    public void PlainTokensRestoreSpacingCaseArabicDigitsAndReorderedBrackets(string translated, string expected)
    {
        var text = ProtectedStoryText.Create("Alice, go to Silver Keep!", "en", "ar", Context, useXml: false)!;
        Assert.Equal(expected, text.ReadTranslation(translated));
        Assert.DoesNotContain("ZXQ", text.ReadTranslation(translated));
    }

    [Theory]
    [InlineData("اذهبي [[ZXQ0QXZ]]")]
    [InlineData("[[ZXQ0QXZ]] [[ZXQ0QXZ]] [[ZXQ1QXZ]]")]
    [InlineData("[[ZXQ0QXZ]] [[ZXQ1QXZ]] [[ZXQ99QXZ]]")]
    [InlineData("[[ZXQ0QXZ]] [[ZXQbrokenQXZ]]")]
    public void MissingDuplicatedOrCorruptTokensCannotReachTheOverlay(string translated)
    {
        var text = ProtectedStoryText.Create("Alice, go to Silver Keep!", "en", "ar", Context, useXml: false)!;
        Assert.Throws<TranslationProviderException>(() => text.ReadTranslation(translated));
    }

    [Theory]
    [InlineData("<cortex-dialogue><cortex-term>أليس</cortex-term></cortex-dialogue>")]
    [InlineData("<cortex-dialogue><other>أليس</other><cortex-term>قلعة الفضة</cortex-term></cortex-dialogue>")]
    [InlineData("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///private'>]><cortex-dialogue>&e;</cortex-dialogue>")]
    public void InvalidXmlIsRejected(string translated)
    {
        var text = ProtectedStoryText.Create("Alice at Silver Keep", "en", "ar", Context)!;
        Assert.Throws<TranslationProviderException>(() => text.ReadTranslation(translated));
    }

    [Fact]
    public void ArabicTypographyKeepsMixedNamesAndVariablesInLogicalOrder()
    {
        Assert.Equal("قالت Alice: مرحباً {player_name}...\nهل نذهب؟",
            ArabicTranslationCleaner.Clean("\u202Eقالت Alice:  مرحباً {player_name} . . .\r\nهل نذهب ؟", "ar"));
    }
}
