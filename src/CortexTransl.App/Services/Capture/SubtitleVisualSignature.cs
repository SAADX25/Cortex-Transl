using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.CompilerServices;

namespace CortexTransl.App.Services.Capture;

internal static class SubtitleVisualSignature
{
    public readonly record struct Signature(ulong Hash, int LightPixels, int DarkPixels);
    // A scheduling hint, not proof that text is unchanged. Every pixel is
    // inspected; periodic OCR and exact recognized-text comparison remain final.
    public static ulong Compute(Bitmap bitmap) => Analyze(bitmap).Hash;
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static unsafe Signature Analyze(Bitmap bitmap)
    {
        // Bitmap dimension getters cross into GDI+. Cache them outside the
        // pixel loops, otherwise a subtitle region incurs thousands of calls.
        var width = bitmap.Width;
        var height = bitmap.Height;
        var data = bitmap.LockBits(new(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            ulong hash = ((ulong)width << 32) | (uint)height;
            var lights = 0; var darks = 0;
            for (var y = 0; y < height; y++)
            {
                var row = (byte*)data.Scan0 + y * data.Stride;
                ulong word = 0;
                for (var x = 0; x < width; x++)
                {
                    var p = row + x * 4;
                    var light = (p[2] * 77 + p[1] * 150 + p[0] * 29) >> 8;
                    // White/light caption strokes remain stable while mid-tone
                    // animated backgrounds vary. Dark captions also contribute.
                    var yellow = p[2] >= 180 && p[1] >= 160 && p[0] < Math.Min(p[2], p[1]) * 3 / 4;
                    var bucket = light >= 210 || yellow ? 2UL : light <= 35 ? 1UL : 0UL;
                    if (bucket == 2) lights++; else if (bucket == 1) darks++;
                    word |= bucket << ((x & 31) * 2);
                    if ((x & 31) == 31) { hash = (hash ^ word) * 1099511628211UL; word = 0; }
                }
                if ((width & 31) != 0) hash = (hash ^ word) * 1099511628211UL;
            }
            return new(hash, lights, darks);
        }
        finally { bitmap.UnlockBits(data); }
    }
}
