namespace CortexTransl.App.Models;

public sealed record PipelineResult(
    string OriginalText,
    string TranslatedText,
    string Status,
    string CacheStatus,
    IReadOnlyList<TimingEntry> Timings);
