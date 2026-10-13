using CortexTransl.App.Models;
using System.Drawing;

namespace CortexTransl.App.Services.Capture;

internal static class CaptureCrop
{
    // Screen regions and window bounds are physical pixels, including negative
    // multi-monitor origins. Never stretch, round to DIPs, or allow a partial crop.
    public static Rectangle? Calculate(CaptureRegion region, CaptureRegion bounds, int width, int height)
    {
        var x = (long)region.X - bounds.X; var y = (long)region.Y - bounds.Y;
        if (region.IsEmpty || x < 0 || y < 0 || x + region.Width > width || y + region.Height > height) return null;
        return new Rectangle((int)x, (int)y, region.Width, region.Height);
    }
}
