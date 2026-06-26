using CortexTransl.App.Data;
using CortexTransl.App.Models;

namespace CortexTransl.App.Services.Profiles;

public sealed class SqliteGameProfileRepository : IGameProfileRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteGameProfileRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<GameProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var profiles = new List<GameProfile>();

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, RegionX, RegionY, RegionWidth, RegionHeight,
                   SourceLanguage, TargetLanguage, OcrEngine, TranslationProvider
            FROM GameProfiles
            ORDER BY Name COLLATE NOCASE;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            profiles.Add(new GameProfile
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                Region = new CaptureRegion(reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5)),
                SourceLanguage = reader.GetString(6),
                TargetLanguage = reader.GetString(7),
                OcrEngine = reader.GetString(8),
                TranslationProvider = reader.GetString(9)
            });
        }

        return profiles;
    }

    public async Task SaveAsync(GameProfile profile, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");

        await using var connection = _connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO GameProfiles
                (Name, RegionX, RegionY, RegionWidth, RegionHeight, SourceLanguage, TargetLanguage, OcrEngine, TranslationProvider, CreatedUtc, UpdatedUtc)
            VALUES
                ($name, $x, $y, $width, $height, $sourceLanguage, $targetLanguage, $ocrEngine, $translationProvider, $now, $now)
            ON CONFLICT(Name)
            DO UPDATE SET
                RegionX = excluded.RegionX,
                RegionY = excluded.RegionY,
                RegionWidth = excluded.RegionWidth,
                RegionHeight = excluded.RegionHeight,
                SourceLanguage = excluded.SourceLanguage,
                TargetLanguage = excluded.TargetLanguage,
                OcrEngine = excluded.OcrEngine,
                TranslationProvider = excluded.TranslationProvider,
                UpdatedUtc = excluded.UpdatedUtc;
            """;
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$x", profile.Region.X);
        command.Parameters.AddWithValue("$y", profile.Region.Y);
        command.Parameters.AddWithValue("$width", profile.Region.Width);
        command.Parameters.AddWithValue("$height", profile.Region.Height);
        command.Parameters.AddWithValue("$sourceLanguage", profile.SourceLanguage);
        command.Parameters.AddWithValue("$targetLanguage", profile.TargetLanguage);
        command.Parameters.AddWithValue("$ocrEngine", profile.OcrEngine);
        command.Parameters.AddWithValue("$translationProvider", profile.TranslationProvider);
        command.Parameters.AddWithValue("$now", now);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
