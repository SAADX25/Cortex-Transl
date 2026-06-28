using System.Net;
using System.Text.RegularExpressions;

namespace CortexTransl.App.Services.Translation;

public sealed class PlaceholderTranslationProvider : ITranslationProvider
{
    private static readonly Dictionary<string, string> DemoTranslations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hello"] = "\u0645\u0631\u062d\u0628\u0627",
        ["hello."] = "\u0645\u0631\u062d\u0628\u0627.",
        ["yes"] = "\u0646\u0639\u0645",
        ["no"] = "\u0644\u0627",
        ["continue"] = "\u0627\u0633\u062a\u0645\u0631",
        ["start"] = "\u0627\u0628\u062f\u0623",
        ["save"] = "\u062d\u0641\u0638",
        ["load"] = "\u062a\u062d\u0645\u064a\u0644",
        ["game over"] = "\u0627\u0646\u062a\u0647\u062a \u0627\u0644\u0644\u0639\u0628\u0629",
        ["thank you"] = "\u0634\u0643\u0631\u0627 \u0644\u0643",
        ["where are we?"] = "\u0623\u064a\u0646 \u0646\u062d\u0646\u061f"
    };

    public string Id => "placeholder";

    public string DisplayName => "Placeholder";

    public string GetStatus()
    {
        return "Placeholder ready";
    }

    public Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(string.Empty);
        }

        if (TryTranslateLineBatch(text, targetLanguage, out var batchTranslation))
        {
            return Task.FromResult(batchTranslation);
        }

        if (targetLanguage.Equals("ar", StringComparison.OrdinalIgnoreCase) &&
            DemoTranslations.TryGetValue(text.Trim(), out var translated))
        {
            return Task.FromResult(translated);
        }

        var placeholder = targetLanguage.Equals("ar", StringComparison.OrdinalIgnoreCase)
            ? $"\u062a\u0631\u062c\u0645\u0629 \u062a\u062c\u0631\u064a\u0628\u064a\u0629: {text}"
            : $"Placeholder translation: {text}";

        return Task.FromResult(placeholder);
    }

    private static bool TryTranslateLineBatch(string text, string targetLanguage, out string translation)
    {
        var matches = Regex.Matches(
            text,
            """<line\s+id\s*=\s*["'](?<id>\d+)["']\s*>(?<text>.*?)</line>""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (matches.Count == 0)
        {
            translation = string.Empty;
            return false;
        }

        var lines = new List<string>();
        foreach (Match match in matches)
        {
            var id = match.Groups["id"].Value;
            var sourceLine = WebUtility.HtmlDecode(match.Groups["text"].Value).Trim();
            var translatedLine = TranslateSingleLine(sourceLine, targetLanguage);
            lines.Add($"<line id=\"{id}\">{WebUtility.HtmlEncode(translatedLine)}</line>");
        }

        translation = string.Join(Environment.NewLine, lines);
        return true;
    }

    private static string TranslateSingleLine(string text, string targetLanguage)
    {
        if (targetLanguage.Equals("ar", StringComparison.OrdinalIgnoreCase) &&
            DemoTranslations.TryGetValue(text.Trim(), out var translated))
        {
            return translated;
        }

        return targetLanguage.Equals("ar", StringComparison.OrdinalIgnoreCase)
            ? $"\u062a\u0631\u062c\u0645\u0629 \u062a\u062c\u0631\u064a\u0628\u064a\u0629: {text}"
            : $"Placeholder translation: {text}";
    }
}
