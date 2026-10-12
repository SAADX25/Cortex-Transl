using CortexTransl.App.Models;
using CortexTransl.App.Services.Cache;
using CortexTransl.App.Services.Ocr;
using CortexTransl.App.Services.Translation;
using CortexTransl.App.Utils;

namespace CortexTransl.App.Services.Capture;

internal sealed class MenuTranslationProcessor(ITranslationProvider provider, ITranslationCacheRepository cache)
{
    private readonly ITranslationProvider _translationProvider = provider;
    private readonly ITranslationCacheRepository _cacheRepository = cache;
    public async Task<PipelineResult> TranslateAsync(
        string cacheProviderId,
        OcrResult ocrResult,
        string sourceLanguage,
        string targetLanguage,
        StoryTranslationContext context,
        CancellationToken cancellationToken)
    {
        var originalText = string.Join(
            Environment.NewLine,
            ocrResult.Blocks.Select(block => block.Text).Where(static text => !string.IsNullOrWhiteSpace(text)));
        if (string.IsNullOrWhiteSpace(originalText))
        {
            originalText = DialogueTextAssembler.Assemble(ocrResult, sourceLanguage);
        }

        var uniqueTexts = ocrResult.Blocks
            .Select(block => block.Text)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var text in uniqueTexts)
        {
            if (context.TryExactArabicTerm(text, sourceLanguage, targetLanguage, out var term))
            {
                translations[text] = term;
                continue;
            }
            if (LooksMostlyArabic(text))
            {
                continue;
            }

            var cached = await _cacheRepository.GetAsync(
                text,
                sourceLanguage,
                targetLanguage,
                cacheProviderId,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(cached))
            {
                translations[text] = cached;
            }
            else
            {
                missing.Add(text);
            }
        }

        if (missing.Count > 0)
        {
            var translatedMissing = new string[missing.Count];
            try
            {
                var batchTranslations = await _translationProvider.TranslateManyAsync(
                    missing,
                    sourceLanguage,
                    targetLanguage,
                    cancellationToken,
                    TranslationContentKind.UiLabel);

                for (var index = 0; index < missing.Count; index++)
                {
                    var translated = index < batchTranslations.Count ? batchTranslations[index] : string.Empty;
                    if (string.IsNullOrWhiteSpace(translated))
                    {
                        translated = await _translationProvider.TranslateAsync(
                            missing[index],
                            sourceLanguage,
                            targetLanguage,
                            cancellationToken,
                            TranslationContentKind.UiLabel);
                    }

                    translatedMissing[index] = translated;
                }
            }
            catch (TranslationProviderException ex)
            {
                return new PipelineResult(originalText, string.Empty, ex.Message, false, false);
            }

            for (var index = 0; index < missing.Count; index++)
            {
                var translated = translatedMissing[index];
                translations[missing[index]] = translated;
                if (!string.IsNullOrWhiteSpace(translated))
                {
                    await _cacheRepository.SaveAsync(
                        missing[index],
                        sourceLanguage,
                        targetLanguage,
                        cacheProviderId,
                        translated,
                        cancellationToken);
                }
            }
        }

        var blocks = ocrResult.Blocks
            .Select(block => new TranslatedBlock(
                block.Text,
                translations.TryGetValue(block.Text, out var translated) ? translated : string.Empty,
                block.Bounds))
            .Where(block => !string.IsNullOrWhiteSpace(block.TranslatedText))
            .ToArray();

        if (blocks.Length == 0)
        {
            return new PipelineResult(
                originalText,
                string.Empty,
                "Labels were found, but translation returned empty text.",
                false,
                false);
        }

        var translatedText = string.Join(Environment.NewLine, blocks.Select(block => block.TranslatedText));

        var usedCache = missing.Count == 0;
        return new PipelineResult(
            originalText,
            translatedText,
            usedCache ? $"Loaded {blocks.Length} labels from cache." : $"Translated {blocks.Length} labels.",
            usedCache,
            false,
            blocks);
    }

    private static bool LooksMostlyArabic(string text)
    {
        var arabic = 0;
        var letters = 0;
        foreach (var character in text)
        {
            if (!char.IsLetter(character))
            {
                continue;
            }

            letters++;
            if (character is >= '\u0600' and <= '\u06FF')
            {
                arabic++;
            }
        }

        return letters > 0 && arabic * 2 >= letters;
    }

}
