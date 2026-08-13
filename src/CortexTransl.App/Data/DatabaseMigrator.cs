using Microsoft.Data.Sqlite;

namespace CortexTransl.App.Data;

public sealed class DatabaseMigrator
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public DatabaseMigrator(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS TranslationCache (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceTextHash TEXT NOT NULL,
                SourceText TEXT NOT NULL,
                SourceLanguage TEXT NOT NULL,
                TargetLanguage TEXT NOT NULL,
                Provider TEXT NOT NULL,
                TranslatedText TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                LastUsedUtc TEXT NOT NULL,
                HitCount INTEGER NOT NULL DEFAULT 0,
                UNIQUE(SourceTextHash, SourceLanguage, TargetLanguage, Provider)
            );
            """, cancellationToken);

        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS GameProfiles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE,
                RegionX INTEGER NOT NULL,
                RegionY INTEGER NOT NULL,
                RegionWidth INTEGER NOT NULL,
                RegionHeight INTEGER NOT NULL,
                SourceLanguage TEXT NOT NULL,
                TargetLanguage TEXT NOT NULL,
                OcrEngine TEXT NOT NULL,
                TranslationProvider TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL
            );
            """, cancellationToken);

        await EnsureColumnAsync(connection, "GameProfiles", "TranslationMode", "TEXT NOT NULL DEFAULT 'dialogue'", cancellationToken);
        await EnsureColumnAsync(connection, "GameProfiles", "OverlayPlacement", "TEXT NOT NULL DEFAULT 'cover'", cancellationToken);
        await EnsureColumnAsync(connection, "GameProfiles", "OverlayBackgroundOpacity", "REAL NOT NULL DEFAULT 0.88", cancellationToken);
        await EnsureColumnAsync(connection, "GameProfiles", "OverlayTextSize", "TEXT NOT NULL DEFAULT 'medium'", cancellationToken);
        await EnsureColumnAsync(connection, "GameProfiles", "OverlayTextColor", "TEXT NOT NULL DEFAULT 'white'", cancellationToken);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string table,
        string column,
        string definition,
        CancellationToken cancellationToken)
    {
        var exists = false;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info({table})";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            await ExecuteAsync(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition}", cancellationToken);
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
