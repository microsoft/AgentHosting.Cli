using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Telemetry;

internal readonly record struct CommandTelemetryProfileContext(
    string AccountKey,
    Guid TenantId,
    Guid? PrincipalObjectId)
{
    internal static CommandTelemetryProfileContext? FromProfile(
        AuthProfile? profile,
        AuthSessionContext? sessionContext = null)
    {
        if (profile is null
            || string.IsNullOrWhiteSpace(profile.AccountKey)
            || sessionContext is not null
                && (!SameOptional(
                        profile.ClientId,
                        sessionContext.ClientId)
                    || !SameOptional(
                        profile.Authority,
                        sessionContext.Authority)
                    || profile.Cloud != sessionContext.Cloud)
            || !TryParseNonemptyGuid(
                profile.AuthenticatedTenantId,
                out Guid tenantId))
        {
            return null;
        }

        Guid? principalObjectId = null;
        if (!string.IsNullOrWhiteSpace(profile.PrincipalObjectId))
        {
            if (TryParseNonemptyGuid(
                    profile.PrincipalObjectId,
                    out Guid parsedPrincipal))
            {
                principalObjectId = parsedPrincipal;
            }
        }

        return new(profile.AccountKey, tenantId, principalObjectId);
    }

    private static bool TryParseNonemptyGuid(string? value, out Guid parsed) =>
        Guid.TryParse(value, out parsed) && parsed != Guid.Empty;

    private static bool SameOptional(string? left, string? right) =>
        string.Equals(
            string.IsNullOrWhiteSpace(left) ? null : left,
            string.IsNullOrWhiteSpace(right) ? null : right,
            StringComparison.OrdinalIgnoreCase);
}

internal interface ICommandTelemetryProfileContextSource
{
    ValueTask<CommandTelemetryProfileContext?> GetSelectedAsync(
        CancellationToken cancellationToken);
}

internal sealed class CommandTelemetryProfileContextSource(
    IAuthProfileTelemetryStore profileStore,
    AuthSessionContext sessionContext) :
    ICommandTelemetryProfileContextSource
{
    private readonly IAuthProfileTelemetryStore _profileStore =
        profileStore ?? throw new ArgumentNullException(nameof(profileStore));
    private readonly AuthSessionContext _sessionContext =
        sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));

    public async ValueTask<CommandTelemetryProfileContext?> GetSelectedAsync(
        CancellationToken cancellationToken)
    {
        AuthProfileTelemetryContext? context =
            await _profileStore
                .GetActiveTelemetryContextAsync(cancellationToken)
                .ConfigureAwait(false);
        return context is { } selected
            && SameOptional(selected.ClientId, _sessionContext.ClientId)
            && SameOptional(selected.Authority, _sessionContext.Authority)
            && selected.Cloud == _sessionContext.Cloud
            ? new CommandTelemetryProfileContext(
                selected.AccountKey,
                selected.TenantId,
                selected.PrincipalObjectId)
            : null;
    }

    private static bool SameOptional(string? left, string? right) =>
        string.Equals(
            string.IsNullOrWhiteSpace(left) ? null : left,
            string.IsNullOrWhiteSpace(right) ? null : right,
            StringComparison.OrdinalIgnoreCase);
}
