using System.Net;

namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>
/// Reads whether a delete left the connection gone.
///
/// The connections API answers 404 both for a connection that is not there and for an environment or
/// route it could not reach. Only the first proves the connection is gone; treating the second as a
/// deletion reports a surviving connection as cleaned up.
/// </summary>
public static class PowerAppsConnectionRemoval
{
    private static readonly string[] UnreachableTargetMarkers =
    [
        "EnvironmentNotFound",
        "RouteNotFound",
        "ServiceDecommissioned",
    ];

    /// <summary>Whether a delete response establishes that the connection no longer exists.</summary>
    /// <param name="response">What the service answered.</param>
    /// <returns><see langword="true"/> when the connection is gone.</returns>
    public static bool ProvesAbsent(PowerAppsConnectionResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        return response.IsSuccess
            || (response.StatusCode == HttpStatusCode.NotFound && !IsTargetUnreachable(response.Body));
    }

    private static bool IsTargetUnreachable(string? detail) =>
        !string.IsNullOrWhiteSpace(detail)
        && UnreachableTargetMarkers.Any(marker => detail.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
