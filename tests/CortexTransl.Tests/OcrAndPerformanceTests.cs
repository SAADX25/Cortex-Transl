using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Ocr;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace CortexTransl.Tests;

[CollectionDefinition("Preprocessing measurements", DisableParallelization = true)]
public sealed class PreprocessingMeasurementCollection;

[Collection("Preprocessing measurements")]
public sealed class OcrAndPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void CaptionMaskRetainsWhiteYellowAndPunctuationButRemovesOutlineAndSceneBars()
    {
        using var source = new Bitmap(160, 80, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.FromArgb(75, 90, 135));
            graphics.FillRectangle(Brushes.Black, 10, 20, 100, 25);
            graphics.FillRectangle(Brushes.White, 15, 25, 20, 12);
            using var yellow = new SolidBrush(Color.FromArgb(220, 205, 25));
            graphics.FillRectangle(yellow, 45, 25, 20, 12);
            graphics.FillRectangle(Brushes.White, 75, 35, 2, 2);
            graphics.FillRectangle(Brushes.White, 140, 0, 5, 80);
        }
        using var prepared = OcrImagePreprocessor.Preprocess(source, "caption-strokes");
        Assert.Equal(0, prepared.Bitmap.GetPixel(20, 30).R);
        Assert.Equal(0, prepared.Bitmap.GetPixel(50, 30).R);
        Assert.Equal(0, prepared.Bitmap.GetPixel(75, 35).R);
        Assert.Equal(255, prepared.Bitmap.GetPixel(11, 21).R);
        Assert.Equal(255, prepared.Bitmap.GetPixel(5, 5).R);
        Assert.Equal(255, prepared.Bitmap.GetPixel(142, 30).R);
        Assert.Equal(220, source.GetPixel(50, 30).R);
        Assert.True(SubtitleVisualSignature.Analyze(source).LightPixels >= 16);
    }

    [Fact]
    public void MeasurePreprocessingLatencyAndManagedAllocations()
    {
        using var bitmap = new Bitmap(1280, 180, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Arial", 30, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.FromArgb(75, 90, 135));
            graphics.FillRectangle(Brushes.White, 1100, 0, 25, 180);
            graphics.DrawString("We must reach the bridge before dawn!", font, Brushes.White, 60, 40);
            graphics.DrawString("Hello world!", font, Brushes.Yellow, 60, 90);
        }
        var rows = new List<object>();
        foreach (var preset in new[] { "original", "auto", "small-text", "caption-strokes" })
        {
            for (var warmup = 0; warmup < 10; warmup++)
            { using var image = OcrImagePreprocessor.Preprocess(bitmap, preset); }
            var samples = new double[40];
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var sample = 0; sample < samples.Length; sample++)
            {
                var started = Stopwatch.GetTimestamp();
                using (var image = OcrImagePreprocessor.Preprocess(bitmap, preset))
                    Assert.True(image.Bitmap.Width > 0);
                samples[sample] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
            var bytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / samples.Length;
            Array.Sort(samples);
            rows.Add(new { preset, samples = samples.Length, p50Milliseconds = samples[19],
                p95Milliseconds = samples[37], managedBytesPerFrame = bytes });
        }
        var report = JsonSerializer.Serialize(new { width = bitmap.Width, height = bitmap.Height,
            scope = "Synthetic preprocessing only; excludes capture, Windows OCR, inference and UI rendering", rows },
            new JsonSerializerOptions { WriteIndented = true });
        output.WriteLine(report);
        var path = Environment.GetEnvironmentVariable("CORTEX_TRANSL_PREPROCESS_REPORT");
        if (!string.IsNullOrWhiteSpace(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, report);
        }
    }

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
