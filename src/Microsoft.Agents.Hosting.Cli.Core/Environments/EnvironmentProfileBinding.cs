using System.Security.Cryptography;
using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

internal static class EnvironmentProfileBinding
{
    internal static bool HasValidOwnershipMetadata(ActiveEnvironment environment) =>
        environment.Owner is not { } owner
            ? environment.DefaultGeneration is null
            : !string.IsNullOrWhiteSpace(owner.ProfileId)
                && !string.IsNullOrWhiteSpace(owner.TenantId)
                && string.Equals(owner.TenantId, environment.TenantId, StringComparison.OrdinalIgnoreCase)
                && Enum.IsDefined(owner.Cloud)
                && PowerPlatformApiRingSelection.IsDeclared(owner.Ring)
                && (owner.Cloud == CloudInstance.Public || owner.Ring == PowerPlatformApiRing.Production)
                && owner.IdentityFingerprint is { Length: 64 } fingerprint
                && fingerprint.All(char.IsAsciiHexDigit)
                && Guid.TryParseExact(environment.DefaultGeneration, "N", out _);

    internal static ActiveEnvironment BindDefault(AuthProfile profile, ActiveEnvironment environment) =>
        environment with
        {
            Owner = Owner(profile),
            DefaultGeneration = Guid.NewGuid().ToString("N"),
            Context = null,
        };

    internal static bool IsOwnedBy(ActiveEnvironment environment, AuthProfile? profile) =>
        profile is not null
        && !string.IsNullOrWhiteSpace(profile.ProfileId)
        && !string.IsNullOrWhiteSpace(profile.TenantId)
        && string.Equals(environment.TenantId, profile.TenantId, StringComparison.OrdinalIgnoreCase)
        && environment.Owner == Owner(profile);

    internal static bool IsCurrentOverride(ActiveEnvironment environment, AuthProfile? profile) =>
        IsOwnedBy(environment, profile)
        && (profile!.DefaultEnvironment is null
            || (!string.IsNullOrEmpty(environment.DefaultGeneration)
                && environment.DefaultGeneration == profile.DefaultEnvironment.DefaultGeneration));

    private static EnvironmentSelectionOwner Owner(AuthProfile profile) =>
        new(profile.ProfileId, profile.Cloud, profile.Ring, profile.TenantId ?? string.Empty, Fingerprint(profile));

    private static string Fingerprint(AuthProfile profile)
    {
        string?[] dimensions =
        [
            profile.PrincipalType == AuthPrincipalType.User ? profile.HomeAccountId ?? profile.AccountKey : profile.AccountKey,
            profile.ClientId, profile.TenantId, profile.Authority, profile.Cloud.ToString(), profile.Ring.ToString(),
            profile.CredentialMethod.ToString(), profile.PrincipalType.ToString(),
            profile.CertificateThumbprint, profile.CertificatePath,
        ];
        var identity = string.Join('|', dimensions.Select(value => FormattableString.Invariant($"{value?.Length ?? -1}:{value}")));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}
