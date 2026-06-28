using CortexTransl.App.Models;
using CortexTransl.App.Services.Cache;
using CortexTransl.App.Services.Ocr;
using CortexTransl.App.Services.Translation;
using CortexTransl.App.Utils;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CortexTransl.App.Services.Capture;

public sealed class CaptureTranslatePipeline
{
    private const string MenuTranslationMode = "menu";
    private readonly IScreenCaptureService _screenCaptureService;
    private readonly IReadOnlyDictionary<string, IOcrEngine> _ocrEngines;
    private readonly IReadOnlyDictionary<string, ITranslationProvider> _translationProviders;
    private readonly ITranslationCacheRepository _cacheRepository;
    private readonly TimingLogger _timingLogger;
    private readonly string? _debugCaptureDirectory;

    private string? _lastOcrKey;
    private string? _lastOcrText;
    private IReadOnlyList<RecognizedTextBlock> _lastOcrBlocks = [];
    private string? _lastTranslationKey;
    private string? _lastTranslatedText;
    private IReadOnlyList<RecognizedTextBlock> _lastTranslatedBlocks = [];

    public CaptureTranslatePipeline(
        IScreenCaptureService screenCaptureService,
        IEnumerable<IOcrEngine> ocrEngines,
        IEnumerable<ITranslationProvider> translationProviders,
        ITranslationCacheRepository cacheRepository,
        TimingLogger timingLogger,
        string? debugCaptureDirectory = null)
    {
        _screenCaptureService = screenCaptureService;
        _ocrEngines = ocrEngines.ToDictionary(engine => engine.Id, StringComparer.OrdinalIgnoreCase);
        _translationProviders = translationProviders.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        _cacheRepository = cacheRepository;
        _timingLogger = timingLogger;
        _debugCaptureDirectory = debugCaptureDirectory;
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

        if (IsMenuMode(settings))
        {
            var debugCaptureStopwatch = Stopwatch.StartNew();
            SaveDebugCapture(bitmap);
            debugCaptureStopwatch.Stop();
            timings.Add(new TimingEntry("debug capture", debugCaptureStopwatch.ElapsedMilliseconds));
        }

        var compareStopwatch = Stopwatch.StartNew();
        var imageFingerprint = ImageFingerprint.Compute(bitmap);
        compareStopwatch.Stop();
        timings.Add(new TimingEntry("image compare", compareStopwatch.ElapsedMilliseconds));

        var ocrKey = CreateOcrKey(imageFingerprint, settings);
        var ocrWasSkipped = ocrKey == _lastOcrKey;
        string normalizedText;
        IReadOnlyList<RecognizedTextBlock> textBlocks;

        if (ocrWasSkipped)
        {
            normalizedText = _lastOcrText ?? string.Empty;
            textBlocks = CloneBlocks(_lastOcrBlocks);
            timings.Add(new TimingEntry("ocr skipped", 0));
        }
        else
        {
            var ocrStopwatch = Stopwatch.StartNew();
            var ocrEngine = ResolveOcrEngine(settings.OcrEngine);
            var ocrResult = await ocrEngine.RecognizeAsync(
                bitmap,
                settings.SourceLanguage,
                settings.OcrPreset,
                cancellationToken);

            textBlocks = NormalizeTextBlocks(ocrResult.Blocks);
            normalizedText = IsMenuMode(settings) && textBlocks.Count > 0
                ? string.Join(Environment.NewLine, textBlocks.Select(block => block.Text))
                : TextNormalizer.Normalize(ocrResult.Text);
            ocrStopwatch.Stop();
            timings.Add(new TimingEntry("ocr", ocrStopwatch.ElapsedMilliseconds));

            _lastOcrKey = ocrKey;
            _lastOcrText = normalizedText;
            _lastOcrBlocks = CloneBlocks(textBlocks);
        }

        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            _lastTranslationKey = null;
            _lastTranslatedText = string.Empty;
            _lastTranslatedBlocks = [];
            return await CompleteAsync(
                string.Empty,
                string.Empty,
                IsMenuMode(settings)
                    ? "OCR did not detect menu text. Select a clear UI panel and try Small Text or High Contrast Text."
                    : "OCR did not detect text. Make sure the selected region contains clear dialogue.",
                "not checked",
                "not checked",
                timings,
                cancellationToken);
        }

        if (IsMenuMode(settings))
        {
            return await RunMenuTranslationAsync(
                normalizedText,
                textBlocks,
                settings,
                ocrWasSkipped,
                timings,
                cancellationToken);
        }

