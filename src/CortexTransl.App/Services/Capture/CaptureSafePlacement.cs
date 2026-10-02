using CortexTransl.App.Models;

namespace CortexTransl.App.Services.Capture;

internal static class CaptureSafePlacement
{
    public static bool Overlaps(CaptureRegion first, CaptureRegion second) =>
        first.X < second.X + second.Width && first.X + first.Width > second.X
        && first.Y < second.Y + second.Height && first.Y + first.Height > second.Y;

    public static CaptureRegion? Find(CaptureRegion source, CaptureRegion overlay, CaptureRegion screen)
    {
        if (overlay.Width > screen.Width || overlay.Height > screen.Height)
        {
            return null;
        }

        const int gap = 8;
        var x = Math.Clamp(overlay.X, screen.X, screen.X + screen.Width - overlay.Width);
        var y = Math.Clamp(overlay.Y, screen.Y, screen.Y + screen.Height - overlay.Height);
        CaptureRegion[] candidates =
        [
            new(x, source.Y + source.Height + gap, overlay.Width, overlay.Height),
            new(x, source.Y - overlay.Height - gap, overlay.Width, overlay.Height),
            new(source.X + source.Width + gap, y, overlay.Width, overlay.Height),
            new(source.X - overlay.Width - gap, y, overlay.Width, overlay.Height)
        ];

        return candidates.FirstOrDefault(candidate =>
            candidate.X >= screen.X && candidate.Y >= screen.Y
            && candidate.X + candidate.Width <= screen.X + screen.Width
            && candidate.Y + candidate.Height <= screen.Y + screen.Height
            && !Overlaps(candidate, source));
    }
}

internal sealed class CaptureUnavailableException(string message) : Exception(message);
