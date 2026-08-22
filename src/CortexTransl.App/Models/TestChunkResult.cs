namespace CortexTransl.App.Models;

public sealed class TestChunkResult
{
    public int Index { get; init; }
    public string OriginalText { get; init; } = string.Empty;
    public string TranslatedText { get; init; } = string.Empty;
    public bool IsEmpty => string.IsNullOrWhiteSpace(TranslatedText);
    public string StatusIcon => IsEmpty ? "⚠" : "✓";
    public string StatusColor => IsEmpty ? "#FFB74D" : "#66BB6A";
}
