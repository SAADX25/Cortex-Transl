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

namespace CortexTransl.App.Services.Ocr;

public sealed class WindowsOcrEngine : IOcrEngine
{
    private string? _engineLanguage;
    private OcrEngine? _engine;
    private DateTimeOffset _nextRecoveryPass;
    private string? _recoveryFingerprint;
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
            using var preprocessed = OcrImagePreprocessor.Preprocess(bitmap, preset);
            using var softwareBitmap = await ToSoftwareBitmapAsync(preprocessed.Bitmap, cancellationToken);

            if (_engineLanguage != sourceLanguage || _engine is null)
            {
                _engine = CreateEngine(sourceLanguage);
                _engineLanguage = sourceLanguage;
            }
            var engine = _engine;
            if (engine is null)
            {
                throw new InvalidOperationException($"Windows OCR for {sourceLanguage} is unavailable. Install that Windows language's OCR feature.");
            }

            var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
            var recognized = ToOcrResult(result, preprocessed.Scale, bitmap.Width, bitmap.Height, sourceLanguage,
                captureKind.Equals("list", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(recognized.Text))
            {
                var fingerprint = ImageFingerprint.Compute(bitmap);
                // The throttle applies only to repeated failed images. A new short
                // line must get its own recovery passes immediately.
                if (_recoveryFingerprint != fingerprint || DateTimeOffset.UtcNow >= _nextRecoveryPass)
                {
                    _recoveryFingerprint = fingerprint;
                    _nextRecoveryPass = DateTimeOffset.UtcNow.AddSeconds(1);
                    using var recovery = OcrImagePreprocessor.Preprocess(bitmap, "reduced-text");
                    using var recoveryBitmap = await ToSoftwareBitmapAsync(recovery.Bitmap, cancellationToken);
                    var retry = await engine.RecognizeAsync(recoveryBitmap).AsTask(cancellationToken);
                    recognized = ToOcrResult(retry, recovery.Scale, bitmap.Width, bitmap.Height, sourceLanguage,
                        captureKind.Equals("list", StringComparison.OrdinalIgnoreCase), recovery.OffsetX, recovery.OffsetY);
                    if (string.IsNullOrWhiteSpace(recognized.Text))
                    {
                        using var enlarged = OcrImagePreprocessor.Preprocess(bitmap, "auto-recovery");
                        using var enlargedBitmap = await ToSoftwareBitmapAsync(enlarged.Bitmap, cancellationToken);
                        var lastAttempt = await engine.RecognizeAsync(enlargedBitmap).AsTask(cancellationToken);
                        recognized = ToOcrResult(lastAttempt, enlarged.Scale, bitmap.Width, bitmap.Height, sourceLanguage,
                            captureKind.Equals("list", StringComparison.OrdinalIgnoreCase), enlarged.OffsetX, enlarged.OffsetY);
                    }
                }
            }
            return recognized;
        }
        catch (OperationCanceledException)
        {
            throw;
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
        var left = words.Min(word => word.BoundingRect.X);
        var top = words.Min(word => word.BoundingRect.Y);
        var right = words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
        var bottom = words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);

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
        var softwareBitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            bitmap.Width,
            bitmap.Height,
            BitmapAlphaMode.Premultiplied);

        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            using var buffer = softwareBitmap.LockBuffer(BitmapBufferAccessMode.Write);
            using var reference = buffer.CreateReference();
            var access = reference.As<IMemoryBufferByteAccess>();
            access.GetBuffer(out var destination, out var capacity);

            var sourceStride = data.Stride;
            var destinationStride = buffer.GetPlaneDescription(0).Stride;
            var rowBytes = bitmap.Width * 4;
            var source = (byte*)data.Scan0;

            if (sourceStride == destinationStride && sourceStride > 0)
            {
                System.Buffer.MemoryCopy(source, destination, capacity, (long)sourceStride * bitmap.Height);
            }
            else
            {
                for (var y = 0; y < bitmap.Height; y++)
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
            bitmap.UnlockBits(data);
        }

        return softwareBitmap;
    }

    private static async Task<SoftwareBitmap> CopyViaDecoderAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        await using var memoryStream = new MemoryStream();
        bitmap.Save(memoryStream, ImageFormat.Bmp);
        var bytes = memoryStream.ToArray();

        using var randomAccessStream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(randomAccessStream.GetOutputStreamAt(0)))
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
