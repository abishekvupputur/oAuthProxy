using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using OAuthProxy.App.Helpers;
using OAuthProxy.App.ViewModels;

namespace OAuthProxy.App;

public partial class MainWindow : Window
{
    public MainWindow(CredentialsViewModel credentialsViewModel, RoutesViewModel routesViewModel, SettingsViewModel settingsViewModel)
    {
        InitializeComponent();

        CredentialsViewControl.DataContext = credentialsViewModel;
        RoutesViewControl.DataContext = routesViewModel;
        SettingsViewControl.DataContext = settingsViewModel;

        SourceInitialized += (_, _) => WindowHelper.ApplyDarkTitleBar(this);
        Closing += MainWindow_Closing;
    }

    private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Only react to the TabControl itself; ComboBoxes inside the tabs raise the same
        // routed event and would otherwise trigger a reload on every dropdown change.
        if (!ReferenceEquals(e.OriginalSource, sender)) return;

        // The Routes tab shows credentials owned by the Credentials tab, so re-read them
        // on every switch — otherwise a newly added credential is missing from the dropdown.
        if (RoutesViewControl.DataContext is RoutesViewModel routesViewModel)
        {
            routesViewModel.Reload();
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        // Never actually close from the X button — only the tray "Exit" command shuts the app down.
        e.Cancel = true;
        Hide();
    }
}
