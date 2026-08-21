using CortexTransl.App.Models;
using System.Drawing;
using System.Windows;
using System.Windows.Threading;

namespace CortexTransl.App.Services.Capture;

public sealed class ScreenCaptureService : IScreenCaptureService
{
    private readonly object _gate = new();
    private WindowsGraphicsMonitorCapturer? _capturer;
    private bool _graphicsCaptureUnavailable;
    private bool _excludesOverlay;

    public bool ExcludesOverlayWindows => _excludesOverlay;

    public Task<Bitmap> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        region = ScreenCoordinates.ClampToVirtualScreen(region);
        if (region.IsEmpty)
        {
            throw new InvalidOperationException("Select the dialogue region first.");
        }

        try
        {
            var captured = TryCaptureWithGraphics(region);
            if (captured is not null && !GdiScreenCapture.LooksBlank(captured))
            {
                _excludesOverlay = true;
                return Task.FromResult(captured);
            }

            captured?.Dispose();
        }
        catch
        {
            _graphicsCaptureUnavailable = true;
        }

        _excludesOverlay = false;
        return Task.FromResult(GdiScreenCapture.Capture(region));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _capturer?.Dispose();
            _capturer = null;
        }
    }

    private Bitmap? TryCaptureWithGraphics(CaptureRegion region)
    {
        if (_graphicsCaptureUnavailable)
        {
            return null;
        }

        try
        {
            var monitor = ScreenCoordinates.GetMonitorHandle(region);
            var capturer = GetOrCreateCapturer(monitor);
            return capturer?.CaptureRegion(region, TimeSpan.FromMilliseconds(80));
        }
        catch
        {
            _graphicsCaptureUnavailable = true;
            return null;
        }
    }

    private WindowsGraphicsMonitorCapturer? GetOrCreateCapturer(nint monitor)
    {
        lock (_gate)
        {
            if (_capturer is not null && _capturer.Monitor == monitor)
            {
                return _capturer;
            }

            _capturer?.Dispose();
            _capturer = null;
        }

        var created = CreateOnUi(() => WindowsGraphicsMonitorCapturer.TryCreate(monitor));
        if (created is null)
        {
            _graphicsCaptureUnavailable = true;
            return null;
        }

        lock (_gate)
        {
            _capturer?.Dispose();
            _capturer = created;
            return _capturer;
        }
    }

    private static T CreateOnUi<T>(Func<T> factory)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return factory();
        }

        if (dispatcher.CheckAccess())
        {
            return factory();
        }

        try
        {
            return dispatcher.Invoke(factory, DispatcherPriority.Send);
        }
        catch
        {
            return factory();
        }
    }
}
