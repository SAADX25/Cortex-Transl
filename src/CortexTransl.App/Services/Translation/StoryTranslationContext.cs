using System.Text;
using System.Text.Json;
using CortexTransl.App.Utils;

namespace CortexTransl.App.Services.Translation;

public sealed record StoryDialogue(string Text, string? Speaker = null);

// Contains only observed dialogue and facts explicitly supplied by the player.
public sealed record StoryTranslationContext
{
    public string GameKey { get; init; } = "session";
    public IReadOnlyList<StoryDialogue> RecentDialogue { get; init; } = [];
    public string? Speaker { get; init; }
    public string? SceneState { get; init; }
    public IReadOnlyDictionary<string, string> Terminology { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> SpeakerNames { get; init; } = new Dictionary<string, string>();
    public string? DeepLGlossaryId { get; init; }

    public string CacheScope => TextHasher.Sha256(JsonSerializer.Serialize(new
    {
        Version = 4, GameKey, Speaker, SceneState, RecentDialogue,
        Terms = Terminology.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray(),
        Names = SpeakerNames.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray(), DeepLGlossaryId
    }));

    public string OfflineCacheScope => TextHasher.Sha256(JsonSerializer.Serialize(new
    {
        Version = 5, GameKey,
        Terms = Terminology.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray(),
        Names = SpeakerNames.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray()
    }));

    public string ToDeepLContext()
    {
        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(SceneState)) text.AppendLine($"Known scene: {Limit(SceneState, 800)}");
        if (!string.IsNullOrWhiteSpace(Speaker)) text.AppendLine($"Known current speaker: {Limit(Speaker, 120)}");
        foreach (var entry in Terminology.Concat(SpeakerNames).Take(100))
            text.AppendLine($"{Limit(entry.Key, 120)} = {Limit(entry.Value, 120)}");
        text.AppendLine("Previously observed dialogue (oldest first):");
        foreach (var line in RecentDialogue.TakeLast(6))
            text.AppendLine($"{(line.Speaker is null ? "" : Limit(line.Speaker, 120) + ": ")}{Limit(line.Text, 800)}");
        return text.ToString();
    }

    public bool TryExactArabicTerm(string source, string sourceLanguage, string targetLanguage, out string value)
    {
        value = string.Empty;
        if (!targetLanguage.Equals("ar", StringComparison.OrdinalIgnoreCase)
            && !targetLanguage.StartsWith("ar-", StringComparison.OrdinalIgnoreCase)) return false;
        var normalizedSource = TextNormalizer.Normalize(source);
        foreach (var term in Terminology.Concat(SpeakerNames))
        {
            if (normalizedSource.Equals(TextNormalizer.Normalize(term.Key), StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(term.Value))
            { value = term.Value; return true; }
        }
        return false;
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
}
