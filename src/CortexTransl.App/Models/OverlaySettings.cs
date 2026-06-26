namespace CortexTransl.App.Models;

public sealed record OverlaySettings(
    double FontSize,
    double Opacity,
    double BackgroundOpacity,
    double MaxWidth,
    string PositionMode,
    string PositionPreset,
    double? CustomLeft,
    double? CustomTop,
    string RenderMode,
    bool PositionUnlocked,
    bool ClickThrough)
{
    public bool IsRecordingSafe => RenderMode.Equals("recording-safe", StringComparison.OrdinalIgnoreCase);

    public bool UsesSmartPlacement => PositionMode.Equals("smart", StringComparison.OrdinalIgnoreCase);
}
