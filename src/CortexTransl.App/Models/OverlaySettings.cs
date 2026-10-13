using CortexTransl.App.Utils;

namespace CortexTransl.App.Models;

public sealed record OverlaySettings(
    string Placement,
    double BackgroundOpacity,
    string Mode,
    double FontScale = 1.0,
    string TextColor = "white")
{
    public static string NormalizePlacement(string? placement) => placement?.Trim().ToLowerInvariant() switch
    {
        "below" => "below",
        "above" => "above",
        _ => "cover"
    };

    public static string NormalizeMode(string? mode) =>
        string.Equals(mode?.Trim(), "list", StringComparison.OrdinalIgnoreCase) ? "list" : "dialogue";

    public string NormalizedPlacement => NormalizePlacement(Placement);

    public bool IsListMode => Mode.Equals("list", StringComparison.OrdinalIgnoreCase);

    public double NormalizedFontScale => Math.Clamp(FontScale, 0.7, 1.6);

    public string NormalizedTextColor => OverlayTextColors.Normalize(TextColor);
}
