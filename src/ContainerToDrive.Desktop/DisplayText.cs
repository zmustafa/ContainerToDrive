using System.Globalization;
using ContainerToDrive.Core;

namespace ContainerToDrive.Desktop;

internal static class DisplayText
{
    public static string Clean(string? text)
    {
        var redacted = Redaction.Clean(text);
        return new string(redacted.Where(c => !char.IsControl(c) && c is not '\u202A' and not '\u202B' and not '\u202C'
            and not '\u202D' and not '\u202E' and not '\u2066' and not '\u2067' and not '\u2068' and not '\u2069').ToArray());
    }

    public static string Resource(Profile profile)
    {
        // The endpoint is credential-free in the contract. Defensively omit any query.
        var host = Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var uri) ? uri.Host : "Unknown endpoint";
        return Clean(host + "/" + profile.Container + (profile.Prefix.Length == 0 ? "" : "/" + profile.Prefix));
    }

    public static string Authentication(Profile profile) => profile.AuthenticationKind switch
    {
        AuthenticationKind.AccountKey => "Account key",
        AuthenticationKind.MicrosoftEntra => "Microsoft Entra",
        _ => "Container SAS"
    };

    public static string Time(DateTimeOffset? value) => value?.ToLocalTime().ToString("G", CultureInfo.CurrentCulture) ?? "Not observed";

    public static string Bytes(long? bytes) => bytes is >= 0
        ? $"{bytes.Value / (1024d * 1024 * 1024):0.##} GiB"
        : "Not reported";

    public static string Failure(string operation, Guid requestId) => operation switch
    {
        "Save" => $"The connection was not confirmed saved. It may have changed in another window. Refresh before retrying. Request {requestId:N}.",
        "Clone" => $"Cloning was not confirmed. Check for a free drive letter and a valid saved credential, then refresh before retrying. Request {requestId:N}.",
        "Validate" => $"The connection test did not succeed. Check the selected credential, network, prerequisites, and data role: Reader for read-only or Contributor for writable access. Request {requestId:N}.",
        "Renew" => $"Credential renewal was not confirmed. Check that the replacement grants access to the same container, then refresh before retrying. Request {requestId:N}.",
        _ => $"{operation} was not confirmed. Refresh status and review prerequisites or recovery. The controller may still have completed the request. Request {requestId:N}."
    };
}