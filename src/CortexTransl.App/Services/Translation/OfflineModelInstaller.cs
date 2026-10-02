using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CortexTransl.App.Services.Translation;

public sealed class OfflineModelInstaller
{
    private const string RegistryUrl =
        "https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/db/models.json";

    private readonly string _userModelsDirectory;
    private readonly string _bundledModelsDirectory;
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private MozillaModelRegistry? _registry;

    public OfflineModelInstaller(string userModelsDirectory, string? bundledModelsDirectory = null, HttpClient? httpClient = null)
    {
        _userModelsDirectory = userModelsDirectory;
        _bundledModelsDirectory = string.IsNullOrWhiteSpace(bundledModelsDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "models")
            : bundledModelsDirectory;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        Directory.CreateDirectory(_userModelsDirectory);
    }

    public bool IsPairReady(string direction)
    {
        return IsFolderReady(ResolvePairFolder(direction));
    }

    public string GetConfigPath(string direction)
    {
        return Path.Combine(ResolvePairFolder(direction), "config.yml");
    }

    public IReadOnlyList<string> DirectionsFor(string sourceLanguage, string targetLanguage)
    {
        var source = MapLanguage(sourceLanguage);
        var target = MapLanguage(targetLanguage);
        if (source == target)
        {
            return [];
        }

        if (source == "en")
        {
            return [$"en-{target}"];
        }

        if (target == "en")
        {
            return [$"{source}-en"];
        }

        return [$"{source}-en", $"en-{target}"];
    }

