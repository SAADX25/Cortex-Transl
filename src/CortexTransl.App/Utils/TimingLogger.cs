using CortexTransl.App.Models;
using System.Diagnostics;
using System.IO;

namespace CortexTransl.App.Utils;

public sealed class TimingLogger
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TimingLogger(string path)
    {
        _path = path;
    }

    public async Task LogAsync(string operation, IReadOnlyCollection<TimingEntry> timings, CancellationToken cancellationToken = default)
    {
        var line = $"{DateTimeOffset.Now:O} {operation} {string.Join(", ", timings.Select(t => $"{t.Name}={t.ElapsedMilliseconds}ms"))}";
        Debug.WriteLine(line);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await _gate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Timing log skipped: {ex.Message}");
            return;
        }

        try
        {
            await File.AppendAllTextAsync(_path, line + Environment.NewLine, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Timing log skipped: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }
}
