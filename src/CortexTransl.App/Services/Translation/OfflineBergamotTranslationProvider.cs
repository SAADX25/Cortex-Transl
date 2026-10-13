using BergamotTranslatorSharp;
using CortexTransl.App.Utils;
using CortexTransl.App.Services.Diagnostics;

namespace CortexTransl.App.Services.Translation;

public sealed class OfflineBergamotTranslationProvider : ITranslationProvider, IDisposable
{
    private readonly OfflineModelInstaller _installer;
    private readonly Dictionary<string, BlockingService> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private bool _disposed;
    private readonly PerformanceDiagnostics _diagnostics;

    public OfflineBergamotTranslationProvider(OfflineModelInstaller installer, PerformanceDiagnostics? diagnostics = null)
    {
        _installer = installer;
        _diagnostics = diagnostics ?? new();
    }

    public string Id => "offline";

    public bool SupportsContextualLongText => false;
    public bool UsesHistoricalContext => false;
    public bool IsPairReady(string source, string target = "ar") => _installer.DirectionsFor(source, target).All(_installer.IsPairReady);

    public Task WarmAsync(string source, string target = "ar", CancellationToken cancellationToken = default)
    {
        RequireInstalledModels(source, target);
        return Task.Run(() =>
        {
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                var key = string.Join('|', _installer.DirectionsFor(source, target));
                if (_services.ContainsKey(key)) return;
                var service = GetService(source, target);
                using (_diagnostics.Measure(LatencyStage.NativeWarmup)) TranslateText(service, "Hello.");
            }
        }, cancellationToken);
    }

    private void RequireInstalledModels(string source, string target)
    {
        if (!IsPairReady(source, target))
            throw new TranslationProviderException("Offline files are missing. Download this language pair before playback.", "offline model missing");
    }

    public async Task<string> TranslateStoryAsync(string text, string sourceLanguage, string targetLanguage,
        StoryTranslationContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.TryExactArabicTerm(text, sourceLanguage, targetLanguage, out var term)) return term;
        if (text.Length <= 350 && !text.Contains('\n'))
            return await TranslateAsync(text, sourceLanguage, targetLanguage, cancellationToken, TranslationContentKind.GameDialogue);
        var chunks = TranslationChunker.Split(text);
        var translated = await TranslateManyAsync(chunks, sourceLanguage, targetLanguage, cancellationToken, TranslationContentKind.GameDialogue);
        cancellationToken.ThrowIfCancellationRequested();
        if (translated.Count != chunks.Count || translated.Any(string.IsNullOrWhiteSpace))
            throw new TranslationProviderException("Offline translation returned incomplete dialogue.", "offline incomplete result");
        return TranslationChunker.Join(translated);
    }

    public bool IsEnglishArabicReady => _installer.IsPairReady("en-ar");

    public async Task EnsureReadyAsync(
        string sourceLanguage,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _installer.EnsureReadyAsync(sourceLanguage, "ar", progress, cancellationToken);
    }

    public async Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default,
        TranslationContentKind contentKind = TranslationContentKind.General)
    {
        var translations = await TranslateManyAsync([text], sourceLanguage, targetLanguage, cancellationToken, contentKind);
        return translations.Count > 0 ? translations[0] : string.Empty;
    }

    public async Task<IReadOnlyList<string>> TranslateManyAsync(
        IReadOnlyList<string> texts,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default,
        TranslationContentKind contentKind = TranslationContentKind.General)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (texts.Count == 0)
        {
            return [];
        }

        var useUiTerms = contentKind == TranslationContentKind.UiLabel
            && (sourceLanguage.Equals("en", StringComparison.OrdinalIgnoreCase) || sourceLanguage.StartsWith("en-", StringComparison.OrdinalIgnoreCase))
            && (targetLanguage.Equals("ar", StringComparison.OrdinalIgnoreCase) || targetLanguage.StartsWith("ar-", StringComparison.OrdinalIgnoreCase));
        var results = new string[texts.Count];
        var remaining = new List<int>();
        for (var index = 0; index < texts.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(texts[index]))
            {
                results[index] = string.Empty;
            }
            else if (useUiTerms && ArabicUiLabels.TryTranslate(texts[index], out var label))
            {
                results[index] = label;
            }
            else
            {
                remaining.Add(index);
            }
        }

        if (remaining.Count == 0)
        {
            return results;
        }

        // Playback never downloads a model or falls back to a network provider.
        RequireInstalledModels(sourceLanguage, targetLanguage);
        return await Task.Run(() =>
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    throw new TranslationProviderException(
                        "Cortex Transl is closing.",
                        "offline disposed");
                }

                var service = GetService(sourceLanguage, targetLanguage);
                foreach (var index in remaining)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var text = useUiTerms ? ArabicUiLabels.Normalize(texts[index]) : texts[index];
                    using (_diagnostics.Measure(LatencyStage.NativeInference)) results[index] = TranslateText(service, text);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            return (IReadOnlyList<string>)results;
        }, cancellationToken);
    }

    private BlockingService GetService(string sourceLanguage, string targetLanguage)
    {
        var directions = _installer.DirectionsFor(sourceLanguage, targetLanguage);
        if (directions.Count == 0)
        {
            throw new TranslationProviderException(
                "Offline translation does not need this language pair.",
                "Offline unused pair");
        }

        var key = string.Join('|', directions);
        if (_services.TryGetValue(key, out var existing))
        {
            return existing;
        }

        using var initialization = _diagnostics.Measure(LatencyStage.ModelInitialization);
        var configs = directions.Select(_installer.GetRuntimeConfigPath).ToArray();
        BlockingService service;
        try
        {
            service = configs.Length == 1
                ? new BlockingService(configs[0])
                : new BlockingService(configs[0], configs[1]);
        }
        catch (Exception ex)
        {
            throw new TranslationProviderException(
                "Offline Arabic files failed to load. Open Translate and download them again.",
                "offline load failed",
                ex);
        }

        _services[key] = service;
        return service;
    }

    private static string TranslateText(BlockingService service, string text)
    {
        try
        {
            return (service.Translate(text) ?? string.Empty).Trim();
        }
        catch (Exception ex)
        {
            throw new TranslationProviderException(
                "Offline translation failed. Select the region with F9, then press F8.",
                "offline translate failed",
                ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var service in _services.Values)
            {
                try
                {
                    service.Dispose();
                }
                catch
                {
                }
            }

            _services.Clear();
        }
    }
}
