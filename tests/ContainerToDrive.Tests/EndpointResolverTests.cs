using System.Net;
using ContainerToDrive.Core;
using ContainerToDrive.Desktop;
using Xunit;

namespace ContainerToDrive.Tests;

[Trait("Category", "Unit")]
public sealed class EndpointResolverTests
{
    private static Profile Profile() => new() { Name = "DNS test", Endpoint = "https://syntheticstore.blob.core.windows.net", Container = "files", DriveLetter = "Z" };

    [Theory]
    [InlineData("10.1.2.3", EndpointKind.Private)]
    [InlineData("172.16.0.1", EndpointKind.Private)]
    [InlineData("172.31.255.254", EndpointKind.Private)]
    [InlineData("192.168.1.4", EndpointKind.Private)]
    [InlineData("fd12:3456::1", EndpointKind.Private)]
    [InlineData("::ffff:10.1.2.3", EndpointKind.Private)]
    [InlineData("52.239.148.88", EndpointKind.Public)]
    [InlineData("2603:1030::1", EndpointKind.Public)]
    [InlineData("172.32.0.1", EndpointKind.Public)]
    [InlineData("127.0.0.1", EndpointKind.Unknown)]
    [InlineData("169.254.1.2", EndpointKind.Unknown)]
    [InlineData("100.64.1.1", EndpointKind.Unknown)]
    [InlineData("0.0.0.0", EndpointKind.Unknown)]
    [InlineData("192.0.2.1", EndpointKind.Unknown)]
    [InlineData("198.18.0.1", EndpointKind.Unknown)]
    [InlineData("224.0.0.1", EndpointKind.Unknown)]
    [InlineData("255.255.255.255", EndpointKind.Unknown)]
    [InlineData("::1", EndpointKind.Unknown)]
    [InlineData("fe80::1", EndpointKind.Unknown)]
    [InlineData("ff02::1", EndpointKind.Unknown)]
    [InlineData("2001:db8::1", EndpointKind.Unknown)]
    public void AddressesAreClassifiedConservatively(string address, EndpointKind expected)
    {
        Assert.Equal(expected, EndpointResolver.Classify([IPAddress.Parse(address)]));
    }

    [Fact]
    public void MixedOrMissingAnswersAreNotPresentedAsOneRoute()
    {
        Assert.Equal(EndpointKind.Mixed, EndpointResolver.Classify([IPAddress.Parse("10.0.0.4"), IPAddress.Parse("52.239.148.88")]));
        Assert.Equal(EndpointKind.Unknown, EndpointResolver.Classify([]));
        Assert.Equal(EndpointKind.Unknown, EndpointResolver.Classify([IPAddress.Parse("10.0.0.4"), IPAddress.Loopback]));
    }

