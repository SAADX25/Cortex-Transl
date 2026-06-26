using CortexTransl.App.Data;
using CortexTransl.App.Models;
using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Overlay;
using CortexTransl.App.Services.Profiles;
using CortexTransl.App.Services.Translation;
using CortexTransl.App.Utils;
using System.Collections.ObjectModel;

namespace CortexTransl.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly DatabaseMigrator _databaseMigrator;
    private readonly IRegionSelectionService _regionSelectionService;
    private readonly CaptureTranslatePipeline _pipeline;
    private readonly IOverlayService _overlayService;
    private readonly IGameProfileRepository _profileRepository;
    private readonly TranslationProviderSettings _translationProviderSettings;
    private readonly TimingLogger _timingLogger;

    private CaptureRegion _selectedRegion = CaptureRegion.Empty;
    private string _sourceLanguage = "en";
    private string _targetLanguage = "ar";
    private string _ocrEngine = "windows";
    private string _translationProvider = "placeholder";
    private double _overlayFontSize = 32;
    private double _overlayOpacity = 0.86;
    private string _originalText = string.Empty;
    private string _translatedText = string.Empty;
    private string _statusMessage = "Ready. Select a dialogue region to begin.";
    private string _profileName = string.Empty;
    private string _deepLApiKey;
    private bool _useDeepLFreeApi = true;
    private GameProfile? _selectedProfile;

    public MainViewModel(
        DatabaseMigrator databaseMigrator,
        IRegionSelectionService regionSelectionService,
        CaptureTranslatePipeline pipeline,
        IOverlayService overlayService,
        IGameProfileRepository profileRepository,
        TranslationProviderSettings translationProviderSettings,
        TimingLogger timingLogger)
    {
        _databaseMigrator = databaseMigrator;
        _regionSelectionService = regionSelectionService;
        _pipeline = pipeline;
        _overlayService = overlayService;
        _profileRepository = profileRepository;
        _translationProviderSettings = translationProviderSettings;
        _timingLogger = timingLogger;
        _deepLApiKey = translationProviderSettings.DeepLApiKey;
        _useDeepLFreeApi = translationProviderSettings.UseDeepLFreeApi;

        SelectRegionCommand = new AsyncRelayCommand(_ => SelectRegionAsync());
        CaptureAndTranslateCommand = new AsyncRelayCommand(_ => CaptureAndTranslateAsync());
        SaveProfileCommand = new AsyncRelayCommand(_ => SaveProfileAsync(), _ => !SelectedRegion.IsEmpty);
        LoadSelectedProfileCommand = new RelayCommand(_ => LoadSelectedProfile(), _ => SelectedProfile is not null);
        ResetDebugMetrics();
    }

    public IReadOnlyList<OptionItem> SourceLanguageOptions { get; } =
    [
        new("en", "English"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("zh-Hans", "Chinese Simplified"),
        new("fr", "French"),
        new("de", "German"),
        new("es", "Spanish"),
        new("auto", "User Profile")
    ];

    public IReadOnlyList<OptionItem> TargetLanguageOptions { get; } =
    [
        new("ar", "Arabic")
    ];

    public IReadOnlyList<OptionItem> OcrEngineOptions { get; } =
    [
        new("windows", "Windows OCR")
    ];

    public IReadOnlyList<OptionItem> TranslationProviderOptions { get; } =
    [
        new("placeholder", "Placeholder"),
        new("deepl", "DeepL")
    ];

    public ObservableCollection<GameProfile> Profiles { get; } = [];

    public ObservableCollection<TimingEntry> LastTimings { get; } = [];

    public ObservableCollection<DebugMetric> DebugMetrics { get; } = [];

    public AsyncRelayCommand SelectRegionCommand { get; }

    public AsyncRelayCommand CaptureAndTranslateCommand { get; }

    public AsyncRelayCommand SaveProfileCommand { get; }

    public RelayCommand LoadSelectedProfileCommand { get; }

    public CaptureRegion SelectedRegion
    {
        get => _selectedRegion;
        private set
        {
            if (SetProperty(ref _selectedRegion, value))
            {
                OnPropertyChanged(nameof(SelectedRegionDisplay));
                SaveProfileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectedRegionDisplay => SelectedRegion.ToString();

    public string SourceLanguage
    {
        get => _sourceLanguage;
        set => SetProperty(ref _sourceLanguage, value);
    }

    public string TargetLanguage
    {
        get => _targetLanguage;
        set => SetProperty(ref _targetLanguage, value);
    }

    public string OcrEngine
    {
        get => _ocrEngine;
        set => SetProperty(ref _ocrEngine, value);
    }

    public string TranslationProvider
    {
        get => _translationProvider;
        set
        {
            if (SetProperty(ref _translationProvider, value))
            {
                ResetDebugMetrics();
            }
        }
    }

    public string DeepLApiKey
    {
        get => _deepLApiKey;
        set
        {
            if (SetProperty(ref _deepLApiKey, value))
            {
                _translationProviderSettings.DeepLApiKey = value;
                ResetDebugMetrics();
            }
        }
    }

    public bool UseDeepLFreeApi
    {
        get => _useDeepLFreeApi;
        set
        {
            if (SetProperty(ref _useDeepLFreeApi, value))
            {
                _translationProviderSettings.UseDeepLFreeApi = value;
                ResetDebugMetrics();
            }
        }
    }

    public double OverlayFontSize
    {
        get => _overlayFontSize;
        set => SetProperty(ref _overlayFontSize, value);
    }

    public double OverlayOpacity
    {
        get => _overlayOpacity;
        set => SetProperty(ref _overlayOpacity, value);
    }

    public string OriginalText
    {
        get => _originalText;
        private set => SetProperty(ref _originalText, value);
    }

    public string TranslatedText
    {
        get => _translatedText;
        private set => SetProperty(ref _translatedText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ProfileName
    {
        get => _profileName;
        set => SetProperty(ref _profileName, value);
    }

    public GameProfile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                LoadSelectedProfileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _databaseMigrator.InitializeAsync(cancellationToken);
        await RefreshProfilesAsync(cancellationToken);
    }

    public void SetStatus(string message)
    {
        StatusMessage = message;
    }

    public async Task CaptureAndTranslateAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedRegion.IsEmpty)
        {
            StatusMessage = "Select a dialogue region before capturing.";
            ResetDebugMetrics();
            return;
        }

        try
        {
            StatusMessage = "Capturing selected region...";
            _translationProviderSettings.DeepLApiKey = DeepLApiKey;
            _translationProviderSettings.UseDeepLFreeApi = UseDeepLFreeApi;

            var settings = new PipelineSettings(SourceLanguage, TargetLanguage, OcrEngine, TranslationProvider);
            var result = await _pipeline.RunAsync(SelectedRegion, settings, cancellationToken);

            OriginalText = result.OriginalText;
            TranslatedText = result.TranslatedText;

            LastTimings.Clear();
            foreach (var timing in result.Timings)
            {
                LastTimings.Add(timing);
            }

            long? overlayElapsed = null;
            if (!string.IsNullOrWhiteSpace(result.TranslatedText))
            {
                overlayElapsed = await _overlayService.ShowTextAsync(
                    result.TranslatedText,
                    SelectedRegion,
                    OverlayFontSize,
                    OverlayOpacity);

                var overlayTiming = new TimingEntry("overlay update", overlayElapsed.Value);
                LastTimings.Add(overlayTiming);
                await _timingLogger.LogAsync("overlay-update", [overlayTiming], cancellationToken);
            }
            else
            {
                _overlayService.Hide();
            }

            UpdateDebugMetrics(result, overlayElapsed);
            StatusMessage = result.Status;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            ResetDebugMetrics();
        }
    }

    private async Task SelectRegionAsync()
    {
        try
        {
            var region = await _regionSelectionService.SelectRegionAsync();
            if (region is null || region.IsEmpty)
            {
                StatusMessage = "Region selection cancelled.";
                return;
            }

            SelectedRegion = region;
            StatusMessage = "Region selected.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task SaveProfileAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var name = string.IsNullOrWhiteSpace(ProfileName)
                ? $"Profile {DateTime.Now:yyyy-MM-dd HHmm}"
                : ProfileName.Trim();

            var profile = new GameProfile
            {
                Name = name,
                Region = SelectedRegion,
                SourceLanguage = SourceLanguage,
                TargetLanguage = TargetLanguage,
                OcrEngine = OcrEngine,
                TranslationProvider = TranslationProvider
            };

            await _profileRepository.SaveAsync(profile, cancellationToken);
            ProfileName = name;
            await RefreshProfilesAsync(cancellationToken);
            SelectedProfile = Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            StatusMessage = $"Saved profile '{name}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private void LoadSelectedProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        SelectedRegion = SelectedProfile.Region;
        SourceLanguage = SelectedProfile.SourceLanguage;
        TargetLanguage = SelectedProfile.TargetLanguage;
        OcrEngine = SelectedProfile.OcrEngine;
        TranslationProvider = SelectedProfile.TranslationProvider;
        ProfileName = SelectedProfile.Name;
        StatusMessage = $"Loaded profile '{SelectedProfile.Name}'.";
    }

    private async Task RefreshProfilesAsync(CancellationToken cancellationToken = default)
    {
        var selectedName = SelectedProfile?.Name;
        Profiles.Clear();

        foreach (var profile in await _profileRepository.GetAllAsync(cancellationToken))
        {
            Profiles.Add(profile);
        }

        if (!string.IsNullOrWhiteSpace(selectedName))
        {
            SelectedProfile = Profiles.FirstOrDefault(p => p.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void UpdateDebugMetrics(PipelineResult result, long? overlayElapsed)
    {
        DebugMetrics.Clear();
        DebugMetrics.Add(new DebugMetric("Capture", FormatTiming(result.Timings, "capture")));
        DebugMetrics.Add(new DebugMetric("OCR", FormatFirstTiming(result.Timings, "ocr", "ocr skipped")));
        DebugMetrics.Add(new DebugMetric("Cache", result.CacheStatus));
        DebugMetrics.Add(new DebugMetric("Provider", result.ProviderStatus));
        DebugMetrics.Add(new DebugMetric("Translation", FormatFirstTiming(result.Timings, "translation", "translation skipped")));
        DebugMetrics.Add(new DebugMetric("Overlay", overlayElapsed is null ? "not shown" : $"{overlayElapsed.Value} ms"));
    }

    private void ResetDebugMetrics()
    {
        DebugMetrics.Clear();
        DebugMetrics.Add(new DebugMetric("Capture", "-"));
        DebugMetrics.Add(new DebugMetric("OCR", "-"));
        DebugMetrics.Add(new DebugMetric("Cache", "-"));
        DebugMetrics.Add(new DebugMetric("Provider", CurrentProviderStatus()));
        DebugMetrics.Add(new DebugMetric("Translation", "-"));
        DebugMetrics.Add(new DebugMetric("Overlay", "-"));
    }

    private static string FormatFirstTiming(IReadOnlyList<TimingEntry> timings, string measuredName, string skippedName)
    {
        var measured = timings.FirstOrDefault(t => t.Name.Equals(measuredName, StringComparison.OrdinalIgnoreCase));
        if (measured is not null)
        {
            return $"{measured.ElapsedMilliseconds} ms";
        }

        return timings.Any(t => t.Name.Equals(skippedName, StringComparison.OrdinalIgnoreCase))
            ? "skipped"
            : "-";
    }

    private static string FormatTiming(IReadOnlyList<TimingEntry> timings, string name)
    {
        var timing = timings.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return timing is null ? "-" : $"{timing.ElapsedMilliseconds} ms";
    }

    private string CurrentProviderStatus()
    {
        if (TranslationProvider.Equals("deepl", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(DeepLApiKey)
                ? "DeepL missing API key"
                : "DeepL ready";
        }

        return "Placeholder ready";
    }

    public void Dispose()
    {
        _overlayService.Dispose();
    }
}
