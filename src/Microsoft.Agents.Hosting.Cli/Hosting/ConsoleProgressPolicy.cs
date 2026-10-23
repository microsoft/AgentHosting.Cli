namespace Microsoft.Agents.Hosting.Cli.Hosting;

/// <summary>Decides whether the terminal can show the progress spinner.</summary>
public static class ConsoleProgressPolicy
{
    /// <summary>Returns whether the spinner may be drawn on standard error.</summary>
    /// <param name="errorRedirected">Whether standard error is redirected away from the terminal.</param>
    /// <param name="continuousIntegration">The <c>CI</c> environment variable.</param>
    /// <returns><see langword="false"/> when standard error is redirected or <c>CI</c> is set.</returns>
    public static bool ShouldShowProgress(bool errorRedirected, string? continuousIntegration) => !errorRedirected && !IsSet(continuousIntegration);

    private static bool IsSet(string? value) => !string.IsNullOrEmpty(value) && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) && value != "0";
}
