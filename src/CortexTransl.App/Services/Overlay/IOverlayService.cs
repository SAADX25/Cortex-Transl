using CortexTransl.App.Models;

namespace CortexTransl.App.Services.Overlay;

public interface IOverlayService : IDisposable
{
    event EventHandler<OverlayPositionChangedEventArgs>? OverlayPositionChanged;

    bool IsVisible { get; }

    Task<long> ShowTextAsync(
        string text,
        CaptureRegion region,
        OverlaySettings settings,
        CancellationToken cancellationToken = default);

    void Hide();

    void ClearText();
}

public sealed class OverlayPositionChangedEventArgs(double left, double top) : EventArgs
{
    public double Left { get; } = left;

    public double Top { get; } = top;
}
