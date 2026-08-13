using CortexTransl.App.Models;
using CortexTransl.App.Views;
using System.Windows;
using System.Windows.Threading;

namespace CortexTransl.App.Services.Overlay;

public sealed class OverlayService : IOverlayService
{
    private OverlayWindow? _window;
    private CaptureRegion _pinnedRegion = CaptureRegion.Empty;
    private OverlaySettings? _pinnedSettings;

    public bool IsVisible => _window?.IsVisible == true;

    public void Pin(CaptureRegion region, OverlaySettings settings)
    {
        if (region.IsEmpty)
        {
            return;
        }

        RunOnUi(() =>
        {
            EnsureWindow();
            _pinnedRegion = region;
            _pinnedSettings = settings;
            _window!.Pin(region, settings);

            if (!_window.IsVisible)
            {
                _window.Show();
                _window.Pin(region, settings);
            }
        });
    }

    public void SetText(string text)
    {
        RunOnUi(() =>
        {
            if (_window is null || !IsVisible)
            {
                return;
            }

            _window.SetText(text);
        });
    }

    public void SetBlocks(IReadOnlyList<TranslatedBlock> blocks)
    {
        RunOnUi(() =>
        {
            if (_window is null || !IsVisible)
            {
                return;
            }

            _window.SetBlocks(blocks);
        });
    }

    public void Hide()
    {
        RunOnUi(() =>
        {
            if (_window?.IsVisible == true)
            {
                _window.Hide();
            }
        });
    }

    public void DisposeOverlay()
    {
        RunOnUi(() =>
        {
            if (_window is null)
            {
                return;
            }

            _window.AllowClose();
            _window.Close();
            _window = null;
            _pinnedRegion = CaptureRegion.Empty;
            _pinnedSettings = null;
        });
    }

    public void Dispose()
    {
        DisposeOverlay();
    }

    private void EnsureWindow()
    {
        _window ??= new OverlayWindow();
    }

    private void RunOnUi(Action action)
    {
        var dispatcher = _window?.Dispatcher ?? Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action, DispatcherPriority.Send);
    }
}
