using CortexTransl.App.Data;
using CortexTransl.App.Models;
using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Overlay;
using CortexTransl.App.Services.Profiles;
using CortexTransl.App.Services.Settings;
using CortexTransl.App.Services.Translation;
using CortexTransl.App.ViewModels;
using CortexTransl.App.Views;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Drawing;
using Xunit;

namespace CortexTransl.Tests;

[CollectionDefinition("WPF cleanup", DisableParallelization = true)]
public sealed class WpfCleanupCollection;

[Collection("WPF cleanup")]
public sealed class WpfCleanupTests
{
    [Fact]
    public async Task StopRestartAndDisposeReleaseCaptureOffDispatcherAndRejectLateResults()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                using var capture = new BlockingLifecycleCapture();
                try
                {
                    var late = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var provider = new FakeProvider { Translate = (_, _) => late.Task };
                    using var pipeline = new TranslationPipeline(capture, new FakeOcr(), provider, new FakeCache());
                    var folder = Path.Combine(Path.GetTempPath(), "CortexTransl-lifecycle-" + Guid.NewGuid().ToString("N"));
                    using var client = new HttpClient(new DelegateHttpHandler((_, _) => throw new InvalidOperationException("No network")));
                    using var offline = new OfflineBergamotTranslationProvider(new OfflineModelInstaller(folder, folder, client));
                    var overlay = new FakeOverlay();
                    await using var viewModel = new PlayViewModel(new DatabaseMigrator(new SqliteConnectionFactory(Path.Combine(folder, "fixture.db"))),
                        new FakeSelector(), pipeline, overlay, new FakeProfiles(), new(), offline, new AppSettingsService(folder), new());
                    viewModel.DeepLApiKey = "synthetic-key";
                    viewModel.UsePresetCommand.Execute(new GameProfile { Name = "Fixture", Region = new(0, 0, 64, 32),
                        SourceLanguage = "en", TranslationProvider = "deepl" });
                    await viewModel.HandleF8Async();
                    await WaitUntilAsync(() => provider.Calls.Count == 1);
                    var stop = viewModel.HandleF8Async();
                    Assert.True(stop.IsCompletedSuccessfully);
                    Assert.False(viewModel.IsPlaying);
                    await capture.ReleaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    Assert.NotEqual(Environment.CurrentManagedThreadId, capture.ReleaseThread);
                    var restart = viewModel.HandleF8Async();
                    Assert.False(restart.IsCompleted);
                    capture.AllowRelease.Set();
                    await restart.WaitAsync(TimeSpan.FromSeconds(3));
                    Assert.True(viewModel.IsPlaying);
                    // Shutdown remains responsive even while a provider ignores
                    // cancellation. Async cleanup finishes once it returns.
                    viewModel.Dispose();
                    Assert.True(overlay.Disposed);
                    var shutdown = viewModel.DisposeAsync().AsTask();
                    Assert.False(shutdown.IsCompleted);
                    late.TrySetResult("obsolete Arabic");
                    await shutdown.WaitAsync(TimeSpan.FromSeconds(3));
                    Assert.False(capture.Active);
                    Assert.True(capture.Releases >= 2);
                    Assert.Empty(viewModel.TranslatedText);
                    completed.SetResult();
                }
                catch (Exception ex) { completed.TrySetException(ex); }
                finally
                {
                    capture.AllowRelease.Set();
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class BlockingLifecycleCapture : IScreenCaptureService
    {
        public TaskCompletionSource ReleaseEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim AllowRelease { get; } = new(false);
        public int ReleaseThread { get; private set; }
        public int Releases { get; private set; }
        public bool Active { get; private set; }
        public void BindTarget(CaptureRegion region, bool listMode, bool onlyIfUnbound = false) => Active = true;
        public Task<Bitmap> CaptureAsync(CaptureRegion region, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new Bitmap(64, 32));
        }
        public void ReleaseSession()
        {
            ReleaseThread = Environment.CurrentManagedThreadId;
            ReleaseEntered.TrySetResult();
            if (!AllowRelease.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test capture release was not unblocked.");
            Releases++;
            Active = false;
        }
        public void Dispose() => AllowRelease.Dispose();
    }

    [Fact]
    public async Task OverlayKeepsArabicAndMixedTextInRtlLogicalOrder()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var window = new OverlayWindow();
                var text = (TextBlock)window.FindName("TranslationText");
                var container = (Border)window.FindName("OverlayChrome");
                const string dialogue = "قالت Alice: انتظر... هل تسمعني؟";
                text.FlowDirection = FlowDirection.LeftToRight;
                window.SetText(dialogue);
                Assert.Equal(dialogue, text.Text);
                Assert.Equal(FlowDirection.RightToLeft, text.FlowDirection);
                Assert.Equal(FlowDirection.RightToLeft, container.FlowDirection);
                Assert.Equal("ar", text.Language.IetfLanguageTag);
                window.AllowClose();
                window.Close();
                completed.SetResult();
            }
            catch (Exception ex) { completed.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task StartupPreservesSavedPlacementAndThemesWhileTestPageUsesOnlyOfflineEngine()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                try
                {
                    foreach (var theme in new[] { "Dark", "Light", "Common" })
                        app.Resources.MergedDictionaries.Add(new ResourceDictionary
                        { Source = new Uri($"/CortexTransl.App;component/Views/Styles/Theme.{theme}.xaml", UriKind.Relative) });
                    var directory = Path.Combine(Path.GetTempPath(), "CortexTransl-wpf-" + Guid.NewGuid().ToString("N"));
                    var settings = new AppSettingsService(directory);
                    await settings.SaveAsync(new() { OverlayPlacement = "below", SourceLanguage = "ja", Theme = "Light" });
                    var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var downloadResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var client = new HttpClient(new DelegateHttpHandler((_, token) =>
                    { downloadStarted.TrySetResult(); return downloadResponse.Task.WaitAsync(token); }));
                    using var offline = new OfflineBergamotTranslationProvider(new OfflineModelInstaller(directory, directory, client));
                    var capture = new FakeCapture();
                    using var pipeline = new TranslationPipeline(capture, new FakeOcr(), offline, new FakeCache());
                    var overlay = new FakeOverlay();
                    using var viewModel = new PlayViewModel(new DatabaseMigrator(new SqliteConnectionFactory(Path.Combine(directory, "fixture.db"))),
                        new FakeSelector(), pipeline, overlay, new FakeProfiles(), new(), offline, settings, new());
                    await viewModel.InitializeAsync();
                    Assert.Equal("below", viewModel.OverlayPlacement);
                    Assert.Equal("ja", viewModel.SourceLanguage);
                    Assert.False(viewModel.IsOfflineReady);
                    viewModel.SourceLanguage = "en";
                    var download = viewModel.DownloadOfflineModelCommand.ExecuteAsync();
                    await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    Assert.True(viewModel.IsDownloadingModel);
                    viewModel.SourceLanguage = "fr";
                    downloadResponse.SetException(new HttpRequestException("obsolete en model error"));
                    await download;
                    Assert.Equal("fr", viewModel.SourceLanguage);
                    Assert.False(viewModel.IsOfflineReady);
                    Assert.False(viewModel.IsDownloadingModel);
                    Assert.DoesNotContain("obsolete", viewModel.OfflineModelStatus);
                    viewModel.TranslationEngine = "deepl";
                    viewModel.DeepLApiKey = "test-key";
                    viewModel.TestInputText = "Run!";
                    Assert.False(viewModel.RunTestCommand.CanExecute(null));
                    viewModel.Dispose(); viewModel.Dispose();
                    await viewModel.DisposeAsync();
                    Assert.Equal(1, capture.ReleasedSessions);
                    Assert.True(overlay.Disposed);
                    completed.SetResult();
                }
                catch (Exception ex) { completed.TrySetException(ex); }
                // WPF permits only one Application per process. Keep this
                // explicit-shutdown test host alive until testhost exits so
                // later capture fixtures can create their own STA windows.
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private sealed class FakeSelector : IRegionSelectionService
    {
        public bool IsSelecting => false;
        public void Cancel() { }
        public Task<CaptureRegion?> SelectRegionAsync(string? hint = null) => Task.FromResult<CaptureRegion?>(null);
    }
    private sealed class FakeProfiles : IGameProfileRepository
    {
        public Task<IReadOnlyList<GameProfile>> GetAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameProfile>>([]);
        public Task SaveAsync(GameProfile profile, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(long id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class FakeOverlay : IOverlayService
    {
        public bool Disposed { get; private set; }
        public bool IsVisible => false;
        public void Pin(CaptureRegion region, OverlaySettings settings) { }
        public void SetText(string text) { }
        public void SetBlocks(IReadOnlyList<TranslatedBlock> blocks) { }
        public void Hide() { }
        public void DisposeOverlay() { }
        public void Dispose() => Disposed = true;
    }
}
