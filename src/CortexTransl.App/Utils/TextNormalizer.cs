using System.Text.RegularExpressions;

namespace CortexTransl.App.Utils;

public static partial class TextNormalizer
{
    public static string Normalize(string value)
    {
        return CollapseWhitespace().Replace(value.Trim(), " ");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespace();
}
