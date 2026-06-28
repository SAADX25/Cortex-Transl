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
    private const string LensTranslationMode = "lens";
    private const string DesktopIconGranularity = "desktop-icons";

    private static readonly IReadOnlyDictionary<string, string> DesktopLabelAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["this pc"] = "This PC",
        ["his pc"] = "This PC",
        ["is pc"] = "This PC",
        ["recycle bin"] = "Recycle Bin",
        ["ecycle bin"] = "Recycle Bin",
        ["cycle bin"] = "Recycle Bin",
        ["control panel"] = "Control Panel",
        ["ntrol panel"] = "Control Panel",
        ["ontrol panel"] = "Control Panel",
        ["microsoft edge"] = "Microsoft Edge",
        ["icrosoft edge"] = "Microsoft Edge",
        ["visual studio code"] = "Visual Studio Code",
        ["isual studio code"] = "Visual Studio Code",
        ["steam"] = "Steam",
        ["discord"] = "Discord",
        ["ubisoft connect"] = "Ubisoft Connect",
        ["brave"] = "Brave",
        ["google chrome"] = "Google Chrome"
    };

    private static readonly IReadOnlyDictionary<string, string> OfficialArabicScreenTranslations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["this pc"] = "\u0647\u0630\u0627 \u0627\u0644\u0643\u0645\u0628\u064a\u0648\u062a\u0631",
        ["recycle bin"] = "\u0633\u0644\u0629 \u0627\u0644\u0645\u062d\u0630\u0648\u0641\u0627\u062a",
        ["control panel"] = "\u0644\u0648\u062d\u0629 \u0627\u0644\u062a\u062d\u0643\u0645"
    };

    private static readonly IReadOnlySet<string> KnownAppNameKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft edge",
        "visual studio code",
        "steam",
        "discord",
        "ubisoft connect",
        "brave",
        "google chrome"
    };
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
        CancellationToken cancellationToken = default,
        Action? afterCapture = null)
    {
        var timings = new List<TimingEntry>();

        var captureStopwatch = Stopwatch.StartNew();
        using var bitmap = await _screenCaptureService.CaptureAsync(region, cancellationToken);
        captureStopwatch.Stop();
        timings.Add(new TimingEntry("capture", captureStopwatch.ElapsedMilliseconds));
        afterCapture?.Invoke();

        if (IsScreenBlockMode(settings))
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
                settings.OcrGranularity,
                cancellationToken);

            textBlocks = NormalizeTextBlocks(ocrResult.Blocks);
            normalizedText = IsScreenBlockMode(settings) && textBlocks.Count > 0
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
                IsScreenBlockMode(settings)
                    ? "OCR did not detect screen text. Select a clear UI panel and try Small Text or High Contrast Text."
                    : "OCR did not detect text. Make sure the selected region contains clear dialogue.",
                "not checked",
                "not checked",
                timings,
                cancellationToken);
        }

        if (IsScreenBlockMode(settings))
        {
            return await RunScreenBlockTranslationAsync(
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

    private async Task<PipelineResult> RunScreenBlockTranslationAsync(
        string panelText,
        IReadOnlyList<RecognizedTextBlock> recognizedBlocks,
        PipelineSettings settings,
        bool ocrWasSkipped,
        List<TimingEntry> timings,
        CancellationToken cancellationToken)
    {
        var isLensMode = IsLensMode(settings);
        var modeDisplayName = isLensMode ? "Lens Mode" : "Menu / Screen Mode";
        var blockDisplayName = isLensMode ? "block" : "line";
        var translationProvider = ResolveTranslationProvider(settings.TranslationProvider);
        var providerStatus = translationProvider.GetStatus();
        var translatedBlocks = PrepareScreenBlocksForTranslation(recognizedBlocks, settings);
        panelText = string.Join(Environment.NewLine, translatedBlocks.Select(block => block.Text));
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
                    ? $"{modeDisplayName} region is unchanged; reused OCR and translations."
                    : $"{modeDisplayName} text is unchanged; reused previous translations.",
                "skipped",
                providerStatus,
                timings,
                cancellationToken,
                CloneBlocks(_lastTranslatedBlocks));
        }

        var missingBlocksByText = new Dictionary<string, List<RecognizedTextBlock>>(StringComparer.Ordinal);
        var cachedTranslationsByText = new Dictionary<string, string>(StringComparer.Ordinal);
        var checkedTexts = new HashSet<string>(StringComparer.Ordinal);
        var cacheHits = 0;

        var cacheStopwatch = Stopwatch.StartNew();
        foreach (var block in translatedBlocks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(block.TranslatedText))
            {
                cachedTranslationsByText[block.Text] = block.TranslatedText;
                cacheHits++;
                continue;
            }

            if (cachedTranslationsByText.TryGetValue(block.Text, out var alreadyCachedTranslation))
            {
                block.TranslatedText = alreadyCachedTranslation;
                cacheHits++;
                continue;
            }

            if (checkedTexts.Contains(block.Text))
            {
                AddMissingBlock(missingBlocksByText, block);
                continue;
            }

            checkedTexts.Add(block.Text);
            var cachedTranslation = await _cacheRepository.GetAsync(
                block.Text,
                settings.SourceLanguage,
                settings.TargetLanguage,
                translationProvider.Id,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(cachedTranslation))
            {
                AddMissingBlock(missingBlocksByText, block);
            }
            else
            {
                block.TranslatedText = cachedTranslation;
                cachedTranslationsByText[block.Text] = cachedTranslation;
                cacheHits++;
            }
        }
        cacheStopwatch.Stop();
        timings.Add(new TimingEntry("cache lookup", cacheStopwatch.ElapsedMilliseconds));

        var cacheStatus = cacheHits == translatedBlocks.Count ? "hit" : cacheHits > 0 ? "partial hit" : "miss";
        if (missingBlocksByText.Count == 0)
        {
            timings.Add(new TimingEntry("cache hit", 0));
        }
        else
        {
            timings.Add(new TimingEntry("cache miss", 0));
            var representativeMissingBlocks = missingBlocksByText.Values
                .Select(blocks => blocks[0])
                .ToArray();
            var batch = BuildLineBatch(representativeMissingBlocks);
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
                if (isLensMode)
                {
                    _lastTranslationKey = null;
                    _lastTranslatedText = string.Empty;
                    _lastTranslatedBlocks = CloneBlocks(translatedBlocks);

                    return await CompleteAsync(
                        panelText,
                        string.Empty,
                        "Lens Mode requires per-block mapping, but the provider returned a merged translation. No inline overlay was shown.",
                        cacheStatus,
                        providerStatus,
                        timings,
                        cancellationToken,
                        translatedBlocks);
                }

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

            foreach (var representativeBlock in representativeMissingBlocks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!parsedTranslations.TryGetValue(representativeBlock.LineIndex, out var translatedLine) ||
                    string.IsNullOrWhiteSpace(translatedLine))
                {
                    translatedLine = string.Empty;
                }

                foreach (var block in missingBlocksByText[representativeBlock.Text])
                {
                    block.TranslatedText = translatedLine;
                }

                if (!string.IsNullOrWhiteSpace(translatedLine))
                {
                    await _cacheRepository.SaveAsync(
                        representativeBlock.Text,
                        settings.SourceLanguage,
                        settings.TargetLanguage,
                        translationProvider.Id,
                        translatedLine,
                        cancellationToken);
                }
            }
        }

        var translatedText = BuildScreenDisplayText(translatedBlocks);
        _lastTranslationKey = translationKey;
        _lastTranslatedText = translatedText;
        _lastTranslatedBlocks = CloneBlocks(translatedBlocks);

        var status = cacheStatus.Equals("hit", StringComparison.OrdinalIgnoreCase)
            ? $"Loaded {translatedBlocks.Count} {blockDisplayName}s from cache."
            : $"{modeDisplayName} translated {translatedBlocks.Count} detected {blockDisplayName}s in one batch.";

        return await CompleteAsync(
            panelText,
            translatedText,
            status,
            cacheStatus,
            cacheStatus.Equals("hit", StringComparison.OrdinalIgnoreCase)
                ? $"{providerStatus} ({blockDisplayName} cache hit)"
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

    private static bool IsLensMode(PipelineSettings settings)
    {
        return settings.TranslationMode.Equals(LensTranslationMode, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsScreenBlockMode(PipelineSettings settings)
    {
        return IsMenuMode(settings) || IsLensMode(settings);
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

    private static IReadOnlyList<RecognizedTextBlock> PrepareScreenBlocksForTranslation(
        IReadOnlyList<RecognizedTextBlock> blocks,
        PipelineSettings settings)
    {
        return blocks
            .Select(block =>
            {
                var text = PrepareScreenText(block.Text, settings);
                var translatedText = TryGetLocalScreenTranslation(text, settings, out var localTranslation)
                    ? localTranslation
                    : string.Empty;

                return new RecognizedTextBlock
                {
                    Text = text,
                    Bounds = block.Bounds,
                    LineIndex = block.LineIndex,
                    Confidence = block.Confidence,
                    TranslatedText = translatedText
                };
            })
            .Where(block => !string.IsNullOrWhiteSpace(block.Text))
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

    private static string PrepareScreenText(string text, PipelineSettings settings)
    {
        var normalized = TextNormalizer.Normalize(text);
        var key = NormalizeScreenLabelKey(normalized);
        if (DesktopLabelAliases.TryGetValue(key, out var canonicalText))
        {
            return canonicalText;
        }

        if (!IsDesktopIconMode(settings))
        {
            return normalized;
        }

        return normalized;
    }

    private static bool TryGetLocalScreenTranslation(
        string text,
        PipelineSettings settings,
        out string translatedText)
    {
        var key = NormalizeScreenLabelKey(text);
        if (settings.TargetLanguage.Equals("ar", StringComparison.OrdinalIgnoreCase) &&
            OfficialArabicScreenTranslations.TryGetValue(key, out var officialArabic))
        {
            translatedText = officialArabic;
            return true;
        }

        if (!settings.TranslateAppNames &&
            (KnownAppNameKeys.Contains(key) || IsDesktopIconMode(settings) || LooksLikeFileOrShortcutName(text)))
        {
            translatedText = text;
            return true;
        }

        translatedText = string.Empty;
        return false;
    }

    private static bool IsDesktopIconMode(PipelineSettings settings)
    {
        return settings.OcrGranularity.Equals(DesktopIconGranularity, StringComparison.OrdinalIgnoreCase) ||
            settings.OcrGranularity.Equals("desktop icon labels", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeScreenLabelKey(string text)
    {
        var lowered = TextNormalizer.Normalize(text).ToLowerInvariant();
        lowered = Regex.Replace(lowered, @"[^\p{L}\p{N}\s]+", " ");
        return Regex.Replace(lowered, @"\s+", " ").Trim();
    }

    private static bool LooksLikeFileOrShortcutName(string text)
    {
        var trimmed = text.Trim();
        if (Regex.IsMatch(trimmed, @"\.[A-Za-z0-9]{1,5}$"))
        {
            return true;
        }

        var key = NormalizeScreenLabelKey(trimmed);
        return key.Length is >= 2 and <= 24 &&
            Regex.IsMatch(trimmed, @"^[A-Z0-9][A-Z0-9 _.-]*$");
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

    private static void AddMissingBlock(
        IDictionary<string, List<RecognizedTextBlock>> missingBlocksByText,
        RecognizedTextBlock block)
    {
        if (!missingBlocksByText.TryGetValue(block.Text, out var blocks))
        {
            blocks = [];
            missingBlocksByText[block.Text] = blocks;
        }

        blocks.Add(block);
    }

    private static string BuildScreenDisplayText(IReadOnlyList<RecognizedTextBlock> blocks)
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
            settings.OcrPreset,
            settings.OcrGranularity);
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
            var path = Path.Combine(_debugCaptureDirectory, "last-screen-capture.png");
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
            settings.TranslationMode,
            settings.OcrGranularity,
            settings.TranslateAppNames);
    }
}
