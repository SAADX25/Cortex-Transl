using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Buffers;
using Windows.Media.Ocr;

namespace CortexTransl.App.Services.Ocr;

public static class OcrImagePreprocessor
{
    private const int TargetSmallTextHeight = 180;
    private const int TargetSmallTextWidth = 480;

    public static OcrPreprocessedImage Preprocess(Bitmap source, string preset)
    {
        var options = preset is "auto" or "auto-contrast" or "auto-recovery"
            ? SelectAdaptiveOptions(source, preset == "auto-contrast")
            : OcrPreprocessingOptions.FromPreset(preset);
        if (preset == "auto-recovery") options = options with { MinimumScale = 2.0, MaximumScale = 3.0 };
        var crop = preset == "auto-recovery" ? FindUniformBackgroundTextBounds(source) : new Rectangle(0, 0, source.Width, source.Height);
        var scaled = ScaleForOcr(source, options, crop);

        try
        {
            if (options.Grayscale || options.ContrastFactor != 1.0 || options.Threshold || options.Invert)
                EnhanceForOcr(scaled.Bitmap, options);
            if (options.Sharpen) Sharpen(scaled.Bitmap);
            if (preset == "caption-strokes") SuppressFullHeightBrightBars(scaled.Bitmap);
            return scaled;
        }
        catch { scaled.Dispose(); throw; }
    }