    public async Task EnsureReadyAsync(
        string sourceLanguage,
        string targetLanguage,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var direction in DirectionsFor(sourceLanguage, targetLanguage))
        {
            await EnsurePairAsync(direction, progress, cancellationToken);
        }
    }

    public async Task EnsurePairAsync(
        string direction,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        direction = NormalizeDirection(direction);
        if (IsPairReady(direction))
        {
            return;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (IsPairReady(direction))
            {
                return;
            }

            progress?.Report($"Downloading {direction.ToUpperInvariant()} translation files...");
            var registry = await GetRegistryAsync(cancellationToken);
            if (!registry.Models.TryGetValue(direction, out var candidates) || candidates.Count == 0)
            {
                throw new TranslationProviderException(
                    $"No offline model is available for {direction}.",
                    "Offline model missing");
            }

            var model = PickCandidate(candidates);
            var folder = Path.Combine(_userModelsDirectory, direction);
            var tempFolder = folder + ".download";
            if (Directory.Exists(tempFolder))
            {
                Directory.Delete(tempFolder, recursive: true);
            }

            Directory.CreateDirectory(tempFolder);

            var files = new List<(string Url, string FileName)>();
            AddFile(files, registry.BaseUrl, model.Files.Model?.Path);
            AddFile(files, registry.BaseUrl, model.Files.Vocab?.Path);
            AddFile(files, registry.BaseUrl, model.Files.SrcVocab?.Path);
            AddFile(files, registry.BaseUrl, model.Files.TrgVocab?.Path);
            AddFile(files, registry.BaseUrl, model.Files.LexicalShortlist?.Path);

            if (files.Count == 0)
            {
                throw new TranslationProviderException(
                    $"Offline model files for {direction} are incomplete.",
                    "Offline model missing");
            }

            for (var index = 0; index < files.Count; index++)
            {
                var (url, fileName) = files[index];
                progress?.Report($"Downloading {direction.ToUpperInvariant()} ({index + 1}/{files.Count})...");
                await DownloadAndDecompressAsync(url, Path.Combine(tempFolder, fileName), cancellationToken);
            }

            WriteConfig(tempFolder, model);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            Directory.Move(tempFolder, folder);
            progress?.Report($"{direction.ToUpperInvariant()} model is ready.");
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<MozillaModelRegistry> GetRegistryAsync(CancellationToken cancellationToken)
    {
        if (_registry is not null)
        {
            return _registry;
        }

        using var stream = await _httpClient.GetStreamAsync(RegistryUrl, cancellationToken);
        var registry = await JsonSerializer.DeserializeAsync<MozillaModelRegistry>(
            stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken);
        _registry = registry ?? throw new TranslationProviderException(
            "Could not read the offline translation catalog.",
            "Offline catalog failed");
        return _registry;
    }

    private static MozillaModelCandidate PickCandidate(IReadOnlyList<MozillaModelCandidate> candidates)
    {
        return candidates.FirstOrDefault(item =>
                   string.Equals(item.ReleaseStatus, "Release", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(item.ReleaseStatus, "Release Desktop", StringComparison.OrdinalIgnoreCase))
               ?? candidates[0];
    }

    private static void AddFile(List<(string Url, string FileName)> files, string? baseUrl, string? path)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var fileName = Path.GetFileName(path);
        if (fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^3];
        }

        if (files.Any(item => item.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        files.Add(($"{baseUrl.TrimEnd('/')}/{path.TrimStart('/')}", fileName));
    }

    private async Task DownloadAndDecompressAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        await using var response = await _httpClient.GetStreamAsync(url, cancellationToken);
        await using var memory = new MemoryStream();
        await response.CopyToAsync(memory, cancellationToken);
        memory.Position = 0;

        var isGzip = memory.Length >= 2
            && memory.ReadByte() == 0x1F
            && memory.ReadByte() == 0x8B;
        memory.Position = 0;

        await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        if (isGzip)
        {
            await using var gzip = new GZipStream(memory, CompressionMode.Decompress, leaveOpen: true);
            await gzip.CopyToAsync(output, cancellationToken);
        }
        else
        {
            await memory.CopyToAsync(output, cancellationToken);
        }
    }

    private static void WriteConfig(string folder, MozillaModelCandidate model)
    {
        var modelFile = FindFile(folder, "model.");
        var shortlistFile = FindFile(folder, "lex.");
        var vocabFiles = Directory.GetFiles(folder, "*.spm")
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToArray();
        if (vocabFiles.Length == 1)
        {
            vocabFiles = [vocabFiles[0], vocabFiles[0]];
        }

        if (string.IsNullOrWhiteSpace(modelFile) || vocabFiles.Length < 2)
        {
            throw new TranslationProviderException(
                "Downloaded translation files are incomplete.",
                "Offline model missing");
        }

        var precision = modelFile.Contains("alphas", StringComparison.OrdinalIgnoreCase)
            ? "int8shiftAlphaAll"
            : "int8shiftAll";
        var shortlistBlock = string.IsNullOrWhiteSpace(shortlistFile)
            ? string.Empty
            : $"""
            shortlist:
            - {shortlistFile}
            - false
            """;

        var config = $"""
            relative-paths: true
            models:
            - {modelFile}
            vocabs:
            - {vocabFiles[0]}
            - {vocabFiles[1]}
            {shortlistBlock}
            beam-size: 1
            normalize: 1.0
            word-penalty: 0
            max-length-break: 128
            mini-batch-words: 1024
            workspace: 128
            max-length-factor: 2.0
            skip-cost: true
            cpu-threads: 0
            quiet: true
            quiet-translation: true
            gemm-precision: {precision}
            """;

        File.WriteAllText(Path.Combine(folder, "config.yml"), config);
    }

    private static string? FindFile(string folder, string prefix)
    {
        return Directory.GetFiles(folder)
            .Select(Path.GetFileName)
            .FirstOrDefault(name => name is not null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private string ResolvePairFolder(string direction)
    {
        direction = NormalizeDirection(direction);
        var userFolder = Path.Combine(_userModelsDirectory, direction);
        if (IsFolderReady(userFolder))
        {
            return userFolder;
        }

        var bundledFolder = Path.Combine(_bundledModelsDirectory, direction);
        if (IsFolderReady(bundledFolder))
        {
            return bundledFolder;
        }

        return userFolder;
    }

    private static bool IsFolderReady(string folder)
    {
        return Directory.Exists(folder)
            && File.Exists(Path.Combine(folder, "config.yml"))
            && Directory.GetFiles(folder, "model.*").Length > 0
            && Directory.GetFiles(folder, "*.spm").Length > 0;
    }

    private static string NormalizeDirection(string direction)
    {
        return direction.Trim().ToLowerInvariant().Replace('_', '-');
    }

    private static string MapLanguage(string language)
    {
        return language.Trim().ToLowerInvariant() switch
        {
            "auto" => "en",
            "zh-hans" or "zh-cn" or "zh" => "zh",
            _ => language.Trim().ToLowerInvariant()
        };
    }

    private sealed class MozillaModelRegistry
    {
        [JsonPropertyName("baseUrl")]
        public string? BaseUrl { get; set; }

        [JsonPropertyName("models")]
        public Dictionary<string, List<MozillaModelCandidate>> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class MozillaModelCandidate
    {
        [JsonPropertyName("releaseStatus")]
        public string? ReleaseStatus { get; set; }

        [JsonPropertyName("files")]
        public MozillaModelFiles Files { get; set; } = new();
    }

    private sealed class MozillaModelFiles
    {
        [JsonPropertyName("model")]
        public MozillaModelFile? Model { get; set; }

        [JsonPropertyName("vocab")]
        public MozillaModelFile? Vocab { get; set; }

        [JsonPropertyName("srcVocab")]
        public MozillaModelFile? SrcVocab { get; set; }

        [JsonPropertyName("trgVocab")]
        public MozillaModelFile? TrgVocab { get; set; }

        [JsonPropertyName("lexicalShortlist")]
        public MozillaModelFile? LexicalShortlist { get; set; }
    }

    private sealed class MozillaModelFile
    {
        [JsonPropertyName("path")]
        public string? Path { get; set; }
    }
}
