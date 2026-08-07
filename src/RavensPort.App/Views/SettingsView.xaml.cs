using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using RavensPort.App.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace RavensPort.App.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _viewModel;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// The half of the certificate-password field that XAML cannot express. PasswordBox.Password is
    /// a plain CLR property, deliberately: making it bindable would leave the password sitting in
    /// the binding engine's caches. So the value is pushed across by hand instead.
    /// </summary>
    private void OnNewCertificatePasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.NewCertificatePassword = NewCertificatePasswordBox.Password;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as SettingsViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    /// <summary>
    /// The other direction, and the reason this is not a one-line handler. The view model clears the
    /// password once the certificate is written, and a PasswordBox owns its own text: without this,
    /// the box would still be holding the last password typed into it — masked, but recoverable
    /// from the control — for as long as the window stays open.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsViewModel.NewCertificatePassword) || _viewModel is null)
        {
            return;
        }

        // Guarded, because assigning Password raises PasswordChanged, which writes back here. Equal
        // values are already in step and the assignment would only restart the loop.
        if (NewCertificatePasswordBox.Password != _viewModel.NewCertificatePassword)
        {
            NewCertificatePasswordBox.Password = _viewModel.NewCertificatePassword;
        }
    }
}
