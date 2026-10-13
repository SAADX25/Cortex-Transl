using CortexTransl.App.Services.Ocr;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using CortexTransl.App.Utils;
using Windows.Globalization;
using Windows.Media.Ocr;
using Xunit;

namespace CortexTransl.Tests;

public sealed class WindowsOcrTheoryAttribute : TheoryAttribute
{
    public WindowsOcrTheoryAttribute()
    {
        try
        {
            if (!OcrEngine.IsLanguageSupported(new Language("en"))) Skip = "English Windows OCR feature is unavailable.";
        }
        catch (Exception) { Skip = "Windows OCR runtime is unavailable."; }
    }
}

public sealed class WindowsOcrTests
{
    [WindowsOcrTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutlinedLyricsAndNegationSurviveBusyHighContrastBackgrounds(bool glow)
    {
        var engine = new WindowsOcrEngine();
        foreach (var line in new[] { "Cr-cr-cry since the day we parted", "I can't help you.", "No!" })
        {
            using var bitmap = new Bitmap(1280, 180, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var path = new GraphicsPath())
            using (var family = new FontFamily("Arial"))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.FromArgb(90, 110, 145));
                for (var x = 0; x < 1280; x += 110)
                    graphics.FillRectangle(x % 220 == 0 ? Brushes.White : Brushes.Black, x, 0, 25, 180);
                path.AddString(line, family, (int)FontStyle.Bold, 38, new Point(40, 65), StringFormat.GenericDefault);
                if (glow) { using var halo = new Pen(Color.FromArgb(100, 80, 150, 255), 9); graphics.DrawPath(halo, path); }
                using var outline = new Pen(Color.Black, 4);
                graphics.DrawPath(outline, path); graphics.FillPath(Brushes.White, path);
            }
            var recognized = await engine.RecognizeAsync(bitmap, "en");
            Assert.True(OcrTextCanonicalizer.Normalize(line).Equals(OcrTextCanonicalizer.Normalize(recognized.Text), StringComparison.OrdinalIgnoreCase),
                $"Fixture '{line}' (glow={glow}) read '{recognized.Text}'.");
        }
    }

    [WindowsOcrTheory]
    [InlineData("Segoe UI", false)]
    [InlineData("Segoe UI", true)]
    [InlineData("Arial", false)]
    [InlineData("Arial", true)]
    [InlineData("Georgia", false)]
    [InlineData("Georgia", true)]
    public async Task RealWindowsOcrReadsShortDialogueAcrossRenderedFontsAndBackgrounds(string family, bool brightBackground)
    {
        var engine = new WindowsOcrEngine();
        foreach (var text in new[] { "Run!", "No!", "Help!" })
        {
            using var bitmap = new Bitmap(1000, 180, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var font = new Font(family, 34, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                graphics.Clear(brightBackground ? Color.White : Color.Black);
                graphics.DrawString(text, font, brightBackground ? Brushes.Black : Brushes.White, 60, 55);
            }
            var result = await engine.RecognizeAsync(bitmap, "en");
            Assert.True(text.Equals(result.Text, StringComparison.OrdinalIgnoreCase), $"Expected '{text}', OCR read '{result.Text}' for {family}, brightBackground={brightBackground}.");
            var block = Assert.Single(result.Blocks);
            // Native OCR's vertical rectangles can clip or include whitespace.
            // Verify horizontal scale mapping and safe source-region bounds.
            Assert.InRange(95, block.Bounds.X, block.Bounds.X + block.Bounds.Width);
            Assert.InRange(block.Bounds.Y, 0, bitmap.Height - 1);
            Assert.InRange(block.Bounds.Y + block.Bounds.Height, 1, bitmap.Height);
        }
    }
}
