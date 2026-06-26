using Microsoft.Win32;
using System.Windows;

namespace CortexTransl.App.Services.Settings;

public sealed class ThemeService
{
    private const string LightTheme = "Light";
    private const string DarkTheme = "Dark";
    private const string SystemTheme = "System";
    private const string LightThemePath = "Views/Styles/Theme.Light.xaml";
    private const string DarkThemePath = "Views/Styles/Theme.Dark.xaml";

    public string CurrentTheme { get; private set; } = LightTheme;

    public void ApplyTheme(string theme)
    {
        CurrentTheme = NormalizeTheme(theme);
        var effectiveTheme = CurrentTheme.Equals(SystemTheme, StringComparison.OrdinalIgnoreCase)
            ? ResolveSystemTheme()
            : CurrentTheme;

        var sourcePath = effectiveTheme.Equals(DarkTheme, StringComparison.OrdinalIgnoreCase)
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
    }

    public static string NormalizeTheme(string? theme)
    {
        if (string.IsNullOrWhiteSpace(theme))
        {
            return LightTheme;
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
}
