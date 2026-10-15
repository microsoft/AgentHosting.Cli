using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Utils;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>Keeps MSAL's <see cref="MacMainThreadScheduler"/> running on the process main thread, where the macOS broker must show its sign-in UI from a console app.</summary>
public static class MacOsMainThread
{
    /// <summary>Runs <paramref name="body"/> to completion and returns its exit code, on a worker thread while the main thread services MSAL's scheduler when called from the macOS main thread.</summary>
    public static int Run(Func<Task<int>> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var scheduler = MacMainThreadScheduler.Instance();
        if (!OperatingSystem.IsMacOS() || !scheduler.IsCurrentlyOnMainThread())
        {
            return body().GetAwaiter().GetResult();
        }

        var work = Task.Run(async () =>
        {
            try
            {
                return await body().ConfigureAwait(false);
            }
            finally
            {
                _ = scheduler.RunOnMainThreadAsync(StopAsync);
            }
        });

        scheduler.StartMessageLoop();
        return work.GetAwaiter().GetResult();
    }

    internal static async Task<T> InvokeAsync<T>(IPublicClientApplication client, Func<Task<T>> interactiveCall)
    {
        var scheduler = MacMainThreadScheduler.Instance();
        if (!OperatingSystem.IsMacOS() || !client.AppConfig.IsBrokerEnabled || !scheduler.IsRunning() || scheduler.IsCurrentlyOnMainThread())
        {
            return await interactiveCall().ConfigureAwait(false);
        }

        Task<T>? pending = null;
        await scheduler.RunOnMainThreadAsync(() => pending = interactiveCall()).ConfigureAwait(false);
        return await pending!.ConfigureAwait(false);
    }

    private static Task StopAsync()
    {
        MacMainThreadScheduler.Instance().Stop();
        return Task.CompletedTask;
    }
}
