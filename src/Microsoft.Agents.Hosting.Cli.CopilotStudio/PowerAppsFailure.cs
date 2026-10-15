namespace Microsoft.Agents.Hosting.Cli.CopilotStudio;

/// <summary>
/// Reads the status the sync library folds into a message, which is the only form it reports one in.
/// </summary>
internal static class PowerAppsFailure
{
    private const string NotFound = "request failed (404)";

    /// <summary>Whether a failure is the service reporting that the resource does not exist.</summary>
    /// <param name="failure">What the library threw.</param>
    /// <returns><see langword="true"/> only for a reported 404.</returns>
    public static bool IsNotFound(Exception failure) =>
        failure is InvalidOperationException && failure.Message.Contains(NotFound, StringComparison.OrdinalIgnoreCase);
}
