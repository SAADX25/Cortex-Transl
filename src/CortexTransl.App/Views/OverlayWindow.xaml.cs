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
    private const double ScreenMargin = 18;
    private const double RegionMargin = 14;

    private readonly bool _recordingSafeMode;
    private string? _lastText;
    private double _lastFontSize = double.NaN;
    private double _lastOpacity = double.NaN;
    private double _lastBackgroundOpacity = double.NaN;
    private double _lastMaxWidth = double.NaN;
    private bool? _lastClickThrough;

    public OverlayWindow(bool recordingSafeMode)
    {
        _recordingSafeMode = recordingSafeMode;
        InitializeComponent();
        AllowsTransparency = !recordingSafeMode;
        Background = recordingSafeMode
            ? new SolidColorBrush(Color.FromRgb(2, 6, 23))
            : Brushes.Transparent;
        SourceInitialized += OnSourceInitialized;
    }

    public void UpdateText(string text, CaptureRegion region, OverlaySettings settings)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        ApplyContent(text);
        ApplyVisualSettings(settings);
        UpdateLayout();

        var overlaySize = GetOverlaySize();
        var screen = GetVirtualScreenRect();
        var captureRect = ToDipRect(region, dpi);
        var location = ChooseLocation(settings.PositionPreset, captureRect, overlaySize, screen);
        var adjustedLocation = AvoidCaptureRegion(location, overlaySize, captureRect, screen);

        Left = adjustedLocation.X;
        Top = adjustedLocation.Y;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyExtendedStyles();
    }

    private void ApplyContent(string text)
    {
        if (_lastText == text)
        {
            return;
        }

        TranslationText.Text = text;
        _lastText = text;
    }

    private void ApplyVisualSettings(OverlaySettings settings)
    {
        if (!AreClose(_lastFontSize, settings.FontSize))
        {
            TranslationText.FontSize = settings.FontSize;
            _lastFontSize = settings.FontSize;
        }

        if (!AreClose(_lastMaxWidth, settings.MaxWidth))
        {
            TranslationText.MaxWidth = settings.MaxWidth;
            _lastMaxWidth = settings.MaxWidth;
        }

        if (!AreClose(_lastOpacity, settings.Opacity))
        {
            Opacity = _recordingSafeMode ? 1 : settings.Opacity;
            _lastOpacity = settings.Opacity;
        }

        if (!AreClose(_lastBackgroundOpacity, settings.BackgroundOpacity))
        {
            var minimumOpacity = _recordingSafeMode ? 0.88 : 0.35;
            var backgroundOpacity = Math.Clamp(settings.BackgroundOpacity, minimumOpacity, 1);
            var alpha = (byte)Math.Clamp(backgroundOpacity * 255, 90, 255);
            OverlayChrome.Background = new SolidColorBrush(Color.FromArgb(alpha, 2, 6, 23));
            _lastBackgroundOpacity = settings.BackgroundOpacity;
        }

        OverlayChrome.CornerRadius = _recordingSafeMode ? new CornerRadius(4) : new CornerRadius(8);
        OverlayChrome.Padding = _recordingSafeMode ? new Thickness(22, 14, 22, 14) : new Thickness(24, 16, 24, 16);

        if (_lastClickThrough != settings.ClickThrough)
        {
            _lastClickThrough = settings.ClickThrough;
            ApplyExtendedStyles();
        }
    }

    private Size GetOverlaySize()
    {
        var width = ActualWidth;
        var height = ActualHeight;

        if (width > 0 && height > 0)
        {
            return new Size(width, height);
        }

        Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return DesiredSize;
    }

    private static Rect GetVirtualScreenRect()
    {
        return new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
    }

    private static Rect ToDipRect(CaptureRegion region, DpiScale dpi)
    {
        if (region.IsEmpty)
        {
            return Rect.Empty;
        }

        return new Rect(
            region.X / dpi.DpiScaleX,
            region.Y / dpi.DpiScaleY,
            region.Width / dpi.DpiScaleX,
            region.Height / dpi.DpiScaleY);
    }

    private static Point ChooseLocation(string preset, Rect captureRect, Size overlaySize, Rect screen)
    {
        return preset.ToLowerInvariant() switch
        {
            "top-center" => new Point(
                screen.Left + ((screen.Width - overlaySize.Width) / 2),
                screen.Top + ScreenMargin),
            "bottom-center" => new Point(
                screen.Left + ((screen.Width - overlaySize.Width) / 2),
                screen.Bottom - overlaySize.Height - ScreenMargin),
            _ => ChooseRegionRelativeLocation(captureRect, overlaySize, screen)
        };
    }

    private static Point ChooseRegionRelativeLocation(Rect captureRect, Size overlaySize, Rect screen)
    {
        if (captureRect.IsEmpty)
        {
            return new Point(
                screen.Left + ((screen.Width - overlaySize.Width) / 2),
                screen.Bottom - overlaySize.Height - ScreenMargin);
        }

        var left = captureRect.Left + ((captureRect.Width - overlaySize.Width) / 2);
        var belowTop = captureRect.Bottom + RegionMargin;
        var aboveTop = captureRect.Top - overlaySize.Height - RegionMargin;
        var top = belowTop + overlaySize.Height <= screen.Bottom - ScreenMargin
            ? belowTop
            : aboveTop;

        return new Point(left, top);
    }

    private static Point AvoidCaptureRegion(Point desiredLocation, Size overlaySize, Rect captureRect, Rect screen)
    {
        var clamped = ClampToScreen(desiredLocation, overlaySize, screen);
        if (captureRect.IsEmpty || !BuildRect(clamped, overlaySize).IntersectsWith(captureRect))
        {
            return clamped;
        }

        var candidates = new[]
        {
            new Point(captureRect.Left + ((captureRect.Width - overlaySize.Width) / 2), captureRect.Top - overlaySize.Height - RegionMargin),
            new Point(captureRect.Left + ((captureRect.Width - overlaySize.Width) / 2), captureRect.Bottom + RegionMargin),
            new Point(screen.Left + ((screen.Width - overlaySize.Width) / 2), screen.Top + ScreenMargin),
            new Point(screen.Left + ((screen.Width - overlaySize.Width) / 2), screen.Bottom - overlaySize.Height - ScreenMargin)
        };

        foreach (var candidate in candidates.Select(point => ClampToScreen(point, overlaySize, screen)))
        {
            if (!BuildRect(candidate, overlaySize).IntersectsWith(captureRect))
            {
                return candidate;
            }
        }

        return clamped;
    }

    private static Point ClampToScreen(Point point, Size overlaySize, Rect screen)
    {
        var minLeft = screen.Left + ScreenMargin;
        var maxLeft = screen.Right - overlaySize.Width - ScreenMargin;
        var minTop = screen.Top + ScreenMargin;
        var maxTop = screen.Bottom - overlaySize.Height - ScreenMargin;

        var left = maxLeft < minLeft
            ? minLeft
            : Math.Clamp(point.X, minLeft, maxLeft);
        var top = maxTop < minTop
            ? minTop
            : Math.Clamp(point.Y, minTop, maxTop);

        return new Point(left, top);
    }

    private static Rect BuildRect(Point point, Size size)
    {
        return new Rect(point, size);
    }

    private void ApplyExtendedStyles()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        var extendedStyle = GetWindowLong(handle, GwlExStyle);
        extendedStyle |= WsExToolWindow;

        if (!_recordingSafeMode)
        {
            extendedStyle |= WsExLayered;
        }

        if (_lastClickThrough == true)
        {
            extendedStyle |= WsExTransparent;
        }
        else
        {
            extendedStyle &= ~WsExTransparent;
        }

        SetWindowLong(handle, GwlExStyle, extendedStyle);
    }

    private static bool AreClose(double left, double right)
    {
        return Math.Abs(left - right) < 0.001;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hwnd, int index, int newStyle);
}
