using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Auth;

internal sealed class OwnedProfileCredentialInventory
{
    private readonly OwnedProfileFiles _files;
    private readonly IAuthProfileStore _profiles;
    private readonly IOwnedProtectedStorageBackend _backend;
    internal OwnedProfileCredentialInventory(string directory, IAuthProfileStore profiles, IOwnedProtectedStorageBackend backend)
    {
        _files = new(directory);
        _profiles = profiles;
        _backend = backend;
    }

    internal IDisposable Lease(OwnedProfileReferences references, CancellationToken ct)
    {
        var streams = new List<FileStream>();
        try
        {
            foreach (var item in references.Items.OrderBy(r => r.OpaqueId, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                streams.Add(_files.OpenLease(Name(item, "lease")));
            }
            return new LeaseHandle(streams);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var stream in streams) stream.Dispose();
            throw new ProfileCredentialCleanupException(false);
        }
        catch
        {
            foreach (var stream in streams) stream.Dispose();
            throw;
        }
    }

    internal async Task TrackAsync(OwnedProfileReferences references, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Marker { Reference = references.Value });
        foreach (var item in references.Items)
            await _files.WriteAsync(Name(item, "marker"), bytes, ct).ConfigureAwait(false);
    }

    internal async Task RemoveWhileLeasedAsync(OwnedProfileReferences references, CancellationToken ct)
    {
        foreach (var item in references.Items)
            await RemoveItemWhileLeasedAsync(item, ct).ConfigureAwait(false);
    }

    private async Task RemoveItemWhileLeasedAsync(OwnedProtectedStorageReference item, CancellationToken ct)
    {
        var profiles = await _profiles.GetProfilesAsync(ct).ConfigureAwait(false);
        if (profiles.Any(p => OwnedProfileReferences.IsOwned(p.CredentialReference) &&
            OwnedProfileReferences.Parse(p.CredentialReference).Contains(item))) return;
        await _backend.ClearAsync(item, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        _files.Delete(Name(item, "marker"));
    }

    internal async Task ClearAsync(CancellationToken ct)
    {
        bool incomplete = false;
        AgentHostingException? primary = null;
        foreach (var name in _files.Markers())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var item = FromMarkerName(name);
                using var lease = _files.OpenLease(Name(item, "lease"));
                await RemoveItemWhileLeasedAsync(item, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AgentHostingException or IOException or UnauthorizedAccessException)
            {
                incomplete = true;
                primary ??= ex as AgentHostingException;
            }
        }
        if (incomplete) throw new ProfileCredentialCleanupException(false, primary);
    }

    private static OwnedProtectedStorageReference FromMarkerName(string name)
    {
        var kind = name.StartsWith("credential-", StringComparison.Ordinal) ? OwnedProtectedStorageKind.Credential : OwnedProtectedStorageKind.TokenCache;
        var prefix = kind == OwnedProtectedStorageKind.Credential ? "credential-" : "token-cache-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".marker", StringComparison.Ordinal))
            throw new AuthProfileStateException("Invalid owned reference inventory filename.");
        try { return new(kind, name[prefix.Length..^".marker".Length]); }
        catch (ArgumentException) { throw new AuthProfileStateException("Invalid owned reference inventory filename."); }
    }

    private static string Name(OwnedProtectedStorageReference r, string extension) =>
        $"{(r.Kind == OwnedProtectedStorageKind.Credential ? "credential" : "token-cache")}-{r.OpaqueId}.{extension}";

    private sealed class Marker
    {
        public string? Reference { get; init; }
    }
    private sealed class LeaseHandle(List<FileStream> streams) : IDisposable
    {
        public void Dispose() { foreach (var stream in streams) stream.Dispose(); }
    }
}
