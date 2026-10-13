using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CortexTransl.App.Services.Diagnostics;

public enum LatencyStage
{
    Capture, FrameWait, GpuCopyReadback, Fingerprint, TextChangeDetection, Preprocess,
    WindowsOcr, OcrRecovery, Stabilization, QueueWait, CacheLookup,
    ModelInitialization, NativeWarmup, NativeInference, OverlayDispatch, OverlayPublication, OverlayRender,
    ObservedToOverlayRender, ControlledAppearanceToOverlayRender,
    CpuPercentOfMachine, WorkingSetMiB, PrivateMemoryMiB, GpuBusiestEnginePercent
}

public sealed record StageStatistics(int Count, double P50, double P95, double Maximum);

// No source text, translations, screenshots, titles, paths, API keys, or game
// identities enter this collector. Memory and export size are strictly bounded.
public sealed class PerformanceDiagnostics : IDisposable
{
    private const int Capacity = 512;
    private readonly object _gate = new();
    private readonly Dictionary<LatencyStage, Queue<double>> _samples = new();
    private long _resourceAt, _cpuAt;
    private TimeSpan _cpu;
    private GpuUsageSampler? _gpu;
    private bool _enabled;
    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set
        {
            lock (_gate)
            {
                if (_enabled == value) return;
                _enabled = value;
                _resourceAt = _cpuAt = 0;
                _gpu?.Dispose(); _gpu = null;
            }
        }
    }

    public Scope Measure(LatencyStage stage) => Enabled ? new(this, stage, Stopwatch.GetTimestamp()) : default;
    public void Elapsed(LatencyStage stage, long start, long end = 0)
    {
        if (start > 0) Record(stage, Stopwatch.GetElapsedTime(start, end == 0 ? Stopwatch.GetTimestamp() : end).TotalMilliseconds);
    }
    public void Record(LatencyStage stage, double value)
    {
        if (!double.IsFinite(value) || value < 0) return;
        lock (_gate)
        {
            if (!_enabled) return;
            if (!_samples.TryGetValue(stage, out var values)) _samples[stage] = values = new();
            if (values.Count == Capacity) values.Dequeue();
            values.Enqueue(value);
        }
    }
    public IReadOnlyDictionary<string, StageStatistics> Snapshot()
    {
        lock (_gate) return _samples.ToDictionary(x => x.Key.ToString(), x => Summarize(x.Value));
    }
    public void Reset() { lock (_gate) { _samples.Clear(); _resourceAt = _cpuAt = 0; } }
    public void SampleResources()
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (!_enabled || (_resourceAt != 0 && Stopwatch.GetElapsedTime(_resourceAt, now).TotalSeconds < 1)) return;
            _resourceAt = now;
            using var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            if (_cpuAt != 0)
                Record(LatencyStage.CpuPercentOfMachine, (cpu - _cpu).TotalSeconds / Stopwatch.GetElapsedTime(_cpuAt, now).TotalSeconds / Environment.ProcessorCount * 100);
            _cpu = cpu; _cpuAt = now;
            Record(LatencyStage.WorkingSetMiB, process.WorkingSet64 / 1048576.0);
            Record(LatencyStage.PrivateMemoryMiB, process.PrivateMemorySize64 / 1048576.0);
            _gpu ??= new();
            var gpu = _gpu.Sample();
            if (gpu.HasValue) Record(LatencyStage.GpuBusiestEnginePercent, gpu.Value);
        }
    }
    public void Export(string path)
    {
        var report = new { Schema = 1, SampleCapacityPerStage = Capacity, Stopwatch.Frequency,
            TimingUnit = "milliseconds", CpuUnit = "percent of all logical processors", GpuUnit = "busiest engine percent for this process (not total GPU)",
            Note = "Observed-to-render starts at the first captured observation, not an unknown external source onset. Controlled onset is measured only by the benchmark fixture. Missing metrics are unavailable, not zero.",
            Stages = Snapshot() };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true); // One bounded report; no append log.
    }
    private static StageStatistics Summarize(IEnumerable<double> samples)
    {
        var sorted = samples.Order().ToArray();
        double Percentile(double p) => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * p) - 1, 0, sorted.Length - 1)];
        return new(sorted.Length, Percentile(.5), Percentile(.95), sorted[^1]);
    }
    public readonly struct Scope(PerformanceDiagnostics owner, LatencyStage stage, long started) : IDisposable
    {
        public void Dispose() { if (owner is not null) owner.Elapsed(stage, started); }
    }
    public void Dispose() { lock (_gate) { _gpu?.Dispose(); _gpu = null; _enabled = false; } }
}
