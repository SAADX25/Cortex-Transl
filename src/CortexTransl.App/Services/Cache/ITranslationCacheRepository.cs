namespace CortexTransl.App.Services.Cache;

public interface ITranslationCacheRepository
{
    Task<string?> GetAsync(
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        string provider,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        string provider,
        string translatedText,
        CancellationToken cancellationToken = default);
}
