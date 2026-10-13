using CortexTransl.App.Models;
using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Diagnostics;
using CortexTransl.App.Utils;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace CortexTransl.App.ViewModels;

public sealed partial class PlayViewModel
{
    public async Task HandleF8Async()
    {
        await TogglePlayAsync();
    }

    public async Task HandleF9Async()
    {
        if (_regionSelectionService.IsSelecting)
        {
            _regionSelectionService.Cancel();
            StatusMessage = "Region selection closed.";
            return;
        }

        await SelectRegionAsync();
    }

    private bool CanStartOrStop()
    {
        return IsPlaying || (CanTranslate && !SelectedRegion.IsEmpty && !_isBusy);
    }

    private async Task SelectRegionAsync()
    {
        var overlayWasVisible = _overlayService.IsVisible;
        if (overlayWasVisible)
        {
            _overlayService.Hide();
            await Task.Delay(40);
        }

        try
        {
            var region = await _regionSelectionService.SelectRegionAsync(
                IsListMode
                    ? "Drag around the menu or icon names — F9 or Esc to close"
                    : "Drag around the story dialogue — F9 or Esc to close");
            if (region is null || region.IsEmpty)
            {
                if (!_regionSelectionService.IsSelecting)
                {
                    StatusMessage = "Region selection closed.";
                }

                return;
            }

            SelectedRegion = ScreenCoordinates.ClampToVirtualScreen(region);
            if (SelectedRegion.IsEmpty)
            {
                StatusMessage = "The selected region is outside the screen. Select it again.";
                return;
            }
            _pipeline.Reset();
            _pipeline.BindCapture(SelectedRegion, IsListMode);
            if (IsPlaying)
            {
                PinOverlay();
            }

            await SaveSettingsAsync();
            StatusMessage = "Region selected. Press F8 to start translation.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            if (overlayWasVisible && IsPlaying)
            {
                PinOverlay();
            }
        }
    }

