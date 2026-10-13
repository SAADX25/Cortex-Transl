namespace CortexTransl.App.Models;

public sealed record PipelineResult(
    string OriginalText,
    string TranslatedText,
    string Status,
    bool UsedCache,
    bool Skipped,
    IReadOnlyList<TranslatedBlock> Blocks)
{
    public long Sequence { get; init; }
    public bool ClearOverlay { get; init; }
    public long ObservedTimestamp { get; init; }
    public long CompletedTimestamp { get; init; }
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
