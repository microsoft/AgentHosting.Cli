using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Rendering;

internal static class AuthProfileDisplay
{
    internal static string Account(AuthProfileInfo profile) =>
        profile.PrincipalType == AuthPrincipalType.ServicePrincipal && string.IsNullOrWhiteSpace(profile.Account)
            ? profile.ClientId ?? string.Empty : profile.Account;

    internal static string Tenant(string? tenantId) =>
        string.IsNullOrWhiteSpace(tenantId) ? "(none)" : tenantId;
}
