namespace CortexTransl.App.Utils;

public static class OverlayTextSizes
{
    public static string Normalize(string? size) => size?.Trim().ToLowerInvariant() switch
    {
        "small" => "small",
        "large" => "large",
        "xlarge" => "xlarge",
        _ => "medium"
    };

    public static string Label(string? size) => Normalize(size) switch
    {
        "small" => "Small",
        "large" => "Large",
        "xlarge" => "Extra large",
        _ => "Medium"
    };

    public static string ShortLabel(string? size) => Normalize(size) switch
    {
        "small" => "Small",
        "large" => "Large",
        "xlarge" => "XL",
        _ => "Medium"
    };

    public static double FontScale(string? size) => Normalize(size) switch
    {
        "small" => 0.78,
        "large" => 1.24,
        "xlarge" => 1.52,
        _ => 1.0
    };

    public static double PreviewFontSize(string? size) => Normalize(size) switch
    {
        "small" => 15,
        "large" => 23,
        "xlarge" => 28,
        _ => 19
    };
}
