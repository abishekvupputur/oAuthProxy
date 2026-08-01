using System.Windows;
using RavensPort.Core.Vault;

// UseWindowsForms is on for the tray icon, so both frameworks' Application types are in scope.
using Application = System.Windows.Application;

namespace RavensPort.App.Views;

/// <summary>
/// The consent step in front of every Windows Hello prompt RavensPort raises. See the comment in
/// the XAML for why it has no exceptions.
/// </summary>
public partial class HelloConsentWindow : Window
{
    private readonly Func<Task> _action;

    /// <summary>True once the Hello-backed operation actually succeeded.</summary>
    public bool Confirmed { get; private set; }

    private HelloConsentWindow(string heading, string body, string detail, string confirmText, Func<Task> action)
    {
        _action = action;

        InitializeComponent();

        HeadingText.Text = heading;
        BodyText.Text = body;
        DetailText.Text = detail;
        ConfirmButton.Content = confirmText;
    }

    /// <summary>
    /// Asks before unlocking the session with Hello.
    /// </summary>
    public static bool RequestUnlock(Func<Task> unlockAsync) => Show(new HelloConsentWindow(
        "Unlock RavensPort",
        "RavensPort wants to ask Windows Hello to unlock its Proton Pass session on this PC, so the "
        + "proxy can start.",
        "Your Windows Hello gesture decrypts a session key held only on this PC. Nothing is sent to "
        + "Proton, nothing is read from your vault by this step, and your Proton password is not involved.",
        "Unlock with Windows Hello",
        unlockAsync));

    /// <summary>
    /// Asks before storing the session key behind Hello. A separate wording because it is a
    /// different act: this one starts keeping something on disk that was not there before, and a
    /// user is entitled to decline it and keep pasting.
    /// </summary>
    public static bool RequestSave(Func<Task> saveAsync) => Show(new HelloConsentWindow(
        "Remember this key with Windows Hello?",
        "RavensPort can store your session key on this PC so that unlocking it later is a Windows "
        + "Hello gesture instead of pasting the key.",
        "The key is encrypted so that only a Windows Hello gesture on this PC can decrypt it — "
        + "RavensPort cannot read it without you. Decline and nothing is written; you will be asked "
        + "to paste the key after each restart, as now. Keep your own copy of the key either way: "
        + "Windows Hello cannot help you on another PC.",
        "Remember with Windows Hello",
        saveAsync));

    private static bool Show(HelloConsentWindow window)
    {
        // Owned by the main window when there is a visible one, so it centres on it and cannot be
        // lost behind it. At startup there is none — which is the case this window exists to cover.
        var owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true } && !ReferenceEquals(owner, window)) window.Owner = owner;

        window.ShowDialog();
        return window.Confirmed;
    }

    private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        ConfirmButton.IsEnabled = false;
        CancelButton.IsEnabled = false;

        Report("Waiting for Windows Hello…", isError: false);

        try
        {
            await _action();

            Confirmed = true;
            Close();
        }
        catch (VaultCliException ex)
        {
            // Cancelled at the Hello prompt, locked out after too many attempts, or a stored key
            // that no longer opens. All of them leave pasting the key as the way through, and the
            // message already says so — so this window stays open to be retried or dismissed.
            Report(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            Report($"Windows Hello failed: {ex.Message}", isError: true);
        }
        finally
        {
            ConfirmButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Report(string message, bool isError)
    {
        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;
        StatusText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("ErrorBrush")
            : (System.Windows.Media.Brush)FindResource("MutedTextBrush");
    }
}
