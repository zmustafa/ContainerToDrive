using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ContainerToDrive.Core;

namespace ContainerToDrive.Desktop;

public partial class CredentialWindow : Window
{
    private readonly ControllerSession _session;
    private readonly AzureSignInService _azure;
    private readonly Profile _profile;
    private CancellationTokenSource? _requestCancellation;
    private bool _busy;

    internal CredentialWindow(ControllerSession session, AzureSignInService azure, Profile profile)
    {
        _session = session;
        _azure = azure;
        _profile = profile;
        InitializeComponent();
        SourceText.Text = DisplayText.Resource(profile) + " · " + DisplayText.Authentication(profile);
        if (profile.AuthenticationKind == AuthenticationKind.MicrosoftEntra)
        {
            SecretPanel.Visibility = Visibility.Collapsed;
            EntraRenewalPanel.Visibility = Visibility.Visible;
            RenewalWarning.Text = "Renewal signs in to the same tenant and requests a new delegation credential. It does not remotely revoke the old SAS or clear cached files.";
            RenewButton.Content = "_Sign in and renew";
        }
        else
        {
            SecretLabel.Content = profile.AuthenticationKind == AuthenticationKind.AccountKey
                ? "Replacement account _key"
                : "Replacement container _SAS URL";
            RenewalWarning.Text = profile.AuthenticationKind == AuthenticationKind.AccountKey
                ? "Account keys grant broad storage-account access. Rotate only to a key for this same account; Shared Key may be disabled by policy."
                : "Use a replacement SAS for this same container. Renewal does not clear the cache or resolve uncertain uploads by itself.";
            Loaded += (_, _) => SecretBox.Focus();
        }
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CancelOrClose(); } };
    }

    private async void OnRenew(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        using var cancellation = new CancellationTokenSource();
        _requestCancellation = cancellation;
        CredentialSubmission? credential = null;
        string? secret = null;
        var request = new Request();
        var renewed = false;
        SetBusy(true);
        try
        {
            switch (_profile.AuthenticationKind)
            {
                case AuthenticationKind.ContainerSas:
                    secret = SecretBox.Password;
                    SecretBox.Clear();
                    var sas = Validation.ParseSas(secret);
                    if (!string.Equals(sas.Endpoint, _profile.Endpoint, StringComparison.OrdinalIgnoreCase) || sas.Container != _profile.Container)
                        throw new ArgumentException("The replacement target differs.");
                    credential = CredentialSubmission.ContainerSas(secret, _profile.CredentialRevision);
                    break;

                case AuthenticationKind.AccountKey:
                    secret = SecretBox.Password;
                    SecretBox.Clear();
                    Validation.ValidateAccountKey(secret);
                    credential = CredentialSubmission.AccountKeyCredential(secret, _profile.CredentialRevision);
                    break;

                case AuthenticationKind.MicrosoftEntra:
                    StatusText.Text = "Opening Microsoft sign-in…";
                    await _azure.SignInAsync(cancellation.Token, _profile.TenantId);
                    if (!string.Equals(_azure.TenantId, _profile.TenantId, StringComparison.OrdinalIgnoreCase))
                        throw new UnauthorizedAccessException("Sign in to the tenant saved with this profile.");
                    StatusText.Text = _profile.ReadOnly
                        ? "Requesting a new read/list user delegation credential…"
                        : "Requesting a new writable user delegation credential…";
                    secret = await _azure.CreateDelegationSasAsync(
                        Validation.AccountName(_profile), _profile.Container, _profile.ReadOnly, cancellation.Token);
                    credential = CredentialSubmission.MicrosoftEntraSas(secret, _profile.CredentialRevision);
                    break;

                default:
                    throw new ArgumentException("Unsupported authentication method.");
            }

            request = new Request { Operation = "Renew", ProfileId = _profile.Id, Credential = credential };
            StatusText.Text = "Asking the controller to validate and renew access…";
            var response = await _session.SendAsync(request, cancellation.Token);
            if (!response.Success) StatusText.Text = DisplayText.Failure(request.Operation, request.RequestId);
            else renewed = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Renewal was cancelled or timed out; refresh status before retrying.";
        }
        catch (ArgumentException)
        {
            StatusText.Text = _profile.AuthenticationKind == AuthenticationKind.AccountKey
                ? "Use a valid replacement key for this same storage account. The field was cleared."
                : "Use a valid replacement credential for this same container. The field was cleared.";
        }
        catch (Exception)
        {
            StatusText.Text = _profile.AuthenticationKind == AuthenticationKind.MicrosoftEntra
                ? "Azure sign-in or delegation renewal failed. Check tenant consent, delegation-key permission, and the selected mode's Blob Data Reader or Contributor role."
                : DisplayText.Failure(request.Operation, request.RequestId);
        }
        finally
        {
            request = new Request();
            credential = null;
            secret = null;
            SecretBox.Clear();
            _requestCancellation = null;
            SetBusy(false);
        }
        if (renewed) DialogResult = true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Fields.IsEnabled = RenewButton.IsEnabled = !busy;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Content = busy ? "_Cancel request" : "_Cancel";
    }

    private void OnCancel(object sender, RoutedEventArgs e) => CancelOrClose();

    private void CancelOrClose()
    {
        SecretBox.Clear();
        if (_busy) _requestCancellation?.Cancel();
        else Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SecretBox.Clear();
        if (_busy) { e.Cancel = true; _requestCancellation?.Cancel(); }
    }

    private void OnClosed(object? sender, EventArgs e) => SecretBox.Clear();
}
