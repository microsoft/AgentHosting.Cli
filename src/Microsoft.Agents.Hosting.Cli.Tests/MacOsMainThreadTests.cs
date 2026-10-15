using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class MacOsMainThreadTests
{
    [Fact]
    public void Run_OffTheMacOsMainThread_RunsTheBody_AndReturnsItsExitCode() =>
        Assert.Equal(7, MacOsMainThread.Run(() => Task.FromResult(7)));

    [Fact]
    public void Run_OffTheMacOsMainThread_PropagatesTheBodysFailure() =>
        Assert.Throws<InvalidOperationException>(() => MacOsMainThread.Run(() => Task.FromException<int>(new InvalidOperationException("The command failed."))));

    [Fact]
    public void Run_RequiresABody() =>
        Assert.Throws<ArgumentNullException>(() => MacOsMainThread.Run(null!));

    [Fact]
    public async Task InvokeAsync_WithoutTheMacOsScheduler_RunsTheCallOnTheCallingThread()
    {
        var callingThread = Environment.CurrentManagedThreadId;
        var callThread = 0;

        var result = await MacOsMainThread.InvokeAsync(Client(), () =>
        {
            callThread = Environment.CurrentManagedThreadId;
            return Task.FromResult("signed in");
        });

        Assert.Equal("signed in", result);
        Assert.Equal(callingThread, callThread);
    }

    [Fact]
    public async Task InvokeAsync_PropagatesTheCallsFailure() =>
        await Assert.ThrowsAsync<MsalClientException>(() => MacOsMainThread.InvokeAsync<int>(Client(), () => throw new MsalClientException("broker_failed", "The broker failed.")));

    private static IPublicClientApplication Client() => PublicClientApplicationBuilder.Create(Guid.NewGuid().ToString()).Build();
}
