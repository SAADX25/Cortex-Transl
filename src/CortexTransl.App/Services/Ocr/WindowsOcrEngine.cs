using CortexTransl.App.Models;
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

    public async Task<OcrResult> RecognizeAsync(
        Bitmap bitmap,
        string sourceLanguage,
        string ocrPreset,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var preprocessed = OcrImagePreprocessor.Preprocess(bitmap, ocrPreset);
        using var resized = ResizeIfNeeded(preprocessed.Bitmap);
        var bitmapForOcr = resized.Bitmap ?? preprocessed.Bitmap;
        var scaleForBounds = preprocessed.Scale * resized.Scale;
        using var softwareBitmap = await ToSoftwareBitmapAsync(bitmapForOcr, cancellationToken);

        var engine = CreateEngine(sourceLanguage);
        if (engine is null)
        {
            return new OcrResult(string.Empty);
        }

        var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);
        var blocks = CreateTextBlocks(result, scaleForBounds);
        return new OcrResult(result.Text.Trim(), blocks);
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

    private static ResizedBitmap ResizeIfNeeded(Bitmap bitmap)
    {
        var maxDimension = OcrEngine.MaxImageDimension;
        var largest = Math.Max(bitmap.Width, bitmap.Height);
        if (largest <= maxDimension)
        {
            return new ResizedBitmap(null, 1.0);
        }

        var scale = (double)maxDimension / largest;
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));
        var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        using var graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(bitmap, 0, 0, width, height);
        return new ResizedBitmap(resized, scale);
    }

    private static IReadOnlyList<RecognizedTextBlock> CreateTextBlocks(Windows.Media.Ocr.OcrResult result, double scaleForBounds)
    {
        var blocks = new List<RecognizedTextBlock>();
        var lineIndex = 1;

        foreach (var line in result.Lines)
        {
            var text = line.Text.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var bounds = GetLineBounds(line, scaleForBounds);
            if (bounds.IsEmpty || bounds.Width < 3 || bounds.Height < 3)
            {
                continue;
            }

            blocks.Add(new RecognizedTextBlock
            {
                Text = text,
                Bounds = bounds,
                LineIndex = lineIndex
            });
            lineIndex++;
        }

        return MergeCloseBlocks(blocks);
    }

    private static CaptureRegion GetLineBounds(OcrLine line, double scaleForBounds)
    {
        Windows.Foundation.Rect? bounds = null;

        foreach (var word in line.Words)
        {
            bounds = bounds is null
                ? word.BoundingRect
                : Union(bounds.Value, word.BoundingRect);
        }

        if (bounds is null)
        {
            return CaptureRegion.Empty;
        }

        var left = Math.Max(0, (int)Math.Floor(bounds.Value.X / scaleForBounds));
        var top = Math.Max(0, (int)Math.Floor(bounds.Value.Y / scaleForBounds));
        var right = Math.Max(left + 1, (int)Math.Ceiling((bounds.Value.X + bounds.Value.Width) / scaleForBounds));
        var bottom = Math.Max(top + 1, (int)Math.Ceiling((bounds.Value.Y + bounds.Value.Height) / scaleForBounds));

        return new CaptureRegion(left, top, right - left, bottom - top);
    }

    private static Windows.Foundation.Rect Union(Windows.Foundation.Rect first, Windows.Foundation.Rect second)
    {
        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);
        var right = Math.Max(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Max(first.Y + first.Height, second.Y + second.Height);

        return new Windows.Foundation.Rect(left, top, right - left, bottom - top);
    }

    private static IReadOnlyList<RecognizedTextBlock> MergeCloseBlocks(IReadOnlyList<RecognizedTextBlock> blocks)
    {
        var sorted = blocks
            .OrderBy(block => block.Bounds.Y)
            .ThenBy(block => block.Bounds.X)
            .ToList();

        var merged = new List<RecognizedTextBlock>();
        foreach (var block in sorted)
        {
            var previous = merged.LastOrDefault();
            if (previous is not null && IsSameVisualRow(previous, block))
            {
                merged[^1] = new RecognizedTextBlock
                {
                    Text = $"{previous.Text} {block.Text}",
                    Bounds = Union(previous.Bounds, block.Bounds),
                    LineIndex = previous.LineIndex,
                    TranslatedText = previous.TranslatedText
                };
            }
            else
            {
                merged.Add(new RecognizedTextBlock
                {
                    Text = block.Text,
                    Bounds = block.Bounds,
                    LineIndex = merged.Count + 1,
                    Confidence = block.Confidence,
                    TranslatedText = block.TranslatedText
                });
            }
        }

        return merged;
    }

    private static bool IsSameVisualRow(RecognizedTextBlock first, RecognizedTextBlock second)
    {
        var firstCenter = first.Bounds.Y + (first.Bounds.Height / 2.0);
        var secondCenter = second.Bounds.Y + (second.Bounds.Height / 2.0);
        var centerDelta = Math.Abs(firstCenter - secondCenter);
        var tolerance = Math.Max(4, Math.Min(first.Bounds.Height, second.Bounds.Height) * 0.35);
        var horizontalGap = second.Bounds.X - (first.Bounds.X + first.Bounds.Width);

        return centerDelta <= tolerance && horizontalGap is >= 0 and <= 24;
    }

    private static CaptureRegion Union(CaptureRegion first, CaptureRegion second)
    {
        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);
        var right = Math.Max(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Max(first.Y + first.Height, second.Y + second.Height);

        return new CaptureRegion(left, top, right - left, bottom - top);
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

    private sealed record ResizedBitmap(Bitmap? Bitmap, double Scale) : IDisposable
    {
        public void Dispose()
        {
            Bitmap?.Dispose();
        }
    }
}
