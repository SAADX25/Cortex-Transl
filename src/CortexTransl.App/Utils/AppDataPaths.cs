using System.IO;

namespace CortexTransl.App.Utils;

public sealed class AppDataPaths
{
    public string DataDirectory { get; }

    public string DatabasePath { get; }

    public string LogPath { get; }

    private AppDataPaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        DatabasePath = Path.Combine(dataDirectory, "cortex-transl.db");
        LogPath = Path.Combine(dataDirectory, "logs", "app.log");
    }

    public static AppDataPaths CreateDefault()
    {
        var root = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        var dataDirectory = Path.Combine(root, "Cortex Transl");
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(Path.Combine(dataDirectory, "logs"));
        return new AppDataPaths(dataDirectory);
    }
}
