using System.Windows;
using System.Windows.Controls;
using CortexTransl.App.Models;
using CortexTransl.App.Services.Settings;

namespace CortexTransl.App.Views;

public partial class SetupWizardWindow : Window
{
    private readonly AppSettingsService _appSettingsService;

    public SetupWizardWindow(AppSettingsService appSettingsService)
    {
        InitializeComponent();
        _appSettingsService = appSettingsService;
    }

    private async void FinishButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = await _appSettingsService.LoadAsync();
        
        if (UsageTypeComboBox.SelectedItem is ComboBoxItem usageItem)
        {
            settings.UsageType = usageItem.Tag?.ToString() ?? "Game";
        }

        if (ProviderComboBox.SelectedItem is ComboBoxItem providerItem)
        {
            settings.Provider = providerItem.Tag?.ToString() ?? "placeholder";
        }

        settings.HasRunSetupWizard = true;
        await _appSettingsService.SaveAsync(settings);

        DialogResult = true;
        Close();
    }
}
