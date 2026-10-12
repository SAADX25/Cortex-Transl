using CortexTransl.App.Services.Translation;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace CortexTransl.Tests;

internal sealed class DelegateHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
}

public sealed class DeepLTests
{
    [Fact]
    public async Task EmbeddedGameTermsAndNamesAreProtectedUsingDocumentedXmlParameters()
    {
        using var client = new HttpClient(new DelegateHttpHandler(async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = json.RootElement;
            Assert.Equal("xml", root.GetProperty("tag_handling").GetString());
            Assert.Equal("cortex-term", root.GetProperty("ignore_tags")[0].GetString());
            var input = root.GetProperty("text")[0].GetString()!;
            Assert.Contains("<cortex-term>قلعة الفضة</cortex-term>", input);
            Assert.Contains("<cortex-term>أليس</cortex-term>", input);
            Assert.Contains("&amp;", input);
            return ArabicResponse("<cortex-dialogue>رافقت <cortex-term>أليس</cortex-term> إلى <cortex-term>قلعة الفضة</cortex-term>.</cortex-dialogue>");
        }));
        var provider = new DeepLTranslationProvider(Settings(), client);
        var context = new StoryTranslationContext
        {
            Terminology = new Dictionary<string, string> { ["Silver Keep"] = "قلعة الفضة" },
            SpeakerNames = new Dictionary<string, string> { ["Alice"] = "أليس" }
        };
        var result = await provider.TranslateStoryAsync("Alice & I went to Silver Keep.", "en", "ar", context);
        Assert.Equal("رافقت أليس إلى قلعة الفضة.", result);
        Assert.DoesNotContain('<', result);
    }

    [Fact]
    public async Task AlteredProtectedTermIsRejectedInsteadOfDisplayingInconsistentName()
    {
        using var client = new HttpClient(new DelegateHttpHandler((_, _) => Task.FromResult(ArabicResponse("<cortex-dialogue>اذهب إلى <cortex-term>wrong</cortex-term>.</cortex-dialogue>"))));
        var provider = new DeepLTranslationProvider(Settings(), client);
        var context = new StoryTranslationContext { Terminology = new Dictionary<string, string> { ["Keep"] = "قلعة" } };
        await Assert.ThrowsAsync<TranslationProviderException>(() => provider.TranslateStoryAsync("Go to the Keep.", "en", "ar", context));
    }

    private static TranslationProviderSettings Settings() => new() { DeepLApiKey = "synthetic-test-key", TranslationEngine = "deepl" };
    private static HttpResponseMessage ArabicResponse(string arabic = "لا تذهب!") => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { translations = new[] { new { text = arabic } } }), Encoding.UTF8, "application/json") };

    [Fact]
    public async Task StoryContextUsesSupportedParametersAndTranslatesOnlyCurrentDialogue()
    {
        string? body = null;
        using var client = new HttpClient(new DelegateHttpHandler(async (request, ct) =>
        {
            Assert.Equal("api-free.deepl.com", request.RequestUri!.Host);
            Assert.Equal("DeepL-Auth-Key", request.Headers.Authorization!.Scheme);
            body = await request.Content!.ReadAsStringAsync(ct);
            return ArabicResponse();
        }));
        var provider = new DeepLTranslationProvider(Settings(), client);
        var context = new StoryTranslationContext
        {
            GameKey = "Test game", Speaker = "Alice", SceneState = "At the bridge",
            RecentDialogue = [new("The bridge is unsafe.", "Bob")],
            Terminology = new Dictionary<string, string> { ["Silver Keep"] = "قلعة الفضة" },
            SpeakerNames = new Dictionary<string, string> { ["Alice"] = "أليس" },
            DeepLGlossaryId = "test-glossary"
        };
        Assert.Equal("لا تذهب!", await provider.TranslateStoryAsync("Don't go!", "en", "ar", context));
        using var json = JsonDocument.Parse(body!);
        var root = json.RootElement;
        Assert.Equal("Don't go!", Assert.Single(root.GetProperty("text").EnumerateArray()).GetString());
        Assert.Equal("AR", root.GetProperty("target_lang").GetString());
        Assert.Equal("EN", root.GetProperty("source_lang").GetString());
        Assert.Equal("nonewlines", root.GetProperty("split_sentences").GetString());
        Assert.Equal("test-glossary", root.GetProperty("glossary_id").GetString());
        var sentContext = root.GetProperty("context").GetString()!;
        foreach (var fact in new[] { "The bridge is unsafe.", "Bob", "Alice", "At the bridge", "قلعة الفضة", "أليس", "negation", "Never invent" })
            Assert.Contains(fact, sentContext);
        Assert.False(root.TryGetProperty("custom_instructions", out _));
        Assert.False(root.TryGetProperty("formality", out _));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(456)]
    [InlineData(503)]
    public async Task DeepLFailuresAreActionableAndNextRequestRecovers(int status)
    {
        var fail = true;
        using var client = new HttpClient(new DelegateHttpHandler((_, _) => Task.FromResult(fail ? new HttpResponseMessage((HttpStatusCode)status) : ArabicResponse())));
        var provider = new DeepLTranslationProvider(Settings(), client);
        await Assert.ThrowsAsync<TranslationProviderException>(() => provider.TranslateStoryAsync("No!", "en", "ar", new()));
        fail = false;
        Assert.Equal("لا تذهب!", await provider.TranslateStoryAsync("Don't go!", "en", "ar", new()));
    }

    [Fact]
    public async Task EmptyAndMismatchedResponsesCannotBePublished()
    {
        using var client = new HttpClient(new DelegateHttpHandler((_, _) => Task.FromResult(ArabicResponse(""))));
        var provider = new DeepLTranslationProvider(Settings(), client);
        await Assert.ThrowsAsync<TranslationProviderException>(() => provider.TranslateAsync("Help!", "en", "ar"));
        using var mismatchClient = new HttpClient(new DelegateHttpHandler((_, _) => Task.FromResult(ArabicResponse())));
        var mismatchProvider = new DeepLTranslationProvider(Settings(), mismatchClient);
        await Assert.ThrowsAsync<TranslationProviderException>(() => mismatchProvider.TranslateManyAsync(["Run!", "No!"], "en", "ar"));
    }

    [Fact]
    public async Task GlossaryWithAutomaticLanguageFailsBeforeNetwork()
    {
        using var client = new HttpClient(new DelegateHttpHandler((_, _) => throw new InvalidOperationException("Should not send")));
        var provider = new DeepLTranslationProvider(Settings(), client);
        await Assert.ThrowsAsync<TranslationProviderException>(() => provider.TranslateStoryAsync("Help!", "auto", "ar", new() { DeepLGlossaryId = "glossary" }));
    }

    [Fact]
    public async Task CancellationReachesHttpRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new DelegateHttpHandler(async (_, ct) =>
        { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return ArabicResponse(); }));
        var provider = new DeepLTranslationProvider(Settings(), client);
        using var cancellation = new CancellationTokenSource();
        var request = provider.TranslateStoryAsync("Run!", "en", "ar", new(), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public void CacheScopesSeparateGamesSpeakersScenesAndTerminology()
    {
        var basis = new StoryTranslationContext { GameKey = "A" };
        var scopes = new[]
        {
            basis.CacheScope,
            (basis with { GameKey = "B" }).CacheScope,
            (basis with { Speaker = "Alice" }).CacheScope,
            (basis with { SceneState = "Battle" }).CacheScope,
            (basis with { RecentDialogue = [new("Don't trust him!")] }).CacheScope,
            (basis with { Terminology = new Dictionary<string, string> { ["Keep"] = "قلعة" } }).CacheScope,
            (basis with { DeepLGlossaryId = "another-version" }).CacheScope
        };
        Assert.Equal(scopes.Length, scopes.Distinct().Count());
        var first = basis with { Terminology = new Dictionary<string, string> { ["A"] = "أ", ["B"] = "ب" } };
        var second = basis with { Terminology = new Dictionary<string, string> { ["B"] = "ب", ["A"] = "أ" } };
        Assert.Equal(first.CacheScope, second.CacheScope);
    }
}
