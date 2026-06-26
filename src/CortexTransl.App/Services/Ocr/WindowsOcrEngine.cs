using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace CortexTransl.App.Services.Ocr;

public sealed class WindowsOcrEngine : IOcrEngine
{
    public string Id => "windows";

    public string DisplayName => "Windows OCR";

    public async Task<OcrResult> RecognizeAsync(Bitmap bitmap, string sourceLanguage, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var preprocessed = OcrImagePreprocessor.Preprocess(bitmap);
        using var resized = ResizeIfNeeded(preprocessed);
        var bitmapForOcr = resized ?? preprocessed;
        using var softwareBitmap = await ToSoftwareBitmapAsync(bitmapForOcr, cancellationToken);

        var engine = CreateEngine(sourceLanguage);
        if (engine is null)
        {
            return new OcrResult(string.Empty);
        }

        var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
        return new OcrResult(result.Text.Trim());
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

    private static Bitmap? ResizeIfNeeded(Bitmap bitmap)
    {
        var maxDimension = OcrEngine.MaxImageDimension;
        var largest = Math.Max(bitmap.Width, bitmap.Height);
        if (largest <= maxDimension)
        {
            return null;
        }

        var scale = (double)maxDimension / largest;
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        using var graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(bitmap, 0, 0, width, height);
        return resized;
    }

    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bitmap, CancellationToken cancellationToken)
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
}
