using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Playground;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>Builds the credential helper git runs to sign in to GRS with msagent, which <c>auth setup-git</c>, a clone's git config and msagent's own git calls share.</summary>
public sealed class GitCredentialHelper
{
    /// <summary>The variable that turns on msagent features for one run.</summary>
    public const string FeatureVariable = "AH_CLI_FEATURES";

    /// <summary>The feature <c>auth git-credential</c> needs.</summary>
    public const string Feature = "AmsMcsAgents";

    private readonly PlaygroundToolLocator _tools;
    private readonly GitHostTrust _trust;

    /// <summary>Creates the builder.</summary>
    /// <param name="tools">Finds msagent on PATH.</param>
    /// <param name="trust">Names the git host of a personal Dev cluster, which the helper passes on.</param>
    public GitCredentialHelper(PlaygroundToolLocator tools, GitHostTrust trust)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
    }

    /// <summary>The helper, as git's config holds it: a shell command that turns the feature on, passes a personal Dev cluster's git host, and runs msagent by its absolute path, single-quoted for git's sh.</summary>
    public string Command() => $"!{FeatureVariable}={Feature}{(_trust.DevClusterHost() is { } host ? $" {AmsEndpointProfile.GitHostVariable}={host}" : string.Empty)} {(_tools.FindOnPath("msagent") is { } msagent ? $"'{msagent.Replace("'", @"'\''", StringComparison.Ordinal)}'" : "msagent")} auth git-credential";
}
