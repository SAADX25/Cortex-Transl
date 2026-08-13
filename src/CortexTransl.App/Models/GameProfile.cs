using CortexTransl.App.Utils;

namespace CortexTransl.App.Models;

public sealed class GameProfile
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public CaptureRegion Region { get; set; } = CaptureRegion.Empty;

    public string SourceLanguage { get; set; } = "en";

    public string TargetLanguage { get; set; } = "ar";

    public string TranslationMode { get; set; } = "dialogue";

    public string OverlayPlacement { get; set; } = "cover";

    public double OverlayBackgroundOpacity { get; set; } = 0.88;

    public string OverlayTextSize { get; set; } = "medium";

    public string OverlayTextColor { get; set; } = "white";

    public string OcrEngine { get; set; } = "windows";

    public string TranslationProvider { get; set; } = "deepl";

    public bool IsActive { get; set; }

    public string Summary
    {
        get
        {
            var language = string.IsNullOrWhiteSpace(SourceLanguage) ? "EN" : SourceLanguage.ToUpperInvariant();
            var mode = TranslationMode.Equals("list", StringComparison.OrdinalIgnoreCase) ? "List" : "Dialogue";
            var size = OverlayTextSize.Trim().ToLowerInvariant() switch
            {
                "small" => "Small",
                "large" => "Large",
                "xlarge" => "XL",
                _ => "Medium"
            };
            var color = OverlayTextColors.Label(OverlayTextColor);
            var region = Region.IsEmpty ? "No region" : $"{Region.Width} × {Region.Height}";
            return $"{language} · {mode} · {size} · {color} · {region}";
        }
    }

    public override string ToString()
    {
        return Name;
    }
}
