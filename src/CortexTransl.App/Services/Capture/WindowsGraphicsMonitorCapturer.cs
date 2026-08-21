using CortexTransl.App.Models;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace CortexTransl.App.Services.Capture;

internal sealed class WindowsGraphicsMonitorCapturer : IDisposable
{
    private readonly nint _monitor;
    private readonly object _gate = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winRtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly ManualResetEventSlim _frameReady = new(false);

    private Bitmap? _lastFrame;
    private bool _disposed;

    private WindowsGraphicsMonitorCapturer(
        nint monitor,
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDirect3DDevice winRtDevice,
        GraphicsCaptureItem item,
        Direct3D11CaptureFramePool framePool,
        GraphicsCaptureSession session)
    {
        _monitor = monitor;
        _device = device;
        _context = context;
        _winRtDevice = winRtDevice;
        _item = item;
        _framePool = framePool;
        _session = session;
        _framePool.FrameArrived += OnFrameArrived;
        _session.StartCapture();
    }

    public nint Monitor => _monitor;

    public static WindowsGraphicsMonitorCapturer? TryCreate(nint monitor)
    {
        if (monitor == nint.Zero || !GraphicsCaptureSession.IsSupported())
        {
            return null;
        }

        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        IDirect3DDevice? winRtDevice = null;
        GraphicsCaptureItem? item = null;
        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession? session = null;

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
                return null;
            }

            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            winRtDevice = GraphicsCaptureInterop.CreateWinRtDevice(dxgiDevice.NativePointer);
            item = GraphicsCaptureInterop.CreateItemForMonitor(monitor);
            if (item.Size.Width < 2 || item.Size.Height < 2)
            {
                throw new InvalidOperationException("Monitor capture size is invalid.");
            }

            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                winRtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                item.Size);
            session = framePool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;

            return new WindowsGraphicsMonitorCapturer(
                monitor,
                device,
                context,
                winRtDevice,
                item,
                framePool,
                session);
        }
        catch
        {
            session?.Dispose();
            framePool?.Dispose();
            winRtDevice?.Dispose();
            context?.Dispose();
            device?.Dispose();
            return null;
        }
    }

    public Bitmap? CaptureRegion(CaptureRegion region, TimeSpan wait)
    {
        if (_disposed)
        {
            return null;
        }

        if (_lastFrame is null && !_disposed)
        {
            try
            {
                _frameReady.Wait(TimeSpan.FromMilliseconds(Math.Max(wait.TotalMilliseconds, 400)));
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }

        lock (_gate)
        {
            if (_disposed || _lastFrame is null)
            {
                return null;
            }

            var monitor = ScreenCoordinates.GetMonitorPhysical(_monitor);
            var crop = Rectangle.Intersect(
                new Rectangle(0, 0, _lastFrame.Width, _lastFrame.Height),
                new Rectangle(region.X - monitor.X, region.Y - monitor.Y, region.Width, region.Height));
            if (crop.Width < 2 || crop.Height < 2)
            {
                return null;
            }

            var result = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(result);
            graphics.DrawImage(_lastFrame, new Rectangle(0, 0, crop.Width, crop.Height), crop, GraphicsUnit.Pixel);
            return result;
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            var bitmap = CopyFrame(frame);
            if (bitmap is null)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    bitmap.Dispose();
                    return;
                }

                _lastFrame?.Dispose();
                _lastFrame = bitmap;
            }

            if (!_disposed)
            {
                _frameReady.Set();
            }
        }
        catch
        {
        }
    }

    private unsafe Bitmap? CopyFrame(Direct3D11CaptureFrame frame)
    {
        if (_disposed)
        {
            return null;
        }

        var texturePointer = GraphicsCaptureInterop.GetD3D11TexturePointer(frame.Surface);
        if (texturePointer == nint.Zero)
        {
            return null;
        }

        using var source = new ID3D11Texture2D(texturePointer);
        var description = source.Description;
        if (description.Width < 2 || description.Height < 2)
        {
            return null;
        }

        description.Usage = ResourceUsage.Staging;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.MiscFlags = ResourceOptionFlags.None;
        description.MipLevels = 1;
        description.ArraySize = 1;
        description.SampleDescription = new SampleDescription(1, 0);

        using var staging = _device.CreateTexture2D(description);
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            _context.CopyResource(staging, source);
            _context.Map(staging, 0, MapMode.Read, MapFlags.None, out var mapped);
            try
            {
                var width = (int)description.Width;
                var height = (int)description.Height;
                var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, width, height);
                var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var sourcePitch = (int)mapped.RowPitch;
                    var destinationPitch = data.Stride;
                    var rowBytes = width * 4;
                    for (var y = 0; y < height; y++)
                    {
                        NativeMemory.Copy(
                            (byte*)mapped.DataPointer + (y * sourcePitch),
                            (byte*)data.Scan0 + (y * destinationPitch),
                            (nuint)rowBytes);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                return bitmap;
            }
            finally
            {
                if (!_disposed)
                {
                    _context.Unmap(staging, 0);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _framePool.FrameArrived -= OnFrameArrived;
            try
            {
                _session.Dispose();
            }
            catch
            {
            }

            try
            {
                _framePool.Dispose();
            }
            catch
            {
            }

            try
            {
                _winRtDevice.Dispose();
            }
            catch
            {
            }

            try
            {
                _context.Dispose();
            }
            catch
            {
            }

            try
            {
                _device.Dispose();
            }
            catch
            {
            }

            _lastFrame?.Dispose();
            _lastFrame = null;
        }

        try
        {
            _frameReady.Set();
        }
        catch
        {
        }

        try
        {
            _frameReady.Dispose();
        }
        catch
        {
        }
    }
}
