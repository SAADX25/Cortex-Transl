using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CortexTransl.App.Utils;

namespace CortexTransl.App.Services.Translation;

public sealed class DeepLTranslationProvider : ITranslationProvider
{
    private const int MaximumRequestBytes = 128 * 1024;
    private const int MaximumBatchSize = 40;
    private const string GameDialogueContext =
        "Video-game story dialogue. Translate into clear, natural Modern Standard Arabic suitable for subtitles. Preserve the full meaning, intent, emotion, and every detail. Avoid literal calques and keep the wording concise and easy to understand. Keep established character, place, and item names consistent; do not add explanations or omit content.";
    private const string GameMenuContext =
        "Video-game menu and button labels. Translate into concise, natural Modern Standard Arabic that fits a game UI. Use clear action wording for commands and short noun phrases for menu names. Keep established names and brands consistent. Do not add explanations or extra words.";
    private const string GeneralArabicContext =
        "Translate into clear, natural Modern Standard Arabic. Preserve meaning, tone, names, and all details. Do not add explanations or omit content.";
    private readonly TranslationProviderSettings _settings;
    private readonly HttpClient _httpClient;

    public DeepLTranslationProvider(TranslationProviderSettings settings, HttpClient? httpClient = null)
    {
        _settings = settings;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    public string Id => "deepl-ar-story-v4";

    public bool SupportsContextualLongText => true;

    public async Task<string> TranslateStoryAsync(string text, string sourceLanguage, string targetLanguage,
        StoryTranslationContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.TryExactArabicTerm(text, sourceLanguage, targetLanguage, out var term)) return term;
        var apiKey = _settings.DeepLApiKey.Trim();
        if (apiKey.Length == 0) throw new TranslationProviderException("Paste a DeepL API key before translating.", "DeepL missing API key");
        var protectedText = ProtectedStoryText.Create(text, sourceLanguage, targetLanguage, context);
        var result = await TranslateBatchAsync([protectedText?.Text ?? text], sourceLanguage, targetLanguage, apiKey,
            TranslationContentKind.GameDialogue, cancellationToken, context, protectedText is not null);
        if (result.Length != 1 || string.IsNullOrWhiteSpace(result[0]))
            throw new TranslationProviderException("DeepL returned an incomplete translation.", "DeepL invalid response");
        return protectedText is null
            ? ArabicTranslationCleaner.Clean(result[0], targetLanguage)
            : protectedText.ReadTranslation(ArabicTranslationCleaner.Clean(result[0], targetLanguage));
    }

    public async Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default,
        TranslationContentKind contentKind = TranslationContentKind.General)
    {
        var translations = await TranslateManyAsync([text], sourceLanguage, targetLanguage, cancellationToken, contentKind);
        return translations.Count > 0 ? translations[0] : string.Empty;
    }

    public async Task<IReadOnlyList<string>> TranslateManyAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default,
        TranslationContentKind contentKind = TranslationContentKind.General)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (texts.Count == 0)
        {
            return [];
        }

        var apiKey = _settings.DeepLApiKey.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new TranslationProviderException(
                "Paste a DeepL API key before translating.",
                "DeepL missing API key");
        }

        var results = new string[texts.Count];
        var pendingIndexes = new List<int>(texts.Count);
        for (var index = 0; index < texts.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(texts[index]))
            {
                results[index] = string.Empty;
            }
            else
            {
                pendingIndexes.Add(index);
            }
        }

        for (var offset = 0; offset < pendingIndexes.Count; offset += MaximumBatchSize)
        {
            var batchIndexes = pendingIndexes
                .Skip(offset)
                .Take(MaximumBatchSize)
                .ToArray();
            var batchTexts = batchIndexes
                .Select(index => texts[index])
                .ToArray();
            var translatedBatch = await TranslateBatchAsync(
                batchTexts,
                sourceLanguage,
                targetLanguage,
                apiKey,
                contentKind,
                cancellationToken);

            if (translatedBatch.Length != batchTexts.Length)
            {
                throw new TranslationProviderException(
                    "DeepL returned a different number of sentences.",
                    "DeepL invalid response");
            }

            for (var batchIndex = 0; batchIndex < batchIndexes.Length; batchIndex++)
            {
                results[batchIndexes[batchIndex]] = ArabicTranslationCleaner.Clean(translatedBatch[batchIndex], targetLanguage);
            }
        }

        return results;
    }

    private async Task<string[]> TranslateBatchAsync(
        string[] texts,
        string sourceLanguage,
        string targetLanguage,
        string apiKey,
        TranslationContentKind contentKind,
        CancellationToken cancellationToken,
        StoryTranslationContext? storyContext = null,
        bool protectTerminology = false)
    {
        var payload = new DeepLTranslateRequest
        {
            Text = texts,
            TargetLanguage = MapLanguage(targetLanguage, isTarget: true),
            SplitSentences = contentKind == TranslationContentKind.GameDialogue ? "nonewlines" : "0",
            PreserveFormatting = true,
            Context = contentKind switch
            {
                TranslationContentKind.GameDialogue => GameDialogueContext +
                    " Preserve negation, emotion, gender when known, and relationships. Never invent identities or plot details.\n" +
                    (storyContext?.ToDeepLContext() ?? string.Empty),
                TranslationContentKind.UiLabel => GameMenuContext,
                _ => GeneralArabicContext
            },
            GlossaryId = string.IsNullOrWhiteSpace(storyContext?.DeepLGlossaryId) ? null : storyContext.DeepLGlossaryId,
            TagHandling = protectTerminology ? "xml" : null,
            IgnoreTags = protectTerminology ? ["cortex-term"] : null,
            NonSplittingTags = protectTerminology ? ["cortex-term", "cortex-dialogue"] : null,
            OutlineDetection = protectTerminology ? false : null
        };

        if (payload.GlossaryId is not null && sourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
            throw new TranslationProviderException("Select a source language to use a DeepL glossary.", "DeepL glossary requires source language");

        if (!sourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            payload.SourceLanguage = MapLanguage(sourceLanguage, isTarget: false);
        }

        var requestBody = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (requestBody.Length > MaximumRequestBytes)
        {
            throw new TranslationProviderException(
                "The text is too large for one DeepL request.",
                "DeepL request too large");
        }

        var endpoint = _settings.UseDeepLFreeApi
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(requestBody)
        };
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);
        httpRequest.Headers.UserAgent.ParseAdd("CortexTransl/1.0");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TranslationProviderException(
                "DeepL timed out. Check your connection.",
                "DeepL timeout",
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw new TranslationProviderException(
                "Could not reach DeepL.",
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

        var translations = responseBody?.Translations?
            .Select(item => item.Text?.Trim() ?? string.Empty)
            .ToArray();

        if (translations is null || translations.Length == 0 || translations.Any(string.IsNullOrWhiteSpace))
        {
            throw new TranslationProviderException(
                "DeepL returned an empty translation.",
                "DeepL empty result");
        }

        return translations;
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

        [JsonPropertyName("context")]
        public string? Context { get; init; }

        [JsonPropertyName("glossary_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? GlossaryId { get; init; }

        [JsonPropertyName("tag_handling")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TagHandling { get; init; }

        [JsonPropertyName("ignore_tags")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? IgnoreTags { get; init; }

        [JsonPropertyName("non_splitting_tags")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? NonSplittingTags { get; init; }

        [JsonPropertyName("outline_detection")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? OutlineDetection { get; init; }
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
