using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ContainerToDrive.Core;
using CoreValidation = ContainerToDrive.Core.Validation;

namespace ContainerToDrive.Desktop;

public partial class ConnectionWindow : Window
{
    private const long GiB = 1024L * 1024 * 1024;
    private readonly ControllerSession _session;
    private readonly AzureSignInService _azure;
    private readonly AppSnapshot _snapshot;
    private readonly Profile? _existing;
    private readonly Profile _defaults;
    private readonly Guid _id;
    private CancellationTokenSource? _requestCancellation;
    private CancellationTokenSource? _discoveryCancellation;
    private bool _busy;
    private bool _discoveryBusy;
    private bool _updatingAzureSelections;
    private bool _lettersLoaded;
    private bool _closed;
    private bool _showValidation;
    private int _step = 1;

    internal ConnectionWindow(ControllerSession session, AzureSignInService azure, AppSnapshot snapshot, Profile? existing, bool elevated, DesktopPreferences? preferences = null)
    {
        _session = session;
        _azure = azure;
        _snapshot = snapshot;
        _existing = existing;
        _id = existing?.Id ?? Guid.NewGuid();
        _defaults = (preferences ?? new DesktopPreferences()).NewProfile(_id);
        InitializeComponent();
        foreach (var picker in SearchPickers) picker.SearchStateChanged += OnSearchStateChanged;
        Title = existing is null ? "Add connection" : "Edit connection";
        Heading.Text = Title;
        NameBox.Text = existing?.Name ?? "";
        PrefixBox.Text = existing?.Prefix ?? "";
        CacheBox.Text = ((existing?.CacheMaxBytes ?? _defaults.CacheMaxBytes) / (decimal)GiB).ToString("0.##", CultureInfo.CurrentCulture);
        ReadOnlyCheck.IsEnabled = snapshot.WritableEnabled;
        ReadOnlyCheck.IsChecked = snapshot.WritableEnabled ? existing?.ReadOnly ?? ConnectionFormRules.NewProfileReadOnly : true;
        AutoMountCheck.IsChecked = !elevated && (existing?.AutoMount ?? ConnectionFormRules.NewProfileAutoMount);
        AutoMountCheck.IsEnabled = !elevated;
        CacheHint.Text = $"Free-space reserve: {DisplayText.Bytes(existing?.MinFreeBytes ?? _defaults.MinFreeBytes)}. " +
            "The controller owns the cache location and validates local storage before mounting.";
        ModeHint.Text = snapshot.WritableEnabled
            ? "Read-only by default. Clear this option only when this connection should modify Azure."
            : "Read-only developer preview. Writes are unavailable until filesystem and recovery validation passes.";
        WritableWarning.Visibility = ReadOnlyCheck.IsChecked == false ? Visibility.Visible : Visibility.Collapsed;

        if (existing is not null)
        {
            SourcePreview.Text = "Saved source: " + DisplayText.Resource(existing);
            SasHint.Text = "Leave blank to retain the saved SAS. A replacement must address the same container.";
            AccountNameBox.Text = CoreValidation.AccountName(existing);
            KeyContainerBox.Text = existing.Container;
            switch (existing.AuthenticationKind)
            {
                case AuthenticationKind.AccountKey: AccountKeyMethod.IsChecked = true; break;
                case AuthenticationKind.MicrosoftEntra: EntraMethod.IsChecked = true; break;
                default: SasMethod.IsChecked = true; break;
            }
        }
        ApplyAuthenticationPanel();
        ShowStep(1, focus: false);
        AzureAccountText.Text = _azure.AccountLabel;
        AzureSignOutButton.IsEnabled = _azure.IsSignedIn;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CancelOrClose(); } };
    }

    private AuthenticationKind SelectedKind => AccountKeyMethod.IsChecked == true
        ? AuthenticationKind.AccountKey
        : EntraMethod.IsChecked == true ? AuthenticationKind.MicrosoftEntra : AuthenticationKind.ContainerSas;

    private SearchComboBox[] SearchPickers => [SubscriptionBox, ResourceGroupBox, StorageAccountBox, EntraContainerBox, KeyContainerBox];
    private bool SearchBusy => SearchPickers.Any(picker => picker.IsLoading);
    private void OnSearchStateChanged(object? sender, EventArgs e) { if (!_closed) SetActionButtons(); }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var occupied = await Task.Run(() => DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet());
            if (_closed) return;
            foreach (var profile in _snapshot.Profiles.Where(p => p.Id != _id))
                if (profile.DriveLetter.Length == 1) occupied.Add(char.ToUpperInvariant(profile.DriveLetter[0]));
            var letters = Enumerable.Range('D', 'Z' - 'D' + 1).Select(n => (char)n)
                .Where(letter => !occupied.Contains(letter)).Reverse().Select(letter => letter.ToString()).ToList();
            DriveBox.ItemsSource = letters;
            DriveBox.SelectedItem = _existing is null ? letters.FirstOrDefault()
                : letters.Contains(_existing.DriveLetter) ? _existing.DriveLetter : null;
            _lettersLoaded = letters.Count > 0;
            DriveHint.Text = letters.Count == 0 ? "No free letters from D–Z. Free a letter, then reopen this dialog."
                : _existing is not null && !letters.Contains(_existing.DriveLetter)
                    ? "The previous letter is unavailable. Explicitly choose a different free letter before saving."
                    : "Only free, unreserved letters D–Z are listed. The controller checks again before mounting.";
            SetBusy(false);
            NameBox.Focus();
            if (_azure.IsSignedIn && SelectedKind == AuthenticationKind.MicrosoftEntra)
                await RefreshSubscriptionsAsync("Refreshing the saved Azure session…", allowInteractive: false);
        }
        catch (Exception)
        {
            if (!_closed) StatusText.Text = "Available drive letters could not be checked. Close this dialog and retry. No profile was saved.";
        }
    }

    private void OnAuthMethodChanged(object sender, RoutedEventArgs e)
    {
        if (SasPanel is null) return;
        if (TestButton is not null) CancelDiscovery();
        SasBox.Clear();
        AccountKeyBox.Clear();
        ApplyAuthenticationPanel();
        SetActionButtons();
    }

    private void ApplyAuthenticationPanel()
    {
        SasPanel.Visibility = SelectedKind == AuthenticationKind.ContainerSas ? Visibility.Visible : Visibility.Collapsed;
        AccountKeyPanel.Visibility = SelectedKind == AuthenticationKind.AccountKey ? Visibility.Visible : Visibility.Collapsed;
        EntraPanel.Visibility = SelectedKind == AuthenticationKind.MicrosoftEntra ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (WritableWarning is not null)
            WritableWarning.Visibility = ReadOnlyCheck.IsChecked == false ? Visibility.Visible : Visibility.Collapsed;
        if (TestButton is not null) OnFormChanged(sender, e);
    }

    private void OnFormSelectionChanged(object sender, SelectionChangedEventArgs e) => OnFormChanged(sender, e);

    private void OnFormKeyUp(object sender, KeyEventArgs e) => OnFormChanged(sender, e);

    private void OnFormChanged(object sender, RoutedEventArgs e)
    {
        if (TestButton is null) return;
        if (ReferenceEquals(sender, AccountNameBox))
        {
            KeyContainerBox.SetSource(null);
            try
            {
                _ = CoreValidation.ValidateAccountName(AccountNameBox.Text.Trim());
                KeyContainerBox.IsEnabled = true;
                KeyContainerBox.Placeholder = "Container name";
            }
            catch (ArgumentException) { KeyContainerBox.Placeholder = "Enter a storage account first"; }
        }
        if (_showValidation) ValidateStep(_step, showErrors: true, focusInvalid: false);
        if (_step == 3) UpdateReview();
        SetActionButtons();
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_busy || _discoveryBusy || SearchBusy || _step <= 1) return;
        ShowStep(_step - 1);
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_busy || _discoveryBusy || SearchBusy || _step >= 3) return;
        _showValidation = true;
        if (!ValidateStep(_step, showErrors: true, focusInvalid: true))
        {
            StatusText.Text = "Correct the highlighted fields before continuing.";
            SetActionButtons();
            return;
        }
        ShowStep(_step + 1);
    }

    private void ShowStep(int step, bool focus = true)
    {
        _step = Math.Clamp(step, 1, 3);
        _showValidation = false;
        if (ConnectionStep is null) return;
        ConnectionStep.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        DriveStep.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        ReviewStep.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        StepText.Text = _step switch
        {
            1 => "Step 1 of 3 · Connection",
            2 => "Step 2 of 3 · Drive settings",
            _ => "Step 3 of 3 · Review, test, and save"
        };
        BackButton.Visibility = _step == 1 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Visibility = _step < 3 ? Visibility.Visible : Visibility.Collapsed;
        TestButton.Visibility = SaveButton.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.IsDefault = _step < 3;
        SaveButton.IsDefault = _step == 3;
        if (_step == 3) UpdateReview();
        FormScroll.ScrollToTop();
        SetActionButtons();
        if (!focus) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_step == 1) NameBox.Focus();
            else if (_step == 2) DriveBox.Focus();
            else TestButton.Focus();
        });
    }

    private bool ValidateStep(int step, bool showErrors, bool focusInvalid)
    {
        return step switch
        {
            1 => ValidateConnectionStep(showErrors, focusInvalid),
            2 => ValidateDriveStep(showErrors, focusInvalid),
            _ => ValidateConnectionStep(showErrors, focusInvalid) && ValidateDriveStep(showErrors, focusInvalid)
        };
    }

    private bool ValidateConnectionStep(bool showErrors, bool focusInvalid)
    {
        var nameValid = ConnectionFormRules.IsNameValid(NameBox.Text);
        var prefixValid = ConnectionFormRules.IsPrefixValid(PrefixBox.Text);
        var authenticationValid = ValidateAuthenticationInput(fullValidation: showErrors);

        if (showErrors)
        {
            SetFieldError(NameError, !nameValid, "Enter a connection name between 1 and 100 characters.");
            SetFieldError(PrefixError, !prefixValid, "Use a relative prefix with forward slashes and no dot segments.");
            SetAuthenticationError(authenticationValid);
        }
        if (focusInvalid)
        {
            if (!nameValid) NameBox.Focus();
            else if (!authenticationValid) FocusAuthenticationInput();
            else if (!prefixValid) PrefixBox.Focus();
        }
        return nameValid && authenticationValid && prefixValid;
    }

    private bool ValidateAuthenticationInput(bool fullValidation)
    {
        var sasPresent = SasBox.SecurePassword.Length != 0;
        var accountKeyPresent = AccountKeyBox.SecurePassword.Length != 0;
        var accountIdentityValid = false;
        try
        {
            if (SelectedKind == AuthenticationKind.AccountKey)
            {
                _ = CoreValidation.ValidateAccountName(AccountNameBox.Text.Trim());
                _ = CoreValidation.ValidateContainerName(KeyContainerBox.Text.Trim());
                accountIdentityValid = true;
            }

            var ready = ConnectionFormRules.IsAuthenticationReady(SelectedKind,
                _existing?.AuthenticationKind == SelectedKind, sasPresent, accountIdentityValid, accountKeyPresent,
                SubscriptionBox.SelectedItem is AzureSubscriptionChoice subscription &&
                StorageAccountBox.SelectedItem is AzureStorageAccountChoice account &&
                AzureSignInService.IsAccountInScope(subscription.Id, (ResourceGroupBox.SelectedItem as AzureResourceGroupChoice)?.Id, account) &&
                EntraContainerBox.SelectedItem is string);
            if (!ready || !fullValidation) return ready;
            if (SelectedKind == AuthenticationKind.ContainerSas && sasPresent) _ = CoreValidation.ParseSas(SasBox.Password);
            if (SelectedKind == AuthenticationKind.AccountKey && accountKeyPresent) CoreValidation.ValidateAccountKey(AccountKeyBox.Password);
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    private bool ValidateDriveStep(bool showErrors, bool focusInvalid)
    {
        var driveValid = _lettersLoaded && DriveBox.SelectedItem is string;
        var cacheValid = ConnectionFormRules.IsCacheValid(CacheBox.Text, CultureInfo.CurrentCulture);
        var consentValid = ReadOnlyCheck.IsChecked == true || WritableConsent.IsChecked == true;
        if (showErrors)
        {
            SetFieldError(DriveError, !driveValid, "Choose a currently available drive letter.");
            SetFieldError(CacheError, !cacheValid, "Enter a cache target from 0.25 through 1024 GiB.");
            SetFieldError(WritableError, !consentValid, "Accept the writable-connection risks, or select read-only.");
        }
        if (focusInvalid)
        {
            if (!driveValid) DriveBox.Focus();
            else if (!cacheValid) CacheBox.Focus();
            else if (!consentValid) WritableConsent.Focus();
        }
        return ConnectionFormRules.IsDriveStepReady(_lettersLoaded, driveValid, cacheValid,
            ReadOnlyCheck.IsChecked == true, WritableConsent.IsChecked == true);
    }

    private void SetAuthenticationError(bool valid)
    {
        SetFieldError(SasError, SelectedKind == AuthenticationKind.ContainerSas && !valid, "Enter a valid, unexpired HTTPS container SAS URL.");
        SetFieldError(AccountError, SelectedKind == AuthenticationKind.AccountKey && !valid, "Check the lowercase account name, blob container, and account key.");
        SetFieldError(EntraError, SelectedKind == AuthenticationKind.MicrosoftEntra && !valid, "Sign in and select a subscription, storage account, and container. Resource group is an optional filter.");
    }

    private void FocusAuthenticationInput()
    {
        if (SelectedKind == AuthenticationKind.ContainerSas) SasBox.Focus();
        else if (SelectedKind == AuthenticationKind.AccountKey) AccountNameBox.Focus();
        else AzureSignInButton.Focus();
    }

    private static void SetFieldError(TextBlock element, bool visible, string message)
    {
        element.Text = visible ? message : "";
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateReview()
    {
        var source = SelectedKind switch
        {
            AuthenticationKind.AccountKey => AccountNameBox.Text.Trim() + ".blob.core.windows.net / " + KeyContainerBox.Text.Trim(),
            AuthenticationKind.MicrosoftEntra when StorageAccountBox.SelectedItem is AzureStorageAccountChoice account && EntraContainerBox.SelectedItem is string container => account.Name + ".blob.core.windows.net / " + container,
            _ when _existing is not null && SasBox.SecurePassword.Length == 0 => DisplayText.Resource(_existing),
            _ => "Container SAS URL supplied · secret not shown"
        };
        ReviewSource.Text = NameBox.Text.Trim() + " · " + source;
        ReviewDrive.Text = "Drive: " + (DriveBox.SelectedItem as string ?? "not selected") + ":";
        ReviewAccess.Text = ReadOnlyCheck.IsChecked == true ? "Access: Read-only" : "Access: Writable · edits and deletions affect Azure";
        ReviewCache.Text = "Local cache target: " + CacheBox.Text + " GiB · open or dirty files can exceed it";
        ReviewAutoMount.Text = AutoMountCheck.IsChecked == true ? "Startup: Mount automatically" : "Startup: Mount only when requested";
    }

    private async void OnDiscoverKeyContainers(object sender, RoutedEventArgs e)
    {
        if (_busy || _discoveryBusy) return;
        var account = AccountNameBox.Text.Trim();
        string? key = AccountKeyBox.Password;
        AccountKeyBox.Clear();
        try
        {
            CoreValidation.ValidateAccountName(account);
            CoreValidation.ValidateAccountKey(key);
        }
        catch (ArgumentException)
        {
            key = null;
            StatusText.Text = "Enter a valid lowercase storage account name and account key. The key field was cleared.";
            return;
        }

        var cancellation = BeginDiscovery();
        CredentialSubmission? credential = CredentialSubmission.AccountKeyCredential(key);
        key = null;
        var containers = new List<string>();
        string? continuation = null;
        StatusText.Text = "Loading blob containers with the local controller…";
        try
        {
            for (var page = 0; page < 20; page++)
            {
                var response = await _session.SendAsync(new Request
                {
                    Operation = "DiscoverContainers",
                    StorageAccountName = account,
                    Credential = credential,
                    ContinuationToken = continuation,
                    PageSize = 100
                }, cancellation.Token);
                if (!response.Success)
                {
                    StatusText.Text = "Azure denied container discovery. The key may be invalid or Shared Key may be disabled.";
                    return;
                }
                containers.AddRange(response.Containers ?? []);
                continuation = response.ContinuationToken;
                if (continuation is null) break;
            }
            if (continuation is not null)
            {
                StatusText.Text = "More than 2,000 containers were returned. Narrow the account or enter the intended container name.";
                return;
            }
            if (_closed || !string.Equals(account, AccountNameBox.Text.Trim(), StringComparison.Ordinal)) return;
            var choices = containers.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToList();
            KeyContainerBox.SetSource(AsyncSearch.Cached<string>(_ => Task.FromResult<IReadOnlyList<string>>(choices)));
            await KeyContainerBox.RefreshAsync();
            KeyContainerBox.IsDropDownOpen = containers.Count != 0;
            StatusText.Text = containers.Count == 0
                ? "No containers were returned. The account key field was cleared."
                : $"Loaded {containers.Count} containers. Select one, then re-enter the key before Test & save.";
        }
        catch (OperationCanceledException) { if (!_closed) StatusText.Text = "Container discovery was cancelled."; }
        catch (Exception) { if (!_closed) StatusText.Text = "Container discovery failed. Check the account, key, network, firewall, and Shared Key policy."; }
        finally
        {
            credential = null;
            EndDiscovery(cancellation);
        }
    }

    private async void OnAzureSignIn(object sender, RoutedEventArgs e)
    {
        if (_busy || _discoveryBusy) return;
        ClearAzureSelections();
        var cancellation = BeginDiscovery();
        StatusText.Text = "Opening Microsoft sign-in in your browser…";
        try
        {
            await _azure.SignInAsync(cancellation.Token);
            if (_closed) return;
            AzureAccountText.Text = _azure.AccountLabel;
            AzureSignOutButton.IsEnabled = true;
            await LoadSubscriptionsAsync(cancellation.Token);
        }
        catch (OperationCanceledException) { if (!_closed) StatusText.Text = "Azure sign-in was cancelled."; }
        catch (Exception) { if (!_closed) StatusText.Text = "Azure sign-in failed or tenant consent was denied. No profile was saved."; }
        finally { EndDiscovery(cancellation); }
    }

    private async void OnRefreshSubscriptions(object sender, RoutedEventArgs e) =>
        await RefreshSubscriptionsAsync("Refreshing subscriptions across accessible tenants…", allowInteractive: true);

    private async Task RefreshSubscriptionsAsync(string message, bool allowInteractive)
    {
        if (_busy || _discoveryBusy || !_azure.IsSignedIn) return;
        ClearAzureSelections();
        var cancellation = BeginDiscovery();
        StatusText.Text = message;
        try
        {
            await _azure.EnsureArmAuthorizationAsync(allowInteractive, cancellation.Token);
            await LoadSubscriptionsAsync(cancellation.Token);
        }
        catch (OperationCanceledException) { if (!_closed) StatusText.Text = "Subscription refresh was cancelled."; }
        catch (Exception) { if (!_closed) StatusText.Text = "Subscriptions could not be refreshed. Sign in again or check tenant consent and Reader access."; }
        finally { EndDiscovery(cancellation); }
    }

    private async Task LoadSubscriptionsAsync(CancellationToken token)
    {
        if (_closed) return;
        ClearAzureSelections();
        SubscriptionBox.SetSource(AsyncSearch.Cached(_azure.GetSubscriptionsAsync));
        await SubscriptionBox.RefreshAsync();
        token.ThrowIfCancellationRequested();
        if (_closed) return;
        AzureAccountText.Text = _azure.AccountLabel;
        AzureSignOutButton.IsEnabled = true;
        StatusText.Text = SubscriptionBox.HasSearchError ? "Subscriptions could not be refreshed. Sign in again or check tenant consent and Reader access."
            : SubscriptionBox.Items.Count == 0 ? "No visible subscriptions were found for this Azure account." : "";
    }

    private void OnAzureSignOut(object sender, RoutedEventArgs e)
    {
        CancelDiscovery();
        _azure.SignOut();
        AzureAccountText.Text = _azure.AccountLabel;
        AzureSignOutButton.IsEnabled = false;
        ClearAzureSelections();
        StatusText.Text = "ContainerToDrive forgot this in-memory Azure session. Previously issued SAS credentials are not remotely revoked.";
    }

    private async void OnSubscriptionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingAzureSelections) await LoadResourceGroupsAsync();
    }
    private async void OnRefreshResourceGroups(object sender, RoutedEventArgs e) => await LoadResourceGroupsAsync();

    private async Task LoadResourceGroupsAsync()
    {
        ResetAzureDescendants(1);
        if (SubscriptionBox.SelectedItem is not AzureSubscriptionChoice subscription || _busy || _closed) return;
        ResourceGroupBox.SetSource(AsyncSearch.Cached(token => _azure.GetResourceGroupsAsync(subscription.Id, token)));
        await Task.WhenAll(ResourceGroupBox.RefreshAsync(), LoadStorageAccountsAsync());
    }

    private async void OnResourceGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingAzureSelections) await LoadStorageAccountsAsync();
    }
    private async void OnClearResourceGroup(object sender, RoutedEventArgs e)
    {
        _updatingAzureSelections = true;
        try { ResourceGroupBox.ClearSelection(); }
        finally { _updatingAzureSelections = false; }
        await LoadStorageAccountsAsync();
    }
    private async void OnRefreshStorageAccounts(object sender, RoutedEventArgs e) => await LoadStorageAccountsAsync();

    private async Task LoadStorageAccountsAsync()
    {
        ResetAzureDescendants(2);
        if (SubscriptionBox.SelectedItem is not AzureSubscriptionChoice subscription || _busy || _closed) return;
        var group = ResourceGroupBox.SelectedItem as AzureResourceGroupChoice;
        StorageAccountBox.Placeholder = group is null ? "Search this subscription" : "Search this resource group";
        StorageAccountBox.SetSource(AsyncSearch.Cached(token => _azure.GetStorageAccountsAsync(subscription.Id, group?.Id, token)));
        await StorageAccountBox.RefreshAsync();
    }

    private async void OnStorageAccountChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingAzureSelections) await LoadContainersAsync();
    }
    private async void OnRefreshContainers(object sender, RoutedEventArgs e) => await LoadContainersAsync();

    private async Task LoadContainersAsync()
    {
        ResetAzureDescendants(3);
        if (_busy || _closed || SubscriptionBox.SelectedItem is not AzureSubscriptionChoice subscription ||
            StorageAccountBox.SelectedItem is not AzureStorageAccountChoice account ||
            !AzureSignInService.IsAccountInScope(subscription.Id, (ResourceGroupBox.SelectedItem as AzureResourceGroupChoice)?.Id, account)) return;
        EntraContainerBox.Placeholder = "Search " + account.Name;
        EntraContainerBox.SetSource(AsyncSearch.Cached(token => _azure.GetContainersAsync(account.Name, token)));
        await EntraContainerBox.RefreshAsync();
    }

    private async Task<(Profile Profile, CredentialSubmission? Credential)?> BuildSubmissionAsync(CancellationToken token)
    {
        if (DriveBox.SelectedItem is not string letter)
        {
            StatusText.Text = "Choose a currently available drive letter.";
            return null;
        }
        if (!decimal.TryParse(CacheBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var cache) || cache is < 0.25m or > 1024m)
        {
            StatusText.Text = "Enter a cache target from 0.25 through 1024 GiB.";
            return null;
        }

        var kind = SelectedKind;
        var readOnly = ReadOnlyCheck.IsChecked == true;
        var profile = _existing ?? _defaults;
        CredentialSubmission? credential = null;
        string? secret = null;
        try
        {
            switch (kind)
            {
                case AuthenticationKind.ContainerSas:
                    secret = SasBox.Password;
                    SasBox.Clear();
                    if (secret.Length != 0)
                    {
                        var info = CoreValidation.ParseSas(secret);
                        profile = profile with
                        {
                            Endpoint = info.Endpoint,
                            Container = info.Container,
                            ExpiresAt = info.ExpiresAt,
                            AuthenticationKind = kind,
                            TenantId = "",
                            SubscriptionId = "",
                            ResourceGroupName = ""
                        };
                        credential = CredentialSubmission.ContainerSas(secret, _existing?.CredentialRevision ?? 0);
                    }
                    else if (_existing is null || _existing.AuthenticationKind != kind)
                        throw new ArgumentException("Paste a container SAS URL.");
                    break;

                case AuthenticationKind.AccountKey:
                    var accountName = CoreValidation.ValidateAccountName(AccountNameBox.Text.Trim());
                    var keyContainer = CoreValidation.ValidateContainerName(KeyContainerBox.Text.Trim());
                    secret = AccountKeyBox.Password;
                    AccountKeyBox.Clear();
                    profile = profile with
                    {
                        Endpoint = CoreValidation.EndpointForAccount(accountName),
                        Container = keyContainer,
                        ExpiresAt = null,
                        AuthenticationKind = kind,
                        TenantId = "",
                        SubscriptionId = "",
                        ResourceGroupName = ""
                    };
                    if (secret.Length != 0)
                    {
                        CoreValidation.ValidateAccountKey(secret);
                        credential = CredentialSubmission.AccountKeyCredential(secret, _existing?.CredentialRevision ?? 0);
                    }
                    else if (_existing is null || _existing.AuthenticationKind != kind)
                        throw new ArgumentException("Enter the storage account key.");
                    break;

                case AuthenticationKind.MicrosoftEntra:
                    if (SubscriptionBox.SelectedItem is AzureSubscriptionChoice subscription &&
                        StorageAccountBox.SelectedItem is AzureStorageAccountChoice account &&
                        EntraContainerBox.SelectedItem is string container &&
                        AzureSignInService.IsAccountInScope(subscription.Id, (ResourceGroupBox.SelectedItem as AzureResourceGroupChoice)?.Id, account))
                    {
                        profile = profile with
                        {
                            Endpoint = CoreValidation.EndpointForAccount(account.Name),
                            Container = container,
                            Prefix = PrefixBox.Text
                        };
                        if (!ValidateSavedSource(profile)) return null;
                        StatusText.Text = readOnly
                            ? "Requesting a one-day read/list user delegation credential…"
                            : "Requesting a one-day writable user delegation credential…";
                        secret = await _azure.CreateDelegationSasAsync(account.Name, container, readOnly, token);
                        var info = CoreValidation.ParseSas(secret);
                        profile = profile with
                        {
                            Endpoint = info.Endpoint,
                            Container = info.Container,
                            ExpiresAt = info.ExpiresAt,
                            AuthenticationKind = kind,
                            TenantId = string.IsNullOrWhiteSpace(subscription.TenantId) ? _azure.TenantId : subscription.TenantId,
                            SubscriptionId = subscription.Id,
                            ResourceGroupName = account.ResourceGroupName
                        };
                        credential = CredentialSubmission.MicrosoftEntraSas(secret, _existing?.CredentialRevision ?? 0);
                    }
                    else if (_existing is null || _existing.AuthenticationKind != kind)
                        throw new ArgumentException("Sign in and select a subscription, storage account, and blob container.");
                    break;
            }

            profile = profile with
            {
                Name = NameBox.Text.Trim(),
                Prefix = PrefixBox.Text,
                DriveLetter = letter,
                ReadOnly = readOnly,
                AutoMount = AutoMountCheck.IsChecked == true,
                CacheMaxBytes = (long)(cache * GiB)
            };
            profile = CoreValidation.ValidateProfile(profile);
            if (!profile.ReadOnly && !_snapshot.WritableEnabled)
                throw new ArgumentException("The controller has not enabled writable connections.");
            if (!ValidateSavedSource(profile)) return null;
            return (profile, credential);
        }
        catch (ArgumentException)
        {
            credential = null;
            StatusText.Text = kind switch
            {
                AuthenticationKind.AccountKey => "Check the lowercase storage account name, selected container, and account key. Secret fields were cleared.",
                AuthenticationKind.MicrosoftEntra => "Complete Azure sign-in and select an accessible subscription, storage account, and blob container.",
                _ => "Use a valid, unexpired HTTPS container SAS URL. Writable mode also requires create, write, and delete permissions. Secret fields were cleared."
            };
            return null;
        }
        finally { secret = null; }
    }

    private bool ValidateSavedSource(Profile profile)
    {
        if (_existing is null || CoreValidation.Identity(profile) == CoreValidation.Identity(_existing)) return true;
        StatusText.Text = "This connection's storage account, container, and blob prefix cannot be changed, including on a cloned connection. Use Add connection for a different source, or restore the saved source. No changes were saved.";
        return false;
    }

    private async void OnTest(object sender, RoutedEventArgs e) => await SubmitAsync(save: false);
    private async void OnSave(object sender, RoutedEventArgs e) => await SubmitAsync(save: true);

    private async Task SubmitAsync(bool save)
    {
        if (_busy || _discoveryBusy || SearchBusy || !_lettersLoaded) return;
        if (save && ReadOnlyCheck.IsChecked == false && WritableConsent.IsChecked != true)
        {
            StatusText.Text = "Accept the writable-connection risks, or select read-only.";
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _requestCancellation = cancellation;
        CredentialSubmission? credential = null;
        Request request = new();
        var saved = false;
        SetBusy(true);
        try
        {
            var submission = await BuildSubmissionAsync(cancellation.Token);
            if (submission is null) return;
            var candidate = submission.Value.Profile;
            credential = submission.Value.Credential;
            SourcePreview.Text = "Resource: " + DisplayText.Resource(candidate) + " · Authentication: " + DisplayText.Authentication(candidate) +
                " · Expiry: " + (candidate.ExpiresAt is null ? "not applicable or unknown" : DisplayText.Time(candidate.ExpiresAt));
            request = credential is null && _existing is not null
                ? new Request { Operation = "Validate", ProfileId = _existing.Id }
                : new Request { Operation = "Validate", Profile = candidate, Credential = credential };
            StatusText.Text = "Testing access with the controller. This does not test writing or mount a drive…";
            var test = await _session.SendAsync(request, cancellation.Token);
            if (!test.Success) { StatusText.Text = DisplayText.Failure(request.Operation, request.RequestId); return; }
            if (!save)
            {
                StatusText.Text = "Connection test succeeded; write access was not certified. Secret fields were cleared.";
                return;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            StatusText.Text = "Test succeeded. Saving the profile and protected credential through the controller…";
            request = new Request { Operation = "Save", Profile = candidate, Credential = credential };
            var response = await _session.SendAsync(request, cancellation.Token);
            if (!response.Success) { StatusText.Text = DisplayText.Failure(request.Operation, request.RequestId); return; }
            saved = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "The request was cancelled or timed out; refresh before retrying. Secret fields were cleared.";
        }
        catch (Exception) { StatusText.Text = DisplayText.Failure(request.Operation, request.RequestId); }
        finally
        {
            request = new Request();
            credential = null;
            SasBox.Clear();
            AccountKeyBox.Clear();
            _requestCancellation = null;
            SetBusy(false);
        }
        if (saved) DialogResult = true;
    }

    private CancellationTokenSource BeginDiscovery()
    {
        CancelDiscovery();
        var cancellation = new CancellationTokenSource();
        _discoveryCancellation = cancellation;
        _discoveryBusy = true;
        Progress.Visibility = Visibility.Visible;
        SetActionButtons();
        return cancellation;
    }

    private void EndDiscovery(CancellationTokenSource cancellation)
    {
        if (!ReferenceEquals(_discoveryCancellation, cancellation)) return;
        _discoveryCancellation = null;
        cancellation.Dispose();
        _discoveryBusy = false;
        Progress.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        SetActionButtons();
    }

    private void CancelDiscovery()
    {
        foreach (var picker in SearchPickers) picker.CancelSearch();
        var cancellation = _discoveryCancellation;
        _discoveryCancellation = null;
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
        _discoveryBusy = false;
        Progress.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        SetActionButtons();
    }

    private void ClearAzureSelections()
    {
        ResetAzureDescendants(0);
    }

    private void ResetAzureDescendants(int level)
    {
        _updatingAzureSelections = true;
        try
        {
            if (level == 0) SubscriptionBox.SetSource(null);
            if (level <= 1) ResourceGroupBox.SetSource(null);
            if (level <= 2) { StorageAccountBox.SetSource(null); StorageAccountBox.Placeholder = "Select a subscription first"; }
            EntraContainerBox.SetSource(null);
            EntraContainerBox.Placeholder = "Select a storage account first";
        }
        finally { _updatingAzureSelections = false; }
        SetActionButtons();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Fields.IsEnabled = !busy;
        Progress.Visibility = busy || _discoveryBusy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Content = busy || _discoveryBusy ? "_Cancel request" : "_Cancel";
        SetActionButtons();
    }

    private void SetActionButtons()
    {
        if (TestButton is null) return;
        var searching = SearchBusy;
        var ready = !_busy && !_discoveryBusy && !searching;
        Progress.Visibility = _busy || _discoveryBusy || searching ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Content = _busy || _discoveryBusy || searching ? "_Cancel request" : "_Cancel";
        BackButton.IsEnabled = ready && _step > 1;
        NextButton.IsEnabled = ready && _lettersLoaded && _step < 3 && ValidateStep(_step, showErrors: false, focusInvalid: false);
        TestButton.IsEnabled = SaveButton.IsEnabled = ready && _lettersLoaded && ValidateStep(3, showErrors: false, focusInvalid: false);
        DiscoverKeyContainersButton.IsEnabled = AzureSignInButton.IsEnabled = ready;
        AzureRefreshButton.IsEnabled = AzureSignOutButton.IsEnabled = ready && _azure.IsSignedIn;
        RefreshResourceGroupsButton.IsEnabled = !_busy && !_discoveryBusy && !ResourceGroupBox.IsLoading && SubscriptionBox.SelectedItem is AzureSubscriptionChoice;
        RefreshStorageAccountsButton.IsEnabled = !_busy && !_discoveryBusy && !StorageAccountBox.IsLoading && SubscriptionBox.SelectedItem is AzureSubscriptionChoice;
        RefreshContainersButton.IsEnabled = !_busy && !_discoveryBusy && !EntraContainerBox.IsLoading && StorageAccountBox.SelectedItem is AzureStorageAccountChoice;
        ClearResourceGroupButton.IsEnabled = !_busy && !_discoveryBusy && SubscriptionBox.SelectedItem is AzureSubscriptionChoice;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => CancelOrClose();

    private void CancelOrClose()
    {
        SasBox.Clear();
        AccountKeyBox.Clear();
        if (_busy) _requestCancellation?.Cancel();
        else if (_discoveryBusy || SearchBusy) CancelDiscovery();
        else Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SasBox.Clear();
        AccountKeyBox.Clear();
        if (_busy) { e.Cancel = true; _requestCancellation?.Cancel(); }
        else CancelDiscovery();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        SasBox.Clear();
        AccountKeyBox.Clear();
        CancelDiscovery();
        foreach (var picker in SearchPickers)
        {
            picker.SearchStateChanged -= OnSearchStateChanged;
            picker.Dispose();
        }
    }
}
