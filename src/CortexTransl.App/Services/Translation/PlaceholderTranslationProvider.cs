namespace CortexTransl.App.Services.Translation;

public sealed class PlaceholderTranslationProvider : ITranslationProvider
{
    private static readonly Dictionary<string, string> DemoTranslations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hello"] = "مرحبا",
        ["hello."] = "مرحبا.",
        ["yes"] = "نعم",
        ["no"] = "لا",
        ["continue"] = "استمر",
        ["start"] = "ابدأ",
        ["save"] = "حفظ",
        ["load"] = "تحميل",
        ["game over"] = "انتهت اللعبة",
        ["thank you"] = "شكرا لك",
        ["where are we?"] = "أين نحن؟"
    };

    public string Id => "placeholder";

    public string DisplayName => "Placeholder";

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

        if (targetLanguage.Equals("ar", StringComparison.OrdinalIgnoreCase) &&
            DemoTranslations.TryGetValue(text.Trim(), out var translated))
        {
            return Task.FromResult(translated);
        }

        var placeholder = targetLanguage.Equals("ar", StringComparison.OrdinalIgnoreCase)
            ? $"ترجمة تجريبية: {text}"
            : $"Placeholder translation: {text}";

        return Task.FromResult(placeholder);
    }
}
