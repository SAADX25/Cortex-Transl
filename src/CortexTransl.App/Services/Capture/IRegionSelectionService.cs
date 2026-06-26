using CortexTransl.App.Models;

namespace CortexTransl.App.Services.Capture;

public enum RegionSelectionMode
{
    Auto,
    Live,
    Screenshot
}

public interface IRegionSelectionService
{
    Task<CaptureRegion?> SelectRegionAsync(RegionSelectionMode mode = RegionSelectionMode.Auto);
}
