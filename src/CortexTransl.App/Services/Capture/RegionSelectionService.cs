using CortexTransl.App.Models;
using CortexTransl.App.Views;
using System.Windows;

namespace CortexTransl.App.Services.Capture;

public sealed class RegionSelectionService : IRegionSelectionService
{
    private readonly IScreenCaptureService _screenCaptureService;

    public RegionSelectionService(IScreenCaptureService screenCaptureService)
    {
        _screenCaptureService = screenCaptureService;
    }

    public async Task<CaptureRegion?> SelectRegionAsync(RegionSelectionMode mode = RegionSelectionMode.Auto)
    {
        Window window;
        if (mode == RegionSelectionMode.Screenshot)
        {
            var screenshotWindow = new ScreenshotRegionSelectorWindow(_screenCaptureService);
            await screenshotWindow.InitializeAsync();
            window = screenshotWindow;
        }
        else
        {
            window = new RegionSelectorWindow();
        }

        var accepted = window.ShowDialog() == true;

        if (window is RegionSelectorWindow liveWindow)
        {
            return accepted ? liveWindow.SelectedRegion : null;
        }
        else if (window is ScreenshotRegionSelectorWindow ssWindow)
        {
            return accepted ? ssWindow.SelectedRegion : null;
        }

        return null;
    }
}
