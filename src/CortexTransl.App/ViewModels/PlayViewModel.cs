using CortexTransl.App.Data;
using CortexTransl.App.Models;
using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Overlay;
using CortexTransl.App.Services.Profiles;
using CortexTransl.App.Services.Settings;
using CortexTransl.App.Services.Translation;
using CortexTransl.App.Utils;
using System.Collections.ObjectModel;

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
    private readonly AppSettingsService _appSettingsService;
    private readonly ThemeService _themeService;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    private CaptureRegion _selectedRegion = CaptureRegion.Empty;
    private string _sourceLanguage = "en";
    private string _deepLApiKey = string.Empty;
    private bool _useDeepLFreeApi = true;
    private string _overlayPlacement = "cover";
    private string _translationMode = "dialogue";
    private double _overlayBackgroundOpacity = 0.88;
    private bool _minimizeDuringPlay = true;
    private double _autoTranslateIntervalMs = 500;
    private string _profileName = string.Empty;
    private GameProfile? _selectedProfile;
    private string _originalText = string.Empty;
    private string _translatedText = string.Empty;
    private string _statusMessage = "Paste your DeepL API key, then select the dialogue region.";
    private IReadOnlyList<TranslatedBlock> _lastBlocks = [];
    private bool _isPlaying;
    private bool _isBusy;
    private CancellationTokenSource? _playCts;

    public PlayViewModel(
        DatabaseMigrator databaseMigrator,
        IRegionSelectionService regionSelectionService,
        TranslationPipeline pipeline,
        IOverlayService overlayService,
        IGameProfileRepository profileRepository,
        TranslationProviderSettings translationProviderSettings,
        AppSettingsService appSettingsService,
        ThemeService themeService)
    {
        _databaseMigrator = databaseMigrator;
        _regionSelectionService = regionSelectionService;
        _pipeline = pipeline;
        _overlayService = overlayService;
        _profileRepository = profileRepository;
        _translationProviderSettings = translationProviderSettings;
        _appSettingsService = appSettingsService;
        _themeService = themeService;

        SelectRegionCommand = new AsyncRelayCommand(_ => SelectRegionAsync());
        TogglePlayCommand = new AsyncRelayCommand(_ => TogglePlayAsync(), _ => CanStartOrStop());
        SaveProfileCommand = new AsyncRelayCommand(_ => SaveProfileAsync(), _ => !SelectedRegion.IsEmpty);
        LoadProfileCommand = new RelayCommand(_ => LoadSelectedProfile(), _ => SelectedProfile is not null);
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

    public IReadOnlyList<OptionItem> TranslationModeOptions { get; } =
    [
        new("dialogue", "Dialogue box"),
        new("list", "List / icons")
    ];

    public IReadOnlyList<OptionItem> OverlayPlacementOptions { get; } =
    [
        new("cover", "Cover original text"),
        new("below", "Below dialogue box"),
        new("above", "Above dialogue box")
    ];

    public ObservableCollection<GameProfile> Profiles { get; } = [];

    public AsyncRelayCommand SelectRegionCommand { get; }

    public AsyncRelayCommand TogglePlayCommand { get; }

    public AsyncRelayCommand SaveProfileCommand { get; }

    public RelayCommand LoadProfileCommand { get; }

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
        ? "Drag around the icon names or menu list. Each label is translated in place."
        : "Drag a rectangle around the dialogue box only. Use borderless windowed mode in the game.";

    public string RegionSectionTitle => IsListMode ? "List region" : "Dialogue region";

    public string SelectRegionButtonLabel => IsListMode ? "Select list region" : "Select dialogue region";

    public bool HasRegion => !SelectedRegion.IsEmpty;

    public bool IsDialogueMode => !IsListMode;

    public string OverlayOpacityPercent => $"{Math.Clamp((int)Math.Round(OverlayBackgroundOpacity * 100), 60, 95)}%";

    public string SessionBadgeText =>
        IsPlaying
            ? "Live"
            : HasApiKey && HasRegion
                ? "Ready"
                : HasApiKey
                    ? "Select region"
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
                LoadProfileCommand.RaiseCanExecuteChanged();
            }
        }
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
        _minimizeDuringPlay = settings.MinimizeDuringPlay;
        _autoTranslateIntervalMs = settings.AutoTranslateIntervalMs <= 0 || settings.AutoTranslateIntervalMs >= 900
            ? 500
            : settings.AutoTranslateIntervalMs;
        _useDeepLFreeApi = settings.UseDeepLFreeApi;
        _profileName = settings.ProfileName;
        _deepLApiKey = _appSettingsService.DecryptApiKey(settings.EncryptedDeepLApiKey);
        _translationProviderSettings.DeepLApiKey = _deepLApiKey;
        _translationProviderSettings.UseDeepLFreeApi = _useDeepLFreeApi;

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
        OnPropertyChanged(nameof(OverlayBackgroundOpacity));
        OnPropertyChanged(nameof(OverlayOpacityPercent));
        OnPropertyChanged(nameof(MinimizeDuringPlay));
        OnPropertyChanged(nameof(UseDeepLFreeApi));
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(DeepLApiKey));
        OnPropertyChanged(nameof(HasApiKey));
        OnPropertyChanged(nameof(ApiKeyStatusText));
        OnPropertyChanged(nameof(SelectedRegion));
        OnPropertyChanged(nameof(SelectedRegionDisplay));
        OnPropertyChanged(nameof(HasRegion));
        OnPropertyChanged(nameof(SessionBadgeText));
        SaveProfileCommand.RaiseCanExecuteChanged();
        TogglePlayCommand.RaiseCanExecuteChanged();

        StatusMessage = HasApiKey
            ? SelectedRegion.IsEmpty
                ? "Select the dialogue region, then press F8."
                : "Ready. F8 starts translation. F10 opens the menu."
            : "Paste your DeepL API key, then select the dialogue region.";
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
        return IsPlaying || (HasApiKey && !SelectedRegion.IsEmpty && !_isBusy);
    }

    private async Task SelectRegionAsync()
    {
        try
        {
            var region = await _regionSelectionService.SelectRegionAsync();
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
            StatusMessage = "Region selected. F8 starts translation. F10 opens the menu.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task TogglePlayAsync()
    {
        if (IsPlaying)
        {
            StopPlay();
            StatusMessage = "Translation hidden. F8 starts it again. F10 opens the menu.";
            return;
        }

        if (!HasApiKey)
        {
            StatusMessage = "Paste a DeepL API key first.";
            return;
        }

        if (SelectedRegion.IsEmpty)
        {
            StatusMessage = "Select the dialogue region first.";
            return;
        }

        IsPlaying = true;
        _playCts = new CancellationTokenSource();
        PinOverlay();
        StatusMessage = "Translation on. Press F8 to hide it. F10 opens the menu.";
        if (MinimizeDuringPlay)
        {
            MinimizeRequested?.Invoke(this, EventArgs.Empty);
        }

        _ = PlayLoopAsync(_playCts.Token);
        TogglePlayCommand.RaiseCanExecuteChanged();
        await Task.CompletedTask;
    }

    private void StopPlay()
    {
        _playCts?.Cancel();
        _playCts?.Dispose();
        _playCts = null;
        IsPlaying = false;
        _overlayService.Hide();
    }

    private async Task PlayLoopAsync(CancellationToken token)
    {
        try
        {
            await RunTurnAsync(token);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Clamp(_autoTranslateIntervalMs, 350, 800)));
            while (await timer.WaitForNextTickAsync(token))
            {
                await RunTurnAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            StopPlay();
        }
    }

    private async Task RunTurnAsync(CancellationToken cancellationToken)
    {
        if (SuppressAutoCapture)
        {
            return;
        }

        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        _isBusy = true;
        TogglePlayCommand.RaiseCanExecuteChanged();

        try
        {
            if (!_overlayService.IsVisible)
            {
                PinOverlay();
            }

            _translationProviderSettings.DeepLApiKey = DeepLApiKey;
            _translationProviderSettings.UseDeepLFreeApi = UseDeepLFreeApi;

            var result = await _pipeline.RunAsync(
                SelectedRegion,
                SourceLanguage,
                TargetLanguage,
                IsListMode,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(result.OriginalText))
            {
                OriginalText = result.OriginalText;
            }

            if (IsListMode)
            {
                if (result.Blocks.Count > 0)
                {
                    _lastBlocks = result.Blocks;
                    TranslatedText = result.TranslatedText;
                    _overlayService.SetBlocks(_lastBlocks);
                }
            }
            else if (!string.IsNullOrWhiteSpace(result.TranslatedText))
            {
                TranslatedText = result.TranslatedText;
                _overlayService.SetText(TranslatedText);
            }

            if (!result.Skipped)
            {
                StatusMessage = result.Status;
            }
        }
        finally
        {
            _isBusy = false;
            TogglePlayCommand.RaiseCanExecuteChanged();
            _runLock.Release();
        }
    }

    private OverlaySettings CreateOverlaySettings()
    {
        return new OverlaySettings(OverlayPlacement, OverlayBackgroundOpacity, TranslationMode);
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
        }
        else if (!string.IsNullOrWhiteSpace(TranslatedText))
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

    private async Task SaveProfileAsync()
    {
        try
        {
            var name = string.IsNullOrWhiteSpace(ProfileName)
                ? $"Game {DateTime.Now:yyyy-MM-dd HH:mm}"
                : ProfileName.Trim();

            var profile = new GameProfile
            {
                Name = name,
                Region = SelectedRegion,
                SourceLanguage = SourceLanguage,
                TargetLanguage = TargetLanguage,
                OcrEngine = "windows",
                TranslationProvider = "deepl"
            };

            await _profileRepository.SaveAsync(profile);
            ProfileName = name;
            await RefreshProfilesAsync();
            SelectedProfile = Profiles.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            await SaveSettingsAsync();
            StatusMessage = $"Saved \"{name}\".";
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
        ProfileName = SelectedProfile.Name;
        _pipeline.Reset();
        if (IsPlaying)
        {
            PinOverlay();
        }

        _ = SaveSettingsAsync();
        StatusMessage = $"Loaded \"{SelectedProfile.Name}\".";
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
            SelectedProfile = Profiles.FirstOrDefault(item => item.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private async Task SaveSettingsAsync()
    {
        var settings = new AppSettings
        {
            EncryptedDeepLApiKey = _appSettingsService.EncryptApiKey(_deepLApiKey),
            UseDeepLFreeApi = _useDeepLFreeApi,
            Theme = "Dark",
            SourceLanguage = _sourceLanguage,
            OverlayPlacement = _overlayPlacement,
            OverlayBackgroundOpacity = _overlayBackgroundOpacity,
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

    private static string NormalizeTranslationMode(string? mode)
    {
        return mode?.Trim().ToLowerInvariant() == "list" ? "list" : "dialogue";
    }

    public void Dispose()
    {
        StopPlay();
        _overlayService.Dispose();
        _runLock.Dispose();
    }
}
