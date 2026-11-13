using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Auth;

public interface IProfileSelectedAccountTokenAcquisition
{
    Task<TokenAcquisitionResult?> TryAcquireSelectedTokenAsync(AuthProfile profile, IReadOnlyList<string> scopes, string targetTenantId, CancellationToken ct);
}

public interface IProfileConsentTokenAcquisition
{
    Task<TokenAcquisitionResult> AcquireConsentAsync(AuthProfile profile, Guid clientId, string redirectUri,
        IReadOnlyList<string> scopes, string? targetTenantId, CancellationToken ct, bool trySilentFirst = true);
}
