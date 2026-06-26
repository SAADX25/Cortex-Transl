using CortexTransl.App.Models;
using CortexTransl.App.Services.Cache;
using CortexTransl.App.Services.Ocr;
using CortexTransl.App.Services.Translation;
using CortexTransl.App.Utils;
using System.Diagnostics;

namespace CortexTransl.App.Services.Capture;

public sealed class CaptureTranslatePipeline
{
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly IReadOnlyDictionary<string, IOcrEngine> _ocrEngines;
    private readonly IReadOnlyDictionary<string, ITranslationProvider> _translationProviders;
    private readonly ITranslationCacheRepository _cacheRepository;
    private readonly TimingLogger _timingLogger;

    private string? _lastOcrKey;
    private string? _lastOcrText;
    private string? _lastTranslationKey;
    private string? _lastTranslatedText;

    public CaptureTranslatePipeline(
        IScreenCaptureService screenCaptureService,
        IEnumerable<IOcrEngine> ocrEngines,
        IEnumerable<ITranslationProvider> translationProviders,
        ITranslationCacheRepository cacheRepository,
        TimingLogger timingLogger)
    {
        _screenCaptureService = screenCaptureService;
        _ocrEngines = ocrEngines.ToDictionary(engine => engine.Id, StringComparer.OrdinalIgnoreCase);
        _translationProviders = translationProviders.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        _cacheRepository = cacheRepository;
        _timingLogger = timingLogger;
    }

    public async Task<PipelineResult> RunAsync(
        CaptureRegion region,
        PipelineSettings settings,
        CancellationToken cancellationToken = default)
    {
        var timings = new List<TimingEntry>();

        var captureStopwatch = Stopwatch.StartNew();
        using var bitmap = await _screenCaptureService.CaptureAsync(region, cancellationToken);
        captureStopwatch.Stop();
        timings.Add(new TimingEntry("capture", captureStopwatch.ElapsedMilliseconds));

        var compareStopwatch = Stopwatch.StartNew();
        var imageFingerprint = ImageFingerprint.Compute(bitmap);
        compareStopwatch.Stop();
        timings.Add(new TimingEntry("image compare", compareStopwatch.ElapsedMilliseconds));

        var ocrKey = CreateOcrKey(imageFingerprint, settings);
        var ocrWasSkipped = ocrKey == _lastOcrKey;
        string normalizedText;

        if (ocrWasSkipped)
        {
            normalizedText = _lastOcrText ?? string.Empty;
            timings.Add(new TimingEntry("ocr skipped", 0));
        }
        else
        {
            var ocrStopwatch = Stopwatch.StartNew();
            var ocrEngine = ResolveOcrEngine(settings.OcrEngine);
            var ocrResult = await ocrEngine.RecognizeAsync(bitmap, settings.SourceLanguage, cancellationToken);
            normalizedText = TextNormalizer.Normalize(ocrResult.Text);
            ocrStopwatch.Stop();
            timings.Add(new TimingEntry("ocr", ocrStopwatch.ElapsedMilliseconds));

            _lastOcrKey = ocrKey;
            _lastOcrText = normalizedText;
        }

        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            _lastTranslationKey = null;
            _lastTranslatedText = string.Empty;
            return await CompleteAsync(
                string.Empty,
                string.Empty,
                "OCR did not detect text. Make sure the selected region contains clear dialogue.",
                "not checked",
                "not checked",
                timings,
                cancellationToken);
        }

        var translationProvider = ResolveTranslationProvider(settings.TranslationProvider);
        var translationKey = CreateTranslationKey(normalizedText, settings, translationProvider.Id);
        var providerStatus = translationProvider.GetStatus();

        if (translationKey == _lastTranslationKey && !string.IsNullOrWhiteSpace(_lastTranslatedText))
        {
            timings.Add(new TimingEntry("translation skipped", 0));
            var skippedStatus = ocrWasSkipped
                ? "Captured region is unchanged; skipped OCR and translation."
                : "OCR text is unchanged; reused previous translation.";

            return await CompleteAsync(
                normalizedText,
                _lastTranslatedText,
                skippedStatus,
                "skipped",
                providerStatus,
                timings,
                cancellationToken);
        }