    private async Task TogglePlayAsync()
    {
        if (IsPlaying)
        {
            StopPlay();
            StatusMessage = "Translation hidden. F8 starts it again. F10 opens the program.";
            return;
        }

        if (!CanTranslate)
        {
            SelectedNavPage = "key";
            StatusMessage = IsOfflineEngine
                ? "Download the Arabic translation files first."
                : "Paste a DeepL API key first.";
            return;
        }

        if (SelectedRegion.IsEmpty)
        {
            SelectedNavPage = "region";
            StatusMessage = "Select the dialogue region first.";
            return;
        }

        IsPlaying = true;
        _pipeline.Reset();
        _pipeline.BindCapture(SelectedRegion, IsListMode, onlyIfUnbound: true);
        _pipeline.Configure(string.IsNullOrWhiteSpace(ProfileName) ? "session" : ProfileName.Trim(), _storySettings);
        _playCts?.Dispose();
        _playCts = new CancellationTokenSource();
        var token = _playCts.Token;
        if (IsOfflineEngine)
        {
            StatusMessage = "Preparing resident offline translation...";
            try { await _offlineProvider.WarmAsync(SourceLanguage, cancellationToken: token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { StatusMessage = ex.Message; StopPlay(); return; }
            if (token.IsCancellationRequested || !IsPlaying) return;
        }
        StatusMessage = "Translation on. F8 stops it. F10 opens the program.";
        if (MinimizeDuringPlay)
        {
            MinimizeRequested?.Invoke(this, EventArgs.Empty);
        }

        _ = Task.Run(() => PlayLoopAsync(token));
        TogglePlayCommand.RaiseCanExecuteChanged();
    }

    private void StopPlay()
    {
        try
        {
            _playCts?.Cancel();
        }
        catch (Exception ex)
        {
            AppLog.Write("play-cancel", ex);
        }

        IsPlaying = false;
        _renderMeasurement?.Dispose(); _renderMeasurement = null;
        _pipeline.StopCapture();
        _overlayService.Hide();
    }

    private async Task PlayLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var turnStarted = Stopwatch.GetTimestamp();
                try
                {
                    await RunTurnAsync(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (_disposed)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Write("play-turn", ex);
                    RunOnUi(() =>
                    {
                        if (!_disposed && IsPlaying)
                        {
                            StatusMessage = "Temporary capture or translation error. Retrying...";
                        }
                    });
                }

                // Cadence includes work; do not add a full interval after OCR.
                var delay = Math.Max(5, _storySettings.EffectiveCaptureInterval - (int)Stopwatch.GetElapsedTime(turnStarted).TotalMilliseconds);
                await Task.Delay(delay, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            AppLog.Write("play-loop", ex);
            RunOnUi(() =>
            {
                if (_disposed)
                {
                    return;
                }

                StatusMessage = "Translation paused. Press F8 to start again.";
                StopPlay();
            });
        }
    }

    private async Task RunTurnAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

        _isBusy = true;
        RunOnUi(() => TogglePlayCommand.RaiseCanExecuteChanged());

        try
        {
            if (_disposed)
            {
                return;
            }

            var listMode = IsListMode;
            var result = await _pipeline.RunAsync(
                SelectedRegion,
                SourceLanguage,
                TargetLanguage,
                listMode,
                cancellationToken);

            RunOnUi(() => PublishResult(result, cancellationToken));
        }
        finally
        {
            _isBusy = false;
            try
            {
                if (!_disposed)
                {
                    RunOnUi(() => TogglePlayCommand.RaiseCanExecuteChanged());
                    _runLock.Release();
                }
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void OnPipelineResultAvailable(PipelineResult result)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (_disposed || dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(() => PublishResult(result, _playCts?.Token ?? CancellationToken.None), DispatcherPriority.Render);
    }

    private void PublishResult(PipelineResult result, CancellationToken cancellationToken)
    {
        var latest = _pipeline.LatestResult;
        if (_disposed || cancellationToken.IsCancellationRequested || result.Sequence != _pipeline.CurrentSequence
            || result.CompletedTimestamp < latest.CompletedTimestamp) return;
        var publication = Stopwatch.GetTimestamp();
        if (result.ClearOverlay)
        {
            _renderMeasurement?.Dispose(); _renderMeasurement = null;
            OriginalText = string.Empty; TranslatedText = string.Empty; _lastBlocks = [];
            _overlayService.Hide();
        }
        if (!string.IsNullOrWhiteSpace(result.OriginalText)) OriginalText = result.OriginalText;
        var textChanged = !string.IsNullOrWhiteSpace(result.TranslatedText) && TranslatedText != result.TranslatedText;
        if (textChanged) TranslatedText = result.TranslatedText;
        if (IsListMode && !result.Skipped && result.Blocks.Count > 0) { _lastBlocks = result.Blocks; textChanged = true; }
        if (IsPlaying && (textChanged || !_overlayService.IsVisible) && (!IsListMode || _lastBlocks.Count > 0)
            && !string.IsNullOrWhiteSpace(TranslatedText))
        {
            using var measured = _pipeline.Diagnostics.Measure(LatencyStage.OverlayPublication);
            if (result.CompletedTimestamp != 0 && _presentedSequence != result.Sequence)
                _pipeline.Diagnostics.Elapsed(LatencyStage.OverlayDispatch, result.CompletedTimestamp, publication);
            _presentedSequence = result.Sequence;
            PinOverlay();
            if (_pipeline.Diagnostics.Enabled)
            {
                _renderMeasurement?.Dispose();
                _renderMeasurement = new(_pipeline.Diagnostics, result, publication,
                    () => !_disposed && IsPlaying && result.Sequence == _pipeline.CurrentSequence && _overlayService.IsVisible);
            }
        }
        StatusMessage = result.Status;
    }
    private static void RunOnUi(Action action) => UiDispatch.Run(action);

    private OverlaySettings CreateOverlaySettings()
    {
        return new OverlaySettings(OverlayPlacement, OverlayBackgroundOpacity, TranslationMode, OverlayFontScale, OverlayTextColor);
    }

    private void PinOverlay()
    {
        if (SelectedRegion.IsEmpty)
        {
            return;
        }

        _overlayService.Pin(SelectedRegion, CreateOverlaySettings());
        if (IsListMode)
        {
            if (_lastBlocks.Count > 0)
            {
                _overlayService.SetBlocks(_lastBlocks);
            }

            return;
        }

        if (!string.IsNullOrWhiteSpace(TranslatedText))
        {
            _overlayService.SetText(TranslatedText);
        }
    }

    private void RefreshOverlay()
    {
        if (IsPlaying)
        {
            PinOverlay();
        }
    }

}
