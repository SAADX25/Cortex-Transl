using System.Text.RegularExpressions;

namespace CortexTransl.App.Utils;

internal static partial class ArabicTranslationCleaner
{
    public static string Clean(string text, string targetLanguage)
    {
        if (!targetLanguage.Equals("ar", StringComparison.OrdinalIgnoreCase)
            && !targetLanguage.StartsWith("ar-", StringComparison.OrdinalIgnoreCase)) return text.Trim();
        // Repair typography only. Latin names, IDs, numbers and variables can be
        // intentional and must never be deleted to make output appear Arabic.
        text = DirectionControls().Replace(text, "");
        return string.Join('\n', text.Replace("\r\n", "\n").Split('\n').Select(line =>
            BeforePunctuation().Replace(RepeatedSpaces().Replace(line, " "), "$1").Trim())).Trim();
    }

    [GeneratedRegex(@"[\t \u00A0]+")]
    private static partial Regex RepeatedSpaces();

    [GeneratedRegex(@"\s+([،؛؟,.!?…])")]
    private static partial Regex BeforePunctuation();

    [GeneratedRegex(@"[\u200E\u200F\u202A-\u202E\u2066-\u2069]")]
    private static partial Regex DirectionControls();
}
