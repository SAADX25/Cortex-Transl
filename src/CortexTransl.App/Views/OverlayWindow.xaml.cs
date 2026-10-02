using CortexTransl.App.Models;
using CortexTransl.App.Services.Capture;
using CortexTransl.App.Utils;
using System.Globalization;
using Bitmap = System.Drawing.Bitmap;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace CortexTransl.App.Views;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int HwndTopmost = -1;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const int PhysicalGap = 8;

    private string _lastText = string.Empty;
    private CaptureRegion _lastPlacementRect = CaptureRegion.Empty;
    private CaptureRegion? _capturePlacementRegion;
    private CaptureRegion? _capturePlacementBounds;
    private string _lastPlacement = string.Empty;
    private double _lastOpacity = -1;
    private double _lockedWidth;
    private double _lockedHeight;
    private bool _stylesApplied;
    private bool _isListMode;
    private double _labelOpacity = 0.88;
    private double _fontScale = 1.0;
    private SolidColorBrush _textBrush = Brushes.White;
    private Color _lastTextColor = Colors.Transparent;
    private bool _allowClose;

    public OverlayWindow()
    {
        InitializeComponent();
        SizeToContent = SizeToContent.Manual;
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => ReapplyLockedPlacement();
        SizeChanged += OnSizeChanged;
        Closing += OnClosing;
    }

    public void Pin(CaptureRegion region, OverlaySettings settings)
    {
        if (region.IsEmpty)
        {
            return;
        }

        ApplyChrome(settings);
        _fontScale = settings.NormalizedFontScale;
        ApplyTextColor(settings.NormalizedTextColor);
        Place(region, settings.IsListMode ? "cover" : settings.NormalizedPlacement);
        ApplyFontSize();
        ApplyExtendedStyles();
        ReapplyLockedPlacement();
    }

    public void SetText(string text)
    {
        if (_isListMode)
        {
            return;
        }

        var trimmed = text.Trim();
        if (_lastText == trimmed)
        {
            return;
        }

        TranslationText.Text = trimmed;
        _lastText = trimmed;
        FitTranslationText();
        ReapplyLockedPlacement();
    }

    public void SetBlocks(IReadOnlyList<TranslatedBlock> blocks)
    {
        if (!_isListMode || _lastPlacementRect.IsEmpty)
        {
            return;
        }

        LabelCanvas.Children.Clear();
        var dpi = ScreenCoordinates.GetDpiForRegion(_lastPlacementRect);
        var alpha = (byte)Math.Clamp(_labelOpacity * 255, 180, 245);
        var fill = new SolidColorBrush(Color.FromArgb(alpha, 2, 6, 23));
        fill.Freeze();

        foreach (var block in blocks)
        {
            if (string.IsNullOrWhiteSpace(block.TranslatedText) || block.Bounds.IsEmpty)
            {
                continue;
            }

            var left = block.Bounds.X / dpi.DpiScaleX;
            var top = block.Bounds.Y / dpi.DpiScaleY;
            var width = Math.Max(36, block.Bounds.Width / dpi.DpiScaleX * 1.28);
            var height = Math.Max(16, block.Bounds.Height / dpi.DpiScaleY);
            var sourceLines = 1 + block.OriginalText.Count(character => character == '\n');
            var fontSize = Math.Clamp(Math.Max(height / sourceLines * 0.58, 10) * _fontScale, 9, 18);

            var chip = new Border
            {
                Background = fill,
                Padding = new Thickness(4, 1, 4, 1),
                CornerRadius = new CornerRadius(3),
                Width = width,
                MinHeight = height,
                Child = new TextBlock
                {
                    Text = block.TranslatedText,
                    Foreground = _textBrush,
                    FontSize = fontSize,
                    FontWeight = FontWeights.SemiBold,
                    FontFamily = new FontFamily("Segoe UI, Tahoma, Arial"),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    FlowDirection = FlowDirection.RightToLeft,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            Canvas.SetLeft(chip, left);
            Canvas.SetTop(chip, top);
            LabelCanvas.Children.Add(chip);
        }

        ReapplyLockedPlacement();
    }

    public void BringToFront()
    {
        if (!IsVisible)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero)
        {
            return;
        }

        if (_lastPlacementRect.IsEmpty)
        {
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
            return;
        }

        ReapplyLockedPlacement();
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyExtendedStyles();
        ReapplyLockedPlacement();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
        }
    }

    private bool _syncingSize;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_syncingSize || _lockedWidth <= 0 || _lockedHeight <= 0)
        {
            return;
        }

        if (Math.Abs(Width - _lockedWidth) > 0.5 || Math.Abs(Height - _lockedHeight) > 0.5)
        {
            _syncingSize = true;
            Width = _lockedWidth;
            Height = _lockedHeight;
            _syncingSize = false;
        }
    }

    private void ApplyChrome(OverlaySettings settings)
    {
        _isListMode = settings.IsListMode;
        var opacity = Math.Clamp(settings.BackgroundOpacity, 0.72, 0.96);
        _labelOpacity = opacity;

        if (_isListMode)
        {
            OverlayChrome.Visibility = Visibility.Collapsed;
            LabelCanvas.Visibility = Visibility.Visible;
            Background = Brushes.Transparent;
            Opacity = 1;
            _lastOpacity = -1;
            return;
        }

        LabelCanvas.Visibility = Visibility.Collapsed;
        LabelCanvas.Children.Clear();
        OverlayChrome.Visibility = Visibility.Visible;

        if (Math.Abs(_lastOpacity - opacity) >= 0.001)
        {
            var fill = new SolidColorBrush(Color.FromRgb(2, 6, 23));
            fill.Freeze();
            OverlayChrome.Background = fill;
            Background = fill;
            Opacity = opacity;
            _lastOpacity = opacity;
        }
    }

    private void ApplyTextColor(string colorId)
    {
        var color = OverlayTextColors.ToColor(colorId);
        if (_lastTextColor == color)
        {
            return;
        }

        _textBrush = OverlayTextColors.ToBrush(colorId);
        TranslationText.Foreground = _textBrush;
        _lastTextColor = color;
    }

    private void Place(CaptureRegion region, string placement)
    {
        if (_capturePlacementRegion != region || _lastPlacement != placement)
        {
            ResetCapturePlacement();
        }

        var target = _capturePlacementBounds ?? GetPlacementRect(region, placement);
        LockDipSize(target);

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(hwnd, HwndTopmost, target.X, target.Y, target.Width, target.Height, SwpNoActivate);

        _lastPlacementRect = target;
        _lastPlacement = placement;
        ApplyFontSize();
    }

    private void ApplyFontSize()
    {
        FitTranslationText();
    }

    private void FitTranslationText()
    {
        if (_isListMode || _lockedWidth <= 0 || _lockedHeight <= 0)
        {
            return;
        }

        var text = string.IsNullOrWhiteSpace(_lastText) ? TranslationText.Text : _lastText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var maxWidth = Math.Max(48, _lockedWidth - 32);
        var maxHeight = Math.Max(28, _lockedHeight - 22);
        var maxFont = Math.Clamp(_lockedHeight * 0.2 * _fontScale, 13, 34);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi <= 0)
        {
            dpi = 1;
        }

        var typeface = new Typeface(
            TranslationText.FontFamily,
            FontStyles.Normal,
            FontWeights.SemiBold,
            FontStretches.Normal);

        var fontSize = maxFont;
        while (fontSize > 11)
        {
            var formatted = new FormattedText(
                text,
                CultureInfo.GetCultureInfo("ar"),
                FlowDirection.RightToLeft,
                typeface,
                fontSize,
                _textBrush,
                dpi)
            {
                MaxTextWidth = maxWidth,
                Trimming = TextTrimming.None,
                TextAlignment = TextAlignment.Center
            };

            if (formatted.Height <= maxHeight)
            {
                break;
            }

            fontSize -= 0.8;
        }

        TranslationText.FontSize = fontSize;
        TranslationText.LineHeight = Math.Max(fontSize * 1.12, fontSize + 1);
    }

    private void ReapplyLockedPlacement()
    {
        if (_lastPlacementRect.IsEmpty)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero)
        {
            return;
        }

        SetWindowPos(
            hwnd,
            HwndTopmost,
            _lastPlacementRect.X,
            _lastPlacementRect.Y,
            _lastPlacementRect.Width,
            _lastPlacementRect.Height,
            SwpNoActivate);
    }

    private void LockDipSize(CaptureRegion physical)
    {
        var dpi = ScreenCoordinates.GetDpiForRegion(physical);
        _lockedWidth = Math.Max(80, physical.Width / dpi.DpiScaleX);
        _lockedHeight = Math.Max(48, physical.Height / dpi.DpiScaleY);
        SizeToContent = SizeToContent.Manual;
        MinWidth = _lockedWidth;
        MaxWidth = _lockedWidth;
        MinHeight = _lockedHeight;
        MaxHeight = _lockedHeight;
        Width = _lockedWidth;
        Height = _lockedHeight;

        var dipLeft = physical.X / dpi.DpiScaleX;
        var dipTop = physical.Y / dpi.DpiScaleY;
        Left = dipLeft;
        Top = dipTop;
    }

    private static CaptureRegion GetPlacementRect(CaptureRegion region, string placement)
    {
        var width = Math.Max(80, region.Width);
        var height = Math.Max(48, region.Height);
        var left = region.X;
        var top = region.Y;

        if (placement == "below")
        {
            top = region.Y + region.Height + PhysicalGap;
        }
        else if (placement == "above")
        {
            top = region.Y - height - PhysicalGap;
        }

        var screen = ScreenCoordinates.GetVirtualScreenPhysical();
        if (top < screen.Y || top + height > screen.Y + screen.Height)
        {
            top = region.Y;
        }

        if (left < screen.X)
        {
            left = screen.X;
        }

        if (left + width > screen.X + screen.Width)
        {
            left = Math.Max(screen.X, screen.X + screen.Width - width);
        }

        return new CaptureRegion(left, top, width, height);
    }

    private void ApplyExtendedStyles()
    {
        if (_stylesApplied)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        var extendedStyle = GetWindowLongPtr(handle, GwlExStyle);
        extendedStyle = (nint)((long)extendedStyle | WsExToolWindow | WsExNoActivate | WsExTransparent);
        SetWindowLongPtr(handle, GwlExStyle, extendedStyle);
        _stylesApplied = true;
    }

    internal void ResetCapturePlacement()
    {
        _capturePlacementRegion = null;
        _capturePlacementBounds = null;
    }

    internal static void RestoreCapturePlacement(CaptureRegion region)
    {
        var application = Application.Current;
        var dispatcher = application?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => RestoreCapturePlacement(region));
            return;
        }

        foreach (var overlay in application!.Windows.OfType<OverlayWindow>().Where(window => window.IsVisible))
        {
            if (overlay._capturePlacementRegion != region)
            {
                continue;
            }

            var placement = overlay._lastPlacement;
            overlay.ResetCapturePlacement();
            overlay.Place(region, placement);
        }
    }

    private bool UncoverCaptureRegion(CaptureRegion region)
    {
        if (!CaptureSafePlacement.Overlaps(_lastPlacementRect, region))
        {
            return false;
        }

        if (_isListMode)
        {
            throw new CaptureUnavailableException("تعذّر التقاط أسماء القوائم مباشرة. أعد تحديد نافذة القائمة باستخدام F9.");
        }

        var target = CaptureSafePlacement.Find(region, _lastPlacementRect, ScreenCoordinates.GetMonitorWorkArea(region));
        if (target is null)
        {
            throw new CaptureUnavailableException("Select a smaller text region so the translation can stay beside it.");
        }

        _capturePlacementRegion = region;
        _capturePlacementBounds = target;
        _lastPlacementRect = target;
        LockDipSize(target);
        ReapplyLockedPlacement();
        return true;
    }

    // Keep the desktop fallback's OCR region uncovered without repeatedly
    // hiding the translation. The alternate placement lasts for this session.
    internal static Bitmap CaptureDesktopWithUncoveredRegion(CaptureRegion region, Func<Bitmap> capture)
    {
        var application = Application.Current;
        var dispatcher = application?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return capture();
        }

        if (!dispatcher.CheckAccess())
        {
            return dispatcher.Invoke(() => CaptureDesktopWithUncoveredRegion(region, capture));
        }

        var overlays = application!.Windows.OfType<OverlayWindow>()
            .Where(window => window.IsVisible).ToArray();
        var moved = false;
        foreach (var overlay in overlays)
        {
            moved |= overlay.UncoverCaptureRegion(region);
        }

        if (moved)
        {
            DwmFlush();
        }

        return capture();
    }

    private static nint GetWindowLongPtr(nint hwnd, int index)
    {
        return nint.Size == 8
            ? GetWindowLongPtr64(hwnd, index)
            : GetWindowLong32(hwnd, index);
    }

    private static nint SetWindowLongPtr(nint hwnd, int index, nint newStyle)
    {
        return nint.Size == 8
            ? SetWindowLongPtr64(hwnd, index, newStyle)
            : SetWindowLong32(hwnd, index, (int)newStyle);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern nint GetWindowLong32(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern nint GetWindowLongPtr64(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern nint SetWindowLong32(nint hwnd, int index, int newStyle);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern nint SetWindowLongPtr64(nint hwnd, int index, nint newStyle);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
