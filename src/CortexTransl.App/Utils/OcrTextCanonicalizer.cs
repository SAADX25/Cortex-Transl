namespace CortexTransl.App.Utils;

public static class OcrTextCanonicalizer
{
    // Only typographic equivalents, never deleted apostrophes, punctuation,
    // letters, digits, case, or guessed l/I/O/0 substitutions.
    public static string Normalize(string text) => TextNormalizer.Normalize(text
        .Replace('\u2019', '\'').Replace('\u2018', '\'')
        .Replace('\u201C', '"').Replace('\u201D', '"'));
}
