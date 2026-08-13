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
    private const double CompactWidth = 540;
    private const double CompactHeight = 560;
    private static readonly TimeSpan F10DebounceInterval = TimeSpan.FromMilliseconds(280);

    private readonly GlobalHotkeyService _hotkeyService = new();
    private readonly PlayViewModel _viewModel;

    private bool _gamePanelOpen;
    private WindowState _stateBeforePanel = WindowState.Normal;
    private Rect _boundsBeforePanel;
    private double? _compactLeft;
    private double? _compactTop;
    private DateTimeOffset _lastF10Utc = DateTimeOffset.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        var paths = AppDataPaths.CreateDefault();
        var connectionFactory = new SqliteConnectionFactory(paths.DatabasePath);
        var translationSettings = new TranslationProviderSettings();
        var screenCapture = new ScreenCaptureService();
        var pipeline = new TranslationPipeline(
            screenCapture,
            new WindowsOcrEngine(),
            new DeepLTranslationProvider(translationSettings),
            new SqliteTranslationCacheRepository(connectionFactory));

        _viewModel = new PlayViewModel(
            new DatabaseMigrator(connectionFactory),
            new RegionSelectionService(),
            pipeline,
            new OverlayService(),
            new SqliteGameProfileRepository(connectionFactory),
            translationSettings,
            new AppSettingsService(paths.DataDirectory),
            new ThemeService());

        DataContext = _viewModel;
        _viewModel.MinimizeRequested += OnMinimizeRequested;
        Loaded += OnLoaded;
        Closed += OnClosed;
        LocationChanged += OnLocationChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
            ApiKeyPasswordBox.Password = _viewModel.DeepLApiKey;

            _hotkeyService.Register(this, Key.F8);
            _hotkeyService.Register(this, Key.F9);
            _hotkeyService.Register(this, Key.F10);
            _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        }
        catch (Exception ex)
        {
            _viewModel.Dispose();
            MessageBox.Show(this, ex.Message, "Cortex Transl failed to start", MessageBoxButton.OK, MessageBoxImage.Error);
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
            else if (e.Key == Key.F9)
            {
                await SelectRegionFromHotkeyAsync();
            }
            else if (e.Key == Key.F10)
            {
                ToggleGamePanel();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cortex Transl", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _gamePanelOpen)
        {
            e.Handled = true;
            CloseGamePanel();
            return;
        }

        if ((e.Key == Key.System && e.SystemKey == Key.F10) || e.Key == Key.F10)
        {
            e.Handled = true;
            if (!_hotkeyService.IsRegistered(Key.F10))
            {
                ToggleGamePanel();
            }

            return;
        }

        if (e.Key is Key.F8 or Key.F9)
        {
            e.Handled = true;
        }
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        if (_gamePanelOpen && WindowState == WindowState.Normal)
        {
            _compactLeft = Left;
            _compactTop = Top;
        }
    }

    private async Task SelectRegionFromHotkeyAsync()
    {
        var reopenPanel = _gamePanelOpen;
        if (reopenPanel)
        {
            RememberCompactPosition();
            _gamePanelOpen = false;
            _viewModel.SuppressAutoCapture = false;
            Topmost = false;
            Hide();
        }

        try
        {
            await _viewModel.HandleF9Async();
        }
        finally
        {
            if (reopenPanel)
            {
                ShowGamePanelUi();
            }
        }
    }

    private void ToggleGamePanel()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastF10Utc < F10DebounceInterval)
        {
            return;
        }

        _lastF10Utc = now;
        if (_gamePanelOpen)
        {
            CloseGamePanel();
            return;
        }

        OpenGamePanel();
    }

    private void OpenGamePanel()
    {
        _stateBeforePanel = WindowState;
        _boundsBeforePanel = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height)
            : RestoreBounds;
        ShowGamePanelUi();
    }

    private void ShowGamePanelUi()
    {
        _gamePanelOpen = true;
        _viewModel.SuppressAutoCapture = true;
        Show();
        Topmost = true;
        WindowState = WindowState.Normal;
        Width = CompactWidth;
        Height = CompactHeight;
        PlaceCompactPanel();
        Activate();
        _viewModel.SetStatusMessage("Menu open. F10 or Esc returns to the game.");
    }

    private void PlaceCompactPanel()
    {
        var work = ScreenCoordinates.GetCursorWorkAreaDip();
        var width = Width;
        var height = Height;
        var left = _compactLeft ?? (work.Right - width - 16);
        var top = _compactTop ?? (work.Top + 48);
        Left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - width));
        Top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - height));
    }

    private void RememberCompactPosition()
    {
        if (WindowState == WindowState.Normal)
        {
            _compactLeft = Left;
            _compactTop = Top;
        }
    }

    private void CloseGamePanel()
    {
        if (!_gamePanelOpen)
        {
            return;
        }

        RememberCompactPosition();
        _gamePanelOpen = false;
        _viewModel.SuppressAutoCapture = false;
        Topmost = false;

        if (_viewModel.IsPlaying && _viewModel.MinimizeDuringPlay)
        {
            WindowState = WindowState.Minimized;
            _viewModel.SetStatusMessage("Back to the game. F10 opens the menu.");
            return;
        }

        Show();
        WindowState = _stateBeforePanel == WindowState.Maximized
            ? WindowState.Normal
            : _stateBeforePanel;
        if (WindowState == WindowState.Normal && _boundsBeforePanel.Width > 0 && _boundsBeforePanel.Height > 0)
        {
            Left = _boundsBeforePanel.X;
            Top = _boundsBeforePanel.Y;
            Width = _boundsBeforePanel.Width;
            Height = _boundsBeforePanel.Height;
        }

        _viewModel.SetStatusMessage("Menu closed. F10 opens it over the game.");
    }

    private void OnMinimizeRequested(object? sender, EventArgs e)
    {
        RememberCompactPosition();
        _gamePanelOpen = false;
        _viewModel.SuppressAutoCapture = false;
        Topmost = false;
        WindowState = WindowState.Minimized;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.MinimizeRequested -= OnMinimizeRequested;
        _hotkeyService.Dispose();
        _viewModel.Dispose();
    }
}
