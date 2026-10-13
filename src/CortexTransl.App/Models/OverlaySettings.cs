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

    internal static CaptureRegion CalculatePlacement(CaptureRegion region, string placement, CaptureRegion screen)
    {
        const int gap = 8;
        var width = Math.Max(80, region.Width);
        var height = Math.Max(48, region.Height);
        var top = placement switch
        {
            "below" => region.Y + region.Height + gap,
            "above" => region.Y - height - gap,
            _ => region.Y
        };
        if (top < screen.Y || top + height > screen.Y + screen.Height) top = region.Y;
        var left = Math.Max(screen.X, region.X);
        if (left + width > screen.X + screen.Width)
            left = Math.Max(screen.X, screen.X + screen.Width - width);
        return new(left, top, width, height);
    }
}
