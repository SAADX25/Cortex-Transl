using CortexTransl.App.Models;
using System.Drawing;

namespace CortexTransl.App.Services.Capture;

public interface IScreenCaptureService : IDisposable
{
    string CacheIdentity => "isolated-session";
    void BindTarget(CaptureRegion region, bool listMode, bool onlyIfUnbound = false) { }
    Task<Bitmap> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken = default);
}
