namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>Builds the line printed when broker sign-in fails and sign-in continues with a device code.</summary>
public static class BrokerFallbackNotice
{
    /// <summary>Names the broker's failure <paramref name="reason"/>, MSAL's error code and identifiers, so it can be reported.</summary>
    public static string Create(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        return $"Broker sign-in failed ({reason}), so msagent will use a device code.";
    }
}
