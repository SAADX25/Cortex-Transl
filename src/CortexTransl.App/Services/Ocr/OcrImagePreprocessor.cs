using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
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
        using var cropped = crop.Width == source.Width && crop.Height == source.Height ? null : source.Clone(crop, PixelFormat.Format32bppArgb);
        var scaled = ScaleForOcr(cropped ?? source, options, crop.X, crop.Y);

        if (options.Grayscale || options.ContrastFactor != 1.0 || options.Threshold || options.Invert)
        {
            EnhanceForOcr(scaled.Bitmap, options);
        }

        if (options.Sharpen)
        {
            Sharpen(scaled.Bitmap);
        }

        if (preset == "caption-strokes") SuppressFullHeightBrightBars(scaled.Bitmap);

        return scaled;
    }

    private static unsafe void SuppressFullHeightBrightBars(Bitmap bitmap)
    {
        // After bright-stroke inversion, almost-full-height black columns are
        // scene bars, not subtitle-height glyphs. This recovery leaves smaller
        // strokes, apostrophes and punctuation intact.
        var width = bitmap.Width; var height = bitmap.Height;
        var data = bitmap.LockBits(new(0, 0, width, height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            for (var x = 0; x < width; x++)
            {
                var dark = 0;
                for (var y = 0; y < height; y++) if (*((byte*)data.Scan0 + y * data.Stride + x * 4) == 0) dark++;
                if (dark < height * .85) continue;
                for (var y = 0; y < height; y++)
                {
                    var pixel = (byte*)data.Scan0 + y * data.Stride + x * 4;
                    pixel[0] = pixel[1] = pixel[2] = 255;
                }
            }
        }
        finally { bitmap.UnlockBits(data); }
    }

    private static unsafe Rectangle FindUniformBackgroundTextBounds(Bitmap source)
    {
        var full = new Rectangle(0, 0, source.Width, source.Height);
        if (source.Width < 32 || source.Height < 24) return full;
        var background = source.GetPixel(0, 0);
        // Only trim a demonstrably uniform border. Busy game scenes stay intact.
        for (var index = 0; index < 32; index++)
        {
            var x = index * (source.Width - 1) / 31;
            foreach (var y in new[] { 0, source.Height - 1 })
            {
                var pixel = source.GetPixel(x, y);
                if (Math.Abs(pixel.R - background.R) + Math.Abs(pixel.G - background.G) + Math.Abs(pixel.B - background.B) > 48) return full;
            }
        }
        var left = source.Width; var top = source.Height; var right = -1; var bottom = -1;
        var data = source.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < source.Height; y++)
            {
                var row = (byte*)data.Scan0 + y * data.Stride;
                for (var x = 0; x < source.Width; x++)
                {
                    var pixel = row + x * 4;
                    if (Math.Abs(pixel[2] - background.R) + Math.Abs(pixel[1] - background.G) + Math.Abs(pixel[0] - background.B) <= 96) continue;
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
            }
        }
        finally { source.UnlockBits(data); }
        if (right - left < 2 || bottom - top < 2) return full;
        var bounds = Rectangle.FromLTRB(Math.Max(0, left - 16), Math.Max(0, top - 16),
            Math.Min(source.Width, right + 17), Math.Min(source.Height, bottom + 17));
        return (long)bounds.Width * bounds.Height < (long)source.Width * source.Height * 3 / 4 ? bounds : full;
    }

    private static OcrPreprocessingOptions SelectAdaptiveOptions(Bitmap source, bool threshold)
    {
        // A bounded luminance histogram selects polarity and an Otsu threshold.
        // Soft grayscale is the first pass; thresholding is a recovery pass only.
        var histogram = new int[256];
        var total = 0;
        long sum = 0;
        for (var y = 0; y < source.Height; y += Math.Max(1, source.Height / 16))
        for (var x = 0; x < source.Width; x += Math.Max(1, source.Width / 32))
        {
            var pixel = source.GetPixel(x, y);
            var luma = (pixel.R * 77 + pixel.G * 150 + pixel.B * 29) >> 8;
            histogram[luma]++; total++; sum += luma;
        }
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

    private static OcrPreprocessedImage ScaleForOcr(Bitmap source, OcrPreprocessingOptions options, int offsetX = 0, int offsetY = 0)
    {
        var scale = options.MinimumScale;

        if (options.UseSmallTextTargets)
        {
            if (source.Height < TargetSmallTextHeight)
            {
                scale = Math.Max(scale, (double)TargetSmallTextHeight / source.Height);
            }

            if (source.Width < TargetSmallTextWidth && source.Height < TargetSmallTextHeight * 2)
            {
                scale = Math.Max(scale, (double)TargetSmallTextWidth / source.Width);
            }
        }

        scale = Math.Min(scale, options.MaximumScale);

        var largest = Math.Max(source.Width, source.Height);
        if (largest > 0)
        {
            scale = Math.Min(scale, (double)OcrEngine.MaxImageDimension / largest);
        }

        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);

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
        if (width == source.Width && height == source.Height) graphics.DrawImageUnscaled(source, 0, 0);
        else graphics.DrawImage(source, 0, 0, width, height);

        return new OcrPreprocessedImage(result, scale, options.Name, offsetX, offsetY);
    }

    private static unsafe void EnhanceForOcr(Bitmap bitmap, OcrPreprocessingOptions options)
    {
        Span<byte> lookup = stackalloc byte[256];
        for (var level = 0; level < 256; level++)
        {
            var enhanced = ClampToByte(128 + ((level - 128) * options.ContrastFactor));
            if (options.Threshold) enhanced = enhanced > options.ThresholdValue ? (byte)255 : (byte)0;
            else if (enhanced > 246) enhanced = 255;
            else if (enhanced < 10) enhanced = 0;
            lookup[level] = options.Invert ? (byte)(255 - enhanced) : enhanced;
        }
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            // ScaleForOcr draws onto opaque white first. No per-frame pixel arrays
            // or alpha compositing are required here.
            for (var y = 0; y < bitmap.Height; y++)
            {
                var row = (byte*)data.Scan0 + y * data.Stride;
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var pixel = row + x * 4;
                    if (options.Grayscale || options.Threshold)
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
    private static void Sharpen(Bitmap bitmap)
    {
        using var source = (Bitmap)bitmap.Clone();
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var sourceData = source.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var targetData = bitmap.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var byteCount = Math.Abs(sourceData.Stride) * bitmap.Height;
        var sourcePixels = new byte[byteCount];
        var targetPixels = new byte[byteCount];

        try
        {
            Marshal.Copy(sourceData.Scan0, sourcePixels, 0, byteCount);
            Array.Copy(sourcePixels, targetPixels, byteCount);

            for (var y = 1; y < bitmap.Height - 1; y++)
            {
                for (var x = 1; x < bitmap.Width - 1; x++)
                {
                    ApplySharpenKernel(sourcePixels, targetPixels, sourceData.Stride, x, y);
                }
            }

            Marshal.Copy(targetPixels, 0, targetData.Scan0, byteCount);
        }
        finally
        {
            source.UnlockBits(sourceData);
            bitmap.UnlockBits(targetData);
        }
    }

    private static void ApplySharpenKernel(byte[] sourcePixels, byte[] targetPixels, int stride, int x, int y)
    {
        var rowStride = Math.Abs(stride);
        var offset = (y * rowStride) + (x * 4);

        for (var channel = 0; channel < 3; channel++)
        {
            var center = sourcePixels[offset + channel] * 5;
            var left = sourcePixels[offset - 4 + channel];
            var right = sourcePixels[offset + 4 + channel];
            var top = sourcePixels[offset - rowStride + channel];
            var bottom = sourcePixels[offset + rowStride + channel];

            targetPixels[offset + channel] = ClampToByte(center - left - right - top - bottom);
        }

        targetPixels[offset + 3] = 255;
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

    private sealed record OcrPreprocessingOptions(
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
    public OcrPreprocessedImage(Bitmap bitmap, double scale, string presetName, int offsetX = 0, int offsetY = 0)
    {
        Bitmap = bitmap;
        Scale = scale;
        PresetName = presetName;
        OffsetX = offsetX;
        OffsetY = offsetY;
    }

    public Bitmap Bitmap { get; }

    public double Scale { get; }

    public string PresetName { get; }
    public int OffsetX { get; }
    public int OffsetY { get; }

    public void Dispose()
    {
        Bitmap.Dispose();
    }
}
