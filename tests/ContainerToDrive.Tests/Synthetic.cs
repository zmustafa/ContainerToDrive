using ContainerToDrive.Core;

namespace ContainerToDrive.Tests;

internal static class Synthetic
{
    // Fabricated account name and deliberately invalid signature. Never resolve or request this URL.
    internal const string Endpoint = "https://ctdsyntheticunit000.blob.core.windows.net";
    internal const string Container = "unit-fixtures";
    internal const string Signature = "NOT-A-REAL-SIGNATURE";
    internal static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    internal static readonly DateTimeOffset Expiry = Now.AddHours(1);
    internal static readonly Guid ProfileId = Guid.Parse("78ee7f64-9bf0-41b2-9965-40e6297d3623");

    internal static string Sas(string query = "sp=rl&se=2030-01-01T13%3A00%3A00Z") =>
        $"{Endpoint}/{Container}?sv=2025-01-05&sr=c&sig={Signature}&{query}";

    internal static Profile Profile() => new()
    {
        Id = ProfileId,
        Name = "Synthetic profile",
        Endpoint = Endpoint,
        Container = Container,
        DriveLetter = "Z",
        ReadOnly = true
    };
}