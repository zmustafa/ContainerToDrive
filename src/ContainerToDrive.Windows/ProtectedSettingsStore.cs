using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;

namespace ContainerToDrive.Windows;

/// <summary>Small DPAPI CurrentUser store for app settings that may identify an account.</summary>
public sealed class ProtectedSettingsStore
{
    private const int MaxBytes = 256 * 1024;
    private const string CurrentProductName = "ContainerToDrive";
    private const string LegacyProductName = "BlobToDrive";
    private readonly string _directory;
    private readonly string _entropyContext;
    private readonly string[] _legacyEntropyContexts;

    public ProtectedSettingsStore(string dataRoot, bool createDirectory = true)
    {
        var root = AppPaths.NormalizeDataRoot(dataRoot);
        _directory = Path.Combine(root, "settings");
        _entropyContext = root.ToUpperInvariant() + "\n" + AppPaths.CurrentSid;
        _legacyEntropyContexts = LegacyRootCandidates(root)
            .Select(candidate => candidate.ToUpperInvariant() + "\n" + AppPaths.CurrentSid)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (createDirectory) AppPaths.SecureDirectory(_directory);
    }

    public byte[]? Read(string name)
    {
        var path = PathFor(name);
        if (!AppPaths.FileExists(path)) return null;
        AppPaths.SecureFile(path);
        var protectedBytes = File.ReadAllBytes(path);
        if (protectedBytes.Length is < 1 or > MaxBytes) throw new InvalidDataException("Saved application settings are unreadable.");
        byte[]? plaintext = null;
        var migrated = false;
        try
        {
            try { plaintext = ProtectedData.Unprotect(protectedBytes, Entropy(name), DataProtectionScope.CurrentUser); }
            catch (CryptographicException)
            {
                foreach (var context in _legacyEntropyContexts)
                {
                    try
                    {
                        plaintext = ProtectedData.Unprotect(protectedBytes, LegacyEntropy(name, context), DataProtectionScope.CurrentUser);
                        migrated = true;
                        break;
                    }
                    catch (CryptographicException) { }
                }
                if (plaintext is null) throw;
            }
            if (plaintext.Length is < 1 or > MaxBytes) throw new CryptographicException();
            if (migrated) Write(name, plaintext);
            return plaintext;
        }
        catch (CryptographicException)
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            throw new CryptographicException("Saved application settings cannot be opened for this Windows user.");
        }
        catch
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
    }

    public void Write(string name, ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length is < 1 or > MaxBytes) throw new ArgumentException("The application setting is empty or too large.");
        var path = PathFor(name);
        var clearBytes = plaintext.ToArray();
        byte[] protectedBytes;
        try { protectedBytes = ProtectedData.Protect(clearBytes, Entropy(name), DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(clearBytes); }
        if (protectedBytes.Length > MaxBytes) throw new InvalidDataException("The protected application setting is too large.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.FullControl,
                FileShare.None, 4096, FileOptions.WriteThrough, AppPaths.FileAcl()))
            {
                stream.Write(protectedBytes);
                stream.Flush(flushToDisk: true);
            }
            if (AppPaths.FileExists(path))
            {
                AppPaths.SecureFile(path);
                File.Replace(temporary, path, null, ignoreMetadataErrors: false);
            }
            else File.Move(temporary, path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            try { if (AppPaths.FileExists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (!AppPaths.FileExists(path)) return;
        AppPaths.SecureFile(path);
        File.Delete(path);
    }

    private string PathFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 ||
            name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '.')))
            throw new ArgumentException("Invalid application setting name.");
        return Path.Combine(_directory, name + ".bin");
    }

    private byte[] Entropy(string name) => Encoding.UTF8.GetBytes(CurrentProductName + "/settings/v2/" + _entropyContext + "/" + name);

    private static byte[] LegacyEntropy(string name, string context) =>
        Encoding.UTF8.GetBytes(LegacyProductName + "/settings/v1/" + context + "/" + name);

    private static IEnumerable<string> LegacyRootCandidates(string root)
    {
        yield return root;
        var parts = root.Split('\\');
        for (var index = 0; index < parts.Length; index++)
        {
            if (!string.Equals(parts[index], CurrentProductName, StringComparison.OrdinalIgnoreCase)) continue;
            var legacy = (string[])parts.Clone();
            legacy[index] = LegacyProductName;
            yield return string.Join('\\', legacy);
        }
    }
}
