using CortexTransl.App.Data;
using CortexTransl.App.Services.Cache;
using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Hotkeys;
using CortexTransl.App.Services.Ocr;
using CortexTransl.App.Services.Overlay;
using CortexTransl.App.Services.Profiles;
using CortexTransl.App.Services.Translation;
using CortexTransl.App.Utils;
using CortexTransl.App.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace CortexTransl.App.Views;

public partial class MainWindow : Window
{
    private readonly GlobalHotkeyService _hotkeyService = new();
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        var paths = AppDataPaths.CreateDefault();
        var connectionFactory = new SqliteConnectionFactory(paths.DatabasePath);
        var migrator = new DatabaseMigrator(connectionFactory);
        var timingLogger = new TimingLogger(paths.LogPath);
        var cacheRepository = new SqliteTranslationCacheRepository(connectionFactory);
        var profileRepository = new SqliteGameProfileRepository(connectionFactory);
        var translationProviderSettings = new TranslationProviderSettings();
        var pipeline = new CaptureTranslatePipeline(
            new ScreenCaptureService(),
            [new WindowsOcrEngine()],
            [
                new PlaceholderTranslationProvider(),
                new DeepLTranslationProvider(translationProviderSettings)
            ],
            cacheRepository,
            timingLogger);

        _viewModel = new MainViewModel(
            migrator,
            new RegionSelectionService(),
            pipeline,
            new OverlayService(),
            profileRepository,
            translationProviderSettings,
            timingLogger);

        DataContext = _viewModel;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();

            if (_hotkeyService.Register(this, Key.F8))
            {
                _hotkeyService.HotkeyPressed += OnHotkeyPressed;
                _viewModel.SetStatus("Ready. Select a dialogue region to begin. F8 is enabled.");
            }
            else
            {
                _viewModel.SetStatus("Ready, but F8 could not be registered. It may already be in use.");
            }
        }
        catch (Exception ex)
        {
            _viewModel.SetStatus($"Startup failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Cortex Transl startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnHotkeyPressed(object? sender, EventArgs e)
    {
        try
        {
            await await Dispatcher.InvokeAsync(() => _viewModel.CaptureAndTranslateCommand.ExecuteAsync());
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
}
