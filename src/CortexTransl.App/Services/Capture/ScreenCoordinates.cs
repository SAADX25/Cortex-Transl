using CortexTransl.App.Models;
using System.Runtime.InteropServices;
using System.Windows;

namespace CortexTransl.App.Services.Capture;

public static class ScreenCoordinates
{
    private const int MonitorDefaultToNearest = 2;
    private const int EffectiveDpi = 0;
    private const double DefaultDpi = 96.0;

    public static CaptureRegion GetVirtualScreenPhysical()
    {
        var left = GetSystemMetrics(SystemMetric.VirtualScreenX);
        var top = GetSystemMetrics(SystemMetric.VirtualScreenY);
        var width = GetSystemMetrics(SystemMetric.VirtualScreenWidth);
        var height = GetSystemMetrics(SystemMetric.VirtualScreenHeight);
        return new CaptureRegion(left, top, width, height);
    }

    public static CaptureRegion ClampToVirtualScreen(CaptureRegion region)
    {
        if (region.IsEmpty)
        {
            return CaptureRegion.Empty;
        }

        var screen = GetVirtualScreenPhysical();
        var x = Math.Max(screen.X, region.X);
        var y = Math.Max(screen.Y, region.Y);
        var right = Math.Min(screen.X + screen.Width, region.X + region.Width);
        var bottom = Math.Min(screen.Y + screen.Height, region.Y + region.Height);
        var width = right - x;
        var height = bottom - y;
        return width <= 0 || height <= 0
            ? CaptureRegion.Empty
            : new CaptureRegion(x, y, width, height);
    }

    public static CaptureRegion GetMonitorWorkArea(CaptureRegion region)
    {
        var rect = new NativeRect(region.X, region.Y, region.X + region.Width, region.Y + region.Height);
        var monitor = MonitorFromRect(ref rect, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == nint.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return GetVirtualScreenPhysical();
        }

        var work = info.Work;
        return new CaptureRegion(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
    }

    public static DpiScale GetDpiForRegion(CaptureRegion region)
    {
        var nativeRect = region.IsEmpty
            ? new NativeRect(0, 0, 1, 1)
            : new NativeRect(region.X, region.Y, region.X + region.Width, region.Y + region.Height);

        var monitor = MonitorFromRect(ref nativeRect, MonitorDefaultToNearest);
        if (monitor != nint.Zero && GetDpiForMonitor(monitor, EffectiveDpi, out var dpiX, out var dpiY) == 0)
        {
            return new DpiScale(dpiX / DefaultDpi, dpiY / DefaultDpi);
        }

        return new DpiScale(1, 1);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(SystemMetric metric);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref NativeRect rect, int flags);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private enum SystemMetric
    {
        VirtualScreenX = 76,
        VirtualScreenY = 77,
        VirtualScreenWidth = 78,
        VirtualScreenHeight = 79
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }
}
