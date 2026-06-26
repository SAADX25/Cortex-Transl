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
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
