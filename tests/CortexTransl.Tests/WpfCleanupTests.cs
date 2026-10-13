using CortexTransl.App.Data;
using CortexTransl.App.Models;
using CortexTransl.App.Services.Capture;
using CortexTransl.App.Services.Overlay;
using CortexTransl.App.Services.Profiles;
using CortexTransl.App.Services.Settings;
using CortexTransl.App.Services.Translation;
using CortexTransl.App.ViewModels;
using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace CortexTransl.Tests;

[CollectionDefinition("WPF cleanup", DisableParallelization = true)]
public sealed class WpfCleanupCollection;

[Collection("WPF cleanup")]
public sealed class WpfCleanupTests
{
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
