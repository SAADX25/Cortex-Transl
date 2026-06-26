using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace CortexTransl.App.Services.Hotkeys;

public class HotkeyEventArgs : EventArgs
{
    public Key Key { get; }
    public HotkeyEventArgs(Key key) => Key = key;
}

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    
    // We will use the virtual key as the Hotkey ID to allow multiple registrations
    private readonly HashSet<int> _registeredKeys = [];

    private HwndSource? _source;
    private nint _handle;

    public event EventHandler<HotkeyEventArgs>? HotkeyPressed;

    public bool Register(Window window, Key key)
    {
        if (_handle == nint.Zero)
        {
            _handle = new WindowInteropHelper(window).Handle;
            _source = HwndSource.FromHwnd(_handle);
            _source?.AddHook(WndProc);
        }

        if (_source is null)
        {
            return false;
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        int hotkeyId = (int)virtualKey;
        
        if (_registeredKeys.Contains(hotkeyId))
        {
            return true;
        }

        bool registered = RegisterHotKey(_handle, hotkeyId, ModNoRepeat, virtualKey);
        if (registered)
        {
            _registeredKeys.Add(hotkeyId);
        }

        return registered;
    }

    public void Dispose()
    {
        foreach (var id in _registeredKeys)
        {
            UnregisterHotKey(_handle, id);
        }
        _registeredKeys.Clear();

        _source?.RemoveHook(WndProc);
        _source = null;
        _handle = nint.Zero;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            int id = wParam.ToInt32();
            if (_registeredKeys.Contains(id))
            {
                Key key = KeyInterop.KeyFromVirtualKey(id);
                HotkeyPressed?.Invoke(this, new HotkeyEventArgs(key));
                handled = true;
            }
        }

        return nint.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
