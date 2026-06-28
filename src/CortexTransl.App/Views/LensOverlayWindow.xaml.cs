using CortexTransl.App.Models;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace CortexTransl.App.Views;

public partial class LensOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int MonitorDefaultToNearest = 0x00000002;
    private const double AttachedGap = 4;
    private const double CanvasPadding = 2;

    private readonly bool _recordingSafeMode;
    private bool? _lastClickThrough;

    public LensOverlayWindow(bool recordingSafeMode)
    {
        _recordingSafeMode = recordingSafeMode;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    public void UpdateBlocks(
        IReadOnlyList<RecognizedTextBlock> textBlocks,
        CaptureRegion region,
        OverlaySettings settings)
    {
        var dpi = GetDpiForRegion(region);
        var windowRect = ToDipRect(region, dpi);

        Left = windowRect.Left;
        Top = windowRect.Top;
        Width = windowRect.Width;
        Height = windowRect.Height;
        OverlayCanvas.Width = windowRect.Width;
        OverlayCanvas.Height = windowRect.Height;
        Opacity = _recordingSafeMode ? 1 : Math.Clamp(settings.Opacity, 0.35, 1);

        if (_lastClickThrough != settings.ClickThrough)
        {
            _lastClickThrough = settings.ClickThrough;
            ApplyExtendedStyles();
        }

        OverlayCanvas.Children.Clear();

        var placedRects = new List<Rect>();
        foreach (var block in textBlocks
            .Where(block => !block.Bounds.IsEmpty && !string.IsNullOrWhiteSpace(block.TranslatedText))
            .OrderBy(block => block.Bounds.Y)
            .ThenBy(block => block.Bounds.X))
        {
            var sourceRect = ToLocalDipRect(block.Bounds, dpi);
            if (sourceRect.Width <= 0 || sourceRect.Height <= 0)
            {
                continue;
            }

            var card = CreateCard(block, sourceRect, settings);
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var desiredSize = card.DesiredSize;
            if (desiredSize.Width <= 0 || desiredSize.Height <= 0)
            {
                continue;
            }

            var location = ChooseAnchoredLocation(
                settings.LensRenderStyle,
                settings.LensReplaceOriginalText,
                sourceRect,
                desiredSize,
                windowRect.Size,
                placedRects);

            Canvas.SetLeft(card, location.X);
            Canvas.SetTop(card, location.Y);
            OverlayCanvas.Children.Add(card);

            var placed = new Rect(location, desiredSize);
            placed.Inflate(1.5, 1.5);
            placedRects.Add(placed);
        }
    }

    public void ClearBlocks()
    {
        OverlayCanvas.Children.Clear();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyExtendedStyles();
    }

    private Border CreateCard(RecognizedTextBlock block, Rect sourceRect, OverlaySettings settings)
    {
        var style = settings.LensRenderStyle;
        var replacesSource = ShouldReplaceSource(style, settings.LensReplaceOriginalText);
        var fontSize = GetLensFontSize(sourceRect, settings.FontSize, style, replacesSource);
        var maxWidth = GetCardMaxWidth(sourceRect, settings.MaxWidth, style, replacesSource);
        var backgroundOpacity = Math.Clamp(
            settings.BackgroundOpacity,
            _recordingSafeMode ? 0.84 : replacesSource ? 0.62 : 0.56,
            replacesSource ? 0.88 : 0.92);
        var alpha = (byte)Math.Clamp(backgroundOpacity * 255, replacesSource ? 146 : 132, 235);

        var text = new TextBlock
        {
            Text = block.TranslatedText.Trim(),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Right,
            FlowDirection = FlowDirection.RightToLeft,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI, Tahoma, Arial"),
            FontWeight = FontWeights.SemiBold,
            FontSize = fontSize,
            LineHeight = fontSize * 1.08,
            MaxWidth = maxWidth,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(alpha, 2, 6, 23)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(replacesSource ? (byte)96 : (byte)150, 255, 255, 255)),
            BorderThickness = replacesSource && style == OverlayRenderStyle.CompactLens
                ? new Thickness(0)
                : new Thickness(1),
            CornerRadius = new CornerRadius(style == OverlayRenderStyle.CompactLens ? 3 : 5),
            Padding = GetCardPadding(style),
            Child = text,
            IsHitTestVisible = false,
            ClipToBounds = true
        };

        if (replacesSource)
        {
            card.Width = Math.Max(18, sourceRect.Width);
            card.MinHeight = Math.Max(1, sourceRect.Height);
        }

        return card;
    }

    private static Thickness GetCardPadding(OverlayRenderStyle style)
    {
        return style switch
        {
            OverlayRenderStyle.Replace => new Thickness(3, 1, 3, 1.5),
            OverlayRenderStyle.CompactLens => new Thickness(3, 1, 3, 1.5),
            _ => new Thickness(6, 3, 6, 4)
        };
    }

    private static double GetLensFontSize(
        Rect sourceRect,
        double configuredFontSize,
        OverlayRenderStyle style,
        bool replacesSource)
    {
        var sourceBasedSize = sourceRect.Height * (replacesSource ? 0.74 : 0.68);
        var configuredSize = configuredFontSize * (replacesSource ? 0.42 : 0.46);
        var desired = Math.Min(sourceBasedSize, configuredSize);

        return style == OverlayRenderStyle.CompactLens || style == OverlayRenderStyle.Replace
            ? Math.Clamp(desired, 8.5, 15)
            : Math.Clamp(desired, 10.5, 18);
    }

    private static double GetCardMaxWidth(
        Rect sourceRect,
        double configuredMaxWidth,
        OverlayRenderStyle style,
        bool replacesSource)
    {
        if (replacesSource)
        {
            return Math.Max(12, sourceRect.Width - 6);
        }

        var width = style switch
        {
            OverlayRenderStyle.CompactLens => Math.Clamp(Math.Max(sourceRect.Width * 1.1, 42), 42, 180),
            OverlayRenderStyle.AttachedSideBySide => Math.Clamp(Math.Max(sourceRect.Width * 1.35, 62), 62, 210),
            OverlayRenderStyle.AboveBelow => Math.Clamp(Math.Max(sourceRect.Width * 1.18, 56), 56, 190),
            _ => Math.Clamp(Math.Max(sourceRect.Width * 1.1, 42), 42, 180)
        };

        return Math.Min(width, Math.Clamp(configuredMaxWidth, 64, 260));
    }

    private static Point ChooseAnchoredLocation(
        OverlayRenderStyle style,
        bool replaceOriginalText,
        Rect sourceRect,
        Size cardSize,
        Size canvasSize,
        IReadOnlyList<Rect> placedRects)
    {
        var replacesSource = ShouldReplaceSource(style, replaceOriginalText);
        var maxDistance = GetMaxAnchorDistance(sourceRect, style, replacesSource);
        var candidates = BuildAnchoredCandidates(style, replacesSource, sourceRect, cardSize)
            .SelectMany(point => AddSmallNudges(point, style, replacesSource, maxDistance))
            .Select(point => ClampToCanvas(point, cardSize, canvasSize))
            .DistinctBy(point => $"{Math.Round(point.X, 1)}|{Math.Round(point.Y, 1)}")
            .ToArray();

        var fallback = ClampToCanvas(CenterOnSource(sourceRect, cardSize), cardSize, canvasSize);
        var bestPoint = fallback;
        var bestScore = double.MaxValue;

        foreach (var candidate in candidates)
        {
            var candidateRect = new Rect(candidate, cardSize);
            var distance = DistanceToSource(candidateRect, sourceRect);
            if (distance > maxDistance)
            {
                continue;
            }

            var collisionArea = GetCollisionArea(candidateRect, placedRects);
            var overlapPenalty = collisionArea <= 0 ? 0 : collisionArea * 0.04;
            var score = (distance * 8) + overlapPenalty;

            if (score < bestScore)
            {
                bestScore = score;
                bestPoint = candidate;
            }

            if (collisionArea <= 0 && distance <= AttachedGap)
            {
                return candidate;
            }
        }

        return bestPoint;
    }

    private static IEnumerable<Point> BuildAnchoredCandidates(
        OverlayRenderStyle style,
        bool replacesSource,
        Rect sourceRect,
        Size cardSize)
    {
        var inside = CenterOnSource(sourceRect, cardSize);
        var fitsInside = cardSize.Width <= sourceRect.Width + 2 &&
            cardSize.Height <= sourceRect.Height + 2;

        if (replacesSource)
        {
            yield return new Point(sourceRect.Left, sourceRect.Top);
            yield return inside;
            yield break;
        }

        if (style == OverlayRenderStyle.CompactLens)
        {
            if (fitsInside)
            {
                yield return inside;
            }

            yield return new Point(sourceRect.Right + AttachedGap, sourceRect.Top + ((sourceRect.Height - cardSize.Height) / 2));
            yield return new Point(sourceRect.Left - cardSize.Width - AttachedGap, sourceRect.Top + ((sourceRect.Height - cardSize.Height) / 2));
            yield return new Point(sourceRect.Left, sourceRect.Bottom + AttachedGap);
            yield return new Point(sourceRect.Left, sourceRect.Top - cardSize.Height - AttachedGap);
            yield return inside;
            yield break;
        }

        if (style == OverlayRenderStyle.AttachedSideBySide)
        {
            yield return new Point(sourceRect.Right + AttachedGap, sourceRect.Top + ((sourceRect.Height - cardSize.Height) / 2));
            yield return new Point(sourceRect.Left - cardSize.Width - AttachedGap, sourceRect.Top + ((sourceRect.Height - cardSize.Height) / 2));
            yield return inside;
            yield return new Point(sourceRect.Left, sourceRect.Bottom + AttachedGap);
            yield break;
        }

        yield return new Point(sourceRect.Left, sourceRect.Top - cardSize.Height - AttachedGap);
        yield return new Point(sourceRect.Left, sourceRect.Bottom + AttachedGap);
        yield return inside;
        yield return new Point(sourceRect.Right + AttachedGap, sourceRect.Top + ((sourceRect.Height - cardSize.Height) / 2));
        yield return new Point(sourceRect.Left - cardSize.Width - AttachedGap, sourceRect.Top + ((sourceRect.Height - cardSize.Height) / 2));
    }

    private static IEnumerable<Point> AddSmallNudges(
        Point point,
        OverlayRenderStyle style,
        bool replacesSource,
        double maxDistance)
    {
        yield return point;

        var nudge = replacesSource
            ? 0
            : Math.Min(6, Math.Max(2, maxDistance / 2));

        if (nudge <= 0)
        {
            yield break;
        }

        yield return new Point(point.X, point.Y - nudge);
        yield return new Point(point.X, point.Y + nudge);
        yield return new Point(point.X - nudge, point.Y);
        yield return new Point(point.X + nudge, point.Y);
    }

    private static double GetMaxAnchorDistance(Rect sourceRect, OverlayRenderStyle style, bool replacesSource)
    {
        if (replacesSource)
        {
            return 0;
        }

        return Math.Clamp(Math.Max(sourceRect.Height * 0.85, AttachedGap), AttachedGap, 14);
    }

    private static bool ShouldReplaceSource(OverlayRenderStyle style, bool replaceOriginalText)
    {
        return style == OverlayRenderStyle.Replace ||
            (style == OverlayRenderStyle.CompactLens && replaceOriginalText);
    }

    private static Point CenterOnSource(Rect sourceRect, Size cardSize)
    {
        return new Point(
            sourceRect.Left + ((sourceRect.Width - cardSize.Width) / 2),
            sourceRect.Top + ((sourceRect.Height - cardSize.Height) / 2));
    }

    private static double DistanceToSource(Rect cardRect, Rect sourceRect)
    {
        var dx = Math.Max(
            Math.Max(sourceRect.Left - cardRect.Right, cardRect.Left - sourceRect.Right),
            0);
        var dy = Math.Max(
            Math.Max(sourceRect.Top - cardRect.Bottom, cardRect.Top - sourceRect.Bottom),
            0);

        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static double GetCollisionArea(Rect candidate, IReadOnlyList<Rect> placedRects)
    {
        var expanded = candidate;
        expanded.Inflate(1.5, 1.5);

        var area = 0.0;
        foreach (var placed in placedRects)
        {
            if (!expanded.IntersectsWith(placed))
            {
                continue;
            }

            var intersection = Rect.Intersect(expanded, placed);
            area += Math.Max(0, intersection.Width) * Math.Max(0, intersection.Height);
        }

        return area;
    }

    private static Point ClampToCanvas(Point point, Size cardSize, Size canvasSize)
    {
        var maxLeft = Math.Max(CanvasPadding, canvasSize.Width - cardSize.Width - CanvasPadding);
        var maxTop = Math.Max(CanvasPadding, canvasSize.Height - cardSize.Height - CanvasPadding);

        return new Point(
            Math.Clamp(point.X, CanvasPadding, maxLeft),
            Math.Clamp(point.Y, CanvasPadding, maxTop));
    }

    private static Rect ToDipRect(CaptureRegion region, DpiScale dpi)
    {
        return new Rect(
            region.X / dpi.DpiScaleX,
            region.Y / dpi.DpiScaleY,
            region.Width / dpi.DpiScaleX,
            region.Height / dpi.DpiScaleY);
    }

    private static Rect ToLocalDipRect(CaptureRegion region, DpiScale dpi)
    {
        return new Rect(
            region.X / dpi.DpiScaleX,
            region.Y / dpi.DpiScaleY,
            region.Width / dpi.DpiScaleX,
            region.Height / dpi.DpiScaleY);
    }

    private DpiScale GetDpiForRegion(CaptureRegion region)
    {
        try
        {
            var rect = new NativeRect(region.X, region.Y, region.X + region.Width, region.Y + region.Height);
            var monitor = MonitorFromRect(ref rect, MonitorDefaultToNearest);
            if (monitor != nint.Zero &&
                GetDpiForMonitor(monitor, MonitorDpiType.Effective, out var dpiX, out var dpiY) == 0 &&
                dpiX > 0 &&
                dpiY > 0)
            {
                return new DpiScale(dpiX / 96.0, dpiY / 96.0);
            }
        }
        catch
        {
        }

        return VisualTreeHelper.GetDpi(this);
    }

    private void ApplyExtendedStyles()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        var extendedStyle = GetWindowLong(handle, GwlExStyle);
        extendedStyle |= WsExToolWindow | WsExLayered;

        if (_lastClickThrough != false)
        {
            extendedStyle |= WsExTransparent;
        }
        else
        {
            extendedStyle &= ~WsExTransparent;
        }

        SetWindowLong(handle, GwlExStyle, extendedStyle);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hwnd, int index, int newStyle);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref NativeRect rect, int flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint hmonitor,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    private enum MonitorDpiType
    {
        Effective = 0
    }
}
