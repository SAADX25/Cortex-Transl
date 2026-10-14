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
        if (_disposed) return;
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
        _pipeline.Configure(string.IsNullOrWhiteSpace(ProfileName) ? "session" : ProfileName.Trim(), _storySettings);
        _playCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _playCts.Token;
        _activePlayToken = token;
        var region = SelectedRegion;
        var listMode = IsListMode;
        var language = SourceLanguage;
        var offline = IsOfflineEngine;
        var previousStop = _captureStopped.IsCompletedSuccessfully ? Task.CompletedTask : _captureStopped;
        _preparationTask = Task.Run(async () =>
        {
            await previousStop.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _pipeline.Reset();
            _pipeline.BindCapture(region, listMode, onlyIfUnbound: true);
            if (offline) await _offlineProvider.WarmAsync(language, cancellationToken: token).ConfigureAwait(false);
        }, token);
        if (offline) StatusMessage = "Preparing resident offline translation...";
        try { await _preparationTask; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            if (!_disposed && !token.IsCancellationRequested) { StatusMessage = ex.Message; StopPlay(); }
            return;
        }
        if (_disposed || token.IsCancellationRequested || !IsPlaying) return;
        StatusMessage = "Translation on. F8 stops it. F10 opens the program.";
        if (MinimizeDuringPlay)
        {
            MinimizeRequested?.Invoke(this, EventArgs.Empty);
        }

        _playTask = Task.Run(() => PlayLoopAsync(token));
        TogglePlayCommand.RaiseCanExecuteChanged();
    }

    private void StopPlay()
    {
        var cancellation = _playCts;
        _playCts = null;
        try
        {
            cancellation?.Cancel();
        }
        catch (Exception ex)
        {
            AppLog.Write("play-cancel", ex);
        }

        IsPlaying = false;
        _renderMeasurement?.Dispose(); _renderMeasurement = null;
        _pipeline.Reset();
        _overlayService.Hide();
        _captureStopped = StopCaptureAsync(
            _captureStopped.IsCompletedSuccessfully ? Task.CompletedTask : _captureStopped,
            _preparationTask.IsCompletedSuccessfully ? Task.CompletedTask : _preparationTask,
            _playTask.IsCompletedSuccessfully ? Task.CompletedTask : _playTask, cancellation);
    }

    private async Task StopCaptureAsync(Task previousStop, Task preparation, Task playback, CancellationTokenSource? cancellation)
    {
        try { await Task.WhenAll(previousStop, preparation, playback).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLog.Write("play-stop", ex); }
        finally
        {
            try { await Task.Run(_pipeline.StopCapture).ConfigureAwait(false); }
            finally { cancellation?.Dispose(); }
        }
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

            QueuePublication(result, cancellationToken);
        }
        finally
        {
            _isBusy = false;
            try
            {
                _runLock.Release();
                if (!_disposed && !IsPlaying) RunOnUi(() => TogglePlayCommand.RaiseCanExecuteChanged());
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void OnPipelineResultAvailable(PipelineResult result)
    {
        if (_disposed || !IsPlaying) return;
        QueuePublication(result, _activePlayToken);
    }

    private sealed record Publication(PipelineResult Result, CancellationToken Token);
    private Publication? _pendingPublication;
    private int _publicationScheduled;

    private void QueuePublication(PipelineResult result, CancellationToken token)
    {
        if (_disposed || token.IsCancellationRequested || _dispatcher.HasShutdownStarted) return;
        Interlocked.Exchange(ref _pendingPublication, new(result, token));
        if (Interlocked.Exchange(ref _publicationScheduled, 1) != 0) return;
        _dispatcher.BeginInvoke(() =>
        {
            var pending = Interlocked.Exchange(ref _pendingPublication, null);
            Interlocked.Exchange(ref _publicationScheduled, 0);
            if (pending is not null) PublishResult(pending.Result, pending.Token);
        }, DispatcherPriority.Render);
    }

    private void PublishResult(PipelineResult result, CancellationToken cancellationToken)
    {
        var latest = _pipeline.LatestResult;
        if (_disposed || !IsPlaying || cancellationToken.IsCancellationRequested || result.Sequence != _pipeline.CurrentSequence
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
    private void RunOnUi(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

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
