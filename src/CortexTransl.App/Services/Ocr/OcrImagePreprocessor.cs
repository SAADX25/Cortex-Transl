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
    private const double MaximumUpscale = 3.0;
    private const double ContrastFactor = 1.35;

    public static Bitmap Preprocess(Bitmap source)
    {
        var scaled = ScaleForOcr(source);
        EnhanceForOcr(scaled);
        return scaled;
    }

    private static Bitmap ScaleForOcr(Bitmap source)
    {
        var scale = 1.0;

        if (source.Height < TargetSmallTextHeight)
        {
            scale = Math.Max(scale, (double)TargetSmallTextHeight / source.Height);
        }

        if (source.Width < TargetSmallTextWidth && source.Height < TargetSmallTextHeight * 2)
        {
            scale = Math.Max(scale, (double)TargetSmallTextWidth / source.Width);
        }

        scale = Math.Min(scale, MaximumUpscale);

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

        return result;
    }

    private static void EnhanceForOcr(Bitmap bitmap)
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
                    var enhanced = ClampToByte(128 + ((gray - 128) * ContrastFactor));

                    if (enhanced > 238)
                    {
                        enhanced = 255;
                    }
                    else if (enhanced < 17)
                    {
                        enhanced = 0;
                    }

                    pixels[offset] = enhanced;
                    pixels[offset + 1] = enhanced;
                    pixels[offset + 2] = enhanced;
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
}
