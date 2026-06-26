using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace CortexTransl.App.Services.Translation;

public sealed class DeepLTranslationProvider : ITranslationProvider
{
    private const int MaximumRequestBytes = 128 * 1024;
    private readonly TranslationProviderSettings _settings;
    private readonly HttpClient _httpClient;

    public DeepLTranslationProvider(TranslationProviderSettings settings, HttpClient? httpClient = null)
    {
        _settings = settings;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
    }

    public string Id => "deepl";

    public string DisplayName => "DeepL";

    public string GetStatus()
    {
        return string.IsNullOrWhiteSpace(_settings.DeepLApiKey)
            ? "DeepL missing API key"
            : "DeepL ready";
    }

    public async Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var apiKey = _settings.DeepLApiKey.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new TranslationProviderException(
                "DeepL API key is missing. Paste a key or switch back to Placeholder.",
                "DeepL missing API key");
        }

        var request = BuildRequest(text, sourceLanguage, targetLanguage);
        if (request.EstimatedUtf8Bytes > MaximumRequestBytes)
        {
            throw new TranslationProviderException(
                "DeepL request is too large for one translation call.",
                "DeepL request too large");
        }

        var endpoint = _settings.UseDeepLFreeApi
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(request.Payload)
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);
        httpRequest.Headers.UserAgent.ParseAdd("CortexTransl/0.2");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslationProviderException(
                "DeepL request timed out. Check your network connection and try again.",
                "DeepL timeout",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationProviderException(
                "DeepL network request failed. Check your connection and try again.",
                "DeepL network failure",
                ex);
        }

        using var _ = response;

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new TranslationProviderException(
                "DeepL rejected the API key. Check the key and API type.",
                "DeepL API key rejected");
        }

        if ((int)response.StatusCode == 429)
        {
            throw new TranslationProviderException(
                "DeepL rate limit reached. Wait a moment and try again.",
                "DeepL rate limited");
        }

        if ((int)response.StatusCode == 456)
        {
            throw new TranslationProviderException(
                "DeepL quota exceeded for this API key.",
                "DeepL quota exceeded");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new TranslationProviderException(
                $"DeepL returned HTTP {(int)response.StatusCode}.",
                $"DeepL HTTP {(int)response.StatusCode}");
        }

        DeepLTranslateResponse? responseBody;
        try
        {
            responseBody = await response.Content.ReadFromJsonAsync<DeepLTranslateResponse>(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new TranslationProviderException(
                "DeepL response could not be read.",
                "DeepL invalid response",
                ex);
        }

        var translatedText = responseBody?.Translations?.FirstOrDefault()?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(translatedText))
        {
            throw new TranslationProviderException(
                "DeepL returned an empty translation.",
                "DeepL empty result");
        }

        return translatedText;
    }

    private static DeepLRequest BuildRequest(string text, string sourceLanguage, string targetLanguage)
    {
        var payload = new DeepLTranslateRequest
        {
            Text = [text],
            TargetLanguage = MapLanguage(targetLanguage, isTarget: true),
            SplitSentences = "0",
            PreserveFormatting = true
        };

        if (!sourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            payload.SourceLanguage = MapLanguage(sourceLanguage, isTarget: false);
        }

        var estimatedBytes = Encoding.UTF8.GetByteCount(text)
            + Encoding.UTF8.GetByteCount(payload.TargetLanguage)
            + 256;

        return new DeepLRequest(payload, estimatedBytes);
    }

    private static string MapLanguage(string language, bool isTarget)
    {
        return language.ToLowerInvariant() switch
        {
            "en" => isTarget ? "EN-US" : "EN",
            "ar" => "AR",
            "ja" => "JA",
            "ko" => "KO",
            "fr" => "FR",
            "de" => "DE",
            "es" => "ES",
            "zh-hans" => isTarget ? "ZH-HANS" : "ZH",
            "zh" => "ZH",
            _ => language.ToUpperInvariant()
        };
    }

    private sealed record DeepLRequest(DeepLTranslateRequest Payload, int EstimatedUtf8Bytes);

    private sealed class DeepLTranslateRequest
    {
        [JsonPropertyName("text")]
        public required string[] Text { get; init; }

        [JsonPropertyName("target_lang")]
        public required string TargetLanguage { get; init; }

        [JsonPropertyName("source_lang")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SourceLanguage { get; set; }

        [JsonPropertyName("split_sentences")]
        public string SplitSentences { get; init; } = "0";

        [JsonPropertyName("preserve_formatting")]
        public bool PreserveFormatting { get; init; } = true;
    }

    private sealed class DeepLTranslateResponse
    {
        [JsonPropertyName("translations")]
        public DeepLTranslation[]? Translations { get; init; }
    }

    private sealed class DeepLTranslation
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }
    }
}
