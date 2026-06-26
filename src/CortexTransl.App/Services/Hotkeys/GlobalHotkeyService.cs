using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace CortexTransl.App.Services.Hotkeys;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int HotkeyId = 0x4354;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    private HwndSource? _source;
    private nint _handle;
    private bool _registered;

    public event EventHandler? HotkeyPressed;

    public bool Register(Window window, Key key)
    {
        if (_registered)
        {
            return true;
        }

        _handle = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(_handle);
        if (source is null)
        {
            return false;
        }

        source.AddHook(WndProc);
        _source = source;

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        _registered = RegisterHotKey(_handle, HotkeyId, ModNoRepeat, virtualKey);
        if (!_registered)
        {
            source.RemoveHook(WndProc);
            _source = null;
        }

        return _registered;
    }

    public void Dispose()
    {
        if (_registered)
        {
            UnregisterHotKey(_handle, HotkeyId);
            _registered = false;
        }

        _source?.RemoveHook(WndProc);
        _source = null;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return nint.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
