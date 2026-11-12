using Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Auth;

internal sealed class OwnedProfileReferences
{
    internal OwnedProtectedStorageReference Cache { get; }
    internal OwnedProtectedStorageReference? Credential { get; }
    internal string Value => $"{AuthCredentialReference.OwnedV1Prefix}{Cache.OpaqueId}:{Credential?.OpaqueId ?? "-"}";
    internal IEnumerable<OwnedProtectedStorageReference> Items => Credential is null ? [Cache] : [Cache, Credential];

    private OwnedProfileReferences(string cacheId, string? credentialId)
    {
        Cache = new(OwnedProtectedStorageKind.TokenCache, cacheId);
        Credential = credentialId is null ? null : new(OwnedProtectedStorageKind.Credential, credentialId);
    }

    internal static OwnedProfileReferences New(bool credential) =>
        new(Guid.NewGuid().ToString("N"), credential ? Guid.NewGuid().ToString("N") : null);

    internal static bool IsOwned(string? value) => value?.StartsWith(AuthCredentialReference.OwnedV1Prefix, StringComparison.Ordinal) == true;

    internal static OwnedProfileReferences Parse(string? value)
    {
        try
        {
            if (!IsOwned(value)) throw new ArgumentException("Invalid reference.", nameof(value));
            var pieces = value![AuthCredentialReference.OwnedV1Prefix.Length..].Split(':');
            if (pieces.Length != 2) throw new ArgumentException("Invalid reference.", nameof(value));
            return new(pieces[0], pieces[1] == "-" ? null : pieces[1]);
        }
        catch (ArgumentException)
        {
            throw new AuthProfileStateException("The profile's owned credential reference is invalid.",
                "Repair authentication metadata; no credential storage was accessed.");
        }
    }

    internal bool Contains(OwnedProtectedStorageReference item) =>
        Items.Any(r => r.Kind == item.Kind && r.OpaqueId == item.OpaqueId);
}

public sealed class ProfileCredentialCleanupException : AuthenticationException, IAuthOperationPartialState
{
    private const string CleanupGuidance =
        "Retry owned credential cleanup after resolving storage access or in-flight reference use. No shared accounts or source certificates were removed.";

    public ProfileCredentialCleanupException(bool metadataCommitted, AgentHostingException? primary = null)
        : base(
            metadataCommitted
                ? "Authentication metadata was committed, but owned credential finalization or cleanup is incomplete."
                : "Owned credential cleanup is incomplete; recoverable references were retained.",
            primary?.ExitCode ?? ExitCodes.Failure,
            primary?.ErrorKind,
            primary?.Remediation is { Length: > 0 } remediation
                ? $"{remediation} {CleanupGuidance}"
                : CleanupGuidance,
            primary,
            primary?.ErrorCode ?? ErrorCodes.Authentication.OwnedCredentialCleanup)
    {
        MetadataCommitted = metadataCommitted;
    }

    public bool MetadataCommitted { get; }
    public bool CleanupIncomplete => true;
    public AuthProfileInfo? Profile => null;
}
