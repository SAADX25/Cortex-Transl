using System.Text.RegularExpressions;

namespace CortexTransl.App.Utils;

public static partial class TextNormalizer
{
    public static string Normalize(string value)
    {
        return CollapseWhitespace().Replace(value.Trim()
            .Replace('\u2019', '\'').Replace('\u2018', '\'')
            .Replace('\u201C', '"').Replace('\u201D', '"'), " ");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespace();
}
