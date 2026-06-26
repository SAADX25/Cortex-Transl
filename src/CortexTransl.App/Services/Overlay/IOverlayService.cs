using CortexTransl.App.Models;

namespace CortexTransl.App.Services.Overlay;

public interface IOverlayService : IDisposable
{
    Task<long> ShowTextAsync(string text, CaptureRegion region, OverlaySettings settings);

    void Hide();
}
