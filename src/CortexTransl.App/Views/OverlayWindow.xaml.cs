using CortexTransl.App.Models;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CortexTransl.App.Views;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    public void UpdateText(string text, CaptureRegion region, double fontSize, double opacity)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var left = region.X / dpi.DpiScaleX;
        var top = region.Y / dpi.DpiScaleY;
        var width = region.Width / dpi.DpiScaleX;
        var height = region.Height / dpi.DpiScaleY;

        TranslationText.Text = text;
        TranslationText.FontSize = fontSize;
        TranslationText.MaxWidth = Math.Max(360, Math.Min(920, width * 1.35));

        var alpha = (byte)Math.Clamp(opacity * 255, 70, 255);
        OverlayChrome.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, 2, 6, 23));

        Show();
        UpdateLayout();

        Left = left;
        Top = top + height + 12;

        var screenBottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        if (Top + ActualHeight > screenBottom)
        {
            Top = Math.Max(SystemParameters.VirtualScreenTop, top - ActualHeight - 12);
        }

        var screenRight = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        if (Left + ActualWidth > screenRight)
        {
            Left = Math.Max(SystemParameters.VirtualScreenLeft, screenRight - ActualWidth - 12);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, extendedStyle | WsExTransparent | WsExToolWindow | WsExLayered);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hwnd, int index, int newStyle);
}
