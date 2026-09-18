namespace ContainerToDrive.Desktop;

internal static class InteractiveAuthentication
{
    internal static Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Task.Run(operation, token);
    }
}