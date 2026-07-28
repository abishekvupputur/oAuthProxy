using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using OAuthProxy.App.ViewModels;

namespace OAuthProxy.App.Views;

public partial class CredentialsView : UserControl
{
    public CredentialsView()
    {
        InitializeComponent();
    }

    private void ClientSecretBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is CredentialsViewModel vm)
        {
            vm.NewClientSecret = ClientSecretBox.Password;
        }
    }
}
