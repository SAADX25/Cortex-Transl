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
        var options = OcrPreprocessingOptions.FromPreset(preset);
        var scaled = ScaleForOcr(source, options);

        if (options.Grayscale || options.ContrastFactor != 1.0 || options.Threshold)
        {
            EnhanceForOcr(scaled.Bitmap, options);
        }

        if (options.Sharpen)
        {
            Sharpen(scaled.Bitmap);
        }

        return scaled;
    }

    private static OcrPreprocessedImage ScaleForOcr(Bitmap source, OcrPreprocessingOptions options)
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
            ? InterpolationMode.HighQualityBicubic
            : InterpolationMode.NearestNeighbor;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, 0, 0, width, height);

        return new OcrPreprocessedImage(result, scale, options.Name);
    }

    private static void EnhanceForOcr(Bitmap bitmap, OcrPreprocessingOptions options)
    {
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var byteCount = Math.Abs(data.Stride) * bitmap.Height;
        var pixels = new byte[byteCount];

        try
        {
            Marshal.Copy(data.Scan0, pixels, 0, byteCount);

            for (var y = 0; y < bitmap.Height; y++)
            {
                var rowOffset = y * Math.Abs(data.Stride);
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var offset = rowOffset + x * 4;
                    var blue = pixels[offset];
                    var green = pixels[offset + 1];
                    var red = pixels[offset + 2];
                    var alpha = pixels[offset + 3] / 255.0;

                    var compositedRed = (red * alpha) + (255 * (1 - alpha));
                    var compositedGreen = (green * alpha) + (255 * (1 - alpha));
                    var compositedBlue = (blue * alpha) + (255 * (1 - alpha));
                    var gray = (int)Math.Round((0.299 * compositedRed) + (0.587 * compositedGreen) + (0.114 * compositedBlue));
                    var enhanced = ClampToByte(128 + ((gray - 128) * options.ContrastFactor));

                    if (options.Threshold)
                    {
                        enhanced = enhanced >= options.ThresholdValue ? (byte)255 : (byte)0;
                    }
                    else if (enhanced > 246)
                    {
                        enhanced = 255;
                    }
                    else if (enhanced < 10)
                    {
                        enhanced = 0;
                    }

                    if (options.Grayscale || options.Threshold)
                    {
                        pixels[offset] = enhanced;
                        pixels[offset + 1] = enhanced;
                        pixels[offset + 2] = enhanced;
                    }
                    else
                    {
                        pixels[offset] = ClampToByte(128 + ((compositedBlue - 128) * options.ContrastFactor));
                        pixels[offset + 1] = ClampToByte(128 + ((compositedGreen - 128) * options.ContrastFactor));
                        pixels[offset + 2] = ClampToByte(128 + ((compositedRed - 128) * options.ContrastFactor));
                    }

                    pixels[offset + 3] = 255;
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, byteCount);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
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
            Marshal.Copy(targetData.Scan0, targetPixels, 0, byteCount);

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
        bool UseSmallTextTargets)
    {
        public static OcrPreprocessingOptions FromPreset(string preset)
        {
            return preset.Trim().ToLowerInvariant() switch
            {
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
                    1.0,
                    3.0,
                    1.25,
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
    public OcrPreprocessedImage(Bitmap bitmap, double scale, string presetName)
    {
        Bitmap = bitmap;
        Scale = scale;
        PresetName = presetName;
    }

    public Bitmap Bitmap { get; }

    public double Scale { get; }

    public string PresetName { get; }

    public void Dispose()
    {
        Bitmap.Dispose();
    }
}
