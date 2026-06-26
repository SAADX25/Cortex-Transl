using CortexTransl.App.Models;

namespace CortexTransl.App.Services.Capture;

public interface IRegionSelectionService
{
    Task<CaptureRegion?> SelectRegionAsync();
}
