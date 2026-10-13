namespace CortexTransl.App.Services.Translation;

public interface ITranslationProvider
{
    string Id { get; }

    bool SupportsContextualLongText { get; }
    bool UsesHistoricalContext => SupportsContextualLongText;

    // Engines without contextual support translate only the current utterance.
    Task<string> TranslateStoryAsync(string text, string sourceLanguage, string targetLanguage,
        StoryTranslationContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.TryExactArabicTerm(text, sourceLanguage, targetLanguage, out var term))
            return Task.FromResult(term);
        return TranslateAsync(text, sourceLanguage, targetLanguage, cancellationToken, TranslationContentKind.GameDialogue);
    }

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
