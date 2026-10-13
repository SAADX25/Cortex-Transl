using CortexTransl.App.Models;
using CortexTransl.App.Utils;
using CortexTransl.App.Views;
using CortexTransl.App.Services.Diagnostics;
using System.Drawing;

namespace CortexTransl.App.Services.Capture;

public sealed class ScreenCaptureService : IScreenCaptureService
{
    private readonly PerformanceDiagnostics _diagnostics;
    public ScreenCaptureService(PerformanceDiagnostics? diagnostics = null) { _diagnostics = diagnostics ?? new(); }
    private readonly object _gate = new();
    private WindowsGraphicsWindowCapturer? _capturer;
    private DateTime _nextGraphicsCaptureAttemptUtc;
    private CaptureWindowTarget? _boundTarget;
    private bool _bindingAttempted;
    private bool _listMode;
    private readonly string _sessionIdentity = Guid.NewGuid().ToString("N");
    private string? _cacheIdentity;
    public string CacheIdentity { get { lock (_gate) return _cacheIdentity ?? _sessionIdentity; } }

    public void BindTarget(CaptureRegion region, bool listMode, bool onlyIfUnbound = false)
    {
        lock (_gate)
        {
            if (onlyIfUnbound && _bindingAttempted && _listMode == listMode) return;
            _bindingAttempted = true;
            _listMode = listMode;
            _boundTarget = CaptureWindowTarget.Find(region, allowDesktop: listMode);
            _cacheIdentity = _sessionIdentity;
            try
            {
                if (_boundTarget is not null)
                {
                    using var process = System.Diagnostics.Process.GetProcessById((int)_boundTarget.ProcessId);
                    var executable = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(executable)) _cacheIdentity = TextHasher.Sha256(executable.ToUpperInvariant());
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException)
            {
                // A protected process gets a session-specific scope rather than
                // a shared executable-name scope that could collide with a game.
            }
            _capturer?.Dispose();
            _capturer = null;
            _nextGraphicsCaptureAttemptUtc = DateTime.MinValue;
        }
    }

    public Task<Bitmap> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        region = ScreenCoordinates.ClampToVirtualScreen(region);
        if (region.IsEmpty)
        {
            throw new InvalidOperationException("Select the dialogue region first.");
        }

        Bitmap? captured = null;
        CaptureWindowTarget? target;
        lock (_gate) target = _boundTarget?.Refresh(region);
        if (target is null)
            throw new CaptureUnavailableException("Selected game window is unavailable or moved. Select its dialogue again with F9.");
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

        if (!_listMode)
            throw new CaptureUnavailableException("Window capture is unavailable. Use a windowed or borderless game and select it again with F9.");
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

        var created = WindowsGraphicsWindowCapturer.TryCreate(window, _diagnostics);
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

}
