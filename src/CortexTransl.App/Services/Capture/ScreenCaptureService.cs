using CortexTransl.App.Models;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CortexTransl.App.Services.Capture;

public sealed class ScreenCaptureService : IScreenCaptureService
{
    public Task<Bitmap> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (region.IsEmpty)
        {
            throw new InvalidOperationException("Select a screen region before capturing.");
        }

        if (!IsInsideVirtualScreen(region))
        {
            throw new InvalidOperationException("The selected region is outside the visible screen. Select the dialogue region again.");
        }

        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(region.X, region.Y, 0, 0, new Size(region.Width, region.Height), CopyPixelOperation.SourceCopy);
            return Task.FromResult(bitmap);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static bool IsInsideVirtualScreen(CaptureRegion region)
    {
        var left = GetSystemMetrics(SystemMetric.VirtualScreenX);
        var top = GetSystemMetrics(SystemMetric.VirtualScreenY);
        var width = GetSystemMetrics(SystemMetric.VirtualScreenWidth);
        var height = GetSystemMetrics(SystemMetric.VirtualScreenHeight);
        var right = left + width;
        var bottom = top + height;

        return region.X >= left
            && region.Y >= top
            && region.X + region.Width <= right
            && region.Y + region.Height <= bottom;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(SystemMetric metric);

    private enum SystemMetric
    {
        VirtualScreenX = 76,
        VirtualScreenY = 77,
        VirtualScreenWidth = 78,
        VirtualScreenHeight = 79
    }
}
