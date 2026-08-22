using System.Text.RegularExpressions;

namespace CortexTransl.App.Utils;

public static partial class TranslationChunker
{
    private const int MaxChunkLength = 350;

    public static IReadOnlyList<string> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var normalized = text.Replace("\r\n", "\n").Trim();
        if (normalized.Length <= MaxChunkLength && !normalized.Contains('\n'))
        {
            return [normalized];
        }

        var chunks = new List<string>();
        foreach (var paragraph in normalized.Split('\n'))
        {
            var trimmed = paragraph.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.Length <= MaxChunkLength)
            {
                chunks.Add(trimmed);
                continue;
            }

            foreach (var sentence in SentenceBreaks().Split(trimmed))
            {
                AppendFitted(chunks, sentence.Trim());
            }
        }

        return chunks.Count > 0 ? chunks : [normalized];
    }

    public static string Join(IReadOnlyList<string> translations)
    {
        return string.Join(
            Environment.NewLine,
            translations.Where(static text => !string.IsNullOrWhiteSpace(text)).Select(static text => text.Trim()));
    }

    private static void AppendFitted(List<string> chunks, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (text.Length <= MaxChunkLength)
        {
            chunks.Add(text);
            return;
        }

        var remaining = text;
        while (remaining.Length > MaxChunkLength)
        {
            var splitAt = remaining.LastIndexOf(' ', Math.Min(MaxChunkLength, remaining.Length - 1));
            if (splitAt < MaxChunkLength / 3)
            {
                splitAt = MaxChunkLength;
            }

            chunks.Add(remaining[..splitAt].Trim());
            remaining = remaining[splitAt..].Trim();
        }

        if (remaining.Length > 0)
        {
            chunks.Add(remaining);
        }
    }

    [GeneratedRegex(@"(?<=[.!?…。！？؟])\s+")]
    private static partial Regex SentenceBreaks();
}
