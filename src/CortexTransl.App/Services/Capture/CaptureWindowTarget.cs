using CortexTransl.App.Models;
using System.Runtime.InteropServices;
using System.Text;

namespace CortexTransl.App.Services.Capture;

internal sealed record CaptureWindowTarget(nint Handle, CaptureRegion Bounds, nint DesktopView = default)
{
    // EnumWindows visits windows from front to back. Skip our own overlay and
    // tool windows, and require the complete OCR region to fit in the source.
    public static CaptureWindowTarget? Find(CaptureRegion region)
    {
        CaptureWindowTarget? target = null;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window))
            {
                return true;
            }

            GetWindowThreadProcessId(window, out var processId);
            if (processId == (uint)Environment.ProcessId)
            {
                return true;
            }

            // Explorer can host its desktop icons in a tool window. Capture that
            // window directly instead of moving our labels away from the icons.
            var desktopView = FindDesktopView(window);
            var windowClass = new StringBuilder(256);
            GetClassName(window, windowClass, windowClass.Capacity);
            if (windowClass.ToString() is "Progman" or "WorkerW" && desktopView == nint.Zero)
            {
                // A wallpaper-only shell window cannot supply icon captions.
                return true;
            }
            if ((GetWindowLong(window, -20) & 0x80) != 0 && desktopView == nint.Zero)
            {
                return true;
            }

            if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            if (DwmGetWindowAttribute(window, 9, out NativeRect bounds, Marshal.SizeOf<NativeRect>()) != 0
                && !GetWindowRect(window, out bounds))
            {
                return true;
            }

            if (bounds.Left > region.X || bounds.Top > region.Y
                || bounds.Right < region.X + region.Width || bounds.Bottom < region.Y + region.Height)
            {
                return true;
            }

            target = new CaptureWindowTarget(window, new CaptureRegion(
                bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), desktopView);
            return false;
        }, nint.Zero);

        return target;
    }

    private static nint FindDesktopView(nint window)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        if (name.ToString() is not ("Progman" or "WorkerW"))
        {
            return nint.Zero;
        }

        nint view = nint.Zero;
        EnumChildWindows(window, (child, _) =>
        {
            name.Clear();
            GetClassName(child, name, name.Capacity);
            if (name.ToString() != "SHELLDLL_DefView" || !IsWindowVisible(child))
            {
                return true;
            }

            view = child;
            return false;
        }, nint.Zero);
        return view;
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint window, EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int length);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect bounds);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect bounds, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
