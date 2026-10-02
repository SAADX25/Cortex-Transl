using CortexTransl.App.Models;
using CortexTransl.App.Services.Cache;
using CortexTransl.App.Services.Ocr;
using CortexTransl.App.Services.Translation;
using CortexTransl.App.Utils;
using System.Drawing;

namespace CortexTransl.App.Services.Capture;

public sealed class TranslationPipeline
{
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly IOcrEngine _ocrEngine;
    private readonly ITranslationProvider _translationProvider;
    private readonly ITranslationCacheRepository _cacheRepository;

    private string? _lastFingerprint;
    private string? _lastOriginalText;
    private string? _lastTranslatedText;
    private string? _pendingText;
    private IReadOnlyList<TranslatedBlock> _lastBlocks = [];

    public TranslationPipeline(
        IScreenCaptureService screenCaptureService,
        IOcrEngine ocrEngine,
        ITranslationProvider translationProvider,
        ITranslationCacheRepository cacheRepository)
    {
        _screenCaptureService = screenCaptureService;
        _ocrEngine = ocrEngine;
        _translationProvider = translationProvider;
        _cacheRepository = cacheRepository;
    }

    public async Task<PipelineResult> RunAsync(
        CaptureRegion region,
        string sourceLanguage,
        string targetLanguage,
        bool listMode,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var bitmap = await _screenCaptureService.CaptureAsync(region, cancellationToken);
            var fingerprint = ImageFingerprint.Compute(bitmap);

            if (fingerprint == _lastFingerprint
                && !string.IsNullOrWhiteSpace(_lastTranslatedText)
                && !string.IsNullOrWhiteSpace(_lastOriginalText)
                && (!listMode || _lastBlocks.Count > 0))
            {
                return new PipelineResult(
                    _lastOriginalText,
                    _lastTranslatedText,
                    "Same frame.",
                    false,
                    true,
                    _lastBlocks);
            }

            OcrResult ocrResult;
            try
            {
                ocrResult = await _ocrEngine.RecognizeAsync(
                    bitmap,
                    sourceLanguage,
                    cancellationToken,
                    listMode ? "list" : "dialogue");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Write("ocr", ex);
                return KeepLast(fingerprint, "Could not read the text. Select the region again with F9.");
            }

            if (listMode)
            {
                return await TranslateListAsync(
                    fingerprint,
                    ocrResult,
                    sourceLanguage,
                    targetLanguage,
                    cancellationToken);
            }

            return await TranslateDialogueAsync(
                fingerprint,
                ocrResult,
                sourceLanguage,
                targetLanguage,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CaptureUnavailableException ex)
        {
            return new PipelineResult(
                _lastOriginalText ?? string.Empty,
                _lastTranslatedText ?? string.Empty,
                ex.Message,
                false,
                false,
                _lastBlocks);
        }
        catch (Exception ex)
        {
            AppLog.Write("pipeline", ex);
            return new PipelineResult(
                string.Empty,
                string.Empty,
                "Capture failed. Select the region again with F9.",
                false,
                false);
        }
    }

    public void Reset()
    {
        _lastFingerprint = null;
        _lastOriginalText = null;
        _lastTranslatedText = null;
        _pendingText = null;
        _lastBlocks = [];
    }

    private async Task<PipelineResult> TranslateDialogueAsync(
        string fingerprint,
        OcrResult ocrResult,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var originalText = DialogueTextAssembler.Assemble(ocrResult, sourceLanguage);
        if (string.IsNullOrWhiteSpace(originalText))
        {
            return KeepLast(fingerprint, "Waiting for text.");
        }

        if (ShouldHoldLastTranslation(originalText))
        {
            _lastFingerprint = fingerprint;
            return new PipelineResult(
                _lastOriginalText ?? originalText,
                _lastTranslatedText ?? string.Empty,
                "Same dialogue.",
                false,
                true);
        }

        var cached = await _cacheRepository.GetAsync(
            originalText,
            sourceLanguage,
            targetLanguage,
            GetCacheProviderId(TranslationContentKind.GameDialogue),
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(cached))
        {
            Remember(fingerprint, originalText, cached, []);
            return new PipelineResult(originalText, cached, "Loaded from cache.", true, false);
        }

        string translated;
        try
        {
            translated = await TranslateSmartAsync(
                originalText,
                sourceLanguage,
                targetLanguage,
                TranslationContentKind.GameDialogue,
                cancellationToken);
        }
        catch (TranslationProviderException ex)
        {
            return new PipelineResult(originalText, string.Empty, ex.Message, false, false);
        }

        if (!string.IsNullOrWhiteSpace(translated))
        {
            await _cacheRepository.SaveAsync(
                originalText,
                sourceLanguage,
                targetLanguage,
                GetCacheProviderId(TranslationContentKind.GameDialogue),
                translated,
                cancellationToken);
        }

        Remember(fingerprint, originalText, translated, []);
        return new PipelineResult(originalText, translated, "Translated.", false, false);
    }

