namespace CortexTransl.App.Services.Translation;

public interface ITranslationProvider
{
    string Id { get; }

    bool SupportsContextualLongText { get; }

    Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default,
        TranslationContentKind contentKind = TranslationContentKind.General);

    Task<IReadOnlyList<string>> TranslateManyAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default,
        TranslationContentKind contentKind = TranslationContentKind.General);
}
