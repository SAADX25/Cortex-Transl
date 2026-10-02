using CortexTransl.App.Utils;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace CortexTransl.App.Services.Hotkeys;

public class HotkeyEventArgs : EventArgs
{
    public Key Key { get; }
    public ModifierKeys Modifiers { get; }

    public HotkeyEventArgs(Key key, ModifierKeys modifiers)
    {
        Key = key;
        Modifiers = modifiers;
    }
}

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;

    private readonly Dictionary<int, HotkeyEventArgs> _registeredHotkeys = [];

    private HwndSource? _source;
    private nint _handle;

    public int LastRegistrationError { get; private set; }

    public event EventHandler<HotkeyEventArgs>? HotkeyPressed;

    public bool Register(Window window, Key key)
    {
        return Register(window, key, ModifierKeys.None);
    }

    public bool Register(Window window, Key key, ModifierKeys modifiers)
    {
        LastRegistrationError = 0;
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
        int hotkeyId = GetHotkeyId(key, modifiers);

        if (_registeredHotkeys.ContainsKey(hotkeyId))
        {
            return true;
        }

        var nativeModifiers = (uint)modifiers;
        bool registered = RegisterHotKey(_handle, hotkeyId, nativeModifiers | ModNoRepeat, virtualKey)
            || RegisterHotKey(_handle, hotkeyId, nativeModifiers, virtualKey);
        LastRegistrationError = registered ? 0 : Marshal.GetLastWin32Error();
        if (registered)
        {
            _registeredHotkeys.Add(hotkeyId, new HotkeyEventArgs(key, modifiers));
        }

        return registered;
    }

    public bool IsRegistered(Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        return _registeredHotkeys.ContainsKey(GetHotkeyId(key, modifiers));
    }

    public void Dispose()
    {
        foreach (var id in _registeredHotkeys.Keys)
        {
            if (_handle != nint.Zero)
            {
                UnregisterHotKey(_handle, id);
            }
        }
        _registeredHotkeys.Clear();

        _source?.RemoveHook(WndProc);
        _source = null;
        _handle = nint.Zero;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            int id = wParam.ToInt32();
            if (_registeredHotkeys.TryGetValue(id, out var hotkey))
            {
                var messageModifiers = (ModifierKeys)(lParam.ToInt64() & 0xF);
                if (messageModifiers != hotkey.Modifiers || Keyboard.Modifiers != hotkey.Modifiers)
                {
                    return nint.Zero;
                }

                try
                {
                    HotkeyPressed?.Invoke(this, hotkey);
                }
                catch (Exception ex)
                {
                    AppLog.Write("hotkey", ex);
                }

                handled = true;
            }
        }

        return nint.Zero;
    }

    private static int GetHotkeyId(Key key, ModifierKeys modifiers)
    {
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        return virtualKey | ((int)modifiers << 8);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
