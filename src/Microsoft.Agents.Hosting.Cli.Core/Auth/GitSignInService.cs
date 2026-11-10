using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Playground;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>Signs git in to the Power Platform git hosts of the signed-in ring with msagent's token, by configuring git's credential helper and answering its credential requests.</summary>
public sealed class GitSignInService : ICommandHandler<AuthSetupGitRequest, AuthSetupGitResult>, ICommandHandler<AuthGitCredentialRequest, AuthGitCredentialResult>
{
    private readonly PowerPlatformApiTargetConfig _target;
    private readonly ITokenProvider _tokens;
    private readonly PlaygroundToolLocator _tools;
    private readonly GitHostTrust _trust;
    private readonly GitProcessAdapter _git;
    private readonly TextReader _input;

    /// <summary>Creates the service.</summary>
    /// <param name="target">The signed-in Power Platform API ring, whose environment hosts git signs in to.</param>
    /// <param name="endpoints">Names the git host of a personal Dev cluster, when one is set.</param>
    /// <param name="tokens">Acquires the Power Platform API token silently.</param>
    /// <param name="processes">Runs git without shell interpolation.</param>
    /// <param name="environment">The PATH git is found on.</param>
    /// <param name="input">The standard input git writes its credential request to.</param>
    public GitSignInService(PowerPlatformApiTargetConfig target, AmsEndpointProfile endpoints, ITokenProvider tokens, IPlaygroundProcessRunner processes, IPlaygroundToolEnvironment environment, TextReader input)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _tools = new PlaygroundToolLocator(processes ?? throw new ArgumentNullException(nameof(processes)), environment ?? throw new ArgumentNullException(nameof(environment)));
        _trust = new GitHostTrust(target, endpoints ?? throw new ArgumentNullException(nameof(endpoints)));
        _git = new GitProcessAdapter(processes, _tools, new GitCredentialHelper(_tools, _trust));
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    /// <inheritdoc />
    public async Task<AuthSetupGitResult> HandleAsync(AuthSetupGitRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        _ = _tools.FindOnPath("git") ?? throw GitErrors.NotFound("git was not found on PATH, so it cannot sign in with msagent.", "Install git from https://git-scm.com/downloads, open a new terminal, then run `msagent auth setup-git` again.");
        var hosts = _trust.CredentialHostPatterns();
        await _git.SetGlobalHelpersAsync(hosts, cancellationToken).ConfigureAwait(false);
        return new AuthSetupGitResult { Hosts = hosts };
    }

    /// <inheritdoc />
    public async Task<AuthGitCredentialResult> HandleAsync(AuthGitCredentialRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return string.Equals(request.Operation, "get", StringComparison.Ordinal) && Trusts(await ReadRequestAsync(cancellationToken).ConfigureAwait(false))
            ? new AuthGitCredentialResult { CredentialLines = ["username=msagent", $"password={await AcquireTokenAsync(cancellationToken).ConfigureAwait(false)}"] }
            : new AuthGitCredentialResult();
    }

    private async Task<Dictionary<string, string>> ReadRequestAsync(CancellationToken cancellationToken)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { Length: > 0 } line)
        {
            if (line.Split('=', 2) is [var key, var value])
            {
                attributes[key] = value;
            }
        }

        return attributes;
    }

    private bool Trusts(Dictionary<string, string> request) => request.TryGetValue("protocol", out var protocol) && string.Equals(protocol, Uri.UriSchemeHttps, StringComparison.Ordinal) && request.TryGetValue("host", out var host) && _trust.Trusts(host);

    private async Task<string> AcquireTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await _tokens.AcquireTokenSilentAsync(_target.Audience, null, cancellationToken).ConfigureAwait(false)).AccessToken;
        }
        catch (AuthenticationException signedOut) when (signedOut.ExitCode == ExitCodes.NotSignedIn)
        {
            throw new AuthenticationException(signedOut.Message, ExitCodes.Failure, signedOut.Remediation, signedOut.ErrorCode);
        }
    }
}
