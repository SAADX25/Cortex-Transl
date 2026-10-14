using CortexTransl.App.Models;
using CortexTransl.App.Services.Diagnostics;
using CortexTransl.App.Utils;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Foundation.Metadata;
using Windows.Security.Authorization.AppCapabilityAccess;
using WinRT;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace CortexTransl.App.Services.Capture;

internal sealed class WindowsGraphicsWindowCapturer : IDisposable
{
    private readonly nint _window;
    private readonly object _gate = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winRtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly AutoResetEvent _frameReady = new(false);
    private readonly PerformanceDiagnostics _diagnostics;
    private Direct3D11CaptureFrame? _latestFrame;
    private ID3D11Texture2D? _staging;
    private SizeInt32 _frameSize;
    private Format _stagingFormat;
    private int _stagingWidth, _stagingHeight;
    private Bitmap? _cachedCrop;
    private Rectangle? _cachedLocation;
    private volatile Exception? _captureError;
    private volatile bool _disposed;
    private readonly CancellationTokenSource _borderRequestCancellation = new();
    private Task _borderRequestTask = Task.CompletedTask;
    public long ReadbackCount { get; private set; }
    public long ReadbackBytes { get; private set; }
    public long StagingAllocations { get; private set; }

    private WindowsGraphicsWindowCapturer(nint window, ID3D11Device device, ID3D11DeviceContext context,
        IDirect3DDevice winRtDevice, GraphicsCaptureItem item, Direct3D11CaptureFramePool framePool,
        GraphicsCaptureSession session, PerformanceDiagnostics? diagnostics)
    {
        _window = window; _device = device; _context = context; _winRtDevice = winRtDevice;
        _item = item; _frameSize = item.Size; _framePool = framePool; _session = session;
        _diagnostics = diagnostics ?? new();
        _framePool.FrameArrived += OnFrameArrived;
    }
    public nint Window => _window;
    public static WindowsGraphicsWindowCapturer? TryCreate(nint window, PerformanceDiagnostics? diagnostics = null)
    {
        if (window == nint.Zero || !GraphicsCaptureSession.IsSupported())
        {
            return null;
        }

        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        IDirect3DDevice? winRtDevice = null;
        GraphicsCaptureItem? item = null;
        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession? session = null;
        WindowsGraphicsWindowCapturer? capturer = null;

        try
        {
            var createdDevice = D3D11.D3D11CreateDevice(
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0);
            device = createdDevice;
            context = createdDevice.ImmediateContext;
            if (device is null || context is null)
            {
                throw new InvalidOperationException("The graphics capture device was not initialized.");
            }

            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            winRtDevice = GraphicsCaptureInterop.CreateWinRtDevice(dxgiDevice.NativePointer);
            item = GraphicsCaptureInterop.CreateItemForWindow(window);
            if (item.Size.Width < 2 || item.Size.Height < 2)
            {
                throw new InvalidOperationException("Window capture size is invalid.");
            }



            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                winRtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                item.Size);
            session = framePool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            var borderlessSupported = ConfigureBorderlessCapture(session);
            capturer = new WindowsGraphicsWindowCapturer(
                window,
                device,
                context,
                winRtDevice,
                item,
                framePool,
                session, diagnostics);
            session.StartCapture();
            if (borderlessSupported)
                capturer._borderRequestTask = Task.Run(() => capturer.RequestBorderlessCaptureAsync(capturer._borderRequestCancellation.Token));
            return capturer;
        }
        catch (Exception ex)
        {
            AppLog.Write("graphics-capture-create", ex);
            if (capturer is not null)
            {
                capturer.Dispose();
                return null;
            }
            ResourceCleanup.Dispose(session, "capture-session-dispose");
            ResourceCleanup.Dispose(framePool, "capture-pool-dispose");
            ResourceCleanup.Dispose(winRtDevice, "capture-winrt-dispose");
            ResourceCleanup.Dispose(context, "capture-context-dispose");
            ResourceCleanup.Dispose(device, "capture-device-dispose");
            return null;
        }
    }

    private static bool ConfigureBorderlessCapture(GraphicsCaptureSession session)
    {
        if (!ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
            return false;

        try
        {
            session.IsBorderRequired = false;
            return true;
        }
        catch (Exception ex)
        {
            // Borderless capture is optional; an unsupported API or denied
            // capability must not disable the translation capture session.
            AppLog.Write("capture-border", ex);
            return false;
        }
    }

    private async Task RequestBorderlessCaptureAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Consent is optional and must never delay capture or OCR. Reapply
            // the property after permission arrives; Windows enforces denial.
            var access = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)
                .AsTask(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || cancellationToken.IsCancellationRequested) return;
                _session.IsBorderRequired = false;
            }
            if (access != AppCapabilityAccessStatus.Allowed)
                AppLog.Write("capture-border", $"Windows retained the capture border: {access}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { if (!_disposed) AppLog.Write("capture-border", ex); }
    }

    public Bitmap? CaptureRegion(CaptureRegion region, CaptureRegion windowBounds, TimeSpan wait)
    {
        // Only the detection worker performs D3D commands/readback. The frame
        // callback just transfers ownership of the latest GPU frame atomically.
        var frame = AcquireFrame(wait, region, windowBounds);
        if (frame is null)
        {
            lock (_gate)
            {
                if (_disposed) return null;
                if (_captureError is not null)
                    throw new InvalidOperationException("Window capture failed. Recreate the selected window session.", _captureError);
                var requested = CaptureCrop.Calculate(region, windowBounds, _frameSize.Width, _frameSize.Height);
                // A static window need not emit frames. Revalidate its existing
                // region without doing another GPU copy or losing dialogue.
                return requested.HasValue && requested == _cachedLocation && _cachedCrop is not null
                    ? (Bitmap)_cachedCrop.Clone() : null;
            }
        }
        Bitmap? result;
        SizeInt32 size;
        using (frame)
        {
            lock (_gate)
            {
                if (_disposed) return null;
                if (_captureError is not null) throw new InvalidOperationException("Window capture failed. Recreate the selected window session.", _captureError);
                size = frame.ContentSize;
                var crop = CaptureCrop.Calculate(region, windowBounds, size.Width, size.Height);
                if (!crop.HasValue) return null;
                result = CopyRegion(frame, crop.Value);
            }
        }
        try
        {
            lock (_gate)
            {
                if (!_disposed && size.Width > 0 && size.Height > 0 && (size.Width != _frameSize.Width || size.Height != _frameSize.Height))
                {
                    Interlocked.Exchange(ref _latestFrame, null)?.Dispose();
                    _framePool.Recreate(_winRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                    _frameSize = size;
                }
            }
            return result;
        }
        catch { result?.Dispose(); throw; }
    }

    private Direct3D11CaptureFrame? AcquireFrame(TimeSpan wait, CaptureRegion region, CaptureRegion bounds)
    {
        using var waiting = _diagnostics.Measure(LatencyStage.FrameWait);
        var frame = Interlocked.Exchange(ref _latestFrame, null);
        if (frame is null && !_disposed)
        {
            lock (_gate)
            {
                if (_cachedCrop is not null && CaptureCrop.Calculate(region, bounds, _frameSize.Width, _frameSize.Height) == _cachedLocation)
                    return null;
            }
            try { _frameReady.WaitOne(wait); }
            catch (ObjectDisposedException) { return null; }
            frame = Interlocked.Exchange(ref _latestFrame, null);
        }
        return frame;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed) return;
        try
        {
            var frame = sender.TryGetNextFrame();
            if (frame is null) return;
            if (_disposed) { frame.Dispose(); return; }
            Interlocked.Exchange(ref _latestFrame, frame)?.Dispose();
            if (_disposed) Interlocked.Exchange(ref _latestFrame, null)?.Dispose();
            else _frameReady.Set();
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { if (!_disposed) _captureError = ex; }
    }

    private unsafe Bitmap? CopyRegion(Direct3D11CaptureFrame frame, Rectangle crop)
    {
        using var measurement = _diagnostics.Measure(LatencyStage.GpuCopyReadback);
        var pointer = GraphicsCaptureInterop.GetD3D11TexturePointer(frame.Surface);
        if (pointer == 0) return null;
        using var source = new ID3D11Texture2D(pointer);
        var description = source.Description;
        if (crop.Right > description.Width || crop.Bottom > description.Height
            || description.Format != Format.B8G8R8A8_UNorm || description.SampleDescription.Count != 1) return null;
        if (_staging is null || _stagingWidth != crop.Width || _stagingHeight != crop.Height || _stagingFormat != description.Format)
        {
            _staging?.Dispose();
            description.Width = (uint)crop.Width; description.Height = (uint)crop.Height;
            description.Usage = ResourceUsage.Staging; description.BindFlags = BindFlags.None;
            description.CPUAccessFlags = CpuAccessFlags.Read; description.MiscFlags = ResourceOptionFlags.None;
            description.MipLevels = description.ArraySize = 1;
            description.SampleDescription = new SampleDescription(1, 0);
            _staging = _device.CreateTexture2D(description);
            _stagingWidth = crop.Width; _stagingHeight = crop.Height; _stagingFormat = description.Format;
            StagingAllocations++;
        }
        _context.CopySubresourceRegion(_staging, 0, 0, 0, 0, source, 0, new Box(crop.Left, crop.Top, 0, crop.Right, crop.Bottom, 1));
        // Map may wait for this small GPU copy, but never runs on the UI or frame
        // callback. One staging resource remains resident until dimensions change.
        _context.Map(_staging, 0, MapMode.Read, MapFlags.None, out var mapped).CheckError();
        try
        {
            if (_cachedCrop is null || _cachedCrop.Width != crop.Width || _cachedCrop.Height != crop.Height)
            {
                _cachedCrop?.Dispose();
                _cachedCrop = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
            }
            var bitmap = _cachedCrop;
            try
            {
                var data = bitmap.LockBits(new Rectangle(0, 0, crop.Width, crop.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    for (var y = 0; y < crop.Height; y++)
                        NativeMemory.Copy((byte*)mapped.DataPointer + y * (int)mapped.RowPitch,
                            (byte*)data.Scan0 + y * data.Stride, (nuint)(crop.Width * 4));
                }
                finally { bitmap.UnlockBits(data); }
                ReadbackCount++; ReadbackBytes += (long)crop.Width * crop.Height * 4;
                _cachedLocation = crop;
                return (Bitmap)bitmap.Clone();
            }
            catch { bitmap.Dispose(); _cachedCrop = null; _cachedLocation = null; throw; }
        }
        finally { _context.Unmap(_staging, 0); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _borderRequestCancellation.Cancel();
            try { _framePool.FrameArrived -= OnFrameArrived; }
            catch (Exception ex) { AppLog.Write("capture-event-unsubscribe", ex); }
            ResourceCleanup.Dispose(_session, "capture-session-dispose");
            ResourceCleanup.Dispose(_framePool, "capture-pool-dispose");
            ResourceCleanup.Dispose(Interlocked.Exchange(ref _latestFrame, null), "capture-frame-dispose");
            ResourceCleanup.Dispose(_staging, "capture-staging-dispose"); _staging = null;
            ResourceCleanup.Dispose(_cachedCrop, "capture-crop-dispose"); _cachedCrop = null;
            ResourceCleanup.Dispose(_winRtDevice, "capture-winrt-dispose");
            ResourceCleanup.Dispose(_context, "capture-context-dispose");
            ResourceCleanup.Dispose(_device, "capture-device-dispose");
        }
        try { _frameReady.Set(); } catch (ObjectDisposedException) { }
        ResourceCleanup.Dispose(_frameReady, "capture-event-dispose");
        _ = _borderRequestTask.ContinueWith(_ => _borderRequestCancellation.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
