using System.Globalization;
using ContainerToDrive.Core;

namespace ContainerToDrive.Desktop;

internal static class ConnectionFormRules
{
    internal const bool NewProfileReadOnly = true;
    internal const bool NewProfileAutoMount = false;

    internal static bool IsNameValid(string value)
    {
        var name = value.Trim();
        return name.Length is > 0 and <= 100 && !name.Any(char.IsControl);
    }

    internal static bool IsPrefixValid(string value) =>
        value.Length <= 1024 && !value.Any(char.IsControl) && !value.Contains('\\') && !value.Contains(':') &&
        !value.StartsWith('/') && !value.EndsWith('/') && !value.Split('/').Any(part => part is "." or "..");

    internal static bool IsCacheValid(string value, CultureInfo culture) =>
        decimal.TryParse(value, NumberStyles.Number, culture, out var cache) && cache is >= 0.25m and <= 1024m;

    internal static bool IsAuthenticationReady(AuthenticationKind kind, bool existingCredentialMatches,
        bool sasPresent, bool accountIdentityValid, bool accountKeyPresent, bool azureSelectionComplete) => kind switch
    {
        AuthenticationKind.ContainerSas => sasPresent || existingCredentialMatches,
        AuthenticationKind.AccountKey => accountIdentityValid && (accountKeyPresent || existingCredentialMatches),
        AuthenticationKind.MicrosoftEntra => azureSelectionComplete || existingCredentialMatches,
        _ => false
    };

    internal static bool IsDriveStepReady(bool lettersLoaded, bool driveSelected, bool cacheValid,
        bool readOnly, bool writableConsent) =>
        lettersLoaded && driveSelected && cacheValid && (readOnly || writableConsent);
}