using System.Windows;
using AssetManager.Desktop.Localization;

namespace AssetManager.Desktop;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        await LocalizationManager.InitializeAsync(new UiSettingsStore());
        base.OnStartup(e);
        DesktopBootstrapper.CreateMainWindow().Show();
    }
}
