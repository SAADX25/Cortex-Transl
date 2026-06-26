using CortexTransl.App.Models;
using CortexTransl.App.Views;
using System.Diagnostics;
using System.Windows;

namespace CortexTransl.App.Services.Overlay;

public sealed class OverlayService : IOverlayService
{
    private OverlayWindow? _overlayWindow;

    public Task<long> ShowTextAsync(string text, CaptureRegion region, double fontSize, double opacity)
    {
        var stopwatch = Stopwatch.StartNew();
        EnsureWindow();
        _overlayWindow!.UpdateText(text, region, fontSize, opacity);
        stopwatch.Stop();
        return Task.FromResult(stopwatch.ElapsedMilliseconds);
    }

    public void Hide()
    {
        _overlayWindow?.Hide();
    }

    public void Dispose()
    {
        _overlayWindow?.Close();
        _overlayWindow = null;
    }

    private void EnsureWindow()
    {
        if (_overlayWindow is not null)
        {
            return;
        }

        _overlayWindow = new OverlayWindow();
        _overlayWindow.Show();
    }
}
