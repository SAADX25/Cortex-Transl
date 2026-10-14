using CortexTransl.App.Models;
using CortexTransl.App.Utils;
using CortexTransl.App.Services.Capture;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WinRT;
using System.Diagnostics;
using CortexTransl.App.Services.Diagnostics;

namespace CortexTransl.App.Services.Ocr;

public sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly PerformanceDiagnostics _diagnostics;
    private readonly Dictionary<string, OcrEngine> _engines = new(StringComparer.OrdinalIgnoreCase);
    private int _emptyReadings;
    private long _lastRecoveryAt;
    private SubtitleVisualSignature.Signature? _failedSignature;
    public WindowsOcrEngine(PerformanceDiagnostics? diagnostics = null) { _diagnostics = diagnostics ?? new(); }
    private string? _engineLanguage;
    private OcrEngine? _engine;
    public string Id => "windows";

    public async Task<OcrResult> RecognizeAsync(
        Bitmap bitmap,
        string sourceLanguage,
        CancellationToken cancellationToken = default,
        string captureKind = "dialogue")
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var useSmallText = captureKind.Equals("list", StringComparison.OrdinalIgnoreCase)
                || bitmap.Height < 420
                || bitmap.Width < 980;
            // Windows OCR can lose two-letter words after polarity inversion or
            // unnecessary enlargement. Start with original pixels, then adapt
            // only when recognition is empty.
            var preset = captureKind.Equals("list", StringComparison.OrdinalIgnoreCase) && useSmallText ? "small-text" : "original";
            OcrPreprocessedImage? prepared;
            SoftwareBitmap software;
            using (_diagnostics.Measure(LatencyStage.Preprocess))
            {
                prepared = preset == "original" && Math.Max(bitmap.Width, bitmap.Height) <= OcrEngine.MaxImageDimension
                    ? null : OcrImagePreprocessor.Preprocess(bitmap, preset);
                try { software = await ToSoftwareBitmapAsync(prepared?.Bitmap ?? bitmap, cancellationToken); }
                catch { prepared?.Dispose(); throw; }
            }
            using var preprocessed = prepared;
            using var softwareBitmap = software;

            if (_engineLanguage != sourceLanguage || _engine is null)
            {
                _emptyReadings = 0; _failedSignature = null; _lastRecoveryAt = 0;
                if (!_engines.TryGetValue(sourceLanguage, out _engine))
                {
                    _engine = CreateEngine(sourceLanguage);
                    if (_engine is not null)
                    {
                        if (_engines.Count == 4) _engines.Remove(_engines.Keys.First());
                        _engines[sourceLanguage] = _engine;
                    }
                }
                _engineLanguage = sourceLanguage;
            }
            var engine = _engine;
            if (engine is null)
            {
                throw new InvalidOperationException($"Windows OCR for {sourceLanguage} is unavailable. Install that Windows language's OCR feature.");
            }

            Windows.Media.Ocr.OcrResult result;
            using (_diagnostics.Measure(LatencyStage.WindowsOcr))
                result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
            var recognized = ToOcrResult(result, preprocessed?.Scale ?? 1, bitmap.Width, bitmap.Height, sourceLanguage,
                captureKind.Equals("list", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(recognized.Text))
            {
                _emptyReadings++;
                var signature = SubtitleVisualSignature.Analyze(bitmap);
                var now = Stopwatch.GetTimestamp();
                var elapsed = _lastRecoveryAt == 0 ? double.MaxValue : Stopwatch.GetElapsedTime(_lastRecoveryAt, now).TotalMilliseconds;
                var cueChanged = _failedSignature is null ||
                    Math.Abs(signature.LightPixels - _failedSignature.Value.LightPixels) + Math.Abs(signature.DarkPixels - _failedSignature.Value.DarkPixels)
                    >= Math.Max(32, bitmap.Width * (double)bitmap.Height * .0005);
                var recoveryInterval = Math.Min(1000, 125 * (1 << Math.Min(Math.Max(_emptyReadings - 2, 0), 3)));
                // The throttle applies only to repeated failed images. A new short
                // line must get its own recovery passes immediately.
                if (_emptyReadings <= 2 || elapsed >= recoveryInterval || (cueChanged && elapsed >= 80))
                {
                    _lastRecoveryAt = now; _failedSignature = signature;
                    using var recoveryTiming = _diagnostics.Measure(LatencyStage.OcrRecovery);
                    recognized = await RecognizeRecoveryAsync(engine, bitmap, "reduced-text", sourceLanguage, captureKind, cancellationToken);
                    if (string.IsNullOrWhiteSpace(recognized.Text) && signature.LightPixels >= 16)
                    {
                        // Outline/glow can confuse native segmentation even when
                        // resized. Isolate bright strokes without changing words.
                        recognized = await RecognizeRecoveryAsync(engine, bitmap, "caption-strokes", sourceLanguage, captureKind, cancellationToken);
                    }
                    if (string.IsNullOrWhiteSpace(recognized.Text))
                        recognized = await RecognizeRecoveryAsync(engine, bitmap, "auto-recovery", sourceLanguage, captureKind, cancellationToken);
                }
            }
            if (!string.IsNullOrWhiteSpace(recognized.Text)) { _emptyReadings = 0; _failedSignature = null; }
            return recognized;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private async Task<OcrResult> RecognizeRecoveryAsync(OcrEngine engine, Bitmap bitmap, string preset,
        string language, string kind, CancellationToken token)
    {
        OcrPreprocessedImage image;
        SoftwareBitmap software;
        using (_diagnostics.Measure(LatencyStage.Preprocess))
        {
            image = OcrImagePreprocessor.Preprocess(bitmap, preset);
            try { software = await ToSoftwareBitmapAsync(image.Bitmap, token); }
            catch { image.Dispose(); throw; }
        }
        using (image)
        using (software)
        {
            Windows.Media.Ocr.OcrResult result;
            using (_diagnostics.Measure(LatencyStage.WindowsOcr)) result = await engine.RecognizeAsync(software).AsTask(token);
            return ToOcrResult(result, image.Scale, bitmap.Width, bitmap.Height, language, kind == "list", image.OffsetX, image.OffsetY);
        }
    }

    private static OcrEngine? CreateEngine(string sourceLanguage)
    {
        if (sourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return OcrEngine.TryCreateFromUserProfileLanguages();
        }

        try
        {
            var language = new Language(sourceLanguage);
            return OcrEngine.IsLanguageSupported(language)
                ? OcrEngine.TryCreateFromLanguage(language)
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static OcrResult ToOcrResult(
        Windows.Media.Ocr.OcrResult result,
        double scale,
        int width,
        int height,
        string sourceLanguage,
        bool listMode,
        int offsetX = 0,
        int offsetY = 0)
    {
        var blocks = new List<OcrTextBlock>();
        var safeScale = scale <= 0 ? 1 : scale;

        foreach (var line in result.Lines)
        {
            var text = TextNormalizer.Normalize(line.Text);
            if (!DialogueTextAssembler.IsUsefulLine(text) || line.Words.Count == 0)
            {
                continue;
            }

            var bounds = ToBounds(line.Words, safeScale, width, height, offsetX, offsetY);
            if (bounds.IsEmpty)
            {
                continue;
            }

            blocks.Add(new OcrTextBlock(text, bounds));
            if (blocks.Count >= 200)
            {
                break;
            }
        }

        IReadOnlyList<OcrTextBlock> finalBlocks = listMode
            && (sourceLanguage.Equals("en", StringComparison.OrdinalIgnoreCase) || sourceLanguage.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
                ? MenuLabelAssembler.MergeKnownWrappedLabels(blocks)
                : blocks;
        var assembled = new OcrResult(
            string.Join(Environment.NewLine, finalBlocks.Select(block => block.Text)),
            finalBlocks);
        var fullText = DialogueTextAssembler.Assemble(assembled, sourceLanguage);
        return new OcrResult(fullText, finalBlocks);
    }

    private static CaptureRegion ToBounds(
        IReadOnlyList<Windows.Media.Ocr.OcrWord> words,
        double scale,
        int width,
        int height,
        int offsetX,
        int offsetY)
    {
        var left = double.MaxValue; var top = double.MaxValue;
        var right = double.MinValue; var bottom = double.MinValue;
        foreach (var word in words)
        {
            var bounds = word.BoundingRect;
            left = Math.Min(left, bounds.X); top = Math.Min(top, bounds.Y);
            right = Math.Max(right, bounds.X + bounds.Width); bottom = Math.Max(bottom, bounds.Y + bounds.Height);
        }

        var x = Clamp((int)Math.Floor(left / scale) + offsetX - 2, 0, Math.Max(0, width - 1));
        var y = Clamp((int)Math.Floor(top / scale) + offsetY - 2, 0, Math.Max(0, height - 1));
        var blockWidth = Clamp((int)Math.Ceiling((right - left) / scale) + 4, 8, width - x);
        var blockHeight = Clamp((int)Math.Ceiling((bottom - top) / scale) + 4, 10, height - y);
        return new CaptureRegion(x, y, blockWidth, blockHeight);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (max < min)
        {
            return min;
        }

        return Math.Min(max, Math.Max(min, value));
    }

    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return CopyPixelsDirect(bitmap);
        }
        catch (Exception ex) when (ex is InvalidCastException or NotImplementedException or COMException)
        {
            return await CopyViaDecoderAsync(bitmap, cancellationToken);
        }
    }

    private static unsafe SoftwareBitmap CopyPixelsDirect(Bitmap bitmap)
    {
        var width = bitmap.Width; var height = bitmap.Height;
        var softwareBitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            width,
            height,
            BitmapAlphaMode.Premultiplied);

        BitmapData? data = null;
        try
        {
            data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            using var buffer = softwareBitmap.LockBuffer(BitmapBufferAccessMode.Write);
            using var reference = buffer.CreateReference();
            var access = reference.As<IMemoryBufferByteAccess>();
            access.GetBuffer(out var destination, out var capacity);

            var sourceStride = data.Stride;
            var plane = buffer.GetPlaneDescription(0);
            var destinationStride = plane.Stride;
            var rowBytes = checked(width * 4);
            var requiredBytes = (long)plane.StartIndex + (long)(height - 1) * destinationStride + rowBytes;
            if (plane.StartIndex < 0 || destinationStride < rowBytes || requiredBytes > capacity)
                throw new COMException("OCR bitmap buffer has an unsupported layout.");
            destination += plane.StartIndex;
            var source = (byte*)data.Scan0;

            if (sourceStride == destinationStride && sourceStride == rowBytes)
            {
                System.Buffer.MemoryCopy(source, destination, capacity - plane.StartIndex, (long)rowBytes * height);
            }
            else
            {
                for (var y = 0; y < height; y++)
                {
                    System.Buffer.MemoryCopy(
                        source + (y * sourceStride),
                        destination + (y * destinationStride),
                        rowBytes,
                        rowBytes);
                }
            }
        }
        catch
        {
            softwareBitmap.Dispose();
            throw;
        }
        finally
        {
            if (data is not null) bitmap.UnlockBits(data);
        }

        return softwareBitmap;
    }

    private static async Task<SoftwareBitmap> CopyViaDecoderAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        await using var memoryStream = new MemoryStream();
        bitmap.Save(memoryStream, ImageFormat.Bmp);
        var bytes = memoryStream.ToArray();

        using var randomAccessStream = new InMemoryRandomAccessStream();
        using var output = randomAccessStream.GetOutputStreamAt(0);
        using (var writer = new DataWriter(output))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync().AsTask(cancellationToken);
            await writer.FlushAsync().AsTask(cancellationToken);
            writer.DetachStream();
        }

        randomAccessStream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(randomAccessStream).AsTask(cancellationToken);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask(cancellationToken);
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
