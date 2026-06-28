namespace CortexTransl.App.Models;

public sealed class RecognizedTextBlock
{
    public string Text { get; init; } = string.Empty;

    public CaptureRegion Bounds { get; init; } = CaptureRegion.Empty;

    public int LineIndex { get; init; }

    public double? Confidence { get; init; }

    public string TranslatedText { get; set; } = string.Empty;

    public string BoundsDisplay => Bounds.IsEmpty
        ? "-"
        : $"{Bounds.X},{Bounds.Y} {Bounds.Width}x{Bounds.Height}";
}