        return await RunSubtitleTranslationAsync(
            normalizedText,
            textBlocks,
            settings,
            ocrWasSkipped,
            timings,
            cancellationToken);
    }

    private async Task<PipelineResult> RunSubtitleTranslationAsync(
        string normalizedText,
        IReadOnlyList<RecognizedTextBlock> textBlocks,
        PipelineSettings settings,
        bool ocrWasSkipped,
        List<TimingEntry> timings,
        CancellationToken cancellationToken)
    {
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
                cancellationToken,
                textBlocks);
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
                _lastTranslatedBlocks = [];

                return await CompleteAsync(
                    normalizedText,
                    string.Empty,
                    ex.Message,
                    "miss",
                    ex.ProviderStatus,
                    timings,
                    cancellationToken,
                    textBlocks);
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
        _lastTranslatedBlocks = [];

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
            cancellationToken,
            textBlocks);
    }

    private async Task<PipelineResult> RunMenuTranslationAsync(
        string panelText,
        IReadOnlyList<RecognizedTextBlock> recognizedBlocks,
        PipelineSettings settings,
        bool ocrWasSkipped,
        List<TimingEntry> timings,
        CancellationToken cancellationToken)
    {
        var translationProvider = ResolveTranslationProvider(settings.TranslationProvider);
        var providerStatus = translationProvider.GetStatus();
        var translationKey = CreateTranslationKey(panelText, settings, translationProvider.Id);

        if (translationKey == _lastTranslationKey &&
            !string.IsNullOrWhiteSpace(_lastTranslatedText) &&
            _lastTranslatedBlocks.Count > 0)
        {
            timings.Add(new TimingEntry("translation skipped", 0));
            return await CompleteAsync(
                panelText,
                _lastTranslatedText,
                ocrWasSkipped
                    ? "Menu region is unchanged; reused OCR and translations."
                    : "Menu text is unchanged; reused previous translations.",
                "skipped",
                providerStatus,
                timings,
                cancellationToken,
                CloneBlocks(_lastTranslatedBlocks));
        }

        var translatedBlocks = CloneBlocks(recognizedBlocks);
        var missingBlocks = new List<RecognizedTextBlock>();
        var cacheHits = 0;

        var cacheStopwatch = Stopwatch.StartNew();
        foreach (var block in translatedBlocks)
        {
            var cachedTranslation = await _cacheRepository.GetAsync(
                block.Text,
                settings.SourceLanguage,
                settings.TargetLanguage,
                translationProvider.Id,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(cachedTranslation))
            {
                missingBlocks.Add(block);
            }
            else
            {
                block.TranslatedText = cachedTranslation;
                cacheHits++;
            }
        }
        cacheStopwatch.Stop();
        timings.Add(new TimingEntry("cache lookup", cacheStopwatch.ElapsedMilliseconds));

        var cacheStatus = cacheHits == translatedBlocks.Count ? "hit" : cacheHits > 0 ? "partial hit" : "miss";
        if (missingBlocks.Count == 0)
        {
            timings.Add(new TimingEntry("cache hit", 0));
        }
        else
        {
            timings.Add(new TimingEntry("cache miss", 0));
            var batch = BuildLineBatch(missingBlocks);
            var translationStopwatch = Stopwatch.StartNew();
            string translatedBatch;

            try
            {
                translatedBatch = await translationProvider.TranslateAsync(
                    batch,
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
                _lastTranslatedBlocks = [];

                return await CompleteAsync(
                    panelText,
                    string.Empty,
                    ex.Message,
                    cacheStatus,
                    ex.ProviderStatus,
                    timings,
                    cancellationToken,
                    translatedBlocks);
            }

            var parsedTranslations = ParseLineBatch(translatedBatch);
            if (parsedTranslations.Count == 0)
            {
                _lastTranslationKey = translationKey;
                _lastTranslatedText = translatedBatch.Trim();
                _lastTranslatedBlocks = CloneBlocks(translatedBlocks);

                return await CompleteAsync(
                    panelText,
                    _lastTranslatedText,
                    "Menu region translated as one block; line mapping was not preserved by the provider.",
                    cacheStatus,
                    providerStatus,
                    timings,
                    cancellationToken,
                    translatedBlocks);
            }

            foreach (var block in missingBlocks)
            {
                if (!parsedTranslations.TryGetValue(block.LineIndex, out var translatedLine) ||
                    string.IsNullOrWhiteSpace(translatedLine))
                {
                    translatedLine = string.Empty;
                }

                block.TranslatedText = translatedLine;

                if (!string.IsNullOrWhiteSpace(translatedLine))
                {
                    await _cacheRepository.SaveAsync(
                        block.Text,
                        settings.SourceLanguage,
                        settings.TargetLanguage,
                        translationProvider.Id,
                        translatedLine,
                        cancellationToken);
                }
            }
        }

        var translatedText = BuildMenuDisplayText(translatedBlocks);
        _lastTranslationKey = translationKey;
        _lastTranslatedText = translatedText;
        _lastTranslatedBlocks = CloneBlocks(translatedBlocks);

        var status = cacheStatus.Equals("hit", StringComparison.OrdinalIgnoreCase)
            ? $"Loaded {translatedBlocks.Count} menu lines from cache."
            : $"Translated {translatedBlocks.Count} menu lines in one batch.";

        return await CompleteAsync(
            panelText,
            translatedText,
            status,
            cacheStatus,
            cacheStatus.Equals("hit", StringComparison.OrdinalIgnoreCase)
                ? $"{providerStatus} (line cache hit)"
                : providerStatus,
            timings,
            cancellationToken,
            translatedBlocks);
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
        CancellationToken cancellationToken,
        IReadOnlyList<RecognizedTextBlock>? textBlocks = null)
    {
        var result = new PipelineResult
        (
            originalText,
            translatedText,
            status,
            cacheStatus,
            providerStatus,
            timings.ToArray(),
            CloneBlocks(textBlocks ?? [])
        );

        await _timingLogger.LogAsync("capture-translate", result.Timings, cancellationToken);
        return result;
    }

    public void ResetState()
    {
        _lastOcrKey = null;
        _lastOcrText = null;
        _lastOcrBlocks = [];
        _lastTranslationKey = null;
        _lastTranslatedText = null;
        _lastTranslatedBlocks = [];
    }

    private static bool IsMenuMode(PipelineSettings settings)
    {
        return settings.TranslationMode.Equals(MenuTranslationMode, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<RecognizedTextBlock> NormalizeTextBlocks(IReadOnlyList<RecognizedTextBlock> blocks)
    {
        return blocks
            .Select(block => new RecognizedTextBlock
            {
                Text = TextNormalizer.Normalize(block.Text),
                Bounds = block.Bounds,
                LineIndex = block.LineIndex,
                Confidence = block.Confidence,
                TranslatedText = block.TranslatedText
            })
            .Where(block => !string.IsNullOrWhiteSpace(block.Text) &&
                !block.Bounds.IsEmpty &&
                block.Bounds.Width >= 3 &&
                block.Bounds.Height >= 3)
            .Select((block, index) => new RecognizedTextBlock
            {
                Text = block.Text,
                Bounds = block.Bounds,
                LineIndex = index + 1,
                Confidence = block.Confidence,
                TranslatedText = block.TranslatedText
            })
            .ToArray();
    }

    private static IReadOnlyList<RecognizedTextBlock> CloneBlocks(IReadOnlyList<RecognizedTextBlock> blocks)
    {
        return blocks
            .Select(block => new RecognizedTextBlock
            {
                Text = block.Text,
                Bounds = block.Bounds,
                LineIndex = block.LineIndex,
                Confidence = block.Confidence,
                TranslatedText = block.TranslatedText
            })
            .ToArray();
    }

    private static string BuildLineBatch(IEnumerable<RecognizedTextBlock> blocks)
    {
        var builder = new StringBuilder();
        foreach (var block in blocks)
        {
            builder
                .Append("<line id=\"")
                .Append(block.LineIndex)
                .Append("\">")
                .Append(WebUtility.HtmlEncode(block.Text))
                .AppendLine("</line>");
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyDictionary<int, string> ParseLineBatch(string translatedBatch)
    {
        var translations = new Dictionary<int, string>();
        var matches = Regex.Matches(
            translatedBatch,
            """<line\s+id\s*=\s*["'](?<id>\d+)["']\s*>(?<text>.*?)</line>""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match match in matches)
        {
            if (!int.TryParse(match.Groups["id"].Value, out var id))
            {
                continue;
            }

            var translated = WebUtility.HtmlDecode(match.Groups["text"].Value).Trim();
            translated = Regex.Replace(translated, @"\s+", " ");
            translations[id] = translated;
        }

        return translations;
    }

    private static string BuildMenuDisplayText(IReadOnlyList<RecognizedTextBlock> blocks)
    {
        var translatedLines = blocks
            .Where(block => !string.IsNullOrWhiteSpace(block.TranslatedText))
            .Select(block => $"{block.LineIndex}. {block.TranslatedText}");

        return string.Join(Environment.NewLine, translatedLines);
    }

    private static string CreateOcrKey(string imageFingerprint, PipelineSettings settings)
    {
        return string.Join(
            '|',
            imageFingerprint,
            settings.SourceLanguage,
            settings.OcrEngine,
            settings.TranslationMode,
            settings.OcrPreset);
    }

    private void SaveDebugCapture(Bitmap bitmap)
    {
        if (string.IsNullOrWhiteSpace(_debugCaptureDirectory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_debugCaptureDirectory);
            var path = Path.Combine(_debugCaptureDirectory, "last-menu-capture.png");
            bitmap.Save(path, ImageFormat.Png);
        }
        catch
        {
            // Debug captures are best effort and should never block translation.
        }
    }

    private static string CreateTranslationKey(string text, PipelineSettings settings, string providerId)
    {
        return string.Join(
            '|',
            TextHasher.Sha256(text),
            settings.SourceLanguage,
            settings.TargetLanguage,
            providerId,
            settings.TranslationMode);
    }
}
