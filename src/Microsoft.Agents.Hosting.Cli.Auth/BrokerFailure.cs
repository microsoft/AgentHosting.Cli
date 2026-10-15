using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Auth;

internal static class BrokerFailure
{
    public static async Task<T> TranslateAsync<T>(Func<Task<T>> brokerCall)
    {
        try
        {
            return await brokerCall().ConfigureAwait(false);
        }
        catch (MsalException exception) when (exception.ErrorCode == MsalError.AuthenticationCanceledError)
        {
            throw new SignInCanceledException(Describe(exception));
        }
        catch (Exception exception) when (IsBrokerFailure(exception))
        {
            throw Unavailable(exception);
        }
    }

    public static bool IsBrokerFailure(Exception exception) => exception is MsalException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or TypeInitializationException or PlatformNotSupportedException;

    public static BrokerUnavailableException Unavailable(Exception exception) => new(Describe(exception));

    private static string Describe(Exception exception)
    {
        if (exception is not MsalException msal)
        {
            return exception.GetType().Name;
        }

        var parts = new List<string> { msal.ErrorCode };
        if (msal.AdditionalExceptionData?.TryGetValue(MsalException.BrokerErrorStatus, out var status) == true && !string.IsNullOrWhiteSpace(status))
        {
            parts.Add($"broker status {status}");
        }

        if (!string.IsNullOrWhiteSpace(msal.CorrelationId))
        {
            parts.Add($"correlation ID {msal.CorrelationId}");
        }

        return string.Join(", ", parts);
    }
}
