using System.Text;
using System.Text.RegularExpressions;

namespace CortexTransl.App.Utils;

public static partial class OcrTextCanonicalizer
{
    public static string Normalize(string text, bool preserveLineBreaks = false)
    {
        // Keep game variables and markup intact while cleaning OCR outside them.
        var cleaned = new StringBuilder();
        var position = 0;
        foreach (Match match in ProtectedSyntax().Matches(text))
        {
            cleaned.Append(CleanNoise(text[position..match.Index]));
            cleaned.Append(match.Value);
            position = match.Index + match.Length;
        }
        cleaned.Append(CleanNoise(text[position..]));
        var value = RemoveUnmatchedBrackets(cleaned.ToString());
        return preserveLineBreaks
            ? string.Join('\n', value.Replace("\r\n", "\n").Split('\n')
                .Select(TextNormalizer.Normalize).Where(line => line.Length > 0))
            : TextNormalizer.Normalize(value);
    }

    private static string CleanNoise(string value)
    {
        value = NoiseSeparators().Replace(value, " ");
        // Preserve common single-letter words across supported Latin languages,
        // key prompts and capitalized names/initials in otherwise useful text.
        return IsolatedArtifacts().Replace(value, match =>
        {
            var character = match.Value[0];
            if ("aeiouy".Contains(char.ToLowerInvariant(character))
                || (char.IsUpper(character) && value.Trim().Length > 1)
                || KeyPrompt().IsMatch(value[..match.Index])
                || KeySuffix().IsMatch(value[(match.Index + match.Length)..])) return match.Value;
            return "";
        });
    }

    private static string RemoveUnmatchedBrackets(string value)
    {
        var stack = new Stack<(char Character, int Index)>();
        var remove = new HashSet<int>();
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is '(' or '[' or '{' or '<') stack.Push((character, index));
            else if (character is ')' or ']' or '}' or '>')
            {
                if (stack.TryPeek(out var open) && (open.Character, character) is ('(', ')') or ('[', ']') or ('{', '}') or ('<', '>'))
                    stack.Pop();
                else remove.Add(index);
            }
        }
        foreach (var open in stack) remove.Add(open.Index);
        return string.Concat(value.Where((_, index) => !remove.Contains(index)));
    }

    [GeneratedRegex(@"<[^<>\r\n]+>|\{[^{}\r\n]+\}|\[[\p{L}\p{N}_.:-]+\]|%\w+%")]
    private static partial Regex ProtectedSyntax();

    [GeneratedRegex(@"[|_]+")]
    private static partial Regex NoiseSeparators();

    [GeneratedRegex(@"(?<!\S)[A-Za-z](?!\S)")]
    private static partial Regex IsolatedArtifacts();

    [GeneratedRegex(@"\b(?:press|hold|tap|key|button)\s+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyPrompt();

    [GeneratedRegex(@"^\s+(?:key|button)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeySuffix();
}
