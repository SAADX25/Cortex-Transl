using System.Text.Json.Serialization;

namespace CortexTransl.App.Models;

public sealed class AppSettings
{
    public string Provider { get; set; } = "placeholder";

    public string EncryptedDeepLApiKey { get; set; } = string.Empty;

    public bool UseDeepLFreeApi { get; set; } = true;

    public bool AutoTranslateEnabled { get; set; } = false;

    public double AutoTranslateIntervalMs { get; set; } = 700;
}
