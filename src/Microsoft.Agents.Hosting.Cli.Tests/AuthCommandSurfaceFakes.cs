using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Spy <see cref="IAuthenticationSession"/> that records how the facade drives it and returns preset
/// accounts, so the auth command surface is exercised entirely offline: no MSAL, browser, broker, or
/// token acquisition. It exposes call counters so a test can assert, for example, that
/// <c>login --non-interactive</c> touched no login flow and that status/switch/logout opened no UI.
/// </summary>
internal sealed class FakeAuthenticationSession : IAuthenticationSession
{
    public List<AuthAccount> CacheAccounts { get; } = [];

    public AuthAccount? BrokerResult { get; set; }
    public AuthAccount? SilentBrokerResult { get; set; }
    public AuthAccount? DeviceCodeResult { get; set; }
    public Exception? BrokerFailure { get; set; }
    public Exception? SilentBrokerFailure { get; set; }
    public string? DeviceCodeMessage { get; set; } = "To sign in, open https://microsoft.com/devicelogin and enter CODE-1234.";

    public int BrokerCalls { get; private set; }
    public int SilentBrokerCalls { get; private set; }
    public int DeviceCodeCalls { get; private set; }
    public int GetAccountsCalls { get; private set; }
    public int ClearCacheCalls { get; private set; }

    /// <summary>Total number of login-flow invocations across all three flows.</summary>
    public int TotalLoginCalls => BrokerCalls + SilentBrokerCalls + DeviceCodeCalls;

    /// <summary>The login request of the last flow invoked, as the facade shaped it.</summary>
    public AuthLoginRequest? LastLoginRequest { get; private set; }

    public Task<AuthAccount> LoginWithBrokerAsync(AuthLoginRequest request, CancellationToken cancellationToken)
    {
        BrokerCalls++;
        return Respond(request, BrokerResult, BrokerFailure ?? new InvalidOperationException("No broker result configured."));
    }

    public Task<AuthAccount> LoginWithBrokerSilentlyAsync(AuthLoginRequest request, CancellationToken cancellationToken)
    {
        SilentBrokerCalls++;
        return Respond(request, SilentBrokerResult, SilentBrokerFailure ?? new BrokerUnavailableException("the operating-system broker does not hold this account"));
    }

    private Task<AuthAccount> Respond(AuthLoginRequest request, AuthAccount? result, Exception failure)
    {
        LastLoginRequest = request;
        return result is null ? Task.FromException<AuthAccount>(failure) : Task.FromResult(result);
    }

    public async Task<AuthAccount> LoginWithDeviceCodeAsync(
        AuthLoginRequest request,
        Func<string, Task> deviceCodeCallback,
        CancellationToken cancellationToken)
    {
        DeviceCodeCalls++;
        LastLoginRequest = request;
        if (DeviceCodeMessage is not null)
        {
            await deviceCodeCallback(DeviceCodeMessage);
        }

        return DeviceCodeResult ?? throw new InvalidOperationException("No device-code result configured.");
    }

    public Task<IReadOnlyList<AuthAccount>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        GetAccountsCalls++;
        return Task.FromResult<IReadOnlyList<AuthAccount>>(CacheAccounts.ToList());
    }

    public Task ClearCacheAsync(CancellationToken cancellationToken)
    {
        ClearCacheCalls++;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Records the interactive instructions the facade presents (the device-code verification message), so
/// a test can assert the message was surfaced without touching the console.
/// </summary>
internal sealed class RecordingMessagePresenter : IInteractiveMessagePresenter
{
    public List<string> Messages { get; } = [];

    public Task PresentAsync(string message, CancellationToken cancellationToken)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }
}
