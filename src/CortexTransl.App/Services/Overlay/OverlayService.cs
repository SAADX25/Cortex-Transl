using CortexTransl.App.Models;
using CortexTransl.App.Views;
using System.Diagnostics;

namespace CortexTransl.App.Services.Overlay;

public sealed class OverlayService : IOverlayService
{
    private static readonly TimeSpan MinimumVisualUpdateInterval = TimeSpan.FromMilliseconds(125);

    private readonly SemaphoreSlim _updateGate = new(1, 1);

    private OverlayWindow? _overlayWindow;
    private LensOverlayWindow? _lensOverlayWindow;
    private OverlayRequest? _lastAppliedRequest;
    private OverlayLayoutRequest? _lastLayoutRequest;
    private DateTimeOffset _lastVisualUpdateUtc = DateTimeOffset.MinValue;
    private bool? _currentRecordingSafeMode;
    private bool? _currentLensRecordingSafeMode;

    public event EventHandler<OverlayPositionChangedEventArgs>? OverlayPositionChanged;

    public bool IsVisible => _overlayWindow?.IsVisible == true || _lensOverlayWindow?.IsVisible == true;

    public bool IsLensOverlayVisible => _lensOverlayWindow?.IsVisible == true;

    public async Task<long> ShowTextAsync(
        string text,
        CaptureRegion region,
        OverlaySettings settings,
        CancellationToken cancellationToken = default)
    {
        var request = new OverlayRequest(
            text.Trim(),
            region,
            NormalizeSettings(settings));

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return 0;
        }