        var cacheStopwatch = Stopwatch.StartNew();
        var cachedTranslation = await _cacheRepository.GetAsync(
            normalizedText,
            settings.SourceLanguage,
            settings.TargetLanguage,
            translationProvider.Id,
            cancellationToken);
        cacheStopwatch.Stop();
        timings.Add(new TimingEntry("cache lookup", cacheStopwatch.ElapsedMilliseconds));

        string translatedText;
        var usedCache = !string.IsNullOrWhiteSpace(cachedTranslation);

        if (usedCache)
        {
            translatedText = cachedTranslation!;
            timings.Add(new TimingEntry("cache hit", 0));
        }
        else
        {
            timings.Add(new TimingEntry("cache miss", 0));
            var translationStopwatch = Stopwatch.StartNew();
            try
            {
                translatedText = await translationProvider.TranslateAsync(
                    normalizedText,
                    settings.SourceLanguage,
                    settings.TargetLanguage,
                    cancellationToken);
                translationStopwatch.Stop();
                timings.Add(new TimingEntry("translation", translationStopwatch.ElapsedMilliseconds));
                providerStatus = translationProvider.GetStatus();
            }
            catch (TranslationProviderException ex)
            {
                translationStopwatch.Stop();
                timings.Add(new TimingEntry("translation", translationStopwatch.ElapsedMilliseconds));
                _lastTranslationKey = null;
                _lastTranslatedText = string.Empty;

                return await CompleteAsync(
                    normalizedText,
                    string.Empty,
                    ex.Message,
                    "miss",
                    ex.ProviderStatus,
                    timings,
                    cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(translatedText))
            {
                await _cacheRepository.SaveAsync(
                    normalizedText,
                    settings.SourceLanguage,
                    settings.TargetLanguage,
                    translationProvider.Id,
                    translatedText,
                    cancellationToken);
            }
        }

        _lastTranslationKey = translationKey;
        _lastTranslatedText = translatedText;

        var status = usedCache
            ? "Loaded translation from cache."
            : ocrWasSkipped
                ? "Reused OCR text and translated."
                : "Captured, OCR processed, and translated.";

        return await CompleteAsync(
            normalizedText,
            translatedText,
            status,
            usedCache ? "hit" : "miss",
            usedCache ? $"{providerStatus} (cache hit)" : providerStatus,
            timings,
            cancellationToken);
    }

    private IOcrEngine ResolveOcrEngine(string id)
    {
        return _ocrEngines.TryGetValue(id, out var engine)
            ? engine
            : _ocrEngines.Values.First();
    }

    private ITranslationProvider ResolveTranslationProvider(string id)
    {
        return _translationProviders.TryGetValue(id, out var provider)
            ? provider
            : _translationProviders.Values.First();
    }

    private async Task<PipelineResult> CompleteAsync(
        string originalText,
        string translatedText,
        string status,
        string cacheStatus,
        string providerStatus,
        List<TimingEntry> timings,
        CancellationToken cancellationToken)
    {
        var result = new PipelineResult
        (
            originalText,
            translatedText,
            status,
            cacheStatus,
            providerStatus,
            timings.ToArray()
        );

        await _timingLogger.LogAsync("capture-translate", result.Timings, cancellationToken);
        return result;
    }

    private static string CreateOcrKey(string imageFingerprint, PipelineSettings settings)
    {
        return string.Join('|', imageFingerprint, settings.SourceLanguage, settings.OcrEngine);
    }

    private static string CreateTranslationKey(string text, PipelineSettings settings, string providerId)
    {
        return string.Join('|', TextHasher.Sha256(text), settings.SourceLanguage, settings.TargetLanguage, providerId);
    }
}
