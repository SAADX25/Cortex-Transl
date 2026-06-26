using CortexTransl.App.Data;
using CortexTransl.App.Services.Cache;
using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Hotkeys;
using CortexTransl.App.Services.Ocr;
using CortexTransl.App.Services.Overlay;
using CortexTransl.App.Services.Profiles;
using CortexTransl.App.Services.Translation;
using CortexTransl.App.Services.Settings;
using CortexTransl.App.Utils;
using CortexTransl.App.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace CortexTransl.App.Views;

public partial class MainWindow : Window
{
    private readonly GlobalHotkeyService _hotkeyService = new();
    private readonly MainViewModel _viewModel;
    private readonly AppSettingsService _appSettingsService;

    public MainWindow()
    {
        InitializeComponent();

        var paths = AppDataPaths.CreateDefault();
        var connectionFactory = new SqliteConnectionFactory(paths.DatabasePath);
        var migrator = new DatabaseMigrator(connectionFactory);
        var timingLogger = new TimingLogger(paths.LogPath);
        var cacheRepository = new SqliteTranslationCacheRepository(connectionFactory);
        var profileRepository = new SqliteGameProfileRepository(connectionFactory);
        _appSettingsService = new AppSettingsService(paths.DataDirectory);
        var themeService = new ThemeService();
        var translationProviderSettings = new TranslationProviderSettings();
        var screenCaptureService = new ScreenCaptureService();
        var pipeline = new CaptureTranslatePipeline(
            screenCaptureService,
            [new WindowsOcrEngine()],
            [
                new PlaceholderTranslationProvider(),
                new DeepLTranslationProvider(translationProviderSettings)
            ],
            cacheRepository,
            timingLogger);

        _viewModel = new MainViewModel(
            migrator,
            new RegionSelectionService(screenCaptureService),
            pipeline,
            new OverlayService(),
            profileRepository,
            translationProviderSettings,
            _appSettingsService,
            themeService,
            timingLogger);

        DataContext = _viewModel;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = await _appSettingsService.LoadAsync();
            if (!settings.HasRunSetupWizard)
            {
                var wizard = new SetupWizardWindow(_appSettingsService)
                {
                    Owner = this
                };
                wizard.ShowDialog();
            }

            await _viewModel.InitializeAsync();

            ApiKeyPasswordBox.Password = _viewModel.DeepLApiKey;

            bool f8Registered = _hotkeyService.Register(this, Key.F8);
            bool f9Registered = _hotkeyService.Register(this, Key.F9);
            bool f10Registered = _hotkeyService.Register(this, Key.F10);

            if (f8Registered && f9Registered && f10Registered)
            {
                _hotkeyService.HotkeyPressed += OnHotkeyPressed;
                _viewModel.SetStatus("Ready. Select a dialogue region to begin.");
            }
            else
            {
                _hotkeyService.HotkeyPressed += OnHotkeyPressed;
                _viewModel.SetStatus("Ready, but some hotkeys could not be registered. They may already be in use.");
            }
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus($"Startup failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Cortex Transl startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_viewModel.DeepLApiKey != ApiKeyPasswordBox.Password)
        {
            _viewModel.DeepLApiKey = ApiKeyPasswordBox.Password;
        }
    }

    private void ToggleApiKeyVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (ApiKeyPasswordBox.Visibility == Visibility.Visible)
        {
            ApiKeyTextBox.Text = ApiKeyPasswordBox.Password;
            ApiKeyPasswordBox.Visibility = Visibility.Collapsed;
            ApiKeyTextBox.Visibility = Visibility.Visible;
        }
        else
        {
            ApiKeyPasswordBox.Password = ApiKeyTextBox.Text;
            ApiKeyTextBox.Visibility = Visibility.Collapsed;
            ApiKeyPasswordBox.Visibility = Visibility.Visible;
        }
    }

    private async void OnHotkeyPressed(object? sender, HotkeyEventArgs e)
    {
        try
        {
            if (e.Key == Key.F8)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_viewModel.ToggleAutoTranslateCommand.CanExecute(null))
                    {
                        _viewModel.ToggleAutoTranslateCommand.Execute(null);
                    }
                });
            }
            else if (e.Key == Key.F9)
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    if (_viewModel.SelectRegionCommand.CanExecute(null))
                    {
                        await _viewModel.SelectRegionCommand.ExecuteAsync(null);
                    }
                });
            }
            else if (e.Key == Key.F10)
            {
                await Dispatcher.InvokeAsync(async () =>
                {
                    if (_viewModel.CaptureAndTranslateCommand.CanExecute(null))
                    {
                        await _viewModel.CaptureAndTranslateCommand.ExecuteAsync(null);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus(ex.Message);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _hotkeyService.Dispose();
        _viewModel.Dispose();
    }

    private void SimpleMode_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.AppMode = "Simple";
    }

    private void AdvancedMode_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.AppMode = "Advanced";
    }

    private void RunSetupWizard_Click(object sender, RoutedEventArgs e)
    {
        var wizard = new SetupWizardWindow(_appSettingsService)
        {
            Owner = this
        };
        wizard.ShowDialog();
        
        // Reload settings to apply the wizard choices
        _ = _viewModel.InitializeAsync();
    }
}
