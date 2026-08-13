namespace CortexTransl.App.Models;

public sealed record PipelineResult(
    string OriginalText,
    string TranslatedText,
    string Status,
    bool UsedCache,
    bool Skipped,
    IReadOnlyList<TranslatedBlock> Blocks)
{
    public PipelineResult(
        string originalText,
        string translatedText,
        string status,
        bool usedCache,
        bool skipped)
        : this(originalText, translatedText, status, usedCache, skipped, [])
    {
    }
}
