using CortexTransl.App.Models;
using CortexTransl.App.Views;

namespace CortexTransl.App.Services.Capture;

public sealed class RegionSelectionService : IRegionSelectionService
{
    public Task<CaptureRegion?> SelectRegionAsync()
    {
        var window = new RegionSelectorWindow();
        var accepted = window.ShowDialog() == true;
        return Task.FromResult(accepted ? window.SelectedRegion : null);
    }
}
