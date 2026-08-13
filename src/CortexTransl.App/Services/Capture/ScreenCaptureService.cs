using CortexTransl.App.Models;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CortexTransl.App.Services.Capture;

public sealed class ScreenCaptureService : IScreenCaptureService
{
    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;

    public Task<Bitmap> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        region = ScreenCoordinates.ClampToVirtualScreen(region);
        if (region.IsEmpty)
        {
            throw new InvalidOperationException("Select the dialogue region first.");
        }

        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            var destination = graphics.GetHdc();
            var source = GetDC(nint.Zero);
            var copied = false;

            try
            {
                copied = BitBlt(
                    destination,
                    0,
                    0,
                    region.Width,
                    region.Height,
                    source,
                    region.X,
                    region.Y,
                    SrcCopy | CaptureBlt);
            }
            finally
            {
                if (source != nint.Zero)
                {
                    ReleaseDC(nint.Zero, source);
                }

                graphics.ReleaseHdc(destination);
            }

            if (!copied)
            {
                graphics.CopyFromScreen(
                    region.X,
                    region.Y,
                    0,
                    0,
                    new Size(region.Width, region.Height),
                    CopyPixelOperation.SourceCopy);
            }

            return Task.FromResult(bitmap);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        nint destination,
        int destinationX,
        int destinationY,
        int width,
        int height,
        nint source,
        int sourceX,
        int sourceY,
        int rasterOperation);
}
