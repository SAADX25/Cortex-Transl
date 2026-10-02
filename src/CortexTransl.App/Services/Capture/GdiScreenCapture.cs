using CortexTransl.App.Models;
using CortexTransl.App.Views;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CortexTransl.App.Services.Capture;

internal static class GdiScreenCapture
{
    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;
    private const uint PrintWindowRenderFullContent = 0x00000002;

    public static Bitmap Capture(CaptureRegion region, CaptureWindowTarget? source)
    {
        region = ScreenCoordinates.ClampToVirtualScreen(region);
        if (region.IsEmpty)
        {
            throw new InvalidOperationException("Select the dialogue region first.");
        }

        // Rendering Explorer's icon view excludes independent overlay windows,
        // including our Arabic labels and the NVIDIA recording controls.
        var printed = TryPrintWindow(region, source?.DesktopView ?? nint.Zero);
        if (printed is not null && !LooksBlank(printed))
        {
            OverlayWindow.RestoreCapturePlacement(region);
            return printed;
        }

        printed?.Dispose();
        printed = TryPrintWindow(region, source?.Handle ?? nint.Zero);
        if (printed is not null && !LooksBlank(printed))
        {
            OverlayWindow.RestoreCapturePlacement(region);
            return printed;
        }

        printed?.Dispose();
        return OverlayWindow.CaptureDesktopWithUncoveredRegion(region, () => BitBltRegion(region));
    }

    public static bool LooksBlank(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        if (width < 3 || height < 3)
        {
            return true;
        }

        var lit = 0;
        for (var index = 0; index < 48; index++)
        {
            var x = 1 + ((index * 37) % (width - 2));
            var y = 1 + ((index * 53) % (height - 2));
            var color = bitmap.GetPixel(x, y);
            if (color.R > 18 || color.G > 18 || color.B > 18)
            {
                lit++;
            }
        }

        return lit < 3;
    }

    private static Bitmap BitBltRegion(CaptureRegion region)
    {
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

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static Bitmap? TryPrintWindow(CaptureRegion region, nint window)
    {
        if (window == nint.Zero || !GetWindowRect(window, out var bounds))
        {
            return null;
        }

        var windowWidth = bounds.Right - bounds.Left;
        var windowHeight = bounds.Bottom - bounds.Top;
        if (windowWidth < 8 || windowHeight < 8)
        {
            return null;
        }

        using var full = new Bitmap(windowWidth, windowHeight, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(full);
        var hdc = graphics.GetHdc();
        try
        {
            if (!PrintWindow(window, hdc, PrintWindowRenderFullContent))
            {
                return null;
            }
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        var crop = Rectangle.Intersect(
            new Rectangle(0, 0, windowWidth, windowHeight),
            new Rectangle(region.X - bounds.Left, region.Y - bounds.Top, region.Width, region.Height));
        if (crop.Width != region.Width || crop.Height != region.Height)
        {
            return null;
        }

        var result = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
        using var target = Graphics.FromImage(result);
        target.DrawImage(full, new Rectangle(0, 0, crop.Width, crop.Height), crop, GraphicsUnit.Pixel);
        return result;
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

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
