using System.Windows.Media;

namespace CortexTransl.App.Utils;

public static class OverlayTextColors
{
    public static string Normalize(string? id)
    {
        return id?.Trim().ToLowerInvariant() switch
        {
            "cream" => "cream",
            "gold" => "gold",
            "mint" => "mint",
            "sky" => "sky",
            _ => "white"
        };
    }

    public static string Label(string? id)
    {
        return Normalize(id) switch
        {
            "cream" => "Cream",
            "gold" => "Gold",
            "mint" => "Mint",
            "sky" => "Sky",
            _ => "White"
        };
    }

    public static Color ToColor(string? id)
    {
        return Normalize(id) switch
        {
            "cream" => Color.FromRgb(0xF5, 0xE6, 0xC8),
            "gold" => Color.FromRgb(0xFD, 0xE6, 0x8A),
            "mint" => Color.FromRgb(0xBB, 0xF7, 0xD0),
            "sky" => Color.FromRgb(0xBF, 0xDB, 0xFE),
            _ => Colors.White
        };
    }

    public static SolidColorBrush ToBrush(string? id)
    {
        var brush = new SolidColorBrush(ToColor(id));
        brush.Freeze();
        return brush;
    }
}
