namespace CortexTransl.App.Data;

internal static class TranslationCacheVersion
{
    // Bump when OCR assembly or translation semantics change. Legacy writers
    // omit CacheVersion and receive zero; current reads require this version.
    public const int Current = 2;
}
