using CortexTransl.App.Models;
using System.Drawing;

namespace CortexTransl.App.Services.Capture;

public interface IScreenCaptureService : IDisposable
{
    bool ExcludesOverlayWindows { get; }

    Task<Bitmap> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken = default);
}
