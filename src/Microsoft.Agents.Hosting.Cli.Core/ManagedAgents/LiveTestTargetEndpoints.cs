namespace Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

/// <summary>Endpoints derived together for one DirectEngine environment and schema.</summary>
public sealed record LiveTestTargetEndpoints(Uri Standard, Uri GitHubCopilot);

/// <summary>Derives the standard and GitHub Copilot DirectEngine URLs from one validated target.</summary>
public static class LiveTestTargetEndpointsBuilder
{
    /// <summary>Builds both supported endpoint URLs for a Power Platform environment and agent schema.</summary>
    public static LiveTestTargetEndpoints DeriveDirectEngineUrls(string environmentId, string schemaName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);

        var normalizedEnvironment = environmentId.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (normalizedEnvironment.Length < 3)
        {
            throw new ArgumentException("The environment ID is too short to derive an endpoint.", nameof(environmentId));
        }

        var host = $"{normalizedEnvironment[..^2]}.{normalizedEnvironment[^2..]}.environment.api.powerplatform.com";
        var escapedSchema = Uri.EscapeDataString(schemaName);
        var standard = new Uri(
            $"https://{host}/copilotstudio/dataverse-backed/authenticated/bots/{escapedSchema}/conversations?api-version=2022-03-01-preview",
            UriKind.Absolute);
        var githubCopilot = new Uri(
            $"https://{host}/copilotstudio/agenticruntime/3p/dataverse-backed/authenticated/bots/{escapedSchema}/conversations?api-version=1",
            UriKind.Absolute);

        return new LiveTestTargetEndpoints(standard, githubCopilot);
    }
}
