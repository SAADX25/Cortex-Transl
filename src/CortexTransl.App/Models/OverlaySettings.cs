namespace CortexTransl.App.Models;

public sealed record OverlaySettings(string Placement, double BackgroundOpacity, string Mode)
{
    public static OverlaySettings Default { get; } = new("cover", 0.88, "dialogue");

    public string NormalizedPlacement => Placement.ToLowerInvariant() switch
    {
        "below" => "below",
        "above" => "above",
        _ => "cover"
    };

    public bool IsListMode => Mode.Equals("list", StringComparison.OrdinalIgnoreCase);
}
