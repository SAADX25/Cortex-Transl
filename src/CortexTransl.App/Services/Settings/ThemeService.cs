using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CortexTransl.App.Services.Settings;

public sealed class ThemeService
{
    private const string LightTheme = "Light";
    private const string DarkTheme = "Dark";
    private const string SystemTheme = "System";
    private const string LightThemePath = "Views/Styles/Theme.Light.xaml";
    private const string DarkThemePath = "Views/Styles/Theme.Dark.xaml";
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    public string CurrentTheme { get; private set; } = DarkTheme;
    public string EffectiveTheme { get; private set; } = DarkTheme;

    public void ApplyTheme(string theme)
    {
        CurrentTheme = NormalizeTheme(theme);
        EffectiveTheme = CurrentTheme.Equals(SystemTheme, StringComparison.OrdinalIgnoreCase)
            ? ResolveSystemTheme()
            : CurrentTheme;

        var sourcePath = EffectiveTheme.Equals(DarkTheme, StringComparison.OrdinalIgnoreCase)
            ? DarkThemePath
            : LightThemePath;

        var resources = Application.Current.Resources.MergedDictionaries;
        var existingTheme = resources.FirstOrDefault(IsThemeDictionary);
        var nextTheme = new ResourceDictionary
        {
            Source = new Uri(sourcePath, UriKind.Relative)
        };

        if (existingTheme is null)
        {
            resources.Insert(0, nextTheme);
        }
        else
        {
            var index = resources.IndexOf(existingTheme);
            resources[index] = nextTheme;
        }

        ApplyNativeTitleBars(EffectiveTheme);
    }

    public static string NormalizeTheme(string? theme)
    {
        if (string.IsNullOrWhiteSpace(theme))
        {
            return DarkTheme;
        }

        return theme.Trim().ToLowerInvariant() switch
        {
            "dark" => DarkTheme,
            "system" => SystemTheme,
            _ => LightTheme
        };
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString;
        return source is not null
            && (source.EndsWith("Theme.Light.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("Theme.Dark.xaml", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var appsUseLightTheme = key?.GetValue("AppsUseLightTheme");
            return appsUseLightTheme is int value && value == 0
                ? DarkTheme
                : LightTheme;
        }
        catch
        {
            return LightTheme;
        }
    }

    private static void ApplyNativeTitleBars(string effectiveTheme)
    {
        if (Application.Current is null)
        {
            return;
        }

        var useDarkTitleBar = effectiveTheme.Equals(DarkTheme, StringComparison.OrdinalIgnoreCase);

        foreach (Window window in Application.Current.Windows)
        {
            ApplyNativeTitleBar(window, useDarkTitleBar);
        }
    }

    private static void ApplyNativeTitleBar(Window window, bool useDarkTitleBar)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var value = useDarkTitleBar ? 1 : 0;
            var size = Marshal.SizeOf<int>();
            var result = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, size);
            if (result != 0)
            {
                value = useDarkTitleBar ? 1 : 0;
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref value, size);
            }
        }
        catch
        {
            // Native title-bar theming is best effort and must never block the app.
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);
}
