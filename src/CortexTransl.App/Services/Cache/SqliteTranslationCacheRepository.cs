using CortexTransl.App.Data;
using CortexTransl.App.Utils;
using Microsoft.Data.Sqlite;

namespace CortexTransl.App.Services.Cache;

public sealed class SqliteTranslationCacheRepository : ITranslationCacheRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteTranslationCacheRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<string?> GetAsync(
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        string provider,
        CancellationToken cancellationToken = default)
    {
        var hash = TextHasher.Sha256(sourceText);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, TranslatedText
            FROM TranslationCache
            WHERE SourceTextHash = $hash
              AND SourceLanguage = $sourceLanguage
              AND TargetLanguage = $targetLanguage
              AND Provider = $provider
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$sourceLanguage", sourceLanguage);
        command.Parameters.AddWithValue("$targetLanguage", targetLanguage);
        command.Parameters.AddWithValue("$provider", provider);

        long? id = null;
        string? translatedText = null;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                id = reader.GetInt64(0);
                translatedText = reader.GetString(1);
            }
        }

        if (id is null)
        {
            return null;
        }

        await IncrementHitCountAsync(connection, id.Value, cancellationToken);
        return translatedText;
    }

    public async Task SaveAsync(
        string sourceText,
        string sourceLanguage,
        string targetLanguage,
        string provider,
        string translatedText,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var hash = TextHasher.Sha256(sourceText);

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TranslationCache
                (SourceTextHash, SourceText, SourceLanguage, TargetLanguage, Provider, TranslatedText, CreatedUtc, LastUsedUtc, HitCount)
            VALUES
                ($hash, $sourceText, $sourceLanguage, $targetLanguage, $provider, $translatedText, $now, $now, 0)
            ON CONFLICT(SourceTextHash, SourceLanguage, TargetLanguage, Provider)
            DO UPDATE SET
                SourceText = excluded.SourceText,
                TranslatedText = excluded.TranslatedText,
                LastUsedUtc = excluded.LastUsedUtc;
            """;
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$sourceText", sourceText);
        command.Parameters.AddWithValue("$sourceLanguage", sourceLanguage);
        command.Parameters.AddWithValue("$targetLanguage", targetLanguage);
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$translatedText", translatedText);
        command.Parameters.AddWithValue("$now", now);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task IncrementHitCountAsync(SqliteConnection connection, long id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE TranslationCache
            SET LastUsedUtc = $now,
                HitCount = HitCount + 1
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
