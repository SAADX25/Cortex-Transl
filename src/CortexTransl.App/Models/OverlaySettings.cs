using CortexTransl.App.Utils;

namespace CortexTransl.App.Models;

public sealed record OverlaySettings(
    string Placement,
    double BackgroundOpacity,
    string Mode,
    double FontScale = 1.0,
    string TextColor = "white")
{
    public static OverlaySettings Default { get; } = new("cover", 0.88, "dialogue");

    public string NormalizedPlacement => Placement.ToLowerInvariant() switch
    {
        "below" => "below",
        "above" => "above",
        _ => "cover"
    };

    public bool IsListMode => Mode.Equals("list", StringComparison.OrdinalIgnoreCase);

    public double NormalizedFontScale => Math.Clamp(FontScale, 0.7, 1.6);

    public string NormalizedTextColor => OverlayTextColors.Normalize(TextColor);
}
