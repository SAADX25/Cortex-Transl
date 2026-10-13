using System.Runtime.InteropServices;

namespace CortexTransl.App.Services.Diagnostics;

// PDH counters are optional. Export only this process's numeric utilization.
internal sealed class GpuUsageSampler : IDisposable
{
    private nint _query;
    private readonly List<nint> _counters = [];
    private bool _primed;
    public GpuUsageSampler()
    {
        try
        {
            const string pattern = @"\GPU Engine(*)\Utilization Percentage";
            uint size = 0;
            PdhExpandWildCardPath(null, pattern, null, ref size, 0);
            if (size is 0 or > 262144 || PdhOpenQuery(null, 0, out _query) != 0) return;
            var paths = new char[size];
            if (PdhExpandWildCardPath(null, pattern, paths, ref size, 0) != 0) return;
            foreach (var path in new string(paths).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!path.Contains($"(pid_{Environment.ProcessId}_", StringComparison.OrdinalIgnoreCase)) continue;
                if (_counters.Count == 32) break;
                if (PdhAddEnglishCounter(_query, path, 0, out var counter) == 0) _counters.Add(counter);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { Dispose(); }
    }
    public double? Sample()
    {
        if (_query == 0 || _counters.Count == 0 || PdhCollectQueryData(_query) != 0) return null;
        if (!_primed) { _primed = true; return null; }
        double? maximum = null;
        foreach (var counter in _counters)
        {
            if (PdhGetFormattedCounterValue(counter, 0x200, out _, out var value) == 0 && value.Status is 0 or 1)
                maximum = Math.Max(maximum ?? 0, value.Value);
        }
        return maximum;
    }
    public void Dispose() { if (_query != 0) { PdhCloseQuery(_query); _query = 0; } }
    [StructLayout(LayoutKind.Explicit)] private struct CounterValue
    { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhExpandWildCardPathW")]
    private static extern uint PdhExpandWildCardPath(string? source, string wildcard, [Out] char[]? expanded, ref uint length, uint flags);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(string? source, nuint data, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(nint query, string path, nuint data, out nint counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out CounterValue value);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(nint query);
}