    private async Task<string> TranslateSmartAsync(
        string originalText,
        string sourceLanguage,
        string targetLanguage,
        TranslationContentKind contentKind,
        CancellationToken cancellationToken)
    {
        var chunks = TranslationChunker.Split(originalText);
        if (chunks.Count <= 1 || _translationProvider.SupportsContextualLongText)
        {
            return await _translationProvider.TranslateAsync(
                originalText,
                sourceLanguage,
                targetLanguage,
                cancellationToken,
                contentKind);
        }

        var translations = await _translationProvider.TranslateManyAsync(
            chunks,
            sourceLanguage,
            targetLanguage,
            cancellationToken,
            contentKind);

        // Retry the whole text if a provider omitted or emptied any chunk.
        var hasMissingTranslations = translations.Count != chunks.Count
            || translations.Any(static translation => string.IsNullOrWhiteSpace(translation));
        if (hasMissingTranslations)
        {
            var fallback = await _translationProvider.TranslateAsync(
                originalText,
                sourceLanguage,
                targetLanguage,
                cancellationToken,
                contentKind);
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return fallback;
            }

            throw new TranslationProviderException(
                "The translation service returned an incomplete result. Try again.",
                "Incomplete translation");
        }

        return TranslationChunker.Join(translations);
    }

    private async Task<PipelineResult> TranslateListAsync(
        string fingerprint,
        OcrResult ocrResult,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        if (ocrResult.Blocks.Count == 0)
        {
            return KeepLast(
                fingerprint,
                _lastBlocks.Count > 0
                    ? "Waiting for labels."
                    : "No labels found. Select the list or icon names.");
        }

        var originalText = string.Join(
            Environment.NewLine,
            ocrResult.Blocks.Select(block => block.Text).Where(static text => !string.IsNullOrWhiteSpace(text)));
        if (string.IsNullOrWhiteSpace(originalText))
        {
            originalText = DialogueTextAssembler.Assemble(ocrResult, sourceLanguage);
        }

        if (_lastBlocks.Count == ocrResult.Blocks.Count && ShouldHoldLastTranslation(originalText))
        {
            _lastFingerprint = fingerprint;
            return new PipelineResult(
                _lastOriginalText ?? originalText,
                _lastTranslatedText ?? string.Empty,
                "Same list.",
                false,
                true,
                _lastBlocks);
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
            if (LooksMostlyArabic(text))
            {
                continue;
            }

            var cached = await _cacheRepository.GetAsync(
                text,
                sourceLanguage,
                targetLanguage,
                GetCacheProviderId(TranslationContentKind.UiLabel),
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
                        GetCacheProviderId(TranslationContentKind.UiLabel),
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
        Remember(fingerprint, originalText, translatedText, blocks);
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

    private bool ShouldHoldLastTranslation(string originalText)
    {
        if (string.IsNullOrWhiteSpace(_lastTranslatedText) && string.IsNullOrWhiteSpace(_lastOriginalText))
        {
            ClearPending();
            return false;
        }

        if (DialogueTextAssembler.LooksLikeOcrGlitch(_lastOriginalText ?? string.Empty, originalText))
        {
            return true;
        }

        var decision = TextSimilarity.Classify(_lastOriginalText ?? string.Empty, originalText);
        if (decision == DialogueStability.Unchanged)
        {
            ClearPending();
            return true;
        }

        if (decision == DialogueStability.Changed || TextSimilarity.LooksComplete(originalText))
        {
            ClearPending();
            return false;
        }

        if (_pendingText is not null
            && TextSimilarity.Classify(_pendingText, originalText) == DialogueStability.Unchanged)
        {
            ClearPending();
            return false;
        }

        _pendingText = originalText;
        return true;
    }

    private PipelineResult KeepLast(string fingerprint, string status)
    {
        _lastFingerprint = fingerprint;
        if (string.IsNullOrWhiteSpace(_lastTranslatedText) && _lastBlocks.Count == 0)
        {
            return new PipelineResult(string.Empty, string.Empty, status, false, false);
        }

        return new PipelineResult(
            _lastOriginalText ?? string.Empty,
            _lastTranslatedText ?? string.Empty,
            status,
            false,
            true,
            _lastBlocks);
    }

    private void ClearPending()
    {
        _pendingText = null;
    }

    private void Remember(
        string fingerprint,
        string originalText,
        string translatedText,
        IReadOnlyList<TranslatedBlock> blocks)
    {
        _lastFingerprint = fingerprint;
        _lastOriginalText = originalText;
        _lastTranslatedText = translatedText;
        _lastBlocks = blocks;
        ClearPending();
    }

    private string GetCacheProviderId(TranslationContentKind contentKind)
    {
        if (contentKind == TranslationContentKind.UiLabel)
        {
            return $"{_translationProvider.Id}:UiLabel:ar-terms-v1";
        }

        return _translationProvider.SupportsContextualLongText
            ? $"{_translationProvider.Id}:{contentKind}"
            : _translationProvider.Id;
    }
}
