using CortexTransl.App.Models;
using CortexTransl.App.Services.Overlay;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace CortexTransl.App.Views;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int MonitorDefaultToNearest = 0x00000002;
    private const double ScreenMargin = 18;
    private const double SubtitleBottomMargin = 120;
    private const double RegionMargin = 20;

    private readonly bool _recordingSafeMode;
    private string? _lastText;
    private double _lastFontSize = double.NaN;
    private double _lastOpacity = double.NaN;
    private double _lastBackgroundOpacity = double.NaN;
    private double _lastMaxWidth = double.NaN;
    private bool? _lastClickThrough;
    private bool _positionUnlocked;

    public event EventHandler<OverlayPositionChangedEventArgs>? PositionChanged;

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

    public void UpdateText(string text, CaptureRegion region, OverlaySettings settings, bool updatePosition)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        ApplyContent(text);
        ApplyVisualSettings(settings);
        UpdateLayout();

        if (updatePosition)
        {
            UpdatePosition(region, settings, dpi);
        }
    }

    public void ClearText()
    {
        ApplyContent(string.Empty);
    }

    public static string GetScreenLayoutKey()
    {
        return string.Join(
            "|",
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight,
            SystemParameters.WorkArea.Left,
            SystemParameters.WorkArea.Top,
            SystemParameters.WorkArea.Width,
            SystemParameters.WorkArea.Height);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyExtendedStyles();
    }

    private void OnDragHandleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_positionUnlocked || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
            PositionChanged?.Invoke(this, new OverlayPositionChangedEventArgs(Left, Top));
        }
        catch (InvalidOperationException)
        {
        }

        e.Handled = true;
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

        _positionUnlocked = settings.PositionUnlocked;
        DragHandle.Visibility = _positionUnlocked ? Visibility.Visible : Visibility.Collapsed;
        TranslationText.Margin = _positionUnlocked ? new Thickness(0, 28, 0, 0) : new Thickness(0);
        Cursor = Cursors.Arrow;

        if (_lastClickThrough != settings.ClickThrough)
        {
            _lastClickThrough = settings.ClickThrough;
            ApplyExtendedStyles();
        }
    }

    private void UpdatePosition(CaptureRegion region, OverlaySettings settings, DpiScale dpi)
    {
        var overlaySize = GetOverlaySize();
        var captureRect = ToDipRect(region, dpi);
        var screen = GetCurrentMonitorRect(region, dpi);
        var location = settings.UsesSmartPlacement
            ? ChooseSmartLocation(settings, captureRect, overlaySize, screen)
            : ChooseLockedLocation(settings, captureRect, overlaySize, screen);
        var adjustedLocation = ClampToScreen(location, overlaySize, screen);

        Left = adjustedLocation.X;
        Top = adjustedLocation.Y;
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

    private static Rect GetCurrentMonitorRect(CaptureRegion region, DpiScale dpi)
    {
        var nativeRect = region.IsEmpty
            ? new NativeRect(0, 0, 1, 1)
            : new NativeRect(region.X, region.Y, region.X + region.Width, region.Y + region.Height);

        var monitor = MonitorFromRect(ref nativeRect, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };

        if (monitor != nint.Zero && GetMonitorInfo(monitor, ref monitorInfo))
        {
            return ToDipRect(monitorInfo.WorkArea, dpi);
        }

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

    private static Rect ToDipRect(NativeRect rect, DpiScale dpi)
    {
        return new Rect(
            rect.Left / dpi.DpiScaleX,
            rect.Top / dpi.DpiScaleY,
            (rect.Right - rect.Left) / dpi.DpiScaleX,
            (rect.Bottom - rect.Top) / dpi.DpiScaleY);
    }

    private static Point ChooseSmartLocation(OverlaySettings settings, Rect captureRect, Size overlaySize, Rect screen)
    {
        var location = ChoosePresetLocation(settings, captureRect, overlaySize, screen);
        return AvoidCaptureRegion(location, overlaySize, captureRect, screen);
    }

    private static Point ChooseLockedLocation(OverlaySettings settings, Rect captureRect, Size overlaySize, Rect screen)
    {
        return ChoosePresetLocation(settings, captureRect, overlaySize, screen);
    }

    private static Point ChoosePresetLocation(OverlaySettings settings, Rect captureRect, Size overlaySize, Rect screen)
    {
        return settings.PositionPreset.ToLowerInvariant() switch
        {
            "top-center" => new Point(
                screen.Left + ((screen.Width - overlaySize.Width) / 2),
                screen.Top + ScreenMargin),
            "middle-center" => new Point(
                screen.Left + ((screen.Width - overlaySize.Width) / 2),
                screen.Top + ((screen.Height - overlaySize.Height) / 2)),
            "bottom-center" => new Point(
                screen.Left + ((screen.Width - overlaySize.Width) / 2),
                screen.Bottom - overlaySize.Height - SubtitleBottomMargin),
            "above-ocr" => ChooseRegionLocation(captureRect, overlaySize, screen, aboveRegion: true),
            "below-ocr" => ChooseRegionLocation(captureRect, overlaySize, screen, aboveRegion: false),
            "custom" when settings.CustomLeft.HasValue && settings.CustomTop.HasValue =>
                new Point(settings.CustomLeft.Value, settings.CustomTop.Value),
            _ => new Point(
                screen.Left + ((screen.Width - overlaySize.Width) / 2),
                screen.Bottom - overlaySize.Height - SubtitleBottomMargin)
        };
    }

    private static Point ChooseRegionLocation(Rect captureRect, Size overlaySize, Rect screen, bool aboveRegion)
    {
        if (captureRect.IsEmpty)
        {
            return new Point(
                screen.Left + ((screen.Width - overlaySize.Width) / 2),
                screen.Bottom - overlaySize.Height - SubtitleBottomMargin);
        }

        var left = captureRect.Left + ((captureRect.Width - overlaySize.Width) / 2);
        var top = aboveRegion
            ? captureRect.Top - overlaySize.Height - RegionMargin
            : captureRect.Bottom + RegionMargin;

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

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref NativeRect rect, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public int Flags;
    }
}
