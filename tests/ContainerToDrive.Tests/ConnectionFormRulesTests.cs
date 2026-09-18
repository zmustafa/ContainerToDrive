using System.Globalization;
using ContainerToDrive.Core;
using ContainerToDrive.Desktop;
using Xunit;

namespace ContainerToDrive.Tests;

[Trait("Category", "Unit")]
public sealed class ConnectionFormRulesTests
{
    [Fact]
    public void NewConnectionsUseSafeDefaults()
    {
        Assert.True(ConnectionFormRules.NewProfileReadOnly);
        Assert.False(ConnectionFormRules.NewProfileAutoMount);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("drive", true)]
    [InlineData("  drive  ", true)]
    [InlineData("bad\rname", false)]
    public void ConnectionNameReadinessMatchesProfileRules(string value, bool expected) =>
        Assert.Equal(expected, ConnectionFormRules.IsNameValid(value));

    [Theory]
    [InlineData("", true)]
    [InlineData("data/2026", true)]
    [InlineData("/absolute", false)]
    [InlineData("trailing/", false)]
    [InlineData("a/../b", false)]
    [InlineData("back\\slash", false)]
    public void PrefixReadinessRejectsUnsafePaths(string value, bool expected) =>
        Assert.Equal(expected, ConnectionFormRules.IsPrefixValid(value));

    [Theory]
    [InlineData("0.24", false)]
    [InlineData("0.25", true)]
    [InlineData("10", true)]
    [InlineData("1024", true)]
    [InlineData("1024.01", false)]
    public void CacheReadinessEnforcesDocumentedRange(string value, bool expected) =>
        Assert.Equal(expected, ConnectionFormRules.IsCacheValid(value, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(AuthenticationKind.ContainerSas, false, false, false, false, false, false)]
    [InlineData(AuthenticationKind.ContainerSas, false, true, false, false, false, true)]
    [InlineData(AuthenticationKind.ContainerSas, true, false, false, false, false, true)]
    [InlineData(AuthenticationKind.AccountKey, false, false, true, true, false, true)]
    [InlineData(AuthenticationKind.AccountKey, false, false, false, true, false, false)]
    [InlineData(AuthenticationKind.AccountKey, true, false, true, false, false, true)]
    [InlineData(AuthenticationKind.MicrosoftEntra, false, false, false, false, true, true)]
    [InlineData(AuthenticationKind.MicrosoftEntra, true, false, false, false, false, true)]
    public void AuthenticationReadinessRequiresCurrentOrExistingCredential(AuthenticationKind kind,
        bool existing, bool sas, bool accountIdentity, bool accountKey, bool azureSelection, bool expected) =>
        Assert.Equal(expected, ConnectionFormRules.IsAuthenticationReady(kind, existing, sas,
            accountIdentity, accountKey, azureSelection));

    [Theory]
    [InlineData(true, true, true, true, false, true)]
    [InlineData(true, true, true, false, false, false)]
    [InlineData(true, true, true, false, true, true)]
    [InlineData(false, true, true, true, false, false)]
    [InlineData(true, false, true, true, false, false)]
    [InlineData(true, true, false, true, false, false)]
    public void DriveStepRequiresAvailabilityCacheAndWritableConsent(bool lettersLoaded, bool driveSelected,
        bool cacheValid, bool readOnly, bool consent, bool expected) =>
        Assert.Equal(expected, ConnectionFormRules.IsDriveStepReady(lettersLoaded, driveSelected,
            cacheValid, readOnly, consent));
}