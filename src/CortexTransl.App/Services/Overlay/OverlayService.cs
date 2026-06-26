using CortexTransl.App.Models;
using CortexTransl.App.Views;
using System.Diagnostics;

namespace CortexTransl.App.Services.Overlay;

public sealed class OverlayService : IOverlayService
{
    private static readonly TimeSpan MinimumVisualUpdateInterval = TimeSpan.FromMilliseconds(125);

    private readonly SemaphoreSlim _updateGate = new(1, 1);

    private OverlayWindow? _overlayWindow;
    private OverlayRequest? _lastAppliedRequest;
    private DateTimeOffset _lastVisualUpdateUtc = DateTimeOffset.MinValue;
    private bool? _currentRecordingSafeMode;

    public async Task<long> ShowTextAsync(string text, CaptureRegion region, OverlaySettings settings)
    {
        var request = new OverlayRequest(
            text.Trim(),
            region,
            NormalizeSettings(settings));

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return 0;
        }

        await _updateGate.WaitAsync();
        try
        {
            if (_lastAppliedRequest == request && _overlayWindow?.IsVisible == true)
            {
                return 0;
            }

            var elapsedSinceLastUpdate = DateTimeOffset.UtcNow - _lastVisualUpdateUtc;
            if (_lastAppliedRequest is not null && elapsedSinceLastUpdate < MinimumVisualUpdateInterval)
            {
                await Task.Delay(MinimumVisualUpdateInterval - elapsedSinceLastUpdate);
            }

            var stopwatch = Stopwatch.StartNew();
            EnsureWindow(request.Settings);

            if (_overlayWindow!.IsVisible != true)
            {
                _overlayWindow.Show();
            }

            _overlayWindow.UpdateText(request.Text, request.Region, request.Settings);
            stopwatch.Stop();

            _lastAppliedRequest = request;
            _lastVisualUpdateUtc = DateTimeOffset.UtcNow;
            return stopwatch.ElapsedMilliseconds;
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public void Hide()
    {
        _overlayWindow?.Hide();
    }

    public void Dispose()
    {
        _overlayWindow?.Close();
        _overlayWindow = null;
        _updateGate.Dispose();
    }

    private void EnsureWindow(OverlaySettings settings)
    {
        if (_overlayWindow is not null && _currentRecordingSafeMode == settings.IsRecordingSafe)
        {
            return;
        }

        _overlayWindow?.Close();
        _overlayWindow = new OverlayWindow(settings.IsRecordingSafe);
        _currentRecordingSafeMode = settings.IsRecordingSafe;
        _lastAppliedRequest = null;
        _overlayWindow.Show();
    }

    private static OverlaySettings NormalizeSettings(OverlaySettings settings)
    {
        return settings with
        {
            FontSize = Math.Clamp(settings.FontSize, 18, 72),
            Opacity = Math.Clamp(settings.Opacity, 0.35, 1),
            BackgroundOpacity = Math.Clamp(settings.BackgroundOpacity, 0.35, 1),
            MaxWidth = Math.Clamp(settings.MaxWidth, 280, 1400),
            PositionPreset = string.IsNullOrWhiteSpace(settings.PositionPreset) ? "custom" : settings.PositionPreset,
            RenderMode = string.IsNullOrWhiteSpace(settings.RenderMode) ? "transparent" : settings.RenderMode
        };
    }

    private sealed record OverlayRequest(
        string Text,
        CaptureRegion Region,
        OverlaySettings Settings);
}
