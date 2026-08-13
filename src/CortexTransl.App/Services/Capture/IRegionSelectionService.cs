using CortexTransl.App.Models;

namespace CortexTransl.App.Services.Capture;

public interface IRegionSelectionService
{
    bool IsSelecting { get; }

    void Cancel();

    Task<CaptureRegion?> SelectRegionAsync();
}
