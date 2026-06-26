namespace CortexTransl.App.Services.Translation;

public interface ITranslationProvider
{
    string Id { get; }

    string DisplayName { get; }

    Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default);
}
