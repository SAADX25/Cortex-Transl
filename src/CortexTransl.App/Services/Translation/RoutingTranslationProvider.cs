namespace CortexTransl.App.Services.Translation;

public sealed class RoutingTranslationProvider : ITranslationProvider
{
    private readonly TranslationProviderSettings _settings;
    private readonly OfflineBergamotTranslationProvider _offline;
    private readonly ITranslationProvider _online;

    public RoutingTranslationProvider(
        TranslationProviderSettings settings,
        OfflineBergamotTranslationProvider offline,
        ITranslationProvider online)
    {
        _settings = settings;
        _offline = offline;
        _online = online;
    }

    public string Id => UseOffline ? _offline.Id : _online.Id;

    public bool SupportsContextualLongText => Active.SupportsContextualLongText;

    private bool UseOffline => _settings.UseOfflineEngine;

    public Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default,
        TranslationContentKind contentKind = TranslationContentKind.General)
    {
        return Active.TranslateAsync(text, sourceLanguage, targetLanguage, cancellationToken, contentKind);
    }

    public Task<IReadOnlyList<string>> TranslateManyAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default,
        TranslationContentKind contentKind = TranslationContentKind.General)
    {
        return Active.TranslateManyAsync(texts, sourceLanguage, targetLanguage, cancellationToken, contentKind);
    }

    private ITranslationProvider Active => UseOffline ? _offline : _online;
}
