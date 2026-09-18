using ContainerToDrive.Desktop;
using Xunit;

namespace ContainerToDrive.Tests;

[Trait("Category", "Unit")]
public sealed class AzureSearchTests
{
    private const string Subscription = "11111111-1111-1111-1111-111111111111";
    private const string OtherSubscription = "22222222-2222-2222-2222-222222222222";
    private static string Group(string name) => $"/subscriptions/{Subscription}/resourceGroups/{name}";

    [Fact]
    public void MissingResourceGroupUsesSelectedSubscriptionScope()
    {
        var scope = AzureSignInService.StorageAccountScope(Subscription, null);
        Assert.Equal($"/subscriptions/{Subscription}", scope.ToString());
    }

    [Fact]
    public void SelectedResourceGroupNarrowsAccountScope()
    {
        Assert.Equal(Group("team"), AzureSignInService.StorageAccountScope(Subscription, Group("team")).ToString());
        Assert.Throws<ArgumentException>(() => AzureSignInService.StorageAccountScope(OtherSubscription, Group("team")));
        Assert.Throws<ArgumentException>(() => AzureSignInService.StorageAccountScope("", null));
        Assert.Throws<ArgumentException>(() => AzureSignInService.StorageAccountScope(Subscription, $"/subscriptions/{Subscription}"));
    }

    [Fact]
    public void SubscriptionSearchKeepsAccountResourceGroupForSaving()
    {
        var account = new AzureStorageAccountChoice(Group("team") + "/providers/Microsoft.Storage/storageAccounts/syntheticstore", "syntheticstore");
        Assert.Equal("team", account.ResourceGroupName);
        Assert.True(AzureSignInService.IsAccountInScope(Subscription, null, account));
        Assert.True(AzureSignInService.IsAccountInScope(Subscription, Group("team"), account));
        Assert.False(AzureSignInService.IsAccountInScope(Subscription, Group("other"), account));
        Assert.False(AzureSignInService.IsAccountInScope(OtherSubscription, null, account));
        Assert.False(AzureSignInService.IsAccountInScope(Subscription, null, account with { Name = "differentstore" }));
    }

    [Fact]
    public async Task CachedSearchMatchesNamesAnywhereIgnoringCase()
    {
        var loads = 0;
        var source = AsyncSearch.Cached<string>(_ =>
        {
            loads++;
            return Task.FromResult<IReadOnlyList<string>>(["Team Production", "Development"]);
        });
        Assert.Equal("Team Production", Assert.Single(await source(" PROD ", CancellationToken.None)));
        Assert.Equal("Development", Assert.Single(await source("develop", CancellationToken.None)));
        Assert.Empty(await source("unmatched", CancellationToken.None));
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task LateResultsCannotReplaceNewerQueryResults()
    {
        using var search = new AsyncSearch();
        var oldResponse = new TaskCompletionSource<IReadOnlyList<object>>(TaskCreationOptions.RunContinuationsAsynchronously);
        search.SetSource((query, _) => query == "old" ? oldResponse.Task : Task.FromResult<IReadOnlyList<object>>(["current"]));
        var oldSearch = search.RunAsync("old", TimeSpan.Zero);
        Assert.True(search.IsLoading);
        await search.RunAsync("new", TimeSpan.Zero);
        oldResponse.SetResult(["stale"]);
        await oldSearch;
        Assert.Equal("current", Assert.Single(search.Items));
        Assert.False(search.IsLoading);
    }

    [Fact]
    public async Task ChangingParentScopeDiscardsUncooperativeOldRequest()
    {
        using var search = new AsyncSearch();
        var oldResponse = new TaskCompletionSource<IReadOnlyList<object>>(TaskCreationOptions.RunContinuationsAsynchronously);
        search.SetSource((_, _) => oldResponse.Task);
        var pending = search.RunAsync("", TimeSpan.Zero);
        search.SetSource(null);
        oldResponse.SetResult(["container-from-old-account"]);
        await pending;
        Assert.Empty(search.Items);
        Assert.False(search.IsLoading);
    }

    [Fact]
    public async Task DebounceCancelsSupersededQueryBeforeCallingSource()
    {
        using var search = new AsyncSearch();
        var calls = new List<string>();
        search.SetSource((query, _) =>
        {
            calls.Add(query);
            return Task.FromResult<IReadOnlyList<object>>([]);
        });
        var pending = search.RunAsync("partial", TimeSpan.FromDays(1));
        await search.RunAsync("complete", TimeSpan.Zero);
        await pending;
        Assert.Equal("complete", Assert.Single(calls));
        Assert.Equal("No matching results", search.Status);
    }

    [Fact]
    public async Task SearchErrorsDoNotExposeExceptionText()
    {
        using var search = new AsyncSearch();
        search.SetSource((_, _) => throw new InvalidOperationException("DO_NOT_DISPLAY_CREDENTIAL_DETAILS"));
        await search.RunAsync("", TimeSpan.Zero);
        Assert.Empty(search.Items);
        Assert.False(search.IsLoading);
        Assert.Equal("Results could not be loaded. Refresh to retry.", search.Status);
    }

    [Fact]
    public async Task MissingScopeAndDisposedSearchNeverCallSource()
    {
        using var search = new AsyncSearch();
        await search.RunAsync("container", TimeSpan.Zero);
        Assert.Empty(search.Items);
        Assert.False(search.IsLoading);
        search.SetSource((_, _) => throw new InvalidOperationException("Must not run"));
        search.Dispose();
        await search.RunAsync("container", TimeSpan.Zero);
        Assert.Empty(search.Items);
    }
}