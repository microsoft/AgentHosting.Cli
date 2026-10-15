using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Copilot;

/// <summary>
/// Resolves the environment coordinates a Copilot Studio command runs against, turning the selected or
/// explicitly supplied environment into the Dataverse organization the sync library needs.
/// </summary>
public sealed class CopilotEnvironmentResolver
{
    private readonly EnvironmentSelectionService _environments;
    private readonly PowerPlatformCloudMap _cloudMap;

    /// <summary>Creates a resolver over <paramref name="environments"/>.</summary>
    /// <param name="environments">The environment selection service.</param>
    /// <param name="cloudMap">Decides which Dataverse organizations this cloud serves.</param>
    public CopilotEnvironmentResolver(EnvironmentSelectionService environments, PowerPlatformCloudMap cloudMap)
    {
        _environments = environments ?? throw new ArgumentNullException(nameof(environments));
        _cloudMap = cloudMap ?? throw new ArgumentNullException(nameof(cloudMap));
    }

    /// <summary>Resolves the environment context for one command.</summary>
    /// <param name="environmentId">An explicit environment id, or <see langword="null"/> for the active selection.</param>
    /// <param name="cancellationToken">Cancels the resolution.</param>
    /// <returns>The resolved environment context.</returns>
    public async Task<CopilotEnvironmentContext> ResolveAsync(string? environmentId, CancellationToken cancellationToken) =>
        ToContext(await _environments.ResolveForCommandAsync(environmentId, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Every environment the operator can reach, resolved from one listing.
    /// </summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The inventory, ordered by environment id.</returns>
    public async Task<CopilotEnvironmentInventory> ListAsync(CancellationToken cancellationToken)
    {
        var resolved = (await _environments.ListAvailableAsync(cancellationToken).ConfigureAwait(false))
            .OrderBy(environment => environment.EnvironmentId, StringComparer.Ordinal)
            .Select(environment => (environment.EnvironmentId, Context: TryToContext(environment)))
            .ToArray();

        return new CopilotEnvironmentInventory
        {
            Reachable = [.. resolved.Select(entry => entry.Context).OfType<CopilotEnvironmentContext>()],
            Unreachable = [.. resolved.Where(entry => entry.Context is null).Select(entry => entry.EnvironmentId)],
        };
    }

    private CopilotEnvironmentContext ToContext(PowerPlatformEnvironment environment)
    {
        if (TryToContext(environment) is { } context)
        {
            return context;
        }

        throw Uri.TryCreate(environment.InstanceUrl, UriKind.Absolute, out var untrusted)
            ? _cloudMap.UntrustedDataverseOrganization(untrusted, environment.EnvironmentId)
            : CopilotStudioException.EnvironmentMissingInstanceUrl(environment.EnvironmentId);
    }

    private CopilotEnvironmentContext? TryToContext(PowerPlatformEnvironment environment) =>
        Uri.TryCreate(environment.InstanceUrl, UriKind.Absolute, out var dataverseOrganization)
        && _cloudMap.IsTrustedDataverseOrganization(dataverseOrganization)
            ? new CopilotEnvironmentContext
            {
                EnvironmentId = environment.EnvironmentId,
                TenantId = environment.TenantId,
                DataverseOrganization = dataverseOrganization,
                AgentManagementUrl = Uri.TryCreate(environment.AgentManagementUrl, UriKind.Absolute, out var agentManagement) ? agentManagement : null,
            }
            : null;
}

/// <summary>The environments a tenant-wide command can and cannot reach.</summary>
public sealed record CopilotEnvironmentInventory
{
    /// <summary>Environments carrying a usable Dataverse organization url.</summary>
    public required IReadOnlyList<CopilotEnvironmentContext> Reachable { get; init; }

    /// <summary>Environments skipped because they carry no usable Dataverse organization url.</summary>
    public required IReadOnlyList<string> Unreachable { get; init; }
}
