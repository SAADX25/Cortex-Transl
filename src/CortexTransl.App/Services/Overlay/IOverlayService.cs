using CortexTransl.App.Models;

namespace CortexTransl.App.Services.Overlay;

public interface IOverlayService : IDisposable
{
    bool IsVisible { get; }

    void Pin(CaptureRegion region, OverlaySettings settings);

    void SetText(string text);

    void SetBlocks(IReadOnlyList<TranslatedBlock> blocks);

    void Hide();

    void DisposeOverlay();
}
