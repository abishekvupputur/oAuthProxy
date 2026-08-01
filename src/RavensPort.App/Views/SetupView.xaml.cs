using System.Windows.Controls;
using RavensPort.App.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace RavensPort.App.Views;

public partial class SetupView : UserControl
{
    public SetupView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Pushes the pasted session key into the view model.
    ///
    /// Code-behind because <c>PasswordBox.Password</c> is not a DependencyProperty and cannot be
    /// bound — the same reason CredentialsView does this. The sender is read rather than a named
    /// field: this box lives inside a DataTemplate, where x:Name generates nothing to reference.
    /// </summary>
    private void SessionKeyBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is PasswordBox box && DataContext is SetupViewModel vm)
        {
            vm.SessionKeyInput = box.Password;
        }
    }
}
