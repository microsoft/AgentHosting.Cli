using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>Adapts legacy resource consumers to one explicit immutable profile snapshot.</summary>
public sealed class ProfileBoundTokenProvider : ITokenProvider
{
    private readonly IProfileResourceTokenProvider _tokens;
    public EnvironmentResolutionContext Context { get; }

    public ProfileBoundTokenProvider(IProfileResourceTokenProvider tokens, EnvironmentResolutionContext context)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
        string resource, string? targetTenantId, CancellationToken cancellationToken) =>
        (await _tokens.AcquireAsync(Context, resource, targetTenantId ?? Context.Profile.TenantId ?? string.Empty,
            [], cancellationToken).ConfigureAwait(false)).Token;

    public async Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
        IReadOnlyList<string> scopes, string? targetTenantId, CancellationToken cancellationToken) =>
        (await _tokens.AcquireAsync(Context, Context.Target.Audience, targetTenantId ?? Context.Profile.TenantId ?? string.Empty,
            [], cancellationToken, scopes).ConfigureAwait(false)).Token;
}
