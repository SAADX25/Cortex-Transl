using CortexTransl.App.Data;
using CortexTransl.App.Services.Cache;
using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Hotkeys;
using CortexTransl.App.Services.Ocr;
using CortexTransl.App.Services.Overlay;
using CortexTransl.App.Services.Profiles;
using CortexTransl.App.Services.Settings;
using CortexTransl.App.Services.Translation;
using CortexTransl.App.Utils;
using CortexTransl.App.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace CortexTransl.App.Views;

public partial class MainWindow : Window
{
    private static readonly TimeSpan F10DebounceInterval = TimeSpan.FromMilliseconds(280);
    private const ModifierKeys RegionHotkeyModifiers = ModifierKeys.None;

    private readonly GlobalHotkeyService _hotkeyService = new();
    private readonly PlayViewModel _viewModel;
    private readonly ScreenCaptureService _screenCapture;
    private DateTimeOffset _lastF10Utc = DateTimeOffset.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        var paths = AppDataPaths.CreateDefault();
        var connectionFactory = new SqliteConnectionFactory(paths.DatabasePath);
        var translationSettings = new TranslationProviderSettings();
        _screenCapture = new ScreenCaptureService();
        var offlineInstaller = new OfflineModelInstaller(paths.ModelsDirectory, paths.BundledModelsDirectory);
        var offlineProvider = new OfflineBergamotTranslationProvider(offlineInstaller);
        var pipeline = new TranslationPipeline(
            _screenCapture,
            new WindowsOcrEngine(),
            new RoutingTranslationProvider(
                translationSettings,
                offlineProvider,
                new DeepLTranslationProvider(translationSettings)),
            new SqliteTranslationCacheRepository(connectionFactory));

        _viewModel = new PlayViewModel(
            new DatabaseMigrator(connectionFactory),
            new RegionSelectionService(),
            pipeline,
            new OverlayService(),
            new SqliteGameProfileRepository(connectionFactory),
            translationSettings,
            offlineProvider,
            new AppSettingsService(paths.DataDirectory),
            new ThemeService());

        DataContext = _viewModel;
        _viewModel.MinimizeRequested += OnMinimizeRequested;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
            if (ApiKeyPasswordBox is not null)
            {
                ApiKeyPasswordBox.Password = _viewModel.DeepLApiKey;
            }

            RegisterHotkey(Key.F8);
            RegisterHotkey(Key.F9, RegionHotkeyModifiers);
            RegisterHotkey(Key.F10);
            _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        }
        catch (Exception ex)
        {
            AppLog.Write("startup", ex);
            _viewModel.SetStatusMessage("Startup hit a problem. Try selecting the region with F9.");
            MessageBox.Show(
                this,
                "Cortex Transl opened, but one setup step failed. You can still try F9 then F8.",
                "Cortex Transl",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ApiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_viewModel.DeepLApiKey != ApiKeyPasswordBox.Password)
        {
            _viewModel.DeepLApiKey = ApiKeyPasswordBox.Password;
        }
    }

    private async void OnHotkeyPressed(object? sender, HotkeyEventArgs e)
    {
        try
        {
            if (e.Key == Key.F8)
            {
                await _viewModel.HandleF8Async();
            }
            else if (e.Key == Key.F9 && e.Modifiers == RegionHotkeyModifiers)
            {
                await SelectRegionFromHotkeyAsync();
            }
            else if (e.Key == Key.F10)
            {
                ToggleProgramWindow();
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("hotkey", ex);
            _viewModel.SetStatusMessage("That shortcut hit a problem. Try it again.");
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.System || Keyboard.Modifiers != ModifierKeys.None)
        {
            return;
        }

        if ((e.Key == Key.System && e.SystemKey == Key.F10) || e.Key == Key.F10)
        {
            e.Handled = true;
            if (!_hotkeyService.IsRegistered(Key.F10))
            {
                ToggleProgramWindow();
            }

            return;
        }

        if (e.Key == Key.F8)
        {
            e.Handled = true;
            if (!_hotkeyService.IsRegistered(Key.F8))
            {
                _ = _viewModel.HandleF8Async();
            }
            return;
        }

        if (e.Key == Key.F9 && Keyboard.Modifiers == RegionHotkeyModifiers)
        {
            e.Handled = true;
            if (!_hotkeyService.IsRegistered(Key.F9, RegionHotkeyModifiers))
            {
                _ = SelectRegionFromHotkeyAsync();
            }
        }
    }

    private async Task SelectRegionFromHotkeyAsync()
    {
        var restoreAfterSelect = IsVisible && WindowState != WindowState.Minimized && !_viewModel.IsPlaying;
        Hide();
        try
        {
            await _viewModel.HandleF9Async();
        }
        finally
        {
            if (restoreAfterSelect)
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
            }
        }
    }

    private void RegisterHotkey(Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        if (_hotkeyService.Register(this, key, modifiers))
        {
            return;
        }

        var chord = modifiers == ModifierKeys.None ? key.ToString() : $"{modifiers}+{key}";
        AppLog.Write("hotkey-registration", $"Could not register {chord}. Win32 error: {_hotkeyService.LastRegistrationError}.");
    }

    private void ToggleProgramWindow()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastF10Utc < F10DebounceInterval)
        {
            return;
        }

        _lastF10Utc = now;
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            Topmost = false;
            WindowState = WindowState.Minimized;
            _viewModel.SetStatusMessage("Hidden. F8 starts or stops translation. F9 selects the region. F10 opens the program.");
            return;
        }

        Show();
        WindowState = WindowState.Normal;
        Topmost = true;
        Activate();
        _viewModel.SetStatusMessage("Program open. F10 hides it. F9 selects the region. F8 starts translation.");
    }

    private void OnMinimizeRequested(object? sender, EventArgs e)
    {
        Topmost = false;
        WindowState = WindowState.Minimized;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            _viewModel.MinimizeRequested -= OnMinimizeRequested;
            _hotkeyService.Dispose();
            _viewModel.Dispose();
            _screenCapture.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Write("shutdown", ex);
        }
    }
}
