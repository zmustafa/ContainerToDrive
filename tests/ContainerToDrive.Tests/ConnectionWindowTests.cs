using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using ContainerToDrive.Core;
using ContainerToDrive.Desktop;
using ContainerToDrive.Windows;
using Xunit;

namespace ContainerToDrive.Tests;

[Trait("Category", "Unit")]
public sealed class ConnectionWindowTests
{
    [Fact]
    public void AddAndEditConstructWithRealHandlersForEveryAuthenticationKind()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "ContainerToDrive-dialog-" + Guid.NewGuid().ToString("N"));
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                application.Resources.Source = new Uri("pack://application:,,,/ContainerToDrive.Desktop;component/Themes/Theme.xaml");
                using var azure = new AzureSignInService(root);
                var session = new ControllerSession(new ControllerClient(root), allowStart: false);
                var snapshot = new AppSnapshot { WritableEnabled = true };
                var added = new ConnectionWindow(session, azure, snapshot, null, elevated: false);
                added.Close();
                foreach (var kind in Enum.GetValues<AuthenticationKind>())
                {
                    var profile = new Profile
                    {
                        Name = "Synthetic edit", Endpoint = "https://syntheticstore.blob.core.windows.net",
                        Container = "files", DriveLetter = "Z", AuthenticationKind = kind,
                        ReadOnly = false, AutoMount = true
                    };
                    if (kind == AuthenticationKind.MicrosoftEntra)
                        profile = profile with
                        {
                            TenantId = "11111111-1111-1111-1111-111111111111",
                            SubscriptionId = "22222222-2222-2222-2222-222222222222",
                            ResourceGroupName = "synthetic-rg"
                        };
                    var edited = new ConnectionWindow(session, azure, snapshot, profile, elevated: false);
                    try
                    {
                        Assert.Equal("Edit connection", edited.Title);
                        Assert.Equal(profile.Name, ((System.Windows.Controls.TextBox)edited.FindName("NameBox")).Text);
                        Assert.Equal(profile.Container, ((SearchComboBox)edited.FindName("KeyContainerBox")).Text);
                        Assert.False(((System.Windows.Controls.CheckBox)edited.FindName("ReadOnlyCheck")).IsChecked);
                        Assert.True(((System.Windows.Controls.CheckBox)edited.FindName("AutoMountCheck")).IsChecked);
                        Assert.False(((System.Windows.Controls.CheckBox)edited.FindName("WritableConsent")).IsChecked);
                        var drive = (System.Windows.Controls.ComboBox)edited.FindName("DriveBox");
                        drive.Items.Add("Z");
                        drive.SelectedItem = "Z";
                        ((System.Windows.Controls.TextBox)edited.FindName("PrefixBox")).Text = "different-prefix";
                        var build = typeof(ConnectionWindow).GetMethod("BuildSubmissionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        var submission = (Task<(Profile Profile, CredentialSubmission? Credential)?>)build.Invoke(edited, [CancellationToken.None])!;
                        Assert.True(submission.IsCompleted, "A changed saved source must be rejected without external requests.");
                        Assert.Null(submission.GetAwaiter().GetResult());
                        var status = ((System.Windows.Controls.TextBlock)edited.FindName("StatusText")).Text;
                        Assert.Contains("Use Add connection", status);
                        Assert.DoesNotContain("Complete Azure sign-in", status);
                        ((System.Windows.Controls.TextBox)edited.FindName("PrefixBox")).Text = profile.Prefix;
                        var retained = (Task<(Profile Profile, CredentialSubmission? Credential)?>)build.Invoke(edited, [CancellationToken.None])!;
                        Assert.True(retained.IsCompleted);
                        var unchanged = retained.GetAwaiter().GetResult();
                        Assert.NotNull(unchanged);
                        Assert.Equal(profile.Container, unchanged.Value.Profile.Container);
                        Assert.Null(unchanged.Value.Credential);
                        if (kind == AuthenticationKind.MicrosoftEntra)
                        {
                            typeof(ConnectionWindow).GetField("_updatingAzureSelections", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(edited, true);
                            var subscription = new AzureSubscriptionChoice(profile.SubscriptionId, "Synthetic subscription", profile.TenantId);
                            var account = new AzureStorageAccountChoice($"/subscriptions/{profile.SubscriptionId}/resourceGroups/{profile.ResourceGroupName}/providers/Microsoft.Storage/storageAccounts/syntheticstore", "syntheticstore");
                            var subscriptions = (SearchComboBox)edited.FindName("SubscriptionBox");
                            subscriptions.ItemsSource = new[] { subscription };
                            subscriptions.SelectedItem = subscription;
                            var accounts = (SearchComboBox)edited.FindName("StorageAccountBox");
                            accounts.ItemsSource = new[] { account };
                            accounts.SelectedItem = account;
                            var containers = (SearchComboBox)edited.FindName("EntraContainerBox");
                            containers.ItemsSource = new[] { "different-container" };
                            containers.SelectedItem = "different-container";
                            typeof(ConnectionWindow).GetField("_updatingAzureSelections", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(edited, false);
                            var changed = (Task<(Profile Profile, CredentialSubmission? Credential)?>)build.Invoke(edited, [CancellationToken.None])!;
                            Assert.True(changed.IsCompleted, "Changing the selected container must not request an Azure credential.");
                            Assert.Null(changed.GetAwaiter().GetResult());
                            Assert.Contains("Use Add connection", ((System.Windows.Controls.TextBlock)edited.FindName("StatusText")).Text);
                        }
                    }
                    finally { edited.Close(); }
                }
            }
            catch (Exception exception) { failure = exception; }
            finally
            {
                application.Shutdown();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Dialog construction did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}