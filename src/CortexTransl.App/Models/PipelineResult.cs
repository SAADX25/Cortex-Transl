namespace CortexTransl.App.Models;

public sealed record PipelineResult(
    string OriginalText,
    string TranslatedText,
    string Status,
    string CacheStatus,
    string ProviderStatus,
    IReadOnlyList<TimingEntry> Timings,
    IReadOnlyList<RecognizedTextBlock> TextBlocks);
