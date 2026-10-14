using CortexTransl.App.Models;
using CortexTransl.App.Views;
using CortexTransl.App.Services.Diagnostics;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Controls;

namespace CortexTransl.Tests.Benchmarks;

internal sealed class AnimatedSubtitleFixture : IAsyncDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dispatcher _dispatcher = null!;
    private Window _source = null!;
    private AnimatedSurface _surface = null!;
    private OverlayWindow _overlay = null!;
    public nint Handle { get; private set; }
    public CaptureRegion Bounds { get; private set; } = CaptureRegion.Empty;
    public CaptureRegion Region { get; private set; } = CaptureRegion.Empty;

    public AnimatedSubtitleFixture()
    {
        _thread = new Thread(() =>
        {
            try
            {
                _dispatcher = Dispatcher.CurrentDispatcher;
                _surface = new AnimatedSurface();
                _source = new Window { Title = "Cortex controlled animated subtitle benchmark", WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize, Left = SystemParameters.WorkArea.Left + 8, Top = SystemParameters.WorkArea.Top + 8,
                    Width = Math.Min(1280, SystemParameters.WorkArea.Width - 16), Height = Math.Min(720, SystemParameters.WorkArea.Height - 16),
                    Content = _surface, ShowInTaskbar = false };
                _source.Show(); _source.Activate(); _source.UpdateLayout();
                Handle = new WindowInteropHelper(_source).Handle;
                if (DwmGetWindowAttribute(Handle, 9, out NativeRect rect, Marshal.SizeOf<NativeRect>()) != 0)
                    throw new InvalidOperationException("Cannot get fixture's physical bounds.");
                Bounds = new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                var top = _source.PointToScreen(new Point(0, _source.ActualHeight - 160));
                var bottom = _source.PointToScreen(new Point(_source.ActualWidth, _source.ActualHeight));
                Region = new((int)top.X, (int)top.Y, (int)(bottom.X - top.X), (int)(bottom.Y - top.Y));
                _overlay = new OverlayWindow();
                CompositionTarget.Rendering += _surface.Animate;
                _ready.SetResult();
                Dispatcher.Run();
            }
            catch (Exception ex) { _ready.TrySetException(ex); }
        }) { IsBackground = true, Name = "controlled-subtitle-fixture" };
        _thread.SetApartmentState(ApartmentState.STA); _thread.Start();
    }

    public async Task ReadyAsync() => await _ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
    public async Task SetAnimationAsync(bool enabled) => await _dispatcher.InvokeAsync(() => _surface.AnimationEnabled = enabled);
    public async Task MinimizeAsync() => await _dispatcher.InvokeAsync(() => _source.WindowState = WindowState.Minimized);
    public async Task RestoreAsync()
    {
        await _dispatcher.InvokeAsync(() => { _source.WindowState = WindowState.Normal; _source.UpdateLayout(); });
        await NextRenderAsync();
    }
    public async Task<long> PresentAsync(string text)
    {
        await _dispatcher.InvokeAsync(() => { _overlay.Hide(); _surface.Line = text; _surface.InvalidateVisual(); });
        return await NextRenderAsync();
    }
    public async Task<long> PublishAsync(string arabic, PerformanceDiagnostics diagnostics)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            using var measurement = diagnostics.Measure(LatencyStage.OverlayPublication);
            _overlay.Pin(Region, new("cover", .88, "dialogue", 1, "white"));
            if (!_overlay.IsVisible) _overlay.Show();
            _overlay.SetText(arabic);
            _overlay.UpdateLayout();
        }, DispatcherPriority.Render);
        return await NextRenderAsync();
    }

    public async Task<int> PublishMenuAsync(IReadOnlyList<TranslatedBlock> blocks)
    {
        var count = await _dispatcher.InvokeAsync(() =>
        {
            _overlay.Pin(Region, new("cover", .88, "list"));
            if (!_overlay.IsVisible) _overlay.Show();
            _overlay.SetBlocks(blocks);
            _overlay.UpdateLayout();
            return ((Canvas)_overlay.FindName("LabelCanvas")).Children.Count;
        });
        await NextRenderAsync();
        return count;
    }
    private async Task<long> NextRenderAsync()
    {
        var completed = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _dispatcher.InvokeAsync(() =>
        {
            var count = 0;
            EventHandler? handler = null;
            handler = (_, _) =>
            {
                if (++count < 2) return;
                CompositionTarget.Rendering -= handler;
                completed.TrySetResult(Stopwatch.GetTimestamp());
            };
            CompositionTarget.Rendering += handler;
            _surface.InvalidateVisual();
        });
        return await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }
    public async ValueTask DisposeAsync()
    {
        if (!_ready.Task.IsCompletedSuccessfully) return;
        await _dispatcher.InvokeAsync(() =>
        {
            CompositionTarget.Rendering -= _surface.Animate;
            _overlay.AllowClose(); _overlay.Close(); _source.Close();
            _dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        });
        _thread.Join(TimeSpan.FromSeconds(3));
    }
    private sealed class AnimatedSurface : FrameworkElement
    {
        public string Line { get; set; } = "";
        public bool AnimationEnabled { get; set; } = true;
        private int _frame;
        public void Animate(object? sender, EventArgs args) { if (AnimationEnabled) { _frame++; InvalidateVisual(); } }
        protected override void OnRender(DrawingContext drawing)
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb((byte)(65 + _frame % 70), 90, 135)), null, new(0, 0, ActualWidth, ActualHeight));
            for (var index = 0; index < 9; index++)
            {
                var x = (_frame * 7 + index * 150) % Math.Max(1, ActualWidth + 180) - 180;
                drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(115, (byte)(70 + index * 9), 145)), null,
                    new(x, 0, 80, ActualHeight));
            }
            if (Line.Length == 0) return;
            var text = new FormattedText(Line, CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Arial"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                32, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var geometry = text.BuildGeometry(new Point(30, ActualHeight - 115));
            drawing.DrawGeometry(Brushes.White, new Pen(Brushes.Black, 2.5), geometry);
            drawing.DrawGeometry(Brushes.White, null, geometry);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeRect bounds, int size);
}
