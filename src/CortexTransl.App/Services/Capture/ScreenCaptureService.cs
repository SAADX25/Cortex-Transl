using CortexTransl.App.Models;
using CortexTransl.App.Utils;
using CortexTransl.App.Services.Diagnostics;
using System.Drawing;

namespace CortexTransl.App.Services.Capture;

public sealed class ScreenCaptureService : IScreenCaptureService
{
    private readonly PerformanceDiagnostics _diagnostics;
    private readonly Func<CaptureRegion, bool, CaptureWindowTarget?> _findTarget;
    public ScreenCaptureService(PerformanceDiagnostics? diagnostics = null)
        : this(diagnostics ?? new(), CaptureWindowTarget.Find) { }

    internal ScreenCaptureService(PerformanceDiagnostics diagnostics, Func<CaptureRegion, bool, CaptureWindowTarget?> findTarget)
    {
        _diagnostics = diagnostics;
        _findTarget = findTarget;
    }
    private readonly object _gate = new();
    private WindowsGraphicsWindowCapturer? _capturer;
    private DateTime _nextGraphicsCaptureAttemptUtc;
    private CaptureWindowTarget? _boundTarget;
    private bool _bindingAttempted;
    private bool _listMode;
    private bool _suspended;
    private bool _disposed;
    private readonly string _sessionIdentity = Guid.NewGuid().ToString("N");
    private string? _cacheIdentity;
    public string CacheIdentity { get { lock (_gate) return _cacheIdentity ?? _sessionIdentity; } }

    public void BindTarget(CaptureRegion region, bool listMode, bool onlyIfUnbound = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _suspended = false;
            if (onlyIfUnbound && _bindingAttempted && _listMode == listMode) return;
            _bindingAttempted = true;
            _listMode = listMode;
            _boundTarget = _findTarget(region, listMode);
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
            ResetCapturer(TimeSpan.Zero);
        }
    }

    public Task<Bitmap> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken = default)
    {
        // Serialize HWND selection, readback, stop, and disposal. FrameArrived
        // remains free-threaded and never takes this service lock.
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_suspended) throw new CaptureUnavailableException("Translation capture is stopped.");
            region = ScreenCoordinates.ClampToVirtualScreen(region);
            if (region.IsEmpty) throw new InvalidOperationException("Select the dialogue region first.");

            var target = _boundTarget?.Refresh(region);
            if (target is null)
            {
                ResetCapturer(TimeSpan.Zero);
                throw new CaptureUnavailableException("Selected game window is unavailable or moved. Select its dialogue again with F9.");
            }
            // Dark GPU frames are valid; brightness is not a failure signal.
            var captured = TryCaptureWithGraphics(region, target);
            if (captured is null)
            {
                if (!_listMode)
                    throw new CaptureUnavailableException("Window capture is unavailable. Use a windowed or borderless game and select it again with F9.");
                captured = GdiScreenCapture.Capture(region, target);
            }
            if (cancellationToken.IsCancellationRequested)
            {
                captured.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return Task.FromResult(captured);
        }
    }

    public void ReleaseSession()
    {
        lock (_gate)
        {
            _suspended = true;
            ResetCapturer(TimeSpan.Zero);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _suspended = true;
            ResetCapturer(TimeSpan.Zero);
        }
    }

    // These helpers run only under _gate, including creation, so stop/rebind
    // cannot publish or dispose a capturer halfway through a readback.
    private Bitmap? TryCaptureWithGraphics(CaptureRegion region, CaptureWindowTarget target)
    {
        if (_capturer is null && DateTime.UtcNow < _nextGraphicsCaptureAttemptUtc) return null;
        try
        {
            if (_capturer is not null && _capturer.Window != target.Handle) ResetCapturer(TimeSpan.Zero);
            _capturer ??= WindowsGraphicsWindowCapturer.TryCreate(target.Handle, _diagnostics);
            if (_capturer is null)
            {
                _nextGraphicsCaptureAttemptUtc = DateTime.UtcNow.AddSeconds(5);
                return null;
            }
            return _capturer.CaptureRegion(region, target.Bounds, TimeSpan.FromMilliseconds(80));
        }
        catch (Exception ex)
        {
            AppLog.Write("graphics-capture", ex);
            ResetCapturer(TimeSpan.FromSeconds(2));
            return null;
        }
    }

    private void ResetCapturer(TimeSpan retryDelay)
    {
        try { _capturer?.Dispose(); }
        catch (Exception ex) { AppLog.Write("graphics-capture-dispose", ex); }
        finally
        {
            _capturer = null;
            _nextGraphicsCaptureAttemptUtc = DateTime.UtcNow.Add(retryDelay);
        }
    }
}
