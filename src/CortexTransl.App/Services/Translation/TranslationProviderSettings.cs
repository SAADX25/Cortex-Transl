namespace CortexTransl.App.Services.Translation;

public sealed class TranslationProviderSettings
{
    public string DeepLApiKey { get; set; } =
        Environment.GetEnvironmentVariable("CORTEX_TRANSL_DEEPL_API_KEY") ?? string.Empty;

    public bool UseDeepLFreeApi { get; set; } = true;
}
