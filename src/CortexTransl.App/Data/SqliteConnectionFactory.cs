using Microsoft.Data.Sqlite;
using SQLitePCL;
using System.IO;
using System.Threading;

namespace CortexTransl.App.Data;

public sealed class SqliteConnectionFactory
{
    private static int _sqliteInitialized;

    public string DatabasePath { get; }

    public SqliteConnectionFactory(string databasePath)
    {
        DatabasePath = databasePath;
    }

    public SqliteConnection CreateConnection()
    {
        EnsureSqliteInitialized();
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        return new SqliteConnection($"Data Source={DatabasePath}");
    }

    private static void EnsureSqliteInitialized()
    {
        if (Interlocked.Exchange(ref _sqliteInitialized, 1) == 1)
        {
            return;
        }

        Batteries_V2.Init();
    }
}
