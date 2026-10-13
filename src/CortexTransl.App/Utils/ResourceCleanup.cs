namespace CortexTransl.App.Utils;

internal static class ResourceCleanup
{
    // A failing native resource must not prevent the remaining shutdown work.
    public static void Dispose(IDisposable? resource, string source)
    {
        try { resource?.Dispose(); }
        catch (Exception ex) { AppLog.Write(source, ex); }
    }
}
