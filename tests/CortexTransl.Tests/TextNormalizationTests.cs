using CortexTransl.App.Utils;
using Xunit;

namespace CortexTransl.Tests;

public sealed class TextNormalizationTests
{
    [Theory]
    [InlineData("| _ [ We must ] leave ( now > x", "[ We must ] leave now")]
    [InlineData("I am a knight. | x _", "I am a knight.")]
    [InlineData("Hello {player_name}, <b>go</b> to [Silver_Keep].", "Hello {player_name}, <b>go</b> to [Silver_Keep].")]
    [InlineData("I can’t go!", "I can't go!")]
    [InlineData("No!", "No!")]
    [InlineData("Press E to interact. Hold x to run.", "Press E to interact. Hold x to run.")]
    [InlineData("Alice y Bob van a casa.", "Alice y Bob van a casa.")]
    [InlineData("[ We | must _ go ]", "[ We must go ]")]
    public void CanonicalizationRemovesNoiseAndPreservesMeaning(string source, string expected)
    {
        Assert.Equal(expected, OcrTextCanonicalizer.Normalize(source));
        Assert.Equal(expected, OcrTextCanonicalizer.Normalize(expected));
    }

    [Fact]
    public void GeneralNormalizationDoesNotStripVariablesOrText()
        => Assert.Equal("x | player_name", TextNormalizer.Normalize(" x  | player_name "));
}
