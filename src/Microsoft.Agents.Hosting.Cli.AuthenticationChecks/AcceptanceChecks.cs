using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.AuthenticationChecks;

public static class AcceptanceChecks
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static void PrepareDaemonControlDirectory(string argument, bool ownedDirectory,
        Action<UnixFileMode> chmod, Func<UnixFileMode> readMode)
    {
        RequireDaemonControlArgument(argument, ownedDirectory);
        chmod(OwnerOnly);
        RequireDaemonControlDirectory(argument, ownedDirectory, readMode());
    }

    public static void RequireDaemonControlDirectory(string argument, bool ownedDirectory, UnixFileMode mode)
    {
        RequireDaemonControlArgument(argument, ownedDirectory);
        if (mode != OwnerOnly)
            throw new InvalidOperationException("The owned daemon control directory must have exactly owner-only 0700 permissions.");
    }

    public static void RequireDaemonControlPublication(string? publication)
    {
        if (publication != "GNOME_KEYRING_CONTROL=control")
            throw new InvalidOperationException("The daemon must publish only the exact prepared relative control directory; fallback is forbidden.");
    }

    private static void RequireDaemonControlArgument(string argument, bool ownedDirectory)
    {
        // GNOME copies this relative pathname into Linux's 108-byte sockaddr_un.sun_path.
        if (!ownedDirectory || System.Text.Encoding.UTF8.GetByteCount(argument + "/control") > 107 || argument != "control")
            throw new InvalidOperationException("Only the short, owned, ordinary relative daemon control directory is permitted.");
    }

    public static bool DeleteIssuedFixtures(IEnumerable<string> paths, Action<string> delete)
    {
        bool complete = true;
        foreach (var path in paths)
        {
            try { delete(path); }
            catch { complete = false; }
        }
        return complete;
    }

    public static async Task CheckDeniedStorageAsync(IOwnedProtectedStorageBackend backend,
        Func<IReadOnlyList<OwnedProtectedStorageReference>, Task> deny,
        Func<Task> requireDenied, Func<Task> restore)
        {
            var references = new[] { OwnedProtectedStorageKind.Credential, OwnedProtectedStorageKind.TokenCache }
                .Select(kind => new OwnedProtectedStorageReference(kind, Guid.NewGuid().ToString("N"))).ToArray();
            var issued = new List<OwnedProtectedStorageReference>();
            byte[] original = RandomNumberGenerator.GetBytes(48), replacement = RandomNumberGenerator.GetBytes(48);
            bool denialIssued = false, cleanupFailed = false, restorationFailed = false;
            ExceptionDispatchInfo? failure = null;
            try
            {
                foreach (var reference in references)
                {
                    var existing = await backend.ReadAsync(reference, CancellationToken.None);
                    if (existing is not null)
                    {
                        CryptographicOperations.ZeroMemory(existing);
                        throw new InvalidOperationException("Generated denial reference already exists.");
                    }
                    issued.Add(reference);
                    await backend.WriteAsync(reference, original, CancellationToken.None);
                    await RequireOriginal(reference);
                }
                denialIssued = true;
                await deny(references);
                await requireDenied();
                foreach (var reference in references)
                {
                    await RequireFailure(async () =>
                    {
                        byte[]? read = await backend.ReadAsync(reference, CancellationToken.None);
                        if (read is not null) CryptographicOperations.ZeroMemory(read);
                    });
                    await RequireFailure(() => backend.WriteAsync(reference, replacement, CancellationToken.None));
                    await RequireFailure(() => backend.ClearAsync(reference, CancellationToken.None));
                }
            }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally
            {
                if (denialIssued)
                {
                    try
                    {
                        await restore();
                        foreach (var reference in issued) await RequireOriginal(reference);
                    }
                    catch { cleanupFailed = true; restorationFailed = true; }
                }
                foreach (var reference in issued)
                {
                    if (restorationFailed) break;
                    try
                    {
                        await backend.ClearAsync(reference, CancellationToken.None);
                        var remaining = await backend.ReadAsync(reference, CancellationToken.None);
                        if (remaining is not null)
                        {
                            CryptographicOperations.ZeroMemory(remaining);
                            cleanupFailed = true;
                        }
                    }
                    catch { cleanupFailed = true; }
                }
                CryptographicOperations.ZeroMemory(original);
                CryptographicOperations.ZeroMemory(replacement);
            }
            if (cleanupFailed) throw new InvalidOperationException("Denied fixture restoration, retention or exact cleanup failed.", failure?.SourceException);
            failure?.Throw();

            async Task RequireFailure(Func<Task> operation)
            {
                bool rejected = false;
                try { await operation(); }
                catch (CacheProtectionUnavailableException) { rejected = true; }
                await requireDenied();
                if (!rejected) throw new InvalidOperationException("A denied operation did not fail; missing or successful reads cannot prove denial.");
            }

            async Task RequireOriginal(OwnedProtectedStorageReference reference)
            {
                var read = await backend.ReadAsync(reference, CancellationToken.None);
                try
                {
                    if (read is null || !CryptographicOperations.FixedTimeEquals(original, read))
                        throw new InvalidOperationException("Denied fixture changed or lost its original protected value.");
                }
                finally { if (read is not null) CryptographicOperations.ZeroMemory(read); }
            }
        }

    public static void CheckCertificateCases(Func<string, string?, X509Certificate2> load,
        Action<X509Certificate2> verify, string password)
    {
        foreach (var (name, secret) in new (string, string?)[] {
            ("protected.pfx", password), ("passwordless.pfx", null), ("passwordless.pfx", "") })
        {
            using var certificate = load(name, secret);
            verify(certificate);
        }
        foreach (var (name, secret) in new (string, string?)[] {
            ("protected.pfx", null), ("protected.pfx", "wrong-fixture-password"),
            ("passwordless.pfx", password), ("invalid.pfx", password), ("public-only.pfx", password) })
        {
            try
            {
                using var unexpected = load(name, secret);
            }
            catch (AuthenticationException) { continue; }
            throw new InvalidOperationException("A malformed, wrong-password or keyless generated PFX was accepted.");
        }
    }

    public static void RequireNativePreparation(bool dedicated, bool prepared, string expectedRid,
        string os, Architecture process, Architecture hardware)
    {
        if (!dedicated || !prepared)
            throw new InvalidOperationException("Native checks require explicit dedicated, prepared test-owned state.");
        RequireArchitecture(expectedRid, os, process, hardware);
    }

    public static void RequireArchitecture(string expectedRid, string os, Architecture process, Architecture hardware)
    {
        if (!PackageChecks.RuntimeIdentifiers.Contains(expectedRid, StringComparer.Ordinal) ||
            expectedRid != $"{os}-{process.ToString().ToLowerInvariant()}" ||
            process != hardware && !(os == "win" && process == Architecture.X86 && hardware == Architecture.X64))
            throw new InvalidOperationException("Expected RID must match the native OS and process architecture; emulation is not evidence.");
    }

    public static async Task CheckOwnedStorageAsync(IOwnedProtectedStorageBackend backend, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        var credential = new OwnedProtectedStorageReference(OwnedProtectedStorageKind.Credential, id);
        var cache = new OwnedProtectedStorageReference(OwnedProtectedStorageKind.TokenCache, id);
        var sibling = new OwnedProtectedStorageReference(OwnedProtectedStorageKind.Credential, Guid.NewGuid().ToString("N"));
        var issued = new List<OwnedProtectedStorageReference>();
        byte[] value = RandomNumberGenerator.GetBytes(48);
        ExceptionDispatchInfo? failure = null;
        bool cleanupFailed = false;
        try
        {
            foreach (var reference in new[] { credential, cache, sibling })
            {
                if (await HasValue(reference, ct))
                    throw new InvalidOperationException("Generated reference was not empty; no mutation attempted.");
                issued.Add(reference);
                await backend.WriteAsync(reference, value, ct);
                await RequireValue(reference);
            }
            await backend.ClearAsync(credential, ct);
            if (await HasValue(credential, ct))
                throw new InvalidOperationException("Exact reference deletion failed.");
            await RequireValue(cache);
            await RequireValue(sibling);
        }
        catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
            // An issued mutation may have committed before failing or observing cancellation.
            foreach (var reference in issued)
            {
                try
                {
                    await backend.ClearAsync(reference, CancellationToken.None);
                    if (await HasValue(reference, CancellationToken.None)) cleanupFailed = true;
                }
                catch { cleanupFailed = true; }
            }
        }
        if (cleanupFailed) throw new InvalidOperationException("Test-owned native cleanup failed.", failure?.SourceException);
        failure?.Throw();

        async Task<bool> HasValue(OwnedProtectedStorageReference reference, CancellationToken token)
        {
            var read = await backend.ReadAsync(reference, token);
            try { return read is not null; }
            finally { if (read is not null) CryptographicOperations.ZeroMemory(read); }
        }

        async Task RequireValue(OwnedProtectedStorageReference reference)
        {
            byte[]? read = await backend.ReadAsync(reference, ct);
            try
            {
                if (read is null || !CryptographicOperations.FixedTimeEquals(value, read))
                    throw new InvalidOperationException("Protected native round trip failed.");
            }
            finally { if (read is not null) CryptographicOperations.ZeroMemory(read); }
        }
    }
}