        await _updateGate.WaitAsync(cancellationToken);
        try
        {
            var layoutRequest = OverlayLayoutRequest.From(request);
            if (_lastAppliedRequest == request && _lastLayoutRequest == layoutRequest && _overlayWindow?.IsVisible == true)
            {
                return 0;
            }

            var elapsedSinceLastUpdate = DateTimeOffset.UtcNow - _lastVisualUpdateUtc;
            if (_lastAppliedRequest is not null && elapsedSinceLastUpdate < MinimumVisualUpdateInterval)
            {
                await Task.Delay(MinimumVisualUpdateInterval - elapsedSinceLastUpdate, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var stopwatch = Stopwatch.StartNew();
            _lensOverlayWindow?.Hide();
            EnsureWindow(request.Settings);

            if (_overlayWindow!.IsVisible != true)
            {
                _overlayWindow.Show();
            }

            var shouldUpdatePosition = request.Settings.UsesSmartPlacement ||
                _lastLayoutRequest != layoutRequest;

            _overlayWindow.UpdateText(request.Text, request.Region, request.Settings, shouldUpdatePosition);
            stopwatch.Stop();

            _lastAppliedRequest = request;
            _lastLayoutRequest = layoutRequest;
            _lastVisualUpdateUtc = DateTimeOffset.UtcNow;
            return stopwatch.ElapsedMilliseconds;
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public async Task<long> ShowBlocksAsync(
        IReadOnlyList<RecognizedTextBlock> textBlocks,
        CaptureRegion region,
        OverlaySettings settings,
        CancellationToken cancellationToken = default)
    {
        var translatedBlocks = textBlocks
            .Where(block => !block.Bounds.IsEmpty && !string.IsNullOrWhiteSpace(block.TranslatedText))
            .ToArray();

        if (translatedBlocks.Length == 0 || region.IsEmpty)
        {
            return 0;
        }

        var normalizedSettings = NormalizeSettings(settings);

        await _updateGate.WaitAsync(cancellationToken);
        try
        {
            var elapsedSinceLastUpdate = DateTimeOffset.UtcNow - _lastVisualUpdateUtc;
            if (elapsedSinceLastUpdate < MinimumVisualUpdateInterval)
            {
                await Task.Delay(MinimumVisualUpdateInterval - elapsedSinceLastUpdate, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var stopwatch = Stopwatch.StartNew();
            _overlayWindow?.Hide();
            EnsureLensWindow(normalizedSettings);

            if (_lensOverlayWindow!.IsVisible != true)
            {
                _lensOverlayWindow.Show();
            }

            _lensOverlayWindow.UpdateBlocks(translatedBlocks, region, normalizedSettings);
            stopwatch.Stop();

            _lastAppliedRequest = null;
            _lastLayoutRequest = null;
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
        _lensOverlayWindow?.Hide();
    }

    public void ClearText()
    {
        _overlayWindow?.ClearText();
        _lensOverlayWindow?.ClearBlocks();
        _lastAppliedRequest = null;
    }

    public void Dispose()
    {
        _overlayWindow?.Close();
        _overlayWindow = null;
        _lensOverlayWindow?.Close();
        _lensOverlayWindow = null;
        _updateGate.Dispose();
    }

    private void EnsureWindow(OverlaySettings settings)
    {
        if (_overlayWindow is not null && _currentRecordingSafeMode == settings.IsRecordingSafe)
        {
            return;
        }

        if (_overlayWindow is not null)
        {
            _overlayWindow.PositionChanged -= OnOverlayPositionChanged;
            _overlayWindow.Close();
        }

        _overlayWindow = new OverlayWindow(settings.IsRecordingSafe);
        _overlayWindow.PositionChanged += OnOverlayPositionChanged;
        _currentRecordingSafeMode = settings.IsRecordingSafe;
        _lastAppliedRequest = null;
        _lastLayoutRequest = null;
        _overlayWindow.Show();
    }

    private void EnsureLensWindow(OverlaySettings settings)
    {
        if (_lensOverlayWindow is not null && _currentLensRecordingSafeMode == settings.IsRecordingSafe)
        {
            return;
        }

        if (_lensOverlayWindow is not null)
        {
            _lensOverlayWindow.Close();
        }

        _lensOverlayWindow = new LensOverlayWindow(settings.IsRecordingSafe);
        _currentLensRecordingSafeMode = settings.IsRecordingSafe;
    }

    private void OnOverlayPositionChanged(object? sender, OverlayPositionChangedEventArgs e)
    {
        _lastAppliedRequest = null;
        _lastLayoutRequest = null;
        OverlayPositionChanged?.Invoke(this, e);
    }

    private static OverlaySettings NormalizeSettings(OverlaySettings settings)
    {
        var positionMode = settings.PositionMode.Equals("smart", StringComparison.OrdinalIgnoreCase)
            ? "smart"
            : "locked";

        return settings with
        {
            FontSize = Math.Clamp(settings.FontSize, 18, 72),
            Opacity = Math.Clamp(settings.Opacity, 0.35, 1),
            BackgroundOpacity = Math.Clamp(settings.BackgroundOpacity, 0.35, 1),
            MaxWidth = Math.Clamp(settings.MaxWidth, 280, 1400),
            PositionMode = positionMode,
            PositionPreset = NormalizePreset(settings.PositionPreset),
            RenderMode = string.IsNullOrWhiteSpace(settings.RenderMode) ? "transparent" : settings.RenderMode,
            ClickThrough = !settings.PositionUnlocked && settings.ClickThrough
        };
    }

    private static string NormalizePreset(string preset)
    {
        return preset.ToLowerInvariant() switch
        {
            "top-center" => "top-center",
            "middle-center" => "middle-center",
            "bottom-center" => "bottom-center",
            "above-ocr" => "above-ocr",
            "below-ocr" => "below-ocr",
            "custom" => "custom",
            _ => "bottom-center"
        };
    }

    private sealed record OverlayRequest(
        string Text,
        CaptureRegion Region,
        OverlaySettings Settings);

    private sealed record OverlayLayoutRequest(
        CaptureRegion Region,
        string PositionMode,
        string PositionPreset,
        double? CustomLeft,
        double? CustomTop,
        double FontSize,
        double MaxWidth,
        bool IsRecordingSafe,
        string ScreenLayoutKey)
    {
        public static OverlayLayoutRequest From(OverlayRequest request)
        {
            return new OverlayLayoutRequest(
                request.Region,
                request.Settings.PositionMode,
                request.Settings.PositionPreset,
                request.Settings.CustomLeft,
                request.Settings.CustomTop,
                request.Settings.FontSize,
                request.Settings.MaxWidth,
                request.Settings.IsRecordingSafe,
                OverlayWindow.GetScreenLayoutKey());
        }
    }
}
