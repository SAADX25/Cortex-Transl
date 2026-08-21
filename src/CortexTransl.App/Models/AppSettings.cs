namespace CortexTransl.App.Models;

public sealed class AppSettings
{
    public string EncryptedDeepLApiKey { get; set; } = string.Empty;

    public bool UseDeepLFreeApi { get; set; } = true;

    public string TranslationEngine { get; set; } = "offline";

    public string Theme { get; set; } = "Dark";

    public string SourceLanguage { get; set; } = "en";

    public string OverlayPlacement { get; set; } = "cover";

    public double OverlayBackgroundOpacity { get; set; } = 0.88;

    public bool MinimizeDuringPlay { get; set; }

    public double AutoTranslateIntervalMs { get; set; } = 500;

    public int RegionX { get; set; }

    public int RegionY { get; set; }

    public int RegionWidth { get; set; }

    public int RegionHeight { get; set; }

    public string ProfileName { get; set; } = string.Empty;

    public string TranslationMode { get; set; } = "dialogue";

    public string OverlayTextSize { get; set; } = "medium";

    public string OverlayTextColor { get; set; } = "white";
}
