namespace CortexTransl.App.Models;

public sealed record OverlaySettings(
    double FontSize,
    double Opacity,
    double BackgroundOpacity,
    double MaxWidth,
    string PositionPreset,
    string RenderMode,
    bool ClickThrough)
{
    public bool IsRecordingSafe => RenderMode.Equals("recording-safe", StringComparison.OrdinalIgnoreCase);
}
