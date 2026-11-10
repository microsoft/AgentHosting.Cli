using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>Decides which git hosts msagent signs git in to: the environment hosts of the signed-in ring, and the git host of a personal Dev cluster.</summary>
public sealed class GitHostTrust
{
    private readonly PowerPlatformApiTargetConfig _target;
    private readonly AmsEndpointProfile _endpoints;

    /// <summary>Creates the trust.</summary>
    /// <param name="target">The signed-in Power Platform API ring, whose environment hosts git signs in to.</param>
    /// <param name="endpoints">Names the git host of a personal Dev cluster, when one is set.</param>
    public GitHostTrust(PowerPlatformApiTargetConfig target, AmsEndpointProfile endpoints)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
    }

    /// <summary>Whether git may send msagent's token to a host.</summary>
    /// <param name="host">The host, without a scheme or port.</param>
    public bool Trusts(string? host) => Uri.CheckHostName(host) == UriHostNameType.Dns && (string.Equals(host, _endpoints.GitHost, StringComparison.OrdinalIgnoreCase) || (PowerPlatformCloudMap.ForTarget(_target).IsTrusted(new Uri($"https://{host}")) && PowerPlatformApiDiscovery.IsEnvironmentHost(_target, host!)));

    /// <summary>The url patterns git's credential helper is configured for: every environment host of the ring, and the personal Dev cluster's git host.</summary>
    public IReadOnlyList<string> CredentialHostPatterns() => [$"https://*.*.{PowerPlatformApiDiscovery.EnvironmentHostSuffix(_target)}", .. DevClusterHost() is { } devClusterHost ? new[] { $"https://{devClusterHost}" } : []];

    /// <summary>The git host of a personal Dev cluster, or null when none is set.</summary>
    public string? DevClusterHost() => _endpoints.GitHost is not { } host || Uri.CheckHostName(host) == UriHostNameType.Dns ? _endpoints.GitHost : throw AmsAgentException.EndpointOverrideNotUsable($"{AmsEndpointProfile.GitHostVariable}: '{TerminalText.Sanitize(host)}' is not a host name.", "Set it to the host alone, without a scheme, port or path.");
}
