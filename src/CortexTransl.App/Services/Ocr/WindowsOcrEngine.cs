using CortexTransl.App.Models;
using CortexTransl.App.Utils;
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
    public string Id => "windows";

    public async Task<OcrResult> RecognizeAsync(
        Bitmap bitmap,
        string sourceLanguage,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var preset = bitmap.Height < 140 ? "small-text" : "normal";
        using var preprocessed = OcrImagePreprocessor.Preprocess(bitmap, preset);
        using var softwareBitmap = await ToSoftwareBitmapAsync(preprocessed.Bitmap, cancellationToken);

        var engine = CreateEngine(sourceLanguage);
        if (engine is null)
        {
            return new OcrResult(string.Empty);
        }

        var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
        return ToOcrResult(result, preprocessed.Scale, bitmap.Width, bitmap.Height);
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
                : OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (ArgumentException)
        {
            return OcrEngine.TryCreateFromUserProfileLanguages();
        }
    }

    private static OcrResult ToOcrResult(Windows.Media.Ocr.OcrResult result, double scale, int width, int height)
    {
        var blocks = new List<OcrTextBlock>();
        var safeScale = scale <= 0 ? 1 : scale;

        foreach (var line in result.Lines)
        {
            var text = TextNormalizer.Normalize(line.Text);
            if (string.IsNullOrWhiteSpace(text) || line.Words.Count == 0)
            {
                continue;
            }

            var bounds = ToBounds(line.Words, safeScale, width, height);
            if (bounds.IsEmpty)
            {
                continue;
            }

            blocks.Add(new OcrTextBlock(text, bounds));
            if (blocks.Count >= 80)
            {
                break;
            }
        }

        var fullText = blocks.Count > 0
            ? string.Join(Environment.NewLine, blocks.Select(block => block.Text))
            : result.Text.Trim();

        return new OcrResult(fullText, blocks);
    }

    private static CaptureRegion ToBounds(
        IReadOnlyList<Windows.Media.Ocr.OcrWord> words,
        double scale,
        int width,
        int height)
    {
        var left = words.Min(word => word.BoundingRect.X);
        var top = words.Min(word => word.BoundingRect.Y);
        var right = words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
        var bottom = words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);

        var x = Clamp((int)Math.Floor(left / scale) - 2, 0, Math.Max(0, width - 1));
        var y = Clamp((int)Math.Floor(top / scale) - 2, 0, Math.Max(0, height - 1));
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
