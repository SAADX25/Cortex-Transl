using CortexTransl.App.Data;
using CortexTransl.App.Models;
using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Overlay;
using CortexTransl.App.Services.Profiles;
using CortexTransl.App.Services.Settings;
using CortexTransl.App.Services.Translation;
using CortexTransl.App.Utils;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

namespace CortexTransl.App.ViewModels;

public sealed class PlayViewModel : ObservableObject, IDisposable
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
    private double _autoTranslateIntervalMs = 320;
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

        SelectRegionCommand = new AsyncRelayCommand(_ => SelectRegionAsync());
        TogglePlayCommand = new AsyncRelayCommand(_ => TogglePlayAsync(), _ => CanStartOrStop());
        DownloadOfflineModelCommand = new AsyncRelayCommand(_ => EnsureOfflineModelAsync(), _ => CanDownloadModel());
        SaveProfileCommand = new AsyncRelayCommand(_ => SaveProfileAsync(), _ => CanSaveProfile());
        UsePresetCommand = new RelayCommand(parameter => UsePreset(parameter as GameProfile), parameter => parameter is GameProfile);
        DeletePresetCommand = new AsyncRelayCommand(
            parameter => DeletePresetAsync(parameter as GameProfile),
            parameter => parameter is GameProfile);
        RunTestCommand = new AsyncRelayCommand(_ => RunTranslationTestAsync(), _ => CanRunTest());
        UseTestSampleCommand = new RelayCommand(parameter => UseTestSample(parameter as string), _ => !_isTestRunning);
    }

    public event EventHandler? MinimizeRequested;

    public IReadOnlyList<OptionItem> SourceLanguageOptions { get; } =
    [
        new("en", "English"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("zh-Hans", "Chinese"),
        new("fr", "French"),
        new("de", "German"),
        new("es", "Spanish"),
        new("auto", "Auto")
    ];

    public IReadOnlyList<OptionItem> TranslationEngineOptions { get; } =
    [
        new("offline", "Offline Arabic files"),
        new("deepl", "DeepL API")
    ];

    public IReadOnlyList<OptionItem> TranslationModeOptions { get; } =
    [
        new("dialogue", "Story dialogue"),
        new("list", "Menus & icons")
    ];

    public IReadOnlyList<OptionItem> OverlayPlacementOptions { get; } =
    [
        new("cover", "Cover original text"),
        new("below", "Below dialogue box"),
        new("above", "Above dialogue box")
    ];

    public IReadOnlyList<OptionItem> OverlayTextSizeOptions { get; } =
    [
        new("small", "Small"),
        new("medium", "Medium"),
        new("large", "Large"),
        new("xlarge", "Extra large")
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
            if (SetProperty(ref _sourceLanguage, value))
            {
                _pipeline.Reset();
                _ = SaveSettingsAsync();
                if (IsOfflineEngine)
                {
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
                OnPropertyChanged(nameof(CanTranslate));
                OnPropertyChanged(nameof(SessionBadgeText));
                TogglePlayCommand.RaiseCanExecuteChanged();
                DownloadOfflineModelCommand.RaiseCanExecuteChanged();
                _ = SaveSettingsAsync();
                if (IsOfflineEngine)
                {
                    _ = EnsureOfflineModelAsync();
                }
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
            if (SetProperty(ref _overlayPlacement, value))
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
            var normalized = NormalizeTranslationMode(value);
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
            var normalized = NormalizeOverlayTextSize(value);
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

    public double OverlayFontScale => OverlayTextSize switch
    {
        "small" => 0.78,
        "large" => 1.24,
        "xlarge" => 1.52,
        _ => 1.0
    };

    public double OverlayPreviewFontSize => OverlayTextSize switch
    {
        "small" => 15,
        "large" => 23,
        "xlarge" => 28,
        _ => 19
    };

    public string OverlayTextSizeLabel => OverlayTextSizeOptions
        .FirstOrDefault(item => item.Id == OverlayTextSize)?.Name ?? "Medium";

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

    public bool SuppressAutoCapture { get; set; }

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
        _overlayPlacement = NormalizeOverlayPlacement(settings.OverlayPlacement);
        if (_overlayPlacement == "below")
        {
            _overlayPlacement = "cover";
        }
        _translationMode = NormalizeTranslationMode(settings.TranslationMode);
        _overlayBackgroundOpacity = settings.OverlayBackgroundOpacity <= 0 ? 0.88 : settings.OverlayBackgroundOpacity;
        _overlayTextSize = NormalizeOverlayTextSize(settings.OverlayTextSize);
        _overlayTextColor = OverlayTextColors.Normalize(settings.OverlayTextColor);
        _minimizeDuringPlay = settings.MinimizeDuringPlay;
        _autoTranslateIntervalMs = settings.AutoTranslateIntervalMs <= 0 || settings.AutoTranslateIntervalMs >= 500
            ? 320
            : settings.AutoTranslateIntervalMs;
        _useDeepLFreeApi = settings.UseDeepLFreeApi;
        _translationEngine = string.Equals(settings.TranslationEngine, "deepl", StringComparison.OrdinalIgnoreCase)
            ? "deepl"
            : "offline";
        _profileName = settings.ProfileName;
        _deepLApiKey = _appSettingsService.DecryptApiKey(settings.EncryptedDeepLApiKey);
        _translationProviderSettings.DeepLApiKey = _deepLApiKey;
        _translationProviderSettings.UseDeepLFreeApi = _useDeepLFreeApi;
        _translationProviderSettings.TranslationEngine = _translationEngine;
        IsOfflineReady = _offlineProvider.IsEnglishArabicReady;

        if (settings.RegionWidth > 0 && settings.RegionHeight > 0)
        {
            _selectedRegion = new CaptureRegion(settings.RegionX, settings.RegionY, settings.RegionWidth, settings.RegionHeight);
        }

        OnPropertyChanged(nameof(SourceLanguage));
        OnPropertyChanged(nameof(OverlayPlacement));
        OnPropertyChanged(nameof(TranslationMode));
        OnPropertyChanged(nameof(IsListMode));
        OnPropertyChanged(nameof(IsDialogueMode));
        OnPropertyChanged(nameof(RegionHint));
        OnPropertyChanged(nameof(RegionSectionTitle));
        OnPropertyChanged(nameof(SelectRegionButtonLabel));
        OnPropertyChanged(nameof(TranslationModeHint));
        OnPropertyChanged(nameof(OverlayBackgroundOpacity));
        OnPropertyChanged(nameof(OverlayOpacityPercent));
        OnPropertyChanged(nameof(OverlayTextSize));
        OnPropertyChanged(nameof(OverlayFontScale));
        OnPropertyChanged(nameof(OverlayPreviewFontSize));
        OnPropertyChanged(nameof(OverlayTextSizeLabel));
        OnPropertyChanged(nameof(OverlayTextColor));
        OnPropertyChanged(nameof(OverlayTextColorLabel));
        OnPropertyChanged(nameof(MinimizeDuringPlay));
        OnPropertyChanged(nameof(UseDeepLFreeApi));
        OnPropertyChanged(nameof(TranslationEngine));
        OnPropertyChanged(nameof(IsOfflineEngine));
        OnPropertyChanged(nameof(IsDeepLEngine));
        OnPropertyChanged(nameof(IsOfflineReady));
        OnPropertyChanged(nameof(CanTranslate));
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(DeepLApiKey));
        OnPropertyChanged(nameof(HasApiKey));
        OnPropertyChanged(nameof(ApiKeyStatusText));
        OnPropertyChanged(nameof(SelectedRegion));
        OnPropertyChanged(nameof(SelectedRegionDisplay));
        OnPropertyChanged(nameof(HasRegion));
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasNoProfiles));
        OnPropertyChanged(nameof(SessionBadgeText));
        SaveProfileCommand.RaiseCanExecuteChanged();
        TogglePlayCommand.RaiseCanExecuteChanged();

        SelectedNavPage = !CanTranslate ? "key" : !HasRegion ? "region" : "play";

        if (IsOfflineEngine)
        {
            StatusMessage = IsOfflineReady
                ? SelectedRegion.IsEmpty
                    ? "Select the dialogue region, then press F8."
                    : "Ready. F8 starts translation. F10 opens the menu."
                : "Downloading Arabic translation files...";
            _ = EnsureOfflineModelAsync();
        }
        else
        {
            StatusMessage = HasApiKey
                ? SelectedRegion.IsEmpty
                    ? "Select the dialogue region, then press F8."
                    : "Ready. F8 starts translation. F10 opens the menu."
                : "Paste your DeepL API key, then select the dialogue region.";
        }
    }

    public async Task HandleF8Async()
    {
        await TogglePlayAsync();
    }

    public async Task HandleF9Async()
    {
        if (_regionSelectionService.IsSelecting)
        {
            _regionSelectionService.Cancel();
            StatusMessage = "Region selection closed.";
            return;
        }

        await SelectRegionAsync();
    }

    private bool CanStartOrStop()
    {
        return IsPlaying || (CanTranslate && !SelectedRegion.IsEmpty && !_isBusy);
    }

    private async Task SelectRegionAsync()
    {
        var overlayWasVisible = _overlayService.IsVisible;
        if (overlayWasVisible)
        {
            _overlayService.Hide();
            await Task.Delay(40);
        }

        try
        {
            var region = await _regionSelectionService.SelectRegionAsync(
                IsListMode
                    ? "Drag around the menu or icon names — F9 or Esc to close"
                    : "Drag around the story dialogue — F9 or Esc to close");
            if (region is null || region.IsEmpty)
            {
                if (!_regionSelectionService.IsSelecting)
                {
                    StatusMessage = "Region selection closed.";
                }

                return;
            }

            SelectedRegion = ScreenCoordinates.ClampToVirtualScreen(region);
            if (SelectedRegion.IsEmpty)
            {
                StatusMessage = "The selected region is outside the screen. Select it again.";
                return;
            }
            _pipeline.Reset();
            if (IsPlaying)
            {
                PinOverlay();
            }

            await SaveSettingsAsync();
            StatusMessage = "Region selected. Press F8 to start translation.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            if (overlayWasVisible && IsPlaying)
            {
                PinOverlay();
            }
        }
    }

    private async Task TogglePlayAsync()
    {
        if (IsPlaying)
        {
            StopPlay();
            StatusMessage = "Translation hidden. F8 starts it again. F10 opens the program.";
            return;
        }

        if (!CanTranslate)
        {
            SelectedNavPage = "key";
            StatusMessage = IsOfflineEngine
                ? "Download the Arabic translation files first."
                : "Paste a DeepL API key first.";
            return;
        }

        if (SelectedRegion.IsEmpty)
        {
            SelectedNavPage = "region";
            StatusMessage = "Select the dialogue region first.";
            return;
        }

        IsPlaying = true;
        _playCts?.Dispose();
        _playCts = new CancellationTokenSource();
        StatusMessage = "Translation on. F8 stops it. F10 opens the program.";
        if (MinimizeDuringPlay)
        {
            MinimizeRequested?.Invoke(this, EventArgs.Empty);
        }

        _ = Task.Run(() => PlayLoopAsync(_playCts.Token));
        TogglePlayCommand.RaiseCanExecuteChanged();
        await Task.CompletedTask;
    }

    private void StopPlay()
    {
        try
        {
            _playCts?.Cancel();
        }
        catch
        {
        }

        IsPlaying = false;
        try
        {
            _overlayService.Hide();
        }
        catch
        {
        }
    }

    private async Task PlayLoopAsync(CancellationToken token)
    {
        try
        {
            var skipped = false;
            while (!token.IsCancellationRequested)
            {
                skipped = await RunTurnAsync(token);
                var delay = skipped ? 360 : 120;
                await Task.Delay(delay, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Write("play-loop", ex);
            RunOnUi(() =>
            {
                if (_disposed)
                {
                    return;
                }

                StatusMessage = "Translation paused. Press F8 to start again.";
                StopPlay();
            });
        }
    }

    private async Task<bool> RunTurnAsync(CancellationToken cancellationToken)
    {
        if (_disposed || SuppressAutoCapture)
        {
            return true;
        }

        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            return true;
        }

        _isBusy = true;
        RunOnUi(() => TogglePlayCommand.RaiseCanExecuteChanged());

        try
        {
            if (_disposed)
            {
                return true;
            }

            _translationProviderSettings.DeepLApiKey = DeepLApiKey;
            _translationProviderSettings.UseDeepLFreeApi = UseDeepLFreeApi;
            _translationProviderSettings.TranslationEngine = TranslationEngine;

            var listMode = IsListMode;
            var result = await _pipeline.RunAsync(
                SelectedRegion,
                SourceLanguage,
                TargetLanguage,
                listMode,
                cancellationToken);

            var original = result.OriginalText;
            var translated = result.TranslatedText;
            var skipped = result.Skipped;
            var status = result.Status;
            var blocks = result.Blocks;
            var playing = IsPlaying && !_disposed;

            RunOnUi(() =>
            {
                if (_disposed)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(original))
                {
                    OriginalText = original;
                }

                var textChanged = false;
                if (!string.IsNullOrWhiteSpace(translated) && translated != TranslatedText)
                {
                    TranslatedText = translated;
                    textChanged = true;
                }

                if (listMode && !skipped && blocks.Count > 0)
                {
                    _lastBlocks = blocks;
                    textChanged = true;
                }

                if (playing && (textChanged || !_overlayService.IsVisible))
                {
                    if (listMode)
                    {
                        if (_lastBlocks.Count > 0)
                        {
                            PinOverlay();
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(TranslatedText))
                    {
                        PinOverlay();
                    }
                }

                if (!skipped)
                {
                    StatusMessage = status;
                }
            });

            return skipped || string.IsNullOrWhiteSpace(translated);
        }
        finally
        {
            _isBusy = false;
            try
            {
                if (!_disposed)
                {
                    RunOnUi(() => TogglePlayCommand.RaiseCanExecuteChanged());
                    _runLock.Release();
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static void RunOnUi(Action action)
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.Invoke(action, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromMilliseconds(400));
        }
        catch
        {
        }
    }

    private OverlaySettings CreateOverlaySettings()
    {
        return new OverlaySettings(OverlayPlacement, OverlayBackgroundOpacity, TranslationMode, OverlayFontScale, OverlayTextColor);
    }

    private void PinOverlay()
    {
        if (SelectedRegion.IsEmpty)
        {
            return;
        }

        _overlayService.Pin(SelectedRegion, CreateOverlaySettings());
        if (IsListMode)
        {
            if (_lastBlocks.Count > 0)
            {
                _overlayService.SetBlocks(_lastBlocks);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(TranslatedText))
        {
            _overlayService.SetText(TranslatedText);
        }
    }

    private void RefreshOverlay()
    {
        if (IsPlaying)
        {
            PinOverlay();
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
            TranslationProvider = IsOfflineEngine ? "offline" : "deepl"
        };
    }

    private void ApplyPreset(GameProfile profile)
    {
        _selectedRegion = profile.Region;
        _sourceLanguage = string.IsNullOrWhiteSpace(profile.SourceLanguage) ? "en" : profile.SourceLanguage;
        _translationMode = NormalizeTranslationMode(profile.TranslationMode);
        _overlayPlacement = NormalizeOverlayPlacement(profile.OverlayPlacement);
        _overlayBackgroundOpacity = profile.OverlayBackgroundOpacity <= 0 ? 0.88 : Math.Clamp(profile.OverlayBackgroundOpacity, 0.6, 0.95);
        _overlayTextSize = NormalizeOverlayTextSize(profile.OverlayTextSize);
        _overlayTextColor = OverlayTextColors.Normalize(profile.OverlayTextColor);
        _profileName = profile.Name;
        _pipeline.Reset();
        _lastBlocks = [];

        OnPropertyChanged(nameof(SelectedRegion));
        OnPropertyChanged(nameof(SelectedRegionDisplay));
        OnPropertyChanged(nameof(HasRegion));
        OnPropertyChanged(nameof(SessionBadgeText));
        OnPropertyChanged(nameof(SourceLanguage));
        OnPropertyChanged(nameof(TranslationMode));
        OnPropertyChanged(nameof(IsListMode));
        OnPropertyChanged(nameof(IsDialogueMode));
        OnPropertyChanged(nameof(RegionHint));
        OnPropertyChanged(nameof(RegionSectionTitle));
        OnPropertyChanged(nameof(SelectRegionButtonLabel));
        OnPropertyChanged(nameof(TranslationModeHint));
        OnPropertyChanged(nameof(OverlayPlacement));
        OnPropertyChanged(nameof(OverlayBackgroundOpacity));
        OnPropertyChanged(nameof(OverlayOpacityPercent));
        OnPropertyChanged(nameof(OverlayTextSize));
        OnPropertyChanged(nameof(OverlayFontScale));
        OnPropertyChanged(nameof(OverlayPreviewFontSize));
        OnPropertyChanged(nameof(OverlayTextSizeLabel));
        OnPropertyChanged(nameof(OverlayTextColor));
        OnPropertyChanged(nameof(OverlayTextColorLabel));
        OnPropertyChanged(nameof(ProfileName));
        SaveProfileCommand.RaiseCanExecuteChanged();
        TogglePlayCommand.RaiseCanExecuteChanged();

        SelectedProfile = profile;
        _ = RefreshProfilesAsync();
        if (IsPlaying)
        {
            PinOverlay();
        }

        _ = SaveSettingsAsync();
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
            Theme = "Dark",
            SourceLanguage = _sourceLanguage,
            OverlayPlacement = _overlayPlacement,
            OverlayBackgroundOpacity = _overlayBackgroundOpacity,
            OverlayTextSize = _overlayTextSize,
            OverlayTextColor = _overlayTextColor,
            MinimizeDuringPlay = _minimizeDuringPlay,
            AutoTranslateIntervalMs = _autoTranslateIntervalMs,
            RegionX = _selectedRegion.X,
            RegionY = _selectedRegion.Y,
            RegionWidth = _selectedRegion.Width,
            RegionHeight = _selectedRegion.Height,
            ProfileName = _profileName,
            TranslationMode = _translationMode
        };

        await _appSettingsService.SaveAsync(settings);
    }

    private static string NormalizeOverlayPlacement(string? placement)
    {
        return placement?.Trim().ToLowerInvariant() switch
        {
            "below" => "below",
            "above" => "above",
            _ => "cover"
        };
    }

    private static string NormalizeOverlayTextSize(string? size)
    {
        return size?.Trim().ToLowerInvariant() switch
        {
            "small" => "small",
            "large" => "large",
            "xlarge" => "xlarge",
            _ => "medium"
        };
    }

    private static string NormalizeTranslationMode(string? mode)
    {
        return mode?.Trim().ToLowerInvariant() == "list" ? "list" : "dialogue";
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
        return !_isTestRunning && !string.IsNullOrWhiteSpace(_testInputText) && CanTranslate;
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
        if (_isTestRunning || string.IsNullOrWhiteSpace(_testInputText))
        {
            return;
        }

        IsTestRunning = true;
        TestTranslatedText = string.Empty;
        TestStatusMessage = "Translating...";
        TestChunks.Clear();

        var sw = Stopwatch.StartNew();
        try
        {
            var sourceText = _testInputText.Trim();
            var chunks = TranslationChunker.Split(sourceText);
            if (!_translationProviderSettings.UseOfflineEngine)
            {
                TestStatusMessage = "Switch to offline engine first. Test mode uses the offline Arabic files.";
                return;
            }

            IReadOnlyList<string> translations;
            try
            {
                translations = await _offlineProvider.TranslateManyAsync(
                    chunks, SourceLanguage, TargetLanguage);
            }
            catch (TranslationProviderException ex)
            {
                TestStatusMessage = $"Translation failed: {ex.Message}";
                return;
            }

            var emptyCount = 0;
            var totalChunks = chunks.Count;

            for (var i = 0; i < totalChunks; i++)
            {
                var translated = i < translations.Count ? translations[i] : string.Empty;
                if (string.IsNullOrWhiteSpace(translated))
                {
                    emptyCount++;
                }

                TestChunks.Add(new TestChunkResult
                {
                    Index = i + 1,
                    OriginalText = chunks[i],
                    TranslatedText = translated
                });
            }

            var fullTranslation = TranslationChunker.Join(translations);
            TestTranslatedText = fullTranslation;
            sw.Stop();

            var sourceLen = sourceText.Length;
            var translatedLen = fullTranslation.Length;
            var ratio = sourceLen > 0 ? (double)translatedLen / sourceLen : 0;

            var issues = new List<string>();
            if (string.IsNullOrWhiteSpace(fullTranslation))
            {
                issues.Add("Translation is completely empty");
            }
            else if (ratio < 0.3)
            {
                issues.Add($"Translation is very short ({ratio:P0} of original length)");
            }

            if (emptyCount > 0)
            {
                issues.Add($"{emptyCount}/{totalChunks} chunks returned empty");
            }

            if (issues.Count == 0)
            {
                TestStatusMessage = $"✓ All {totalChunks} chunks translated successfully in {sw.ElapsedMilliseconds}ms. " +
                                    $"Source: {sourceLen} chars → Arabic: {translatedLen} chars ({ratio:P0}).";
            }
            else
            {
                TestStatusMessage = $"⚠ Issues found ({sw.ElapsedMilliseconds}ms): {string.Join(". ", issues)}.";
            }
        }
        catch (Exception ex)
        {
            TestStatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsTestRunning = false;
        }
    }

    private bool CanDownloadModel()
    {
        return IsOfflineEngine && !IsDownloadingModel;
    }

    private async Task EnsureOfflineModelAsync()
    {
        if (IsDownloadingModel)
        {
            return;
        }

        IsDownloadingModel = true;
        var progress = new Progress<string>(message =>
        {
            OfflineModelStatus = message;
            StatusMessage = message;
        });

        try
        {
            await _offlineProvider.EnsureReadyAsync(SourceLanguage, progress);
            IsOfflineReady = true;
            OfflineModelStatus = "Arabic files are on this PC. No API key needed.";
            if (!IsPlaying)
            {
                StatusMessage = SelectedRegion.IsEmpty
                    ? "Arabic files ready. Select the dialogue region, then press F8."
                    : "Arabic files ready. F8 starts translation.";
            }

            OnPropertyChanged(nameof(CanTranslate));
            OnPropertyChanged(nameof(SessionBadgeText));
            TogglePlayCommand.RaiseCanExecuteChanged();
            _ = Task.Run(async () =>
            {
                try
                {
                    await _offlineProvider.TranslateAsync("Hello.", "en", "ar");
                }
                catch
                {
                }
            });
        }
        catch (Exception ex)
        {
            IsOfflineReady = _offlineProvider.IsEnglishArabicReady;
            OfflineModelStatus = ex.Message;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsDownloadingModel = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopPlay();
        try
        {
            _playCts?.Dispose();
        }
        catch
        {
        }

        _playCts = null;
        try
        {
            _offlineProvider.Dispose();
        }
        catch
        {
        }

        try
        {
            _overlayService.Dispose();
        }
        catch
        {
        }

        try
        {
            _runLock.Dispose();
        }
        catch
        {
        }
    }
}
