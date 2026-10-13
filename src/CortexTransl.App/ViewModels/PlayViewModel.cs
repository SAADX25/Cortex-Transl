using CortexTransl.App.Data;
using CortexTransl.App.Models;
using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Overlay;
using CortexTransl.App.Services.Profiles;
using CortexTransl.App.Services.Settings;
using CortexTransl.App.Services.Translation;
using CortexTransl.App.Utils;
using System.Collections.ObjectModel;
using CortexTransl.App.Services.Diagnostics;
using System.IO;

namespace CortexTransl.App.ViewModels;

public sealed partial class PlayViewModel : ObservableObject, IDisposable
{
    public const string TargetLanguage = "ar";

    private readonly DatabaseMigrator _databaseMigrator;
    private readonly IRegionSelectionService _regionSelectionService;
    private readonly TranslationPipeline _pipeline;
    private readonly IOverlayService _overlayService;
    private readonly IGameProfileRepository _profileRepository;
    private readonly TranslationProviderSettings _translationProviderSettings;
    private readonly OfflineBergamotTranslationProvider _offlineProvider;
    private readonly AppSettingsService _appSettingsService;
    private readonly ThemeService _themeService;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private CaptureRegion _selectedRegion = CaptureRegion.Empty;
    private string _sourceLanguage = "en";
    private string _deepLApiKey = string.Empty;
    private bool _useDeepLFreeApi = true;
    private string _translationEngine = "offline";
    private bool _isOfflineReady;
    private bool _isDownloadingModel;
    private string _offlineModelStatus = "Arabic translation files download once, then work offline.";
    private string _overlayPlacement = "cover";
    private string _translationMode = "dialogue";
    private double _overlayBackgroundOpacity = 0.88;
    private string _overlayTextSize = "medium";
    private string _overlayTextColor = "white";
    private bool _minimizeDuringPlay;
    private string _profileName = string.Empty;
    private GameProfile? _selectedProfile;
    private string _originalText = string.Empty;
    private string _translatedText = string.Empty;
    private string _statusMessage = "Downloading Arabic files or select the dialogue region.";
    private IReadOnlyList<TranslatedBlock> _lastBlocks = [];
    private bool _isPlaying;
    private bool _isBusy;
    private bool _disposed;
    private string _selectedNavPage = "play";
    private CancellationTokenSource? _playCts;
    private string _testInputText = string.Empty;
    private string _testTranslatedText = string.Empty;
    private string _testStatusMessage = "Type or paste English text, then press Test to check translation quality.";
    private bool _isTestRunning;
    private StorySettings _storySettings = new();
    private OverlayRenderMeasurement? _renderMeasurement;
    private long _presentedSequence = -1;
    public bool FastSubtitles { get => _storySettings.FastSubtitles; set { _storySettings.FastSubtitles = value; UpdateStorySettings(); } }
    public bool PerformanceDiagnosticsEnabled { get => _storySettings.PerformanceDiagnostics; set { _storySettings.PerformanceDiagnostics = value; UpdateStorySettings(); } }
    public RelayCommand ExportPerformanceCommand { get; }

    private void ExportPerformance()
    {
        var path = Path.Combine(AppDataPaths.Default.DataDirectory, "logs", "subtitle-performance.json");
        _pipeline.Diagnostics.Export(path);
        StatusMessage = $"Latency report saved to {path}. Timing and resource numbers only.";
    }

    public string SpeakerIdentity { get => _storySettings.Speaker; set { _storySettings.Speaker = value; UpdateStorySettings(); } }
    public string SceneState { get => _storySettings.SceneState; set { _storySettings.SceneState = value; UpdateStorySettings(); } }
    public string DeepLGlossaryId { get => _storySettings.DeepLGlossaryId; set { _storySettings.DeepLGlossaryId = value; UpdateStorySettings(); } }
    public int SubtitleGraceMilliseconds { get => _storySettings.DisappearanceGraceMilliseconds; set { _storySettings.DisappearanceGraceMilliseconds = Math.Clamp(value, 0, 5000); UpdateStorySettings(); } }
    public int SubtitleMaximumMilliseconds { get => _storySettings.MaximumVisibilityMilliseconds; set { _storySettings.MaximumVisibilityMilliseconds = Math.Clamp(value, 500, 60000); UpdateStorySettings(); } }
    public int TextStabilityMilliseconds { get => _storySettings.StabilityMilliseconds; set { _storySettings.StabilityMilliseconds = Math.Clamp(value, 100, 2000); UpdateStorySettings(); } }
    public string TerminologyText
    {
        get => FormatTerms(_storySettings.Terminology);
        set { if (TryParseTerms(value, out var terms)) { _storySettings.Terminology = terms; UpdateStorySettings(); } }
    }
    public string SpeakerNamesText
    {
        get => FormatTerms(_storySettings.SpeakerNames);
        set { if (TryParseTerms(value, out var terms)) { _storySettings.SpeakerNames = terms; UpdateStorySettings(); } }
    }

