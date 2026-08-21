using System.IO;

namespace CortexTransl.App.Utils;

public static class AppLog
{
    public static void Write(string source, Exception exception)
    {
        Write(source, exception.ToString());
    }

    public static void Write(string source, string message)
    {
        try
        {
            var paths = AppDataPaths.CreateDefault();
            var line = $"{DateTimeOffset.Now:O} {source}{Environment.NewLine}{message}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(paths.DataDirectory, "logs", "errors.log"), line);
        }
        catch
        {
        }
    }
}
