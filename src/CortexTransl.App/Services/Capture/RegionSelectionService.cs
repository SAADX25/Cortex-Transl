using CortexTransl.App.Models;
using CortexTransl.App.Views;
using System.Windows;

namespace CortexTransl.App.Services.Capture;

public sealed class RegionSelectionService : IRegionSelectionService
{
    private Window? _activeSelector;

    public bool IsSelecting => _activeSelector is { IsVisible: true };

    public void Cancel()
    {
        var selector = _activeSelector;
        if (selector is null)
        {
            return;
        }

        selector.Dispatcher.Invoke(() => CloseSelector(selector));
    }

    public Task<CaptureRegion?> SelectRegionAsync(string? hint = null)
    {
        if (IsSelecting)
        {
            Cancel();
            return Task.FromResult<CaptureRegion?>(null);
        }

        var window = new RegionSelectorWindow
        {
            Hint = string.IsNullOrWhiteSpace(hint)
                ? "Drag around the dialogue — F9 or Esc to close"
                : hint
        };
        _activeSelector = window;
        try
        {
            var accepted = window.ShowDialog() == true;
            var region = accepted ? window.SelectedRegion : null;
            return Task.FromResult(region is null ? null : ScreenCoordinates.ClampToVirtualScreen(region));
        }
        finally
        {
            if (ReferenceEquals(_activeSelector, window))
            {
                _activeSelector = null;
            }
        }
    }

    private static void CloseSelector(Window selector)
    {
        if (!selector.IsVisible)
        {
            return;
        }

        try
        {
            selector.DialogResult = false;
        }
        catch (InvalidOperationException)
        {
            selector.Close();
        }
    }
}