    private static string FormatTerms(IReadOnlyDictionary<string, string> terms) =>
        string.Join(Environment.NewLine, terms.Select(x => $"{x.Key} = {x.Value}"));

    private bool TryParseTerms(string text, out Dictionary<string, string> terms)
    {
        terms = new(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace) || terms.Count >= 100)
            { StatusMessage = "Use one English = Arabic entry per line (up to 100 entries)."; return false; }
            terms[parts[0]] = parts[1];
        }
        return true;
    }

    private void UpdateStorySettings()
    {
        _pipeline.Configure(string.IsNullOrWhiteSpace(ProfileName) ? "session" : ProfileName.Trim(), _storySettings);
        _ = SaveSettingsAsync();
    }

    private void LoadStorySettings(StorySettings? settings)
    {
        _storySettings = (settings ?? new()).Clone();
        foreach (var property in new[] { nameof(FastSubtitles), nameof(PerformanceDiagnosticsEnabled), nameof(SpeakerIdentity), nameof(SceneState), nameof(DeepLGlossaryId), nameof(TerminologyText),
            nameof(SpeakerNamesText), nameof(SubtitleGraceMilliseconds), nameof(SubtitleMaximumMilliseconds), nameof(TextStabilityMilliseconds) })
            OnPropertyChanged(property);
        _pipeline.Configure(string.IsNullOrWhiteSpace(ProfileName) ? "session" : ProfileName.Trim(), _storySettings);
    }

    public PlayViewModel(
        DatabaseMigrator databaseMigrator,
        IRegionSelectionService regionSelectionService,
        TranslationPipeline pipeline,
        IOverlayService overlayService,
        IGameProfileRepository profileRepository,
        TranslationProviderSettings translationProviderSettings,
        OfflineBergamotTranslationProvider offlineProvider,
        AppSettingsService appSettingsService,
        ThemeService themeService)
    {
        _databaseMigrator = databaseMigrator;
        _regionSelectionService = regionSelectionService;
        _pipeline = pipeline;
        _overlayService = overlayService;
        _profileRepository = profileRepository;
        _translationProviderSettings = translationProviderSettings;
        _offlineProvider = offlineProvider;
        _appSettingsService = appSettingsService;
        _themeService = themeService;
        _pipeline.ResultAvailable += OnPipelineResultAvailable;
        ExportPerformanceCommand = new RelayCommand(_ => ExportPerformance());

        SelectRegionCommand = new AsyncRelayCommand(_ => SelectRegionAsync());
        TogglePlayCommand = new AsyncRelayCommand(_ => TogglePlayAsync(), _ => CanStartOrStop());
        DownloadOfflineModelCommand = new AsyncRelayCommand(_ => EnsureOfflineModelAsync(downloadIfMissing: true), _ => CanDownloadModel());
        SaveProfileCommand = new AsyncRelayCommand(_ => SaveProfileAsync(), _ => CanSaveProfile());
        UsePresetCommand = new RelayCommand(parameter => UsePreset(parameter as GameProfile), parameter => parameter is GameProfile);
        DeletePresetCommand = new AsyncRelayCommand(
            parameter => DeletePresetAsync(parameter as GameProfile),
            parameter => parameter is GameProfile);
        RunTestCommand = new AsyncRelayCommand(_ => RunTranslationTestAsync(), _ => CanRunTest());
        UseTestSampleCommand = new RelayCommand(parameter => UseTestSample(parameter as string), _ => !_isTestRunning);
    }

    public event EventHandler? MinimizeRequested;

    private static readonly IReadOnlyList<OptionItem> OfflineSourceLanguageOptions =
    [
        new("en", "English"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("zh-Hans", "Chinese"),
        new("fr", "French"),
        new("de", "German"),
        new("es", "Spanish")
    ];

    private static readonly IReadOnlyList<OptionItem> DeepLSourceLanguageOptions =
    [
        new("en", "English"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("zh-Hans", "Chinese"),
        new("fr", "French"),
        new("de", "German"),
        new("es", "Spanish"),
        new("auto", "Auto-detect")
    ];

    public IReadOnlyList<OptionItem> SourceLanguageOptions =>
        IsDeepLEngine ? DeepLSourceLanguageOptions : OfflineSourceLanguageOptions;

    public string SourceLanguageHint => IsDeepLEngine
        ? "Auto-detect is available with DeepL."
        : "Choose the game's language; offline translation cannot auto-detect it.";

    public IReadOnlyList<OptionItem> TranslationEngineOptions { get; } =
    [
        new("offline", "Offline Arabic files"),
        new("deepl", "DeepL API")
    ];

    public ObservableCollection<GameProfile> Profiles { get; } = [];

    public AsyncRelayCommand DownloadOfflineModelCommand { get; }

    public AsyncRelayCommand SelectRegionCommand { get; }

    public AsyncRelayCommand TogglePlayCommand { get; }

    public AsyncRelayCommand SaveProfileCommand { get; }

    public RelayCommand UsePresetCommand { get; }

    public AsyncRelayCommand DeletePresetCommand { get; }

    public AsyncRelayCommand RunTestCommand { get; }

    public RelayCommand UseTestSampleCommand { get; }

    public ObservableCollection<TestChunkResult> TestChunks { get; } = [];

    public CaptureRegion SelectedRegion
    {
        get => _selectedRegion;
        private set
        {
            if (SetProperty(ref _selectedRegion, value))
            {
                OnPropertyChanged(nameof(SelectedRegionDisplay));
                OnPropertyChanged(nameof(HasRegion));
                OnPropertyChanged(nameof(SessionBadgeText));
                SaveProfileCommand.RaiseCanExecuteChanged();
                TogglePlayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string SelectedRegionDisplay => SelectedRegion.IsEmpty
        ? "No region selected"
        : $"{SelectedRegion.Width} × {SelectedRegion.Height}";

    public string RegionHint => IsListMode
        ? "Drag around the menu, icon names, or button labels. Each name is translated in place."
        : "Drag a rectangle around the story dialogue box only. Use borderless windowed mode in the game.";

    public string RegionSectionTitle => IsListMode ? "Menu region" : "Story region";

    public string SelectRegionButtonLabel => IsListMode ? "Select menu region" : "Select story region";

    public string TranslationModeHint => IsListMode
        ? "Menus: Arabic sits on each icon or button name, like desktop labels."
        : "Story: one Arabic box covers the dialogue.";

    public bool HasRegion => !SelectedRegion.IsEmpty;

    public bool IsDialogueMode => !IsListMode;

    public string OverlayOpacityPercent => $"{Math.Clamp((int)Math.Round(OverlayBackgroundOpacity * 100), 60, 95)}%";

    public string SessionBadgeText =>
        IsPlaying
            ? "Live"
            : CanTranslate && HasRegion
                ? "Ready"
                : CanTranslate
                    ? "Select region"
                    : IsOfflineEngine
                        ? "Download files"
                        : "Setup";

    public string SourceLanguage
    {
        get => _sourceLanguage;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "en" : value.Trim();
            if (IsOfflineEngine && normalized.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "en";
            }

            if (SetProperty(ref _sourceLanguage, normalized))
            {
                _pipeline.Reset();
                _ = SaveSettingsAsync();
                if (IsOfflineEngine)
                {
                    IsOfflineReady = _offlineProvider.IsPairReady(normalized);
                    _ = EnsureOfflineModelAsync();
                }
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
                _pipeline.Reset();
                OnPropertyChanged(nameof(ApiKeyStatusText));
                OnPropertyChanged(nameof(HasApiKey));
                OnPropertyChanged(nameof(CanTranslate));
                OnPropertyChanged(nameof(SessionBadgeText));
                TogglePlayCommand.RaiseCanExecuteChanged();
                _ = SaveSettingsAsync();
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
                _pipeline.Reset();
                _ = SaveSettingsAsync();
            }
        }
    }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(DeepLApiKey);

    public string ApiKeyStatusText => HasApiKey ? "API key saved and encrypted" : "Paste a DeepL API key";

    public string TranslationEngine
    {
        get => _translationEngine;
        set
        {
            var normalized = string.Equals(value, "deepl", StringComparison.OrdinalIgnoreCase) ? "deepl" : "offline";
            if (SetProperty(ref _translationEngine, normalized))
            {
                _translationProviderSettings.TranslationEngine = normalized;
                _pipeline.Reset();
                OnPropertyChanged(nameof(IsOfflineEngine));
                OnPropertyChanged(nameof(IsDeepLEngine));
                OnPropertyChanged(nameof(SourceLanguageOptions));
                OnPropertyChanged(nameof(SourceLanguageHint));
                OnPropertyChanged(nameof(CanTranslate));
                OnPropertyChanged(nameof(SessionBadgeText));
                TogglePlayCommand.RaiseCanExecuteChanged();
                DownloadOfflineModelCommand.RaiseCanExecuteChanged();
                RunTestCommand.RaiseCanExecuteChanged();
                if (IsOfflineEngine)
                {
                    if (_sourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
                    {
                        SourceLanguage = "en";
                    }

                    _ = EnsureOfflineModelAsync();
                }

                _ = SaveSettingsAsync();
            }
        }
    }

    public bool IsOfflineEngine => !_translationEngine.Equals("deepl", StringComparison.OrdinalIgnoreCase);

    public bool IsDeepLEngine => !IsOfflineEngine;

    public bool IsOfflineReady
    {
        get => _isOfflineReady;
        private set
        {
            if (SetProperty(ref _isOfflineReady, value))
            {
                OnPropertyChanged(nameof(CanTranslate));
                OnPropertyChanged(nameof(SessionBadgeText));
                TogglePlayCommand.RaiseCanExecuteChanged();
                RunTestCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsDownloadingModel
    {
        get => _isDownloadingModel;
        private set
        {
            if (SetProperty(ref _isDownloadingModel, value))
            {
                DownloadOfflineModelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string OfflineModelStatus
    {
        get => _offlineModelStatus;
        private set => SetProperty(ref _offlineModelStatus, value);
    }

    public bool CanTranslate => IsDeepLEngine ? HasApiKey : IsOfflineReady;

    public string OverlayPlacement
    {
        get => _overlayPlacement;
        set
        {
            var normalized = OverlaySettings.NormalizePlacement(value);
            if (SetProperty(ref _overlayPlacement, normalized))
            {
                _ = SaveSettingsAsync();
                RefreshOverlay();
            }
        }
    }

    public string TranslationMode
    {
        get => _translationMode;
        set
        {
            var normalized = OverlaySettings.NormalizeMode(value);
            if (SetProperty(ref _translationMode, normalized))
            {
                _pipeline.Reset();
                _lastBlocks = [];
                OnPropertyChanged(nameof(IsListMode));
                OnPropertyChanged(nameof(IsDialogueMode));
                OnPropertyChanged(nameof(RegionHint));
                OnPropertyChanged(nameof(RegionSectionTitle));
                OnPropertyChanged(nameof(SelectRegionButtonLabel));
                OnPropertyChanged(nameof(TranslationModeHint));
                _ = SaveSettingsAsync();
                RefreshOverlay();
            }
        }
    }

    public bool IsListMode => _translationMode.Equals("list", StringComparison.OrdinalIgnoreCase);

    public double OverlayBackgroundOpacity
    {
        get => _overlayBackgroundOpacity;
        set
        {
            if (SetProperty(ref _overlayBackgroundOpacity, value))
            {
                OnPropertyChanged(nameof(OverlayOpacityPercent));
                _ = SaveSettingsAsync();
                RefreshOverlay();
            }
        }
    }

    public string OverlayTextSize
    {
        get => _overlayTextSize;
        set
        {
            var normalized = OverlayTextSizes.Normalize(value);
            if (SetProperty(ref _overlayTextSize, normalized))
            {
                OnPropertyChanged(nameof(OverlayFontScale));
                OnPropertyChanged(nameof(OverlayPreviewFontSize));
                OnPropertyChanged(nameof(OverlayTextSizeLabel));
                _ = SaveSettingsAsync();
                RefreshOverlay();
            }
        }
    }

    public double OverlayFontScale => OverlayTextSizes.FontScale(OverlayTextSize);

    public double OverlayPreviewFontSize => OverlayTextSizes.PreviewFontSize(OverlayTextSize);

    public string OverlayTextSizeLabel => OverlayTextSizes.Label(OverlayTextSize);

    public string OverlayTextColor
    {
        get => _overlayTextColor;
        set
        {
            var normalized = OverlayTextColors.Normalize(value);
            if (SetProperty(ref _overlayTextColor, normalized))
            {
                OnPropertyChanged(nameof(OverlayTextColorLabel));
                _ = SaveSettingsAsync();
                RefreshOverlay();
            }
        }
    }

    public string OverlayTextColorLabel => OverlayTextColors.Label(OverlayTextColor);

    public string OverlayPreviewText => "This is how the translation looks over the game.";

    public string SelectedNavPage
    {
        get => _selectedNavPage;
        set
        {
            if (SetProperty(ref _selectedNavPage, value))
            {
                RaiseNavProperties();
            }
        }
    }

    public bool IsNavPlay
    {
        get => _selectedNavPage == "play";
        set { if (value) SelectedNavPage = "play"; }
    }

    public bool IsNavKey
    {
        get => _selectedNavPage == "key";
        set { if (value) SelectedNavPage = "key"; }
    }

    public bool IsNavRegion
    {
        get => _selectedNavPage == "region";
        set { if (value) SelectedNavPage = "region"; }
    }

    public bool IsNavLook
    {
        get => _selectedNavPage == "look";
        set { if (value) SelectedNavPage = "look"; }
    }

    public bool IsNavPresets
    {
        get => _selectedNavPage == "presets";
        set { if (value) SelectedNavPage = "presets"; }
    }

    public bool IsNavTest
    {
        get => _selectedNavPage == "test";
        set { if (value) SelectedNavPage = "test"; }
    }

    public string NavPageTitle => _selectedNavPage switch
    {
        "key" => "Translation engine",
        "region" => "Capture region",
        "look" => "Overlay look",
        "presets" => "Presets",
        "test" => "Test translation",
        _ => "Play"
    };

    public string NavPageSubtitle => _selectedNavPage switch
    {
        "key" => "Offline Arabic files or DeepL.",
        "region" => "Story box or menu labels.",
        "look" => "Size, color, position, and opacity.",
        "presets" => "Save a game setup and tap to load it.",
        "test" => "Check translation quality and find issues.",
        _ => "Pick story or menus, then start translation."
    };

    public bool MinimizeDuringPlay
    {
        get => _minimizeDuringPlay;
        set
        {
            if (SetProperty(ref _minimizeDuringPlay, value))
            {
                _ = SaveSettingsAsync();
            }
        }
    }

    public bool HasProfiles => Profiles.Count > 0;

    public bool HasNoProfiles => Profiles.Count == 0;

    public string ProfileName
    {
        get => _profileName;
        set
        {
            if (SetProperty(ref _profileName, value))
            {
                SaveProfileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public GameProfile? SelectedProfile
    {
        get => _selectedProfile;
        set => SetProperty(ref _selectedProfile, value);
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

    public void SetStatusMessage(string message)
    {
        StatusMessage = message;
    }

    public string TestInputText
    {
        get => _testInputText;
        set
        {
            if (SetProperty(ref _testInputText, value))
            {
                RunTestCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string TestTranslatedText
    {
        get => _testTranslatedText;
        private set => SetProperty(ref _testTranslatedText, value);
    }

    public string TestStatusMessage
    {
        get => _testStatusMessage;
        private set => SetProperty(ref _testStatusMessage, value);
    }

    public bool IsTestRunning
    {
        get => _isTestRunning;
        private set
        {
            if (SetProperty(ref _isTestRunning, value))
            {
                RunTestCommand.RaiseCanExecuteChanged();
                UseTestSampleCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayButtonLabel));
                OnPropertyChanged(nameof(SessionBadgeText));
                TogglePlayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string PlayButtonLabel => IsPlaying ? "Stop" : "Start";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _databaseMigrator.InitializeAsync(cancellationToken);
        await RefreshProfilesAsync(cancellationToken);

        var settings = await _appSettingsService.LoadAsync(cancellationToken);
        _themeService.ApplyTheme(string.IsNullOrWhiteSpace(settings.Theme) ? "Dark" : settings.Theme);
        _sourceLanguage = string.IsNullOrWhiteSpace(settings.SourceLanguage) ? "en" : settings.SourceLanguage;
        _overlayPlacement = OverlaySettings.NormalizePlacement(settings.OverlayPlacement);
        _translationMode = OverlaySettings.NormalizeMode(settings.TranslationMode);
        _overlayBackgroundOpacity = settings.OverlayBackgroundOpacity <= 0 ? 0.88 : settings.OverlayBackgroundOpacity;
        _overlayTextSize = OverlayTextSizes.Normalize(settings.OverlayTextSize);
        _overlayTextColor = OverlayTextColors.Normalize(settings.OverlayTextColor);
        _minimizeDuringPlay = settings.MinimizeDuringPlay;
        _useDeepLFreeApi = settings.UseDeepLFreeApi;
        _translationEngine = string.Equals(settings.TranslationEngine, "deepl", StringComparison.OrdinalIgnoreCase)
            ? "deepl"
            : "offline";
        if (IsOfflineEngine && _sourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            _sourceLanguage = "en";
        }
        _profileName = settings.ProfileName;
        LoadStorySettings(settings.Story);
        _deepLApiKey = _appSettingsService.DecryptApiKey(settings.EncryptedDeepLApiKey);
        _translationProviderSettings.DeepLApiKey = _deepLApiKey;
        _translationProviderSettings.UseDeepLFreeApi = _useDeepLFreeApi;
        _translationProviderSettings.TranslationEngine = _translationEngine;
        IsOfflineReady = _offlineProvider.IsPairReady(_sourceLanguage);

        if (settings.RegionWidth > 0 && settings.RegionHeight > 0)
        {
            _selectedRegion = new CaptureRegion(settings.RegionX, settings.RegionY, settings.RegionWidth, settings.RegionHeight);
        }

        OnPropertyChanged(string.Empty);
        SaveProfileCommand.RaiseCanExecuteChanged();
        TogglePlayCommand.RaiseCanExecuteChanged();

        SelectedNavPage = !CanTranslate ? "key" : !HasRegion ? "region" : "play";

        if (IsOfflineEngine)
        {
            StatusMessage = IsOfflineReady
                ? SelectedRegion.IsEmpty
                    ? "Select the dialogue region with F9, then press F8."
                    : "Ready. F8 starts translation. F10 opens the menu."
                : "Downloading Arabic translation files...";
            _ = EnsureOfflineModelAsync();
        }
        else
        {
            StatusMessage = HasApiKey
                ? SelectedRegion.IsEmpty
                    ? "Select the dialogue region with F9, then press F8."
                    : "Ready. F8 starts translation. F10 opens the menu."
                : "Paste your DeepL API key, then select the dialogue region.";
        }
    }

    private bool CanSaveProfile()
    {
        return HasRegion && !string.IsNullOrWhiteSpace(ProfileName);
    }

    private async Task SaveProfileAsync()
    {
        try
        {
            if (!CanSaveProfile())
            {
                StatusMessage = SelectedRegion.IsEmpty
                    ? "Select the dialogue region first."
                    : "Type a game name, then save the preset.";
                return;
            }

            var name = ProfileName.Trim();
            var profile = CreateProfileFromCurrent(name);
            await _profileRepository.SaveAsync(profile);
            ProfileName = name;
            await RefreshProfilesAsync();
            SelectedProfile = Profiles.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            await SaveSettingsAsync();
            StatusMessage = $"Preset \"{name}\" is ready. Tap it next time to use this look.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private void UsePreset(GameProfile? profile)
    {
        if (profile is null)
        {
            return;
        }

        ApplyPreset(profile);
        StatusMessage = $"Using \"{profile.Name}\". Press F8 to start.";
    }

    private async Task DeletePresetAsync(GameProfile? profile)
    {
        if (profile is null)
        {
            return;
        }

        try
        {
            await _profileRepository.DeleteAsync(profile.Id);
            if (SelectedProfile?.Id == profile.Id)
            {
                SelectedProfile = null;
            }

            await RefreshProfilesAsync();
            StatusMessage = $"Removed \"{profile.Name}\".";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private GameProfile CreateProfileFromCurrent(string name)
    {
        return new GameProfile
        {
            Name = name,
            Region = SelectedRegion,
            SourceLanguage = SourceLanguage,
            TargetLanguage = TargetLanguage,
            TranslationMode = _translationMode,
            OverlayPlacement = OverlayPlacement,
            OverlayBackgroundOpacity = OverlayBackgroundOpacity,
            OverlayTextSize = OverlayTextSize,
            OverlayTextColor = OverlayTextColor,
            OcrEngine = "windows",
            TranslationProvider = IsOfflineEngine ? "offline" : "deepl",
            Story = _storySettings.Clone()
        };
    }

    private void ApplyPreset(GameProfile profile)
    {
        _translationEngine = string.Equals(profile.TranslationProvider, "deepl", StringComparison.OrdinalIgnoreCase)
            ? "deepl"
            : "offline";
        _translationProviderSettings.TranslationEngine = _translationEngine;
        _selectedRegion = ScreenCoordinates.ClampToVirtualScreen(profile.Region);
        _sourceLanguage = string.IsNullOrWhiteSpace(profile.SourceLanguage) ? "en" : profile.SourceLanguage;
        if (IsOfflineEngine && _sourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            _sourceLanguage = "en";
        }
        _translationMode = OverlaySettings.NormalizeMode(profile.TranslationMode);
        _overlayPlacement = OverlaySettings.NormalizePlacement(profile.OverlayPlacement);
        _overlayBackgroundOpacity = profile.OverlayBackgroundOpacity <= 0 ? 0.88 : Math.Clamp(profile.OverlayBackgroundOpacity, 0.6, 0.95);
        _overlayTextSize = OverlayTextSizes.Normalize(profile.OverlayTextSize);
        _overlayTextColor = OverlayTextColors.Normalize(profile.OverlayTextColor);
        _profileName = profile.Name;
        LoadStorySettings(profile.Story);
        _pipeline.Reset();
        _pipeline.BindCapture(SelectedRegion, IsListMode);
        _lastBlocks = [];

        OnPropertyChanged(string.Empty);
        SaveProfileCommand.RaiseCanExecuteChanged();
        TogglePlayCommand.RaiseCanExecuteChanged();
        DownloadOfflineModelCommand.RaiseCanExecuteChanged();

        SelectedProfile = profile;
        _ = RefreshProfilesAsync();
        if (IsPlaying)
        {
            PinOverlay();
        }

        _ = SaveSettingsAsync();
        if (IsOfflineEngine)
        {
            _ = EnsureOfflineModelAsync();
        }
    }

    private async Task RefreshProfilesAsync(CancellationToken cancellationToken = default)
    {
        var selectedId = SelectedProfile?.Id;
        var selectedName = SelectedProfile?.Name ?? ProfileName;
        Profiles.Clear();
        foreach (var profile in await _profileRepository.GetAllAsync(cancellationToken))
        {
            profile.IsActive = selectedId is > 0
                ? profile.Id == selectedId
                : profile.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase);
            Profiles.Add(profile);
        }

        SelectedProfile = Profiles.FirstOrDefault(item => item.IsActive);
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasNoProfiles));
        UsePresetCommand.RaiseCanExecuteChanged();
        DeletePresetCommand.RaiseCanExecuteChanged();
    }

    private async Task SaveSettingsAsync()
    {
        var settings = new AppSettings
        {
            EncryptedDeepLApiKey = _appSettingsService.EncryptApiKey(_deepLApiKey),
            UseDeepLFreeApi = _useDeepLFreeApi,
            TranslationEngine = _translationEngine,
            Theme = _themeService.CurrentTheme,
            SourceLanguage = _sourceLanguage,
            OverlayPlacement = _overlayPlacement,
            OverlayBackgroundOpacity = _overlayBackgroundOpacity,
            OverlayTextSize = _overlayTextSize,
            OverlayTextColor = _overlayTextColor,
            MinimizeDuringPlay = _minimizeDuringPlay,
            RegionX = _selectedRegion.X,
            RegionY = _selectedRegion.Y,
            RegionWidth = _selectedRegion.Width,
            RegionHeight = _selectedRegion.Height,
            ProfileName = _profileName,
            TranslationMode = _translationMode,
            Story = _storySettings.Clone()
        };

        await _appSettingsService.SaveAsync(settings);
    }

    private void RaiseNavProperties()
    {
        OnPropertyChanged(nameof(IsNavPlay));
        OnPropertyChanged(nameof(IsNavKey));
        OnPropertyChanged(nameof(IsNavRegion));
        OnPropertyChanged(nameof(IsNavLook));
        OnPropertyChanged(nameof(IsNavPresets));
        OnPropertyChanged(nameof(IsNavTest));
        OnPropertyChanged(nameof(NavPageTitle));
        OnPropertyChanged(nameof(NavPageSubtitle));
    }

    private bool CanRunTest()
    {
        return !_isTestRunning && !string.IsNullOrWhiteSpace(_testInputText) && IsOfflineEngine && IsOfflineReady;
    }

    private static readonly string[] TestSamples =
    [
        "The knight stood at the edge of the cliff, staring into the abyss below. \"We have no choice,\" she said, gripping her sword tightly. \"If we don't cross now, the kingdom will fall before dawn.\"",
        "Welcome to the village of Eldergrove. The blacksmith can forge new weapons. Visit the inn to rest and save your progress. The merchant sells potions and scrolls.",
        "You have obtained the Crystal of Shadows. This ancient relic holds the power to reveal hidden paths. Use it near mysterious walls to uncover secret passages. Be warned — its power draws the attention of dark creatures.",
        "Long ago, the four kingdoms lived in harmony. But when the Dragon King awakened from his thousand-year slumber, war spread across the land. Now, only one hero remains who can unite the kingdoms and seal the dragon away forever. Your journey begins at the Temple of Dawn, where the Elder will grant you the first seal."
    ];

    private void UseTestSample(string? indexStr)
    {
        if (int.TryParse(indexStr, out var index) && index >= 0 && index < TestSamples.Length)
        {
            TestInputText = TestSamples[index];
        }
    }

    private async Task RunTranslationTestAsync()
    {
        if (!CanRunTest())
        {
            return;
        }

        IsTestRunning = true;
        TestTranslatedText = string.Empty;
        TestStatusMessage = "Translating...";
        TestChunks.Clear();

        try
        {
            var result = await new TranslationTestRunner(_offlineProvider).RunAsync(
                _testInputText, SourceLanguage, TargetLanguage, _lifetime.Token);
            if (_disposed) return;
            foreach (var chunk in result.Chunks) TestChunks.Add(chunk);
            TestTranslatedText = result.Text;
            TestStatusMessage = result.Status;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_disposed) TestStatusMessage = $"Translation failed: {ex.Message}";
            AppLog.Write("translation-test", ex);
        }
        finally
        {
            if (!_disposed) IsTestRunning = false;
        }
    }

    private bool CanDownloadModel()
    {
        return IsOfflineEngine && !IsDownloadingModel;
    }

    private async Task EnsureOfflineModelAsync(bool downloadIfMissing = false)
    {
        if (_disposed || IsDownloadingModel)
        {
            return;
        }

        var language = SourceLanguage;
        if (!downloadIfMissing && !_offlineProvider.IsPairReady(language))
        {
            IsOfflineReady = false;
            OfflineModelStatus = "Offline files are missing. Use Download before playback.";
            return;
        }

        IsDownloadingModel = true;
        var progress = new Progress<string>(message =>
        {
            if (_disposed || SourceLanguage != language) return;
            OfflineModelStatus = message;
            StatusMessage = message;
        });

        try
        {
            await _offlineProvider.EnsureReadyAsync(language, progress, _lifetime.Token);
            if (_disposed || SourceLanguage != language) return;
            IsOfflineReady = true;
            OfflineModelStatus = "Arabic files are on this PC. No API key needed.";
            if (!IsPlaying)
            {
                StatusMessage = SelectedRegion.IsEmpty
                    ? "Arabic files ready. Select the dialogue region with F9, then press F8."
                    : "Arabic files ready. F8 starts translation.";
            }

            OnPropertyChanged(nameof(CanTranslate));
            OnPropertyChanged(nameof(SessionBadgeText));
            TogglePlayCommand.RaiseCanExecuteChanged();
            await _offlineProvider.WarmAsync(language, cancellationToken: _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_disposed || SourceLanguage != language) return;
            IsOfflineReady = _offlineProvider.IsPairReady(SourceLanguage);
            OfflineModelStatus = ex.Message;
            StatusMessage = ex.Message;
        }
        finally
        {
            if (!_disposed)
            {
                IsDownloadingModel = false;
                if (SourceLanguage != language && IsOfflineEngine) _ = EnsureOfflineModelAsync();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        StopPlay();
        _pipeline.ResultAvailable -= OnPipelineResultAvailable;
        ResourceCleanup.Dispose(_pipeline, "pipeline-dispose");
        ResourceCleanup.Dispose(_pipeline.Diagnostics, "diagnostics-dispose");
        ResourceCleanup.Dispose(_playCts, "play-token-dispose");
        _playCts = null;
        ResourceCleanup.Dispose(_offlineProvider, "offline-dispose");
        ResourceCleanup.Dispose(_overlayService, "overlay-dispose");
        ResourceCleanup.Dispose(_runLock, "play-lock-dispose");
        _lifetime.Dispose();
    }
}
