using System.Text.Json.Serialization;

namespace CortexTransl.App.Models;

public sealed class AppSettings
{
    public string Provider { get; set; } = "placeholder";

    public string EncryptedDeepLApiKey { get; set; } = string.Empty;

    public bool UseDeepLFreeApi { get; set; } = true;

    public bool AutoTranslateEnabled { get; set; } = false;

    public double AutoTranslateIntervalMs { get; set; } = 700;

    public double OverlayFontSize { get; set; } = 32;

    public double OverlayOpacity { get; set; } = 1;

    public double OverlayBackgroundOpacity { get; set; } = 0.86;

    public double OverlayMaxWidth { get; set; } = 920;

    public string OverlayPositionPreset { get; set; } = "custom";

    public string OverlayRenderMode { get; set; } = "transparent";

    public bool OverlayClickThrough { get; set; } = true;
}
