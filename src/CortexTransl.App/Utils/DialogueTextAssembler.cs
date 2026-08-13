using CortexTransl.App.Services.Ocr;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CortexTransl.App.Utils;

public static partial class DialogueTextAssembler
{
    public static string Assemble(OcrResult ocrResult, string sourceLanguage)
    {
        var lines = ocrResult.Blocks
            .Select(block => CleanLine(block.Text, sourceLanguage))
            .Where(IsUsefulLine)
            .ToArray();

        if (lines.Length == 0)
        {
            var fallback = CleanLine(ocrResult.Text, sourceLanguage);
            return IsUsefulLine(fallback) ? fallback : string.Empty;
        }

        return JoinLines(lines, sourceLanguage);
    }

    public static bool IsUsefulLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var letters = 0;
        var digits = 0;
        var cjk = 0;
        foreach (var character in text)
        {
            if (char.IsLetter(character))
            {
                letters++;
            }

            if (char.IsDigit(character))
            {
                digits++;
            }

            if (IsCjk(character))
            {
                cjk++;
            }
        }

        if (letters + digits + cjk == 0)
        {
            return false;
        }

        if (text.Length == 1 && cjk == 0)
        {
            return false;
        }

        return true;
    }

    public static bool LooksLikeOcrGlitch(string previous, string current)
    {
        var last = TextNormalizer.Normalize(previous);
        var next = TextNormalizer.Normalize(current);
        if (last.Length < 12 || next.Length == 0)
        {
            return false;
        }

        if (next.Length <= last.Length / 3
            && !last.StartsWith(next, StringComparison.Ordinal)
            && !next.StartsWith(last, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static string JoinLines(IReadOnlyList<string> lines, string sourceLanguage)
    {
        if (IsCjkLanguage(sourceLanguage))
        {
            return CompactCjk(string.Concat(lines.Select(line => CompactCjk(line))));
        }

        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (builder.Length == 0)
            {
                builder.Append(line);
                continue;
            }

            var previous = builder[^1];
            if (previous is '-' or '—' or 'ー')
            {
                builder.Length--;
                builder.Append(line);
                continue;
            }

            builder.Append(' ').Append(line);
        }

        return TextNormalizer.Normalize(builder.ToString());
    }

    private static string CleanLine(string text, string sourceLanguage)
    {
        var cleaned = TextNormalizer.Normalize(text)
            .Replace('\u00A0', ' ')
            .Replace('\u3000', ' ');
        return IsCjkLanguage(sourceLanguage) ? CompactCjk(cleaned) : cleaned;
    }

    private static bool IsCjkLanguage(string sourceLanguage)
    {
        var language = sourceLanguage.Trim().ToLowerInvariant();
        return language is "ja" or "ko" or "zh" or "zh-hans" or "zh-hant";
    }

    private static bool IsCjk(char character)
    {
        return char.GetUnicodeCategory(character) is UnicodeCategory.OtherLetter
            && character >= 0x2E80;
    }

    [GeneratedRegex(@"([\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}])\s+(?=[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}])")]
    private static partial Regex CjkSpacePattern();

    private static string CompactCjk(string text)
    {
        return CjkSpacePattern().Replace(text, "$1");
    }
}
