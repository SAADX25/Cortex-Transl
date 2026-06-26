namespace CortexTransl.App.Services.Translation;

public interface ITranslationProvider
{
    string Id { get; }

    string DisplayName { get; }

    string GetStatus();

    Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default);
}