    [Fact]
    public async Task HostLookupIsCredentialFreeCachedAndRefreshesAfterNetworkChanges()
    {
        var now = DateTimeOffset.UtcNow;
        var calls = 0;
        var resolver = new EndpointResolver((host, _) =>
        {
            Assert.Equal("syntheticstore.blob.core.windows.net", host);
            calls++;
            return Task.FromResult(new[] { IPAddress.Parse(calls == 1 ? "10.0.0.4" : "52.239.148.88") });
        }, () => now, TimeSpan.FromSeconds(1));
        Assert.Equal(EndpointKind.Private, (await resolver.ResolveAsync(Profile(), CancellationToken.None)).Kind);
        Assert.Equal(EndpointKind.Private, (await resolver.ResolveAsync(Profile() with { Container = "another" }, CancellationToken.None)).Kind);
        Assert.Equal(1, calls);
        now = now.AddSeconds(61);
        Assert.Equal(EndpointKind.Public, (await resolver.ResolveAsync(Profile(), CancellationToken.None)).Kind);
        Assert.Equal(2, calls);
        resolver.Invalidate();
        await resolver.ResolveAsync(Profile(), CancellationToken.None);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task FailuresAndTimeoutsReturnUnknownWithoutExceptionTextOrPriorAddresses()
    {
        var now = DateTimeOffset.UtcNow;
        var fail = false;
        var resolver = new EndpointResolver((_, _) => fail ? throw new IOException("DO_NOT_DISPLAY") : Task.FromResult(new[] { IPAddress.Parse("10.0.0.4") }), () => now, TimeSpan.FromSeconds(1));
        await resolver.ResolveAsync(Profile(), CancellationToken.None);
        fail = true;
        now = now.AddSeconds(61);
        var result = await resolver.ResolveAsync(Profile(), CancellationToken.None);
        Assert.Equal(EndpointKind.Unknown, result.Kind);
        Assert.Empty(result.Addresses);
        Assert.Null(result.ResolvedAt);
        Assert.DoesNotContain("DO_NOT_DISPLAY", result.ToString());
        var pending = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hanging = new EndpointResolver((_, _) => pending.Task, () => now, TimeSpan.FromMilliseconds(10));
        Assert.Null((await hanging.ResolveAsync(Profile(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).ResolvedAt);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(300)]
    public async Task ConfiguredRefreshIntervalControlsSuccessfulCacheLifetime(int seconds)
    {
        var now = DateTimeOffset.UtcNow;
        var calls = 0;
        var resolver = new EndpointResolver((_, _) =>
        {
            calls++;
            return Task.FromResult(new[] { IPAddress.Parse("10.0.0.4") });
        }, () => now, TimeSpan.FromSeconds(1)) { RefreshSeconds = seconds };
        await resolver.ResolveAsync(Profile(), CancellationToken.None);
        now = now.AddSeconds(seconds - 1);
        await resolver.ResolveAsync(Profile(), CancellationToken.None);
        Assert.Equal(1, calls);
        now = now.AddSeconds(1);
        await resolver.ResolveAsync(Profile(), CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void HidingEndpointInformationClearsPriorObservations()
    {
        var card = new ProfileCard();
        card.Update(Profile(), null, true, false, false);
        card.SetEndpoint(new("syntheticstore.blob.core.windows.net", ["10.0.0.4"], EndpointKind.Private, DateTimeOffset.UtcNow));
        card.Update(Profile(), null, true, false, false, endpointVisible: false);
        Assert.False(card.EndpointVisible);
        Assert.DoesNotContain("10.0.0.4", card.EndpointDetails);
        card.Update(Profile(), null, true, false, false, endpointVisible: true);
        Assert.True(card.EndpointVisible);
        Assert.Equal("Host IP not resolved", card.EndpointInfo);
    }

    [Fact]
    public void CardShowsAddressClassificationAndNeverPresentsOldOrDifferentHostResultsAsCurrent()
    {
        var profile = Profile();
        var card = new ProfileCard();
        card.Update(profile, null, true, false, false);
        Assert.Equal("Host IP not resolved", card.EndpointInfo);
        card.SetEndpoint(new("syntheticstore.blob.core.windows.net", ["10.0.0.4"], EndpointKind.Private, DateTimeOffset.UtcNow));
        Assert.Equal("Private endpoint (DNS) | 10.0.0.4", card.EndpointInfo);
        Assert.Contains("not verification", card.EndpointHelp);
        card.SetEndpoint(new("different.blob.core.windows.net", ["52.239.148.88"], EndpointKind.Public, DateTimeOffset.UtcNow));
        Assert.Contains("10.0.0.4", card.EndpointInfo);
        card.SetEndpoint(new("syntheticstore.blob.core.windows.net", ["10.0.0.4"], EndpointKind.Private, DateTimeOffset.UtcNow.AddMinutes(-2)));
        Assert.Contains("stale", card.EndpointInfo);
        Assert.DoesNotContain("Private endpoint", card.EndpointInfo);
        card.SetEndpoint(new("syntheticstore.blob.core.windows.net", [], EndpointKind.Unknown, null));
        Assert.Equal("Host IP unavailable | Endpoint type unknown", card.EndpointInfo);
        card.Update(profile with { Endpoint = "https://anotherstore.blob.core.windows.net" }, null, true, false, false);
        Assert.Equal("Host IP not resolved", card.EndpointInfo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshDuringAnInFlightLookupDoesNotRestoreItsOldCacheEntry(bool fails)
    {
        var pending = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var resolver = new EndpointResolver((_, _) => ++calls == 1 ? pending.Task : Task.FromResult(new[] { IPAddress.Parse("52.239.148.88") }), () => DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2));
        var first = resolver.ResolveAsync(Profile(), CancellationToken.None);
        resolver.Invalidate();
        if (fails) pending.SetException(new IOException("Old lookup failed"));
        else pending.SetResult([IPAddress.Parse("10.0.0.4")]);
        await first;
        Assert.Equal(EndpointKind.Public, (await resolver.ResolveAsync(Profile(), CancellationToken.None)).Kind);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task InvalidTargetsAndCancelledCallsNeverStartDns()
    {
        var resolver = new EndpointResolver((_, _) => throw new InvalidOperationException("Lookup must not run"), () => DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<ArgumentException>(() => resolver.ResolveAsync(Profile() with { Endpoint = "https://syntheticstore.blob.core.windows.net/?sig=secret" }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => resolver.ResolveAsync(Profile() with { Endpoint = "https://example.com" }, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(Profile(), new CancellationToken(canceled: true)));
    }
}