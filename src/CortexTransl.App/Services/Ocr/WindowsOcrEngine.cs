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
        string ocrGranularity,
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
        var blocks = CreateTextBlocks(result, scaleForBounds, ocrGranularity, bitmap.Width, bitmap.Height);
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

    private static IReadOnlyList<RecognizedTextBlock> CreateTextBlocks(
        Windows.Media.Ocr.OcrResult result,
        double scaleForBounds,
        string ocrGranularity,
        int sourceWidth,
        int sourceHeight)
    {
        return NormalizeGranularity(ocrGranularity) switch
        {
            "desktop-icons" => CreateDesktopIconBlocks(result, scaleForBounds, sourceWidth, sourceHeight),
            "word-label" => CreateWordBlocks(result, scaleForBounds),
            "small-ui-text" => CreateSmallUiTextBlocks(result, scaleForBounds),
            _ => CreateLineBlocks(result, scaleForBounds)
        };
    }

    private static IReadOnlyList<RecognizedTextBlock> CreateLineBlocks(
        Windows.Media.Ocr.OcrResult result,
        double scaleForBounds)
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

        return blocks;
    }

    private static IReadOnlyList<RecognizedTextBlock> CreateSmallUiTextBlocks(
        Windows.Media.Ocr.OcrResult result,
        double scaleForBounds)
    {
        var blocks = new List<RecognizedTextBlock>();

        foreach (var line in result.Lines)
        {
            var words = line.Words
                .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                .OrderBy(word => word.BoundingRect.X)
                .ToArray();

            if (words.Length == 0)
            {
                continue;
            }

            var cluster = new List<OcrWord>();
            foreach (var word in words)
            {
                if (cluster.Count == 0 || ShouldJoinUiWords(cluster[^1], word, scaleForBounds))
                {
                    cluster.Add(word);
                    continue;
                }

                AddWordCluster(blocks, cluster, scaleForBounds);
                cluster.Clear();
                cluster.Add(word);
            }

            AddWordCluster(blocks, cluster, scaleForBounds);
        }

        return ReindexBlocks(blocks);
    }

    private static IReadOnlyList<RecognizedTextBlock> CreateDesktopIconBlocks(
        Windows.Media.Ocr.OcrResult result,
        double scaleForBounds,
        int sourceWidth,
        int sourceHeight)
    {
        var rawBlocks = CreateLineBlocks(result, scaleForBounds)
            .Where(block => !string.IsNullOrWhiteSpace(block.Text))
            .OrderBy(block => block.Bounds.Y)
            .ThenBy(block => block.Bounds.X)
            .ToArray();

        var groups = new List<List<RecognizedTextBlock>>();
        foreach (var block in rawBlocks)
        {
            var matchingGroup = groups
                .Where(group => ShouldJoinDesktopIconLine(group[^1], block))
                .OrderBy(group => Math.Abs(GetCenterX(group[^1].Bounds) - GetCenterX(block.Bounds)))
                .FirstOrDefault();

            if (matchingGroup is null)
            {
                groups.Add([block]);
            }
            else
            {
                matchingGroup.Add(block);
            }
        }

        var merged = new List<RecognizedTextBlock>();
        foreach (var group in groups)
        {
            var ordered = group
                .OrderBy(block => block.Bounds.Y)
                .ThenBy(block => block.Bounds.X)
                .ToArray();
            var text = string.Join(" ", ordered.Select(block => block.Text.Trim()));
            var bounds = ordered
                .Select(block => block.Bounds)
                .Aggregate(Union);
            bounds = ExpandBounds(bounds, 8, sourceWidth, sourceHeight);

            merged.Add(new RecognizedTextBlock
            {
                Text = text,
                Bounds = bounds
            });
        }

        return ReindexBlocks(merged);
    }

    private static IReadOnlyList<RecognizedTextBlock> CreateWordBlocks(
        Windows.Media.Ocr.OcrResult result,
        double scaleForBounds)
    {
        var blocks = new List<RecognizedTextBlock>();

        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                var text = word.Text.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var bounds = ToCaptureRegion(word.BoundingRect, scaleForBounds);
                if (bounds.IsEmpty || bounds.Width < 3 || bounds.Height < 3)
                {
                    continue;
                }

                blocks.Add(new RecognizedTextBlock
                {
                    Text = text,
                    Bounds = bounds
                });
            }
        }

        return ReindexBlocks(blocks);
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

        return ToCaptureRegion(bounds.Value, scaleForBounds);
    }

    private static Windows.Foundation.Rect Union(Windows.Foundation.Rect first, Windows.Foundation.Rect second)
    {
        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);
        var right = Math.Max(first.X + first.Width, second.X + second.Width);
        var bottom = Math.Max(first.Y + first.Height, second.Y + second.Height);

        return new Windows.Foundation.Rect(left, top, right - left, bottom - top);
    }

    private static void AddWordCluster(
        ICollection<RecognizedTextBlock> blocks,
        IReadOnlyList<OcrWord> words,
        double scaleForBounds)
    {
        if (words.Count == 0)
        {
            return;
        }

        var text = string.Join(" ", words.Select(word => word.Text.Trim()).Where(text => text.Length > 0));
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Windows.Foundation.Rect? bounds = null;
        foreach (var word in words)
        {
            bounds = bounds is null
                ? word.BoundingRect
                : Union(bounds.Value, word.BoundingRect);
        }

        if (bounds is null)
        {
            return;
        }

        var captureBounds = ToCaptureRegion(bounds.Value, scaleForBounds);
        if (captureBounds.IsEmpty || captureBounds.Width < 3 || captureBounds.Height < 3)
        {
            return;
        }

        blocks.Add(new RecognizedTextBlock
        {
            Text = text,
            Bounds = captureBounds
        });
    }

    private static bool ShouldJoinUiWords(OcrWord previous, OcrWord next, double scaleForBounds)
    {
        var gap = next.BoundingRect.X - (previous.BoundingRect.X + previous.BoundingRect.Width);
        if (gap < 0)
        {
            return true;
        }

        var averageHeight = (previous.BoundingRect.Height + next.BoundingRect.Height) / 2.0;
        var maximumUiLabelGap = Math.Max(18 * scaleForBounds, averageHeight * 1.45);
        return gap <= maximumUiLabelGap;
    }

    private static CaptureRegion ToCaptureRegion(Windows.Foundation.Rect bounds, double scaleForBounds)
    {
        var left = Math.Max(0, (int)Math.Floor(bounds.X / scaleForBounds));
        var top = Math.Max(0, (int)Math.Floor(bounds.Y / scaleForBounds));
        var right = Math.Max(left + 1, (int)Math.Ceiling((bounds.X + bounds.Width) / scaleForBounds));
        var bottom = Math.Max(top + 1, (int)Math.Ceiling((bounds.Y + bounds.Height) / scaleForBounds));

        return new CaptureRegion(left, top, right - left, bottom - top);
    }

    private static IReadOnlyList<RecognizedTextBlock> ReindexBlocks(IEnumerable<RecognizedTextBlock> blocks)
    {
        return blocks
            .OrderBy(block => block.Bounds.Y)
            .ThenBy(block => block.Bounds.X)
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

    private static string NormalizeGranularity(string? granularity)
    {
        return string.IsNullOrWhiteSpace(granularity)
            ? "line"
            : granularity.Trim().ToLowerInvariant() switch
            {
                "small ui text mode" => "small-ui-text",
                "small-ui-text" => "small-ui-text",
                "small-ui" => "small-ui-text",
                "desktop icon labels" => "desktop-icons",
                "desktop-icons" => "desktop-icons",
                "desktop" => "desktop-icons",
                "word/label mode" => "word-label",
                "word-label" => "word-label",
                "word" => "word-label",
                _ => "line"
            };
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

    private static bool ShouldJoinDesktopIconLine(RecognizedTextBlock previous, RecognizedTextBlock next)
    {
        var verticalGap = next.Bounds.Y - (previous.Bounds.Y + previous.Bounds.Height);
        if (verticalGap is < -2 or > 14)
        {
            return false;
        }

        var centerDelta = Math.Abs(GetCenterX(previous.Bounds) - GetCenterX(next.Bounds));
        var centerTolerance = Math.Max(18, Math.Max(previous.Bounds.Width, next.Bounds.Width) * 0.58);
        if (centerDelta > centerTolerance)
        {
            return false;
        }

        var overlap = Math.Min(previous.Bounds.X + previous.Bounds.Width, next.Bounds.X + next.Bounds.Width) -
            Math.Max(previous.Bounds.X, next.Bounds.X);
        var minimumWidth = Math.Max(1, Math.Min(previous.Bounds.Width, next.Bounds.Width));

        return overlap > 0 || centerDelta <= Math.Max(12, minimumWidth * 0.75);
    }

    private static double GetCenterX(CaptureRegion region)
    {
        return region.X + (region.Width / 2.0);
    }

    private static CaptureRegion ExpandBounds(CaptureRegion bounds, int padding, int sourceWidth, int sourceHeight)
    {
        var left = Math.Max(0, bounds.X - padding);
        var top = Math.Max(0, bounds.Y - padding);
        var right = Math.Min(sourceWidth, bounds.X + bounds.Width + padding);
        var bottom = Math.Min(sourceHeight, bounds.Y + bounds.Height + padding);

        return new CaptureRegion(
            left,
            top,
            Math.Max(1, right - left),
            Math.Max(1, bottom - top));
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
