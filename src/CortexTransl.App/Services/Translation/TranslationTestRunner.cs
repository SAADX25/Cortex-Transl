using CortexTransl.App.Models;
using CortexTransl.App.Utils;
using System.Diagnostics;

namespace CortexTransl.App.Services.Translation;

// The test page evaluates chunk completeness, not linguistic accuracy. Keeping
// this work independent of WPF lets the same contract run with test providers.
internal sealed class TranslationTestRunner(ITranslationProvider provider)
{
    public async Task<TranslationTestResult> RunAsync(string input, string sourceLanguage,
        string targetLanguage, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        var source = input.Trim();
        var chunks = TranslationChunker.Split(source);
        var translations = await provider.TranslateManyAsync(chunks, sourceLanguage, targetLanguage,
            cancellationToken, TranslationContentKind.GameDialogue);
        cancellationToken.ThrowIfCancellationRequested();
        var results = chunks.Select((text, index) => new TestChunkResult
        {
            Index = index + 1, OriginalText = text,
            TranslatedText = index < translations.Count ? translations[index] : string.Empty
        }).ToArray();
        var fullTranslation = TranslationChunker.Join(results.Select(chunk => chunk.TranslatedText).ToArray());
        var ratio = source.Length > 0 ? (double)fullTranslation.Length / source.Length : 0;
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(fullTranslation)) issues.Add("Translation is completely empty");
        else if (ratio < 0.3) issues.Add($"Translation is very short ({ratio:P0} of original length)");
        var empty = results.Count(chunk => chunk.IsEmpty);
        if (empty > 0) issues.Add($"{empty}/{chunks.Count} chunks returned empty");
        if (translations.Count > chunks.Count) issues.Add("Provider returned extra translation chunks");
        var status = issues.Count == 0
            ? $"✓ All {chunks.Count} chunks translated successfully in {timer.ElapsedMilliseconds}ms. " +
              $"Source: {source.Length} chars → Arabic: {fullTranslation.Length} chars ({ratio:P0})."
            : $"⚠ Issues found ({timer.ElapsedMilliseconds}ms): {string.Join(". ", issues)}.";
        return new(fullTranslation, results, status);
    }
}

internal sealed record TranslationTestResult(string Text, IReadOnlyList<TestChunkResult> Chunks, string Status);
