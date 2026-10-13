using CortexTransl.App.Models;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Threading;

namespace CortexTransl.App.Services.Diagnostics;

public sealed class OverlayRenderMeasurement : IDisposable
{
    private readonly PerformanceDiagnostics _diagnostics;
    private readonly PipelineResult _result;
    private readonly Func<bool> _current;
    private readonly long _published;
    private readonly DispatcherTimer _timeout;
    private int _renders;
    private bool _disposed;
    public OverlayRenderMeasurement(PerformanceDiagnostics diagnostics, PipelineResult result, long published, Func<bool> current)
    {
        _diagnostics = diagnostics; _result = result; _published = published; _current = current;
        _timeout = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timeout.Tick += OnTimeout;
        CompositionTarget.Rendering += OnRender;
        _timeout.Start();
    }
    private void OnRender(object? sender, EventArgs args)
    {
        if (++_renders < 2) return;
        if (_current())
        {
            var now = Stopwatch.GetTimestamp();
            _diagnostics.Elapsed(LatencyStage.OverlayRender, _published, now);
            _diagnostics.Elapsed(LatencyStage.ObservedToOverlayRender, _result.ObservedTimestamp, now);
        }
        Dispose();
    }
    private void OnTimeout(object? sender, EventArgs args) => Dispose();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CompositionTarget.Rendering -= OnRender;
        _timeout.Stop(); _timeout.Tick -= OnTimeout;
    }
}
