using CortexTransl.App.Models;
using CortexTransl.App.Utils;
using CortexTransl.App.Views;
using System.Drawing;
using System.Windows;
using System.Windows.Threading;

namespace CortexTransl.App.Services.Capture;

public sealed class ScreenCaptureService : IScreenCaptureService
{
    private readonly object _gate = new();
    private WindowsGraphicsWindowCapturer? _capturer;
    private DateTime _nextGraphicsCaptureAttemptUtc;

    public Task<Bitmap> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        region = ScreenCoordinates.ClampToVirtualScreen(region);
        if (region.IsEmpty)
        {
            throw new InvalidOperationException("Select the dialogue region first.");
        }

        Bitmap? captured = null;
        var target = CaptureWindowTarget.Find(region);
        try
        {
            captured = TryCaptureWithGraphics(region, target);
            // A valid GPU frame can contain a dark dialogue box. Sampling its
            // brightness is not a reliable way to decide whether capture failed.
            if (captured is not null)
            {
                OverlayWindow.RestoreCapturePlacement(region);
                return Task.FromResult(captured);
            }

            captured?.Dispose();
        }
        catch (Exception ex)
        {
            captured?.Dispose();
            AppLog.Write("screen-capture", ex);
            ResetCapturer(TimeSpan.FromSeconds(2));
        }

        return Task.FromResult(GdiScreenCapture.Capture(region, target));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _capturer?.Dispose();
            _capturer = null;
        }
    }

    private Bitmap? TryCaptureWithGraphics(CaptureRegion region, CaptureWindowTarget? target)
    {
        if (target is null)
        {
            return null;
        }

        lock (_gate)
        {
            if (_capturer is null && DateTime.UtcNow < _nextGraphicsCaptureAttemptUtc)
            {
                return null;
            }
        }

        try
        {
            var capturer = GetOrCreateCapturer(target.Handle);
            if (capturer is null)
            {
                lock (_gate)
                {
                    _nextGraphicsCaptureAttemptUtc = DateTime.UtcNow.AddSeconds(5);
                }

                return null;
            }

            return capturer.CaptureRegion(region, target.Bounds, TimeSpan.FromMilliseconds(80));
        }
        catch (Exception ex)
        {
            AppLog.Write("graphics-capture", ex);
            ResetCapturer(TimeSpan.FromSeconds(2));
            return null;
        }
    }

    private WindowsGraphicsWindowCapturer? GetOrCreateCapturer(nint window)
    {
        lock (_gate)
        {
            if (_capturer is not null && _capturer.Window == window)
            {
                return _capturer;
            }

            _capturer?.Dispose();
            _capturer = null;
        }

        var created = CreateOnUi(() => WindowsGraphicsWindowCapturer.TryCreate(window));
        if (created is null)
        {
            lock (_gate)
            {
                _nextGraphicsCaptureAttemptUtc = DateTime.UtcNow.AddSeconds(5);
            }

            return null;
        }

        lock (_gate)
        {
            _capturer?.Dispose();
            _capturer = created;
            _nextGraphicsCaptureAttemptUtc = DateTime.MinValue;
            return _capturer;
        }
    }

    private void ResetCapturer(TimeSpan retryDelay)
    {
        lock (_gate)
        {
            try
            {
                _capturer?.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Write("graphics-capture-dispose", ex);
            }

            _capturer = null;
            _nextGraphicsCaptureAttemptUtc = DateTime.UtcNow.Add(retryDelay);
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
