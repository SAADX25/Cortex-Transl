using CortexTransl.App.Data;
using CortexTransl.App.Models;
using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Overlay;
using CortexTransl.App.Services.Profiles;
using CortexTransl.App.Services.Translation;
using CortexTransl.App.Services.Settings;
using CortexTransl.App.Utils;
using System.Collections.ObjectModel;
using System.Threading;

namespace CortexTransl.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly DatabaseMigrator _databaseMigrator;
    private readonly IRegionSelectionService _regionSelectionService;
    private readonly CaptureTranslatePipeline _pipeline;
    private readonly IOverlayService _overlayService;
    private readonly IGameProfileRepository _profileRepository;
    private readonly TranslationProviderSettings _translationProviderSettings;
    private readonly AppSettingsService _appSettingsService;
    private readonly ThemeService _themeService;
    private readonly TimingLogger _timingLogger;
    private readonly SemaphoreSlim _translationLock = new(1, 1);

    private CaptureRegion _selectedRegion = CaptureRegion.Empty;
    private string _sourceLanguage = "en";
    private string _targetLanguage = "ar";
    private string _ocrEngine = "windows";
    private string _translationProvider = "placeholder";
    private string _selectedTheme = "Light";
    private double _overlayFontSize = 32;
    private double _overlayOpacity = 1;
    private double _overlayBackgroundOpacity = 0.86;
    private double _overlayMaxWidth = 920;
    private string _overlayPositionPreset = "custom";
    private string _overlayRenderMode = "transparent";
    private bool _overlayClickThrough = true;
    private string _originalText = string.Empty;
    private string _translatedText = string.Empty;
    private string _statusMessage = "Ready. Select a dialogue region to begin.";
    private string _profileName = string.Empty;
    private string _deepLApiKey = string.Empty;
    private bool _useDeepLFreeApi = true;
    private GameProfile? _selectedProfile;
    private bool _isAutoTranslateEnabled;
    private double _autoTranslateIntervalMs = 700;
    private CancellationTokenSource? _autoTranslateCts;
    private string _autoTranslateStatusText = "Auto Translate: Stopped";
    private bool _isFocusMode = false;

    public MainViewModel(
        DatabaseMigrator databaseMigrator,
        IRegionSelectionService regionSelectionService,
        CaptureTranslatePipeline pipeline,
        IOverlayService overlayService,
        IGameProfileRepository profileRepository,
        TranslationProviderSettings translationProviderSettings,
        AppSettingsService appSettingsService,
        ThemeService themeService,
        TimingLogger timingLogger)
    {
        _databaseMigrator = databaseMigrator;
        _regionSelectionService = regionSelectionService;
        _pipeline = pipeline;
        _overlayService = overlayService;
        _profileRepository = profileRepository;
        _translationProviderSettings = translationProviderSettings;
        _appSettingsService = appSettingsService;
        _themeService = themeService;
        _timingLogger = timingLogger;

        SelectRegionCommand = new AsyncRelayCommand(_ => SelectRegionAsync());
        CaptureAndTranslateCommand = new AsyncRelayCommand(_ => CaptureAndTranslateAsync());
        SaveProfileCommand = new AsyncRelayCommand(_ => SaveProfileAsync(), _ => !SelectedRegion.IsEmpty);
        LoadSelectedProfileCommand = new RelayCommand(_ => LoadSelectedProfile(), _ => SelectedProfile is not null);
        ToggleAutoTranslateCommand = new RelayCommand(_ => ToggleAutoTranslate());
        ToggleFocusModeCommand = new RelayCommand(_ => ToggleFocusMode());
        ShowOverlayCommand = new AsyncRelayCommand(_ => ShowOverlayAsync());
        HideOverlayCommand = new RelayCommand(_ => HideOverlay());
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

    public IReadOnlyList<OptionItem> ThemeOptions { get; } =
    [
        new("Light", "Light"),
        new("Dark", "Dark"),
        new("System", "System")
    ];

    public IReadOnlyList<OptionItem> OverlayRenderModeOptions { get; } =
    [
        new("transparent", "Transparent / Fancy"),
        new("recording-safe", "Recording Safe")
    ];

    public IReadOnlyList<OptionItem> OverlayPositionOptions { get; } =
    [
        new("custom", "Custom"),
        new("bottom-center", "Bottom Center"),
        new("top-center", "Top Center")
    ];

    public ObservableCollection<GameProfile> Profiles { get; } = [];

    public ObservableCollection<TimingEntry> LastTimings { get; } = [];

    public ObservableCollection<DebugMetric> DebugMetrics { get; } = [];

    public AsyncRelayCommand SelectRegionCommand { get; }

    public AsyncRelayCommand CaptureAndTranslateCommand { get; }

    public AsyncRelayCommand SaveProfileCommand { get; }

    public RelayCommand LoadSelectedProfileCommand { get; }

    public RelayCommand ToggleAutoTranslateCommand { get; }

    public RelayCommand ToggleFocusModeCommand { get; }

    public AsyncRelayCommand ShowOverlayCommand { get; }

    public RelayCommand HideOverlayCommand { get; }

    public bool IsFocusMode
    {
        get => _isFocusMode;
        set => SetProperty(ref _isFocusMode, value);
    }

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
        set
        {
            if (SetProperty(ref _sourceLanguage, value))
            {
                _pipeline.ResetState();
            }
        }
    }

    public string TargetLanguage
    {
        get => _targetLanguage;
        set
        {
            if (SetProperty(ref _targetLanguage, value))
            {
                _pipeline.ResetState();
            }
        }
    }

    public string OcrEngine
    {
        get => _ocrEngine;
        set
        {
            if (SetProperty(ref _ocrEngine, value))
            {
                _pipeline.ResetState();
            }
        }
    }

    public string TranslationProvider
    {
        get => _translationProvider;
        set
        {
            if (SetProperty(ref _translationProvider, value))
            {
                _pipeline.ResetState();
                ResetDebugMetrics();
                OnPropertyChanged(nameof(ProviderHint));
                _ = SaveSettingsAsync();
            }
        }
    }

    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            var normalizedTheme = ThemeService.NormalizeTheme(value);
            if (SetProperty(ref _selectedTheme, normalizedTheme))
            {
                _themeService.ApplyTheme(normalizedTheme);
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
                ResetDebugMetrics();
                OnPropertyChanged(nameof(ProviderHint));
                OnPropertyChanged(nameof(ApiKeyStatusText));
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
                ResetDebugMetrics();
                _ = SaveSettingsAsync();
            }
        }
    }

    public bool IsAutoTranslateEnabled
    {
        get => _isAutoTranslateEnabled;
        private set
        {
            if (SetProperty(ref _isAutoTranslateEnabled, value))
            {
                _ = SaveSettingsAsync();
                OnPropertyChanged(nameof(AutoTranslateButtonLabel));
                UpdateAutoTranslateStatus();
            }
        }
    }

    public string AutoTranslateButtonLabel => IsAutoTranslateEnabled ? "Stop Auto Translate" : "Start Auto Translate";

    public string AutoTranslateStatusText
    {
        get => _autoTranslateStatusText;
        private set => SetProperty(ref _autoTranslateStatusText, value);
    }

    /// <summary>Hint shown below the provider combobox. Shows API key status for DeepL.</summary>
    public string ProviderHint
    {
        get
        {
            if (TranslationProvider.Equals("placeholder", StringComparison.OrdinalIgnoreCase))
                return "Testing mode only. No real translation.";
            if (TranslationProvider.Equals("deepl", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(DeepLApiKey) ? "API key missing" : "API key ready";
            return string.Empty;
        }
    }

    public string ApiKeyStatusText => string.IsNullOrWhiteSpace(DeepLApiKey) ? "No key entered" : "Key saved (encrypted)";

    public double AutoTranslateIntervalMs
    {
        get => _autoTranslateIntervalMs;
        set
        {
            if (SetProperty(ref _autoTranslateIntervalMs, value))
            {
                _ = SaveSettingsAsync();
            }
        }
    }

    public double OverlayFontSize
    {
        get => _overlayFontSize;
        set
        {
            if (SetProperty(ref _overlayFontSize, value))
            {
                _ = SaveSettingsAsync();
            }
        }
    }

    public double OverlayOpacity
    {
        get => _overlayOpacity;
        set
        {
            if (SetProperty(ref _overlayOpacity, value))
            {
                _ = SaveSettingsAsync();
            }
        }
    }

    public double OverlayBackgroundOpacity
    {
        get => _overlayBackgroundOpacity;
        set
        {
            if (SetProperty(ref _overlayBackgroundOpacity, value))
            {
                _ = SaveSettingsAsync();
            }
        }
    }

    public double OverlayMaxWidth
    {
        get => _overlayMaxWidth;
        set
        {
            if (SetProperty(ref _overlayMaxWidth, value))
            {
                _ = SaveSettingsAsync();
            }
        }
    }

    public string OverlayPositionPreset
    {
        get => _overlayPositionPreset;
        set
        {
            if (SetProperty(ref _overlayPositionPreset, value))
            {
                _ = SaveSettingsAsync();
            }
        }
    }

    public string OverlayRenderMode
    {
        get => _overlayRenderMode;
        set
        {
            if (SetProperty(ref _overlayRenderMode, value))
            {
                OnPropertyChanged(nameof(RecordingCompatibilityMode));
                _ = SaveSettingsAsync();
            }
        }
    }

    public bool RecordingCompatibilityMode
    {
        get => OverlayRenderMode.Equals("recording-safe", StringComparison.OrdinalIgnoreCase);
        set
        {
            var nextMode = value ? "recording-safe" : "transparent";
            if (!OverlayRenderMode.Equals(nextMode, StringComparison.OrdinalIgnoreCase))
            {
                OverlayRenderMode = nextMode;
            }
        }
    }

    public bool OverlayClickThrough
    {
        get => _overlayClickThrough;
        set
        {
            if (SetProperty(ref _overlayClickThrough, value))
            {
                _ = SaveSettingsAsync();
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

        var appSettings = await _appSettingsService.LoadAsync(cancellationToken);
        _translationProvider = appSettings.Provider;
        _selectedTheme = ThemeService.NormalizeTheme(appSettings.Theme);
        _themeService.ApplyTheme(_selectedTheme);
        _useDeepLFreeApi = appSettings.UseDeepLFreeApi;
        _isAutoTranslateEnabled = appSettings.AutoTranslateEnabled;
        _autoTranslateIntervalMs = appSettings.AutoTranslateIntervalMs;
        _overlayFontSize = appSettings.OverlayFontSize;
        _overlayOpacity = appSettings.OverlayOpacity;
        _overlayBackgroundOpacity = appSettings.OverlayBackgroundOpacity;
        _overlayMaxWidth = appSettings.OverlayMaxWidth;
        _overlayPositionPreset = appSettings.OverlayPositionPreset;
        _overlayRenderMode = appSettings.OverlayRenderMode;
        _overlayClickThrough = appSettings.OverlayClickThrough;

        var decryptedKey = _appSettingsService.DecryptApiKey(appSettings.EncryptedDeepLApiKey);
        _deepLApiKey = decryptedKey;
        _translationProviderSettings.DeepLApiKey = decryptedKey;
        _translationProviderSettings.UseDeepLFreeApi = _useDeepLFreeApi;

        OnPropertyChanged(nameof(TranslationProvider));
        OnPropertyChanged(nameof(SelectedTheme));
        OnPropertyChanged(nameof(UseDeepLFreeApi));
        OnPropertyChanged(nameof(DeepLApiKey));
        OnPropertyChanged(nameof(IsAutoTranslateEnabled));
        OnPropertyChanged(nameof(AutoTranslateIntervalMs));
        OnPropertyChanged(nameof(OverlayFontSize));
        OnPropertyChanged(nameof(OverlayOpacity));
        OnPropertyChanged(nameof(OverlayBackgroundOpacity));
        OnPropertyChanged(nameof(OverlayMaxWidth));
        OnPropertyChanged(nameof(OverlayPositionPreset));
        OnPropertyChanged(nameof(OverlayRenderMode));
        OnPropertyChanged(nameof(RecordingCompatibilityMode));
        OnPropertyChanged(nameof(OverlayClickThrough));
        OnPropertyChanged(nameof(ProviderHint));
        OnPropertyChanged(nameof(ApiKeyStatusText));
        OnPropertyChanged(nameof(AutoTranslateButtonLabel));

        ResetDebugMetrics();
        UpdateAutoTranslateStatus();

        if (_isAutoTranslateEnabled)
        {
            StartAutoTranslate();
        }
    }

    private async Task SaveSettingsAsync()
    {
        var settings = new AppSettings
        {
            Provider = _translationProvider,
            EncryptedDeepLApiKey = _appSettingsService.EncryptApiKey(_deepLApiKey),
            UseDeepLFreeApi = _useDeepLFreeApi,
            Theme = _selectedTheme,
            AutoTranslateEnabled = _isAutoTranslateEnabled,
            AutoTranslateIntervalMs = _autoTranslateIntervalMs,
            OverlayFontSize = _overlayFontSize,
            OverlayOpacity = _overlayOpacity,
            OverlayBackgroundOpacity = _overlayBackgroundOpacity,
            OverlayMaxWidth = _overlayMaxWidth,
            OverlayPositionPreset = _overlayPositionPreset,
            OverlayRenderMode = _overlayRenderMode,
            OverlayClickThrough = _overlayClickThrough
        };
        await _appSettingsService.SaveAsync(settings);
    }

    private void ToggleAutoTranslate()
    {
        if (IsAutoTranslateEnabled)
        {
            IsAutoTranslateEnabled = false;
            StopAutoTranslate();
        }
        else
        {
            // Validate before starting
            if (SelectedRegion.IsEmpty)
            {
                StatusMessage = "Select a dialogue region before starting Auto Translate.";
                return;
            }
            if (TranslationProvider.Equals("deepl", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(DeepLApiKey))
            {
                StatusMessage = "DeepL API key is required before starting Auto Translate.";
                return;
            }
            IsAutoTranslateEnabled = true;
            StartAutoTranslate();
        }
    }

    private void ToggleFocusMode()
    {
        IsFocusMode = !IsFocusMode;
    }

    private async Task ShowOverlayAsync()
    {
        var textToShow = string.IsNullOrWhiteSpace(TranslatedText) 
            ? "Cortex Transl Overlay\n(Translation will appear here)" 
            : TranslatedText;

        await _overlayService.ShowTextAsync(textToShow, SelectedRegion, CreateOverlaySettings());
        StatusMessage = "Overlay preview shown.";
    }

    private void HideOverlay()
    {
        _overlayService.Hide();
        StatusMessage = "Overlay hidden.";
    }

    private OverlaySettings CreateOverlaySettings()
    {
        return new OverlaySettings(
            OverlayFontSize,
            OverlayOpacity,
            OverlayBackgroundOpacity,
            OverlayMaxWidth,
            OverlayPositionPreset,
            OverlayRenderMode,
            OverlayClickThrough);
    }

    private void StartAutoTranslate()
    {
        StopAutoTranslate();
        _autoTranslateCts = new CancellationTokenSource();
        _ = AutoTranslateLoopAsync(_autoTranslateCts.Token);
    }

    private void StopAutoTranslate()
    {
        _autoTranslateCts?.Cancel();
        _autoTranslateCts?.Dispose();
        _autoTranslateCts = null;
    }

    private async Task AutoTranslateLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var interval = TimeSpan.FromMilliseconds(Math.Clamp(AutoTranslateIntervalMs, 300, 3000));
                using var periodicTimer = new PeriodicTimer(interval);

                await periodicTimer.WaitForNextTickAsync(token);
                if (token.IsCancellationRequested) break;

                if (!SelectedRegion.IsEmpty)
                {
                    await CaptureAndTranslateInternalAsync(token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetStatus($"Auto translate error: {ex.Message}");
            IsAutoTranslateEnabled = false;
        }
    }

    private void UpdateAutoTranslateStatus()
    {
        var statusText = IsAutoTranslateEnabled ? "Auto Translate: Running" : "Auto Translate: Stopped";
        AutoTranslateStatusText = statusText;
        SetStatus(IsAutoTranslateEnabled ? "Auto Translate: Running" : "Auto Translate: Stopped");
    }

    public void SetStatus(string message)
    {
        StatusMessage = message;
    }

    public async Task CaptureAndTranslateAsync(CancellationToken cancellationToken = default)
    {
        await CaptureAndTranslateInternalAsync(cancellationToken);
    }

    private async Task CaptureAndTranslateInternalAsync(CancellationToken cancellationToken)
    {
        if (!await _translationLock.WaitAsync(0, cancellationToken))
        {
            return; // Skip if already translating
        }

        try
        {
            if (SelectedRegion.IsEmpty)
            {
                StatusMessage = "Select a dialogue region before capturing.";
                ResetDebugMetrics();
                return;
            }

            StatusMessage = "Capturing...";
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
                    CreateOverlaySettings());

                var overlayTiming = new TimingEntry("overlay update", overlayElapsed.Value);
                LastTimings.Add(overlayTiming);
                await _timingLogger.LogAsync("overlay-update", [overlayTiming], cancellationToken);
            }
            UpdateDebugMetrics(result, overlayElapsed);
            StatusMessage = result.Status;

            // Refresh the auto translate status pill after each run
            if (IsAutoTranslateEnabled)
                AutoTranslateStatusText = "Auto Translate: Running";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            ResetDebugMetrics();
        }
        finally
        {
            _translationLock.Release();
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
            _pipeline.ResetState();
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
        _pipeline.ResetState();
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
        var isPlaceholder = TranslationProvider.Equals("placeholder", StringComparison.OrdinalIgnoreCase);
        
        if (isPlaceholder && !string.IsNullOrWhiteSpace(DeepLApiKey))
        {
            return "Placeholder (Warning: API key entered, but Provider is Placeholder. Select DeepL to use real translation.)";
        }
        else if (isPlaceholder)
        {
            return "Placeholder ready";
        }
        else if (TranslationProvider.Equals("deepl", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(DeepLApiKey)
                ? "DeepL missing API key"
                : "DeepL ready";
        }

        return "Unknown provider";
    }

    public void Dispose()
    {
        StopAutoTranslate();
        _overlayService.Dispose();
        _translationLock.Dispose();
    }
}

