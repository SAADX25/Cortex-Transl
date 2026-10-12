using CortexTransl.App.Services.Translation;
using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace CortexTransl.Tests;

public sealed class BergamotFactAttribute : FactAttribute
{
    public static string? FindModels()
    {
        var configured = Environment.GetEnvironmentVariable("CORTEX_TRANSL_TEST_MODELS");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var models = Path.Combine(directory.FullName, "dist", "win-x64", "models");
            if (File.Exists(Path.Combine(models, "en-ar", "config.yml"))) return models;
            directory = directory.Parent;
        }
        return null;
    }
    public BergamotFactAttribute()
    {
        if (FindModels() is null) Skip = "Optional native test: set CORTEX_TRANSL_TEST_MODELS to a folder containing an en-ar model.";
    }
}

public sealed class OfflineTests(ITestOutputHelper output)
{
    [Fact]
    public async Task EnglishArabicGlossaryAndSpeakerNamesWorkOfflineWithoutModelOrNetwork()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CortexTransl-offline-" + Guid.NewGuid().ToString("N"));
        using var client = new HttpClient(new DelegateHttpHandler((_, _) => throw new InvalidOperationException("Offline glossary must not access network")));
        using var offline = new OfflineBergamotTranslationProvider(new OfflineModelInstaller(directory, directory, client));
        var context = new StoryTranslationContext
        {
            Terminology = new Dictionary<string, string> { ["Silver Keep"] = "قلعة الفضة" },
            SpeakerNames = new Dictionary<string, string> { ["Alice"] = "أليس" }
        };
        var settings = new TranslationProviderSettings { TranslationEngine = "offline" };
        var route = new RoutingTranslationProvider(settings, offline, new FakeProvider());
        Assert.Equal("قلعة الفضة", await route.TranslateStoryAsync("Silver Keep", "en", "ar", context));
        Assert.Equal("أليس", await route.TranslateStoryAsync("Alice", "en", "ar", context));
        Assert.Equal("متابعة", await offline.TranslateAsync("Continue", "en", "ar", contentKind: TranslationContentKind.UiLabel));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => offline.TranslateStoryAsync("Alice", "en", "ar", context, cancellation.Token));
    }

    [BergamotFact]
    public async Task BundledNativeBergamotTranslatesShortDialogueAndNegation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CortexTransl-native-" + Guid.NewGuid().ToString("N"));
        using var client = new HttpClient(new DelegateHttpHandler((_, _) => throw new InvalidOperationException("Bundled model must not access network")));
        var installer = new OfflineModelInstaller(directory, BergamotFactAttribute.FindModels(), client);
        using var offline = new OfflineBergamotTranslationProvider(installer);
        var rows = new List<object>();
        var timer = Stopwatch.StartNew();
        foreach (var source in new[] { "Run!", "No!", "Help!", "Don't go!", "I can't help you.", "He is alive.", "He is dead." })
        {
            var start = timer.Elapsed.TotalMilliseconds;
            var arabic = await offline.TranslateStoryAsync(source, "en", "ar", new());
            Assert.False(string.IsNullOrWhiteSpace(arabic));
            Assert.Contains(arabic, c => c is >= '\u0600' and <= '\u06FF');
            if (source is "Don't go!" or "I can't help you.") Assert.Contains("لا", arabic);
            rows.Add(new { source, arabic, milliseconds = Math.Round(timer.Elapsed.TotalMilliseconds - start, 2) });
        }
        Assert.Contains("cpu-threads: 2", File.ReadAllText(installer.GetRuntimeConfigPath("en-ar")));
        output.WriteLine("Native Bergamot smoke corpus (negation marker/script checks; no human accuracy score): " + JsonSerializer.Serialize(rows));
    }
}
