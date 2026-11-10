using Microsoft.Agents.Hosting.Cli.Core.Environments;
namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>Input for <c>msagent auth status</c>.</summary>
public sealed record AuthStatusRequest;

/// <summary>Input for <c>msagent auth logout</c>.</summary>
public sealed record AuthLogoutRequest;

/// <summary>Input for <c>msagent auth switch</c>.</summary>
public sealed record AuthSwitchRequest
{
    /// <summary>The account to activate, or empty to keep the active one.</summary>
    public required string Account { get; init; }

    /// <summary>The ring to retarget the active account at, when one is named.</summary>
    public PowerPlatformApiRing? Ring { get; init; }
}

/// <summary>Input for <c>msagent auth setup-git</c>.</summary>
public sealed record AuthSetupGitRequest;

/// <summary>Input for <c>msagent auth git-credential</c>, which git runs as its credential helper.</summary>
public sealed record AuthGitCredentialRequest
{
    /// <summary>The action git asks for: get, store or erase.</summary>
    public required string Operation { get; init; }
}
