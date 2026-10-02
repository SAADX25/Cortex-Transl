using CortexTransl.App.Models;
using CortexTransl.App.Utils;
using CortexTransl.App.Views;
using System.Windows;
using System.Windows.Threading;

namespace CortexTransl.App.Services.Overlay;

public sealed class OverlayService : IOverlayService
{
    private readonly DispatcherTimer _topmostTimer;
    private OverlayWindow? _window;

    public OverlayService()
    {
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _topmostTimer.Tick += (_, _) => KeepOnTop();
    }

    public bool IsVisible => _window?.IsVisible == true;

    public void Pin(CaptureRegion region, OverlaySettings settings)
    {
        if (region.IsEmpty)
        {
            return;
        }

        RunOnUi(() =>
        {
            try
            {
                EnsureWindow();
                _window!.Pin(region, settings);

                if (!_window.IsVisible)
                {
                    _window.Show();
                    _window.Pin(region, settings);
                }

                _window.BringToFront();
                if (!_topmostTimer.IsEnabled)
                {
                    _topmostTimer.Start();
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("overlay", ex);
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
            _topmostTimer.Stop();
            if (_window?.IsVisible == true)
            {
                _window.Hide();
                _window.ResetCapturePlacement();
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

            _topmostTimer.Stop();
            _window.AllowClose();
            _window.Close();
            _window = null;
        });
    }

    private void KeepOnTop()
    {
        try
        {
            if (_window is { IsVisible: true })
            {
                _window.BringToFront();
            }
        }
        catch
        {
        }
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
        try
        {
            var dispatcher = _window?.Dispatcher ?? Application.Current?.Dispatcher;
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
}
