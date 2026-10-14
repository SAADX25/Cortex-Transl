using CortexTransl.App.Data;
using CortexTransl.App.Models;
using CortexTransl.App.Services.Cache;
using CortexTransl.App.Services.Profiles;
using CortexTransl.App.Utils;
using Xunit;

namespace CortexTransl.Tests;

public sealed class DatabaseTests
{
    [Fact]
    public async Task LegacyMigrationPurgesStaleTranslationsPreservesProfilesAndIsRepeatable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CortexTransl-migration-" + Guid.NewGuid().ToString("N"));
        var factory = new SqliteConnectionFactory(Path.Combine(directory, "fixture.db"));
        await using (var connection = factory.CreateConnection())
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE GameProfiles (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL UNIQUE,
                    RegionX INTEGER NOT NULL, RegionY INTEGER NOT NULL, RegionWidth INTEGER NOT NULL, RegionHeight INTEGER NOT NULL,
                    SourceLanguage TEXT NOT NULL, TargetLanguage TEXT NOT NULL, OcrEngine TEXT NOT NULL,
                    TranslationProvider TEXT NOT NULL, CreatedUtc TEXT NOT NULL, UpdatedUtc TEXT NOT NULL);
                INSERT INTO GameProfiles VALUES (7, 'Old game', 10, 20, 600, 100, 'en', 'ar', 'windows', 'offline', 'old', 'old');
                CREATE TABLE TranslationCache (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, SourceTextHash TEXT NOT NULL, SourceText TEXT NOT NULL,
                    SourceLanguage TEXT NOT NULL, TargetLanguage TEXT NOT NULL, Provider TEXT NOT NULL,
                    TranslatedText TEXT NOT NULL, CreatedUtc TEXT NOT NULL, LastUsedUtc TEXT NOT NULL,
                    HitCount INTEGER NOT NULL DEFAULT 0,
                    UNIQUE(SourceTextHash, SourceLanguage, TargetLanguage, Provider));
                INSERT INTO TranslationCache VALUES (1, $hash, 'No!', 'en', 'ar', 'offline', 'لا!', 'old', 'old', 4);
                """;
            command.Parameters.AddWithValue("$hash", TextHasher.Sha256("No!"));
            await command.ExecuteNonQueryAsync();
        }
        var migration = new DatabaseMigrator(factory);
        await migration.InitializeAsync();
        var cache = new SqliteTranslationCacheRepository(factory);
        await migration.InitializeAsync();
        var profiles = new SqliteGameProfileRepository(factory);
        var old = Assert.Single(await profiles.GetAllAsync());
        Assert.Equal(7, old.Id); Assert.Equal("Old game", old.Name);
        Assert.Equal(new CaptureRegion(10, 20, 600, 100), old.Region);
        Assert.Equal("medium", old.OverlayTextSize);
        Assert.Equal(650, old.Story.DisappearanceGraceMilliseconds);
        Assert.Null(await cache.GetAsync("No!", "en", "ar", "offline"));
        await cache.SaveAsync("No!", "en", "ar", "offline", "لا!");
        await migration.InitializeAsync();
        Assert.Equal("لا!", await cache.GetAsync("No!", "en", "ar", "offline"));
        old.Story.Terminology["Silver Keep"] = "قلعة الفضة";
        old.Story.SpeakerNames["Alice"] = "أليس";
        old.Story.Speaker = "Alice"; old.Story.MaximumVisibilityMilliseconds = 8000;
        await profiles.SaveAsync(old);
        var updated = Assert.Single(await profiles.GetAllAsync());
        Assert.Equal(7, updated.Id);
        Assert.Equal("قلعة الفضة", updated.Story.Terminology["Silver Keep"]);
        Assert.Equal("أليس", updated.Story.SpeakerNames["Alice"]);
        Assert.Equal(8000, updated.Story.MaximumVisibilityMilliseconds);
        Assert.Null(await cache.GetAsync("No!", "en", "ar", "offline:GameDialogue:story-v3:game-A"));
        await cache.SaveAsync("No!", "en", "ar", "offline:GameDialogue:story-v3:game-A", "كلا!");
        Assert.Equal("لا!", await cache.GetAsync("No!", "en", "ar", "offline"));
        Assert.Equal("كلا!", await cache.GetAsync("No!", "en", "ar", "offline:GameDialogue:story-v3:game-A"));
    }

    [Fact]
    public async Task CacheUsesMergedCanonicalTextAndRejectsRowsFromOlderWriters()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(Path.GetTempPath(), "CortexTransl-cache-" + Guid.NewGuid().ToString("N"), "fixture.db"));
        await new DatabaseMigrator(factory).InitializeAsync();
        var cache = new SqliteTranslationCacheRepository(factory);
        await cache.SaveAsync(" | We must\nleave! _ x ", "en", "ar", "provider", "يجب أن نغادر!");
        Assert.Equal("يجب أن نغادر!", await cache.GetAsync("We must leave!", "en", "ar", "provider"));
        Assert.Null(await cache.GetAsync("We must not leave!", "en", "ar", "provider"));
        await using var connection = factory.CreateConnection();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SourceText FROM TranslationCache;";
        Assert.Equal("We must leave!", await command.ExecuteScalarAsync());
        command.CommandText = "UPDATE TranslationCache SET CacheVersion = 0, TranslatedText = 'corrupt';";
        await command.ExecuteNonQueryAsync();
        Assert.Null(await cache.GetAsync("We must leave!", "en", "ar", "provider"));
        await cache.SaveAsync("We must leave!", "en", "ar", "provider", "غادر الآن!");
        Assert.Equal("غادر الآن!", await cache.GetAsync("We must\nleave!", "en", "ar", "provider"));
    }

    [Fact]
    public void TranslationHashesNormalizeWrappingButGeneralHashesRemainExact()
    {
        Assert.Equal(TextHasher.TranslationKey("Hello world!"), TextHasher.TranslationKey("| Hello\nworld! _ x"));
        Assert.NotEqual(TextHasher.TranslationKey("I can go!"), TextHasher.TranslationKey("I can't go!"));
        Assert.NotEqual(TextHasher.Sha256("a\nb"), TextHasher.Sha256("a b"));
    }
}
