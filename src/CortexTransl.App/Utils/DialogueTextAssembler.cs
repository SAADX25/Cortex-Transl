using CortexTransl.App.Models;
using CortexTransl.App.Services.Ocr;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CortexTransl.App.Utils;

public static partial class DialogueTextAssembler
{
    public static string Assemble(OcrResult ocrResult, string sourceLanguage)
    {
        var blocks = ocrResult.Blocks
            .Select(block => (Text: CleanLine(block.Text, sourceLanguage), block.Bounds))
            .Where(block => IsUsefulLine(block.Text) && !block.Bounds.IsEmpty)
            .OrderBy(block => block.Bounds.Y)
            .ThenBy(block => block.Bounds.X)
            .ToArray();

        if (blocks.Length == 0)
        {
            var fallback = CleanLine(ocrResult.Text, sourceLanguage);
            return IsUsefulLine(fallback) ? fallback : string.Empty;
        }

        if (IsCjkLanguage(sourceLanguage))
        {
            return CompactCjk(string.Concat(blocks.Select(block => CompactCjk(block.Text))));
        }

        return JoinReadingOrder(blocks);
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

    private static string JoinReadingOrder(
        IReadOnlyList<(string Text, CaptureRegion Bounds)> blocks)
    {
        var builder = new StringBuilder();
        CaptureRegion? previous = null;

        foreach (var block in blocks)
        {
            if (builder.Length == 0 || previous is null)
            {
                builder.Append(block.Text);
                previous = block.Bounds;
                continue;
            }

            var last = previous;
            var gap = block.Bounds.Y - (last.Y + last.Height);
            var lineHeight = Math.Max(12, Math.Max(last.Height, block.Bounds.Height));
            var sameRow = Math.Abs(block.Bounds.Y - last.Y) <= lineHeight * 0.45;

            if (builder[^1] is '-' or '—' or 'ー')
            {
                builder.Length--;
                builder.Append(block.Text);
            }
            else if (sameRow)
            {
                builder.Append(" · ").Append(block.Text);
            }
            else if (gap > lineHeight * 0.55)
            {
                builder.Append('\n').Append(block.Text);
            }
            else
            {
                builder.Append(' ').Append(block.Text);
            }

            previous = block.Bounds;
        }

        return builder.ToString().Trim();
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
