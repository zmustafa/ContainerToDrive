using System.Globalization;
using ContainerToDrive.Core;

namespace ContainerToDrive.IntegrationTests;

internal sealed record SyntheticCredential(string Url, string Signature, DateTimeOffset ExpiresAt)
{
    internal const string Endpoint = "https://ctdsyntheticlocal000.blob.core.windows.net";
    internal const string Container = "local-fixtures";

    internal static SyntheticCredential Create(string endpoint = Endpoint, string container = Container, string permissions = "rl")
    {
        // Invalid signature, fabricated account, future expiry. No real credentials or environment input.
        // Passed only to the parser/store or rclone's in-memory config/create, never a storage operation.
        var signature = "SYNTHETIC-NOT-A-REAL-SAS-" + Guid.NewGuid().ToString("N");
        var expiry = DateTimeOffset.UtcNow.AddDays(30);
        var expires = Uri.EscapeDataString(expiry.ToString("O", CultureInfo.InvariantCulture));
        return new($"{endpoint}/{container}?sv=2025-01-05&sr=c&sp={permissions}&spr=https&se={expires}&sig={signature}", signature, expiry);
    }

    internal static Profile Profile() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Synthetic local integration",
        Endpoint = Endpoint,
        Container = Container,
        Prefix = "fixtures/CaseSensitive",
        DriveLetter = "Z",
        ReadOnly = true
    };
}