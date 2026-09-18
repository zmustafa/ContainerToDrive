using ContainerToDrive.Desktop;
using Xunit;

namespace ContainerToDrive.Tests;

[Trait("Category", "Unit")]
public sealed class InteractiveAuthenticationTests
{
    [Fact]
    public async Task SynchronousAuthenticationStartupDoesNotBlockCaller()
    {
        using var operationEntered = new ManualResetEventSlim(false);
        using var releaseOperation = new ManualResetEventSlim(false);
        using var callReturned = new ManualResetEventSlim(false);
        Task<int>? authentication = null;
        Exception? invocationFailure = null;
        var caller = new Thread(() =>
        {
            try
            {
                authentication = InteractiveAuthentication.RunAsync(() =>
                {
                    operationEntered.Set();
                    releaseOperation.Wait();
                    return Task.FromResult(42);
                }, CancellationToken.None);
            }
            catch (Exception exception) { invocationFailure = exception; }
            finally { callReturned.Set(); }
        });
        caller.IsBackground = true;

        try
        {
            caller.Start();
            Assert.True(operationEntered.Wait(TimeSpan.FromSeconds(5)), "Authentication work did not start.");
            Assert.True(callReturned.Wait(TimeSpan.FromSeconds(2)), "The caller remained blocked by synchronous authentication startup.");
        }
        finally
        {
            releaseOperation.Set();
            Assert.True(caller.Join(TimeSpan.FromSeconds(5)), "The caller thread did not finish.");
        }

        Assert.Null(invocationFailure);
        Assert.NotNull(authentication);
        Assert.Equal(42, await authentication);
    }
}