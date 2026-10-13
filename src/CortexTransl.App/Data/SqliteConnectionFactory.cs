using Microsoft.Data.Sqlite;
using SQLitePCL;
using System.IO;
using System.Threading;

namespace CortexTransl.App.Data;

public sealed class SqliteConnectionFactory
{
    // Publish initialization only after the native provider is ready. An
    // Interlocked flag allowed another thread to open SQLite during Init().
    private static readonly Lazy<bool> SqliteInitialized = new(() =>
    {
        Batteries_V2.Init();
        return true;
    });

    public string DatabasePath { get; }

    public SqliteConnectionFactory(string databasePath)
    {
        DatabasePath = databasePath;
    }

    public SqliteConnection CreateConnection()
    {
        _ = SqliteInitialized.Value;
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared, DefaultTimeout = 5
        }.ToString());
    }
}