    private static unsafe void SuppressFullHeightBrightBars(Bitmap bitmap)
    {
        // After bright-stroke inversion, almost-full-height black columns are
        // scene bars, not subtitle-height glyphs. This recovery leaves smaller
        // strokes, apostrophes and punctuation intact.
        var width = bitmap.Width; var height = bitmap.Height;
        var counts = ArrayPool<int>.Shared.Rent(width);
        counts.AsSpan(0, width).Clear();
        BitmapData? data = null;
        try
        {
            data = bitmap.LockBits(new(0, 0, width, height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            for (var y = 0; y < height; y++)
            {
                var row = (byte*)data.Scan0 + y * data.Stride;
                for (var x = 0; x < width; x++) if (row[x * 4] == 0) counts[x]++;
            }
            for (var y = 0; y < height; y++)
            {
                var row = (byte*)data.Scan0 + y * data.Stride;
                for (var x = 0; x < width; x++)
                {
                    if (counts[x] < height * .85) continue;
                    var pixel = row + x * 4;
                    pixel[0] = pixel[1] = pixel[2] = 255;
                }
            }
        }
        finally
        {
            if (data is not null) bitmap.UnlockBits(data);
            ArrayPool<int>.Shared.Return(counts);
        }
    }

    private static unsafe Rectangle FindUniformBackgroundTextBounds(Bitmap source)
    {
        var width = source.Width; var height = source.Height;
        var full = new Rectangle(0, 0, width, height);
        if (width < 32 || height < 24) return full;
        var left = width; var top = height; var right = -1; var bottom = -1;
        var data = source.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var background = (byte*)data.Scan0;
            var blue = background[0]; var green = background[1]; var red = background[2];
            // Validate all four borders before cropping, not just top/bottom.
            for (var index = 0; index < 32; index++)
            {
                var x = index * (width - 1) / 31;
                var y = index * (height - 1) / 31;
                if (Different(background + x * 4, red, green, blue, 48)
                    || Different(background + (height - 1) * data.Stride + x * 4, red, green, blue, 48)
                    || Different(background + y * data.Stride, red, green, blue, 48)
                    || Different(background + y * data.Stride + (width - 1) * 4, red, green, blue, 48)) return full;
            }
            for (var y = 0; y < height; y++)
            {
                var row = (byte*)data.Scan0 + y * data.Stride;
                for (var x = 0; x < width; x++)
                {
                    var pixel = row + x * 4;
                    if (!Different(pixel, red, green, blue, 96)) continue;
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
            }
        }
        finally { source.UnlockBits(data); }
        if (right - left < 2 || bottom - top < 2) return full;
        var bounds = Rectangle.FromLTRB(Math.Max(0, left - 16), Math.Max(0, top - 16),
            Math.Min(width, right + 17), Math.Min(height, bottom + 17));
        return (long)bounds.Width * bounds.Height < (long)width * height * 3 / 4 ? bounds : full;
    }

    private static unsafe bool Different(byte* pixel, int red, int green, int blue, int threshold) =>
        Math.Abs(pixel[2] - red) + Math.Abs(pixel[1] - green) + Math.Abs(pixel[0] - blue) > threshold;

    private static unsafe OcrPreprocessingOptions SelectAdaptiveOptions(Bitmap source, bool threshold)
    {
        // A bounded luminance histogram selects polarity and an Otsu threshold.
        // Soft grayscale is the first pass; thresholding is a recovery pass only.
        Span<int> histogram = stackalloc int[256];
        histogram.Clear();
        var total = 0;
        long sum = 0;
        var width = source.Width; var height = source.Height;
        var data = source.LockBits(new(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stepY = Math.Max(1, height / 16); var stepX = Math.Max(1, width / 32);
            for (var y = 0; y < height; y += stepY)
            {
                var row = (byte*)data.Scan0 + y * data.Stride;
                for (var x = 0; x < width; x += stepX)
                {
                    var pixel = row + x * 4;
                    var luma = (pixel[2] * 77 + pixel[1] * 150 + pixel[0] * 29) >> 8;
                    histogram[luma]++; total++; sum += luma;
                }
            }
        }
        finally { source.UnlockBits(data); }
        long backgroundSum = 0;
        var backgroundWeight = 0;
        var bestVariance = -1.0;
        byte cutoff = 128;
        for (var level = 0; level < 255; level++)
        {
            backgroundWeight += histogram[level];
            backgroundSum += level * histogram[level];
            var foregroundWeight = total - backgroundWeight;
            if (backgroundWeight == 0 || foregroundWeight == 0) continue;
            var difference = (double)backgroundSum / backgroundWeight - (double)(sum - backgroundSum) / foregroundWeight;
            var variance = (double)backgroundWeight * foregroundWeight * difference * difference;
            if (variance > bestVariance) { bestVariance = variance; cutoff = (byte)level; }
        }
        return new("Adaptive subtitles", 1.0, 2.5, threshold ? 1.0 : 1.15,
            true, false, threshold, cutoff, true, Invert: total > 0 && sum / total < 110);
    }

    private static OcrPreprocessedImage ScaleForOcr(Bitmap source, OcrPreprocessingOptions options, Rectangle crop)
    {
        var scale = options.MinimumScale;

        if (options.UseSmallTextTargets)
        {
            if (crop.Height < TargetSmallTextHeight)
            {
                scale = Math.Max(scale, (double)TargetSmallTextHeight / crop.Height);
            }

            if (crop.Width < TargetSmallTextWidth && crop.Height < TargetSmallTextHeight * 2)
            {
                scale = Math.Max(scale, (double)TargetSmallTextWidth / crop.Width);
            }
        }

        scale = Math.Min(scale, options.MaximumScale);

        var largest = Math.Max(crop.Width, crop.Height);
        if (largest > 0)
        {
            scale = Math.Min(scale, (double)OcrEngine.MaxImageDimension / largest);
        }

        var width = Math.Max(1, (int)Math.Round(crop.Width * scale));
        var height = Math.Max(1, (int)Math.Round(crop.Height * scale));
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        try
        {
            using var graphics = Graphics.FromImage(result);
            graphics.Clear(Color.White);
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = scale > 1
                ? InterpolationMode.HighQualityBilinear
                : InterpolationMode.NearestNeighbor;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (options.Name == "Reduced text")
            {
                graphics.InterpolationMode = InterpolationMode.Bilinear;
                graphics.PixelOffsetMode = PixelOffsetMode.Default;
                graphics.CompositingQuality = CompositingQuality.Default;
                graphics.SmoothingMode = SmoothingMode.None;
            }
            if (crop == new Rectangle(0, 0, source.Width, source.Height) && width == crop.Width && height == crop.Height)
                graphics.DrawImageUnscaled(source, 0, 0);
            else graphics.DrawImage(source, new Rectangle(0, 0, width, height), crop, GraphicsUnit.Pixel);

            return new OcrPreprocessedImage(result, scale, crop.X, crop.Y);
        }
        catch { result.Dispose(); throw; }
    }

    private static unsafe void EnhanceForOcr(Bitmap bitmap, OcrPreprocessingOptions options)
    {
        var width = bitmap.Width; var height = bitmap.Height;
        var captionStrokes = options.Name == "Bright caption strokes";
        Span<byte> lookup = stackalloc byte[256];
        for (var level = 0; level < 256; level++)
        {
            var enhanced = ClampToByte(128 + ((level - 128) * options.ContrastFactor));
            if (options.Threshold) enhanced = enhanced > options.ThresholdValue ? (byte)255 : (byte)0;
            else if (enhanced > 246) enhanced = 255;
            else if (enhanced < 10) enhanced = 0;
            lookup[level] = options.Invert ? (byte)(255 - enhanced) : enhanced;
        }
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            // ScaleForOcr draws onto opaque white first. No per-frame pixel arrays
            // or alpha compositing are required here.
            for (var y = 0; y < height; y++)
            {
                var row = (byte*)data.Scan0 + y * data.Stride;
                for (var x = 0; x < width; x++)
                {
                    var pixel = row + x * 4;
                    if (captionStrokes)
                    {
                        var luma = (pixel[2] * 77 + pixel[1] * 150 + pixel[0] * 29) >> 8;
                        var yellow = pixel[2] >= 180 && pixel[1] >= 160 && pixel[0] < Math.Min(pixel[2], pixel[1]) * 3 / 4;
                        pixel[0] = pixel[1] = pixel[2] = luma > 210 || yellow ? (byte)0 : (byte)255;
                    }
                    else if (options.Grayscale || options.Threshold)
                    {
                        var gray = (pixel[2] * 77 + pixel[1] * 150 + pixel[0] * 29) >> 8;
                        pixel[0] = pixel[1] = pixel[2] = lookup[gray];
                    }
                    else
                    {
                        pixel[0] = lookup[pixel[0]];
                        pixel[1] = lookup[pixel[1]];
                        pixel[2] = lookup[pixel[2]];
                    }
                    pixel[3] = 255;
                }
            }
        }
        finally { bitmap.UnlockBits(data); }
    }
    private static unsafe void Sharpen(Bitmap bitmap)
    {
        var width = bitmap.Width; var height = bitmap.Height;
        using var source = (Bitmap)bitmap.Clone();
        var rectangle = new Rectangle(0, 0, width, height);
        var sourceData = source.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var targetData = bitmap.LockBits(rectangle, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                for (var y = 1; y < height - 1; y++)
                {
                    var row = (byte*)sourceData.Scan0 + y * sourceData.Stride;
                    var top = row - sourceData.Stride; var bottom = row + sourceData.Stride;
                    var target = (byte*)targetData.Scan0 + y * targetData.Stride;
                    for (var x = 1; x < width - 1; x++)
                    {
                        var offset = x * 4;
                        for (var channel = 0; channel < 3; channel++)
                        {
                            var at = offset + channel;
                            target[at] = (byte)Math.Clamp(row[at] * 5 - row[at - 4] - row[at + 4] - top[at] - bottom[at], 0, 255);
                        }
                        target[offset + 3] = 255;
                    }
                }
            }
            finally { bitmap.UnlockBits(targetData); }
        }
        finally { source.UnlockBits(sourceData); }
    }

    private static byte ClampToByte(double value)
    {
        if (value <= 0)
        {
            return 0;
        }

        if (value >= 255)
        {
            return 255;
        }

        return (byte)Math.Round(value);
    }

    private readonly record struct OcrPreprocessingOptions(
        string Name,
        double MinimumScale,
        double MaximumScale,
        double ContrastFactor,
        bool Grayscale,
        bool Sharpen,
        bool Threshold,
        byte ThresholdValue,
        bool UseSmallTextTargets,
        bool Invert = false)
    {
        public static OcrPreprocessingOptions FromPreset(string preset)
        {
            return preset.Trim().ToLowerInvariant() switch
            {
                "original" => new OcrPreprocessingOptions("Original colors", 1.0, 1.0, 1.0, false, false, false, 128, false),
                "reduced-text" => new OcrPreprocessingOptions("Reduced text", 0.6, 0.6, 1.0, false, false, false, 128, false),
                "caption-strokes" => new OcrPreprocessingOptions("Bright caption strokes", 1.0, 1.0, 1.0,
                    true, false, true, 210, false, Invert: true),
                "small-text" => new OcrPreprocessingOptions(
                    "Small Text",
                    2.0,
                    3.0,
                    1.45,
                    true,
                    true,
                    false,
                    150,
                    true),
                "high-contrast" => new OcrPreprocessingOptions(
                    "High Contrast Text",
                    2.0,
                    3.0,
                    1.7,
                    true,
                    true,
                    true,
                    150,
                    true),
                _ => new OcrPreprocessingOptions(
                    "Normal",
                    1.35,
                    2.5,
                    1.3,
                    true,
                    false,
                    false,
                    150,
                    true)
            };
        }
    }
}

public sealed class OcrPreprocessedImage : IDisposable
{
    public OcrPreprocessedImage(Bitmap bitmap, double scale, int offsetX = 0, int offsetY = 0)
    {
        Bitmap = bitmap;
        Scale = scale;
        OffsetX = offsetX;
        OffsetY = offsetY;
    }

    public Bitmap Bitmap { get; }

    public double Scale { get; }

    public int OffsetX { get; }
    public int OffsetY { get; }

    public void Dispose()
    {
        Bitmap.Dispose();
    }
}
