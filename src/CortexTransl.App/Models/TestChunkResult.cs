namespace CortexTransl.App.Models;

public sealed class TestChunkResult
{
    public int Index { get; init; }
    public string OriginalText { get; init; } = string.Empty;
    public string TranslatedText { get; init; } = string.Empty;
    public bool IsEmpty => string.IsNullOrWhiteSpace(TranslatedText);
    public string StatusIcon => IsEmpty ? "⚠" : "✓";
}
