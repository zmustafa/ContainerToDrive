using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using ContainerToDrive.Windows;
using Xunit;

namespace ContainerToDrive.IntegrationTests;

[Trait("Category", "LocalIntegration")]
public sealed class ProtectedSettingsStoreTests
{
    [Fact]
    public void DpapiSettingRoundTripOverwriteAndDeleteNeverPersistPlaintext()
    {
        var root = LocalTestRoot.Create();
        var store = new ProtectedSettingsStore(root.Root);
        var first = Encoding.UTF8.GetBytes("synthetic-authentication-record-" + Guid.NewGuid().ToString("N"));
        var second = Encoding.UTF8.GetBytes("synthetic-replacement-record-" + Guid.NewGuid().ToString("N"));

        store.Write("entra-authentication-record", first);
        AssertMatches(first, new ProtectedSettingsStore(root.Root).Read("entra-authentication-record"));
        var path = root.PathFor("settings", "entra-authentication-record.bin");
        AssertNoPlaintext(path, first);
        var acl = new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        Assert.Equal(AppPaths.CurrentSid, acl.GetOwner(typeof(SecurityIdentifier))!.Value);
        Assert.True(acl.AreAccessRulesProtected);

        store.Write("entra-authentication-record", second);
        AssertMatches(second, store.Read("entra-authentication-record"));
        AssertNoPlaintext(path, first);
        AssertNoPlaintext(path, second);

        store.Delete("entra-authentication-record");
        Assert.Null(store.Read("entra-authentication-record"));
        Assert.False(File.Exists(path));
        CryptographicOperations.ZeroMemory(first);
        CryptographicOperations.ZeroMemory(second);
    }

    [Fact]
    public void LegacyProductEntropyIsRewrittenWithCurrentProductEntropy()
    {
        var root = LocalTestRoot.Create();
        var store = new ProtectedSettingsStore(root.Root);
        var name = "entra-authentication-record";
        var plaintext = Encoding.UTF8.GetBytes("synthetic-legacy-setting-" + Guid.NewGuid().ToString("N"));
        var legacyEntropy = Encoding.UTF8.GetBytes("BlobToDrive/settings/v1/" +
            root.Root.ToUpperInvariant() + "\n" + AppPaths.CurrentSid + "/" + name);
        byte[] legacyCiphertext;
        try { legacyCiphertext = ProtectedData.Protect(plaintext, legacyEntropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(legacyEntropy); }
        var path = root.PathFor("settings", name + ".bin");
        File.WriteAllBytes(path, legacyCiphertext);
        AppPaths.SecureFile(path);

        AssertMatches(plaintext, store.Read(name));

        var migratedCiphertext = File.ReadAllBytes(path);
        Assert.False(legacyCiphertext.AsSpan().SequenceEqual(migratedCiphertext));
        var currentEntropy = Encoding.UTF8.GetBytes("ContainerToDrive/settings/v2/" +
            root.Root.ToUpperInvariant() + "\n" + AppPaths.CurrentSid + "/" + name);
        byte[]? migratedPlaintext = null;
        try
        {
            migratedPlaintext = ProtectedData.Unprotect(migratedCiphertext, currentEntropy, DataProtectionScope.CurrentUser);
            Assert.True(plaintext.AsSpan().SequenceEqual(migratedPlaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(currentEntropy);
            if (migratedPlaintext is not null) CryptographicOperations.ZeroMemory(migratedPlaintext);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    [Fact]
    public void CiphertextCannotBeTransplantedToAnotherDataRoot()
    {
        var firstRoot = LocalTestRoot.Create();
        var secondRoot = LocalTestRoot.Create();
        var bytes = Encoding.UTF8.GetBytes("synthetic-root-bound-setting");
        new ProtectedSettingsStore(firstRoot.Root).Write("entra-authentication-record", bytes);
        var firstPath = firstRoot.PathFor("settings", "entra-authentication-record.bin");
        _ = new ProtectedSettingsStore(secondRoot.Root);
        var secondPath = secondRoot.PathFor("settings", "entra-authentication-record.bin");
        File.Copy(firstPath, secondPath);
        AppPaths.SecureFile(secondPath);

        Assert.Throws<CryptographicException>(() => new ProtectedSettingsStore(secondRoot.Root).Read("entra-authentication-record"));
        AssertMatches(bytes, new ProtectedSettingsStore(firstRoot.Root).Read("entra-authentication-record"));
        CryptographicOperations.ZeroMemory(bytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("../escape")]
    [InlineData("under_score")]
    public void InvalidSettingNamesAreRejected(string name)
    {
        var store = new ProtectedSettingsStore(LocalTestRoot.Create().Root);
        Assert.Throws<ArgumentException>(() => store.Write(name, [1]));
        Assert.Throws<ArgumentException>(() => store.Read(name));
        Assert.Throws<ArgumentException>(() => store.Delete(name));
    }

    private static void AssertMatches(byte[] expected, byte[]? actual)
    {
        Assert.NotNull(actual);
        try { Assert.True(expected.AsSpan().SequenceEqual(actual), "The protected setting round trip differed; contents are not printed."); }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }

    private static void AssertNoPlaintext(string path, byte[] value) =>
        Assert.True(File.ReadAllBytes(path).AsSpan().IndexOf(value) < 0, "A protected settings file contains plaintext test data.");
}
