using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Ocr;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Xunit;
using Xunit.Abstractions;

namespace CortexTransl.Tests;

public sealed class OcrAndPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void MissingGdiWindowTargetCannotFallBackToDesktopPixels()
        => Assert.ThrowsAny<Exception>(() => GdiScreenCapture.Capture(new(0, 0, 64, 32), null));
    [Fact]
    public void RecoveryCropRetainsSourceOffsetsAndBusyBordersAreNotTrimmed()
    {
        using var bitmap = new Bitmap(1000, 180, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        { graphics.Clear(Color.Black); graphics.FillRectangle(Brushes.White, 300, 60, 60, 30); }
        using var cropped = OcrImagePreprocessor.Preprocess(bitmap, "auto-recovery");
        Assert.InRange(cropped.OffsetX, 280, 300);
        Assert.InRange(cropped.OffsetY, 40, 60);
        bitmap.SetPixel(999, 0, Color.Red);
        using var busy = OcrImagePreprocessor.Preprocess(bitmap, "auto-recovery");
        Assert.Equal(0, busy.OffsetX);
        Assert.Equal(0, busy.OffsetY);
    }
    [Fact]
    public void FingerprintDetectsTinyGlyphChangeBetweenOldSamplePoints()
    {
        using var bitmap = new Bitmap(1280, 180, PixelFormat.Format32bppArgb);
        var first = ImageFingerprint.Compute(bitmap);
        bitmap.SetPixel(7, 11, Color.White);
        Assert.NotEqual(first, ImageFingerprint.Compute(bitmap));
        Assert.Equal(ImageFingerprint.Compute(bitmap), ImageFingerprint.Compute(bitmap));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdaptivePreprocessingPreservesDimensionsAndSelectsTextPolarity(bool brightBackground)
    {
        using var bitmap = new Bitmap(320, 100, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(brightBackground ? Color.White : Color.Black);
            graphics.FillRectangle(brightBackground ? Brushes.Black : Brushes.White, 20, 20, 100, 25);
        }
        using var processed = OcrImagePreprocessor.Preprocess(bitmap, "auto-contrast");
        Assert.InRange(processed.Scale, 1, 2.5);
        var background = processed.Bitmap.GetPixel(2, 2);
        var text = processed.Bitmap.GetPixel((int)(50 * processed.Scale), (int)(30 * processed.Scale));
        Assert.True(background.R > text.R);
        Assert.Equal(processed.Bitmap.Width, (int)Math.Round(bitmap.Width * processed.Scale));
    }

    [Fact]
    public void MeasureSubtitleRegionFingerprintAndAdaptivePreprocessing()
    {
        using var bitmap = new Bitmap(1280, 180, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        { graphics.Clear(Color.Black); graphics.FillRectangle(Brushes.White, 100, 60, 400, 20); }
        ImageFingerprint.Compute(bitmap);
        var timer = Stopwatch.StartNew();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100; index++) ImageFingerprint.Compute(bitmap);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        output.WriteLine($"1280x180 exact RGB fingerprint, 100 iterations: {timer.Elapsed.TotalMilliseconds / 100:F3} ms/frame, {bytes / 100} managed bytes/frame.");
        timer.Restart();
        for (var index = 0; index < 20; index++)
        { using var image = OcrImagePreprocessor.Preprocess(bitmap, "auto"); }
        output.WriteLine($"1280x180 adaptive preprocessing, 20 iterations: {timer.Elapsed.TotalMilliseconds / 20:F3} ms/frame. Synthetic image; excludes Windows OCR, GPU capture, and translation.");
        timer.Restart();
        for (var index = 0; index < 30; index++)
        { using var image = OcrImagePreprocessor.Preprocess(bitmap, "original"); }
        output.WriteLine($"1280x180 original-pixel preprocessing, 30 iterations: {timer.Elapsed.TotalMilliseconds / 30:F3} ms/frame. Default dialogue first pass.");
    }
}
