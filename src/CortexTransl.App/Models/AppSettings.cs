using System.Text.Json.Serialization;

namespace CortexTransl.App.Models;

public sealed class AppSettings
{
    public bool HasRunSetupWizard { get; set; } = false;

    public string AppMode { get; set; } = "Simple";

    public string UsageType { get; set; } = "Game Dialogue";

    public string TranslationMode { get; set; } = "subtitle";

    public string OcrPreset { get; set; } = "normal";

    public string TranslationQuality { get; set; } = "Balanced";

    public string Provider { get; set; } = "placeholder";

    public string EncryptedDeepLApiKey { get; set; } = string.Empty;

    public bool UseDeepLFreeApi { get; set; } = true;

    public string Theme { get; set; } = "Dark";

    public bool AutoTranslateEnabled { get; set; } = false;

    public double AutoTranslateIntervalMs { get; set; } = 700;

    public double OverlayFontSize { get; set; } = 32;

    public double OverlayOpacity { get; set; } = 1;

    public double OverlayBackgroundOpacity { get; set; } = 0.86;

    public double OverlayMaxWidth { get; set; } = 920;

    public string OverlayPositionMode { get; set; } = "locked";

    public string OverlayPositionPreset { get; set; } = "bottom-center";

    public double? OverlayCustomLeft { get; set; }

    public double? OverlayCustomTop { get; set; }

    public bool OverlayPositionUnlocked { get; set; } = false;

    public string OverlayRenderMode { get; set; } = "transparent";

    public bool OverlayClickThrough { get; set; } = true;
}
