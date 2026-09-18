using System.Windows;

namespace ContainerToDrive.Desktop;

public partial class ConfirmationWindow : Window
{
    private ConfirmationWindow(string title, string body, string accept, bool dangerous)
    {
        InitializeComponent();
        Title = title;
        Heading.Text = title;
        Body.Text = body;
        AcceptAction.Content = accept;
        AcceptAction.Style = (Style)FindResource(dangerous ? "DangerButton" : "PrimaryButton");
        Loaded += (_, _) => CancelAction.Focus();
    }

    internal static bool Ask(Window owner, string title, string body, string accept, bool dangerous = false) =>
        new ConfirmationWindow(title, body, accept, dangerous) { Owner = owner }.ShowDialog() == true;

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;
}