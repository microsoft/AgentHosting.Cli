using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>
/// Token-free, atomic Universal profile persistence implementing
/// <see cref="Microsoft.Agents.Hosting.Cli.Core.Ports.IAuthProfileManagementStore"/>.
/// Short interprocess transactions protect read-modify-write operations. Reads migrate version 1
/// in memory only; successful mutations persist version 2. Credential caches are never accessed.
/// </summary>
public sealed class FileAuthProfileStore : IAuthProfileManagementStore, IAuthProfileTelemetryStore, IAuthProfileCreationStore
{
    private readonly string _profilesFilePath;
    private readonly IAuthProfileStateObserver? _stateObserver;
    private readonly AuthProfileMutationRevision _mutationRevision;

    public FileAuthProfileStore(
        string profilesFilePath,
        IAuthProfileStateObserver? stateObserver = null,
        AuthProfileMutationRevision? mutationRevision = null)
    {
        if (string.IsNullOrWhiteSpace(profilesFilePath))
        {
            throw new ArgumentException("Profiles file path is required.", nameof(profilesFilePath));
        }

        _profilesFilePath = Path.GetFullPath(profilesFilePath);
        _stateObserver = stateObserver;
        _mutationRevision = mutationRevision ?? new AuthProfileMutationRevision();
    }

    /// <inheritdoc />
    public async Task<AuthProfile?> GetActiveProfileAsync(CancellationToken cancellationToken) =>
        Active(await ReadDocumentAsync(cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuthProfile>> GetProfilesAsync(CancellationToken cancellationToken)
    {
        var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
        return document.Profiles.Select(p => ToProfile(p, p.ProfileId == document.ActiveProfileId)).ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// A captured opaque ID and revision permit a guarded compatibility ring update.
    /// Only active, unbound legacy user credentials may retarget their ring; the old default is
    /// discarded and the connection URL is refreshed. New profiles retain full identity matching.
    /// </remarks>
    public async Task UpsertAsync(AuthProfile profile, CancellationToken cancellationToken) =>
        _ = await CreateCoreAsync(profile, legacyUpsert: true, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<AuthProfile> CreateAsync(AuthProfile profile, CancellationToken cancellationToken) =>
        (await CreateCoreAsync(profile, legacyUpsert: false, cancellationToken).ConfigureAwait(false)).Committed;

    /// <inheritdoc />
    public Task<AuthProfileCreation> CreateWithReplacementAsync(AuthProfile profile, CancellationToken cancellationToken) =>
        CreateCoreAsync(profile, legacyUpsert: false, cancellationToken);

    private Task<AuthProfileCreation> CreateCoreAsync(AuthProfile profile, bool legacyUpsert, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.AccountKey))
        {
            throw new UsageException("Profile account metadata is required.");
        }

        ValidateName(profile.Name);
        var incoming = ToStored(profile);
        if (legacyUpsert && profile.CredentialMethod == AuthCredentialMethod.Broker)
        {
            // Older callers carry the acquired flow rather than the new credential-method contract.
            incoming.CredentialMethod = LegacyMethod(profile.FlowType);
        }

        ValidateCoordinates(incoming, persisted: false);
        return MutateAsync(document =>
        {
            var existing = document.Profiles.SingleOrDefault(p => Identity(p) == Identity(incoming));
            bool retargeting = false;
            if (legacyUpsert && !string.IsNullOrEmpty(profile.ProfileId))
            {
                var captured = document.Profiles.SingleOrDefault(p => p.ProfileId == profile.ProfileId);
                if (captured is null || captured.Revision != profile.Revision)
                    throw new UsageException("The captured authentication profile changed. Resolve that exact profile and retry.");
                if ((captured.Ring ?? PowerPlatformApiRing.Production) != incoming.Ring)
                {
                    var retargetedIdentity = ToStored(ToProfile(captured, true) with { Ring = profile.Ring });
                    if (captured.ProfileId != document.ActiveProfileId
                        || captured.PrincipalType != AuthPrincipalType.User
                        || captured.CredentialMethod is not (AuthCredentialMethod.Broker or AuthCredentialMethod.Browser or AuthCredentialMethod.DeviceCode)
                        || captured.CredentialReference is not null || incoming.CredentialReference is not null
                        || Identity(retargetedIdentity) != Identity(incoming)
                        || (existing is not null && existing.ProfileId != captured.ProfileId))
                        throw new UsageException("The captured profile cannot be retargeted by legacy ring selection. Select or create a validated profile for the requested target.");

                    existing = captured;
                    retargeting = true;
                    incoming.DefaultEnvironment = null;
                    incoming.ConnectionUrl = ConnectionUrl(incoming);
                }
            }
            var replaced = existing is null ? null : ToProfile(existing, existing.ProfileId == document.ActiveProfileId);
            incoming.ProfileId = existing?.ProfileId ?? Guid.NewGuid().ToString("N");
            incoming.Revision = NextRevision(existing?.Revision ?? 0);
            RequireUniqueName(document, incoming.Name, incoming.ProfileId);
            if (existing is not null)
            {
                document.Profiles.Remove(existing);
                // Legacy sign-in callers do not carry profile-owned selection metadata.
                if (legacyUpsert)
                {
                    incoming.Name ??= existing.Name;
                }

                if (!retargeting)
                    incoming.DefaultEnvironment ??= existing.DefaultEnvironment;
                incoming.CredentialReference ??= existing.CredentialReference;
            }

            if (incoming.DefaultEnvironment is not null && profile.DefaultEnvironment is not null)
            {
                incoming.DefaultEnvironment = EnvironmentProfileBinding.BindDefault(ToProfile(incoming, true), incoming.DefaultEnvironment);
            }

            document.Profiles.Add(incoming);
            Activate(document, incoming, incrementSelected: false);
            return new AuthProfileCreation(ToProfile(incoming, true), replaced);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AuthProfile> SelectAsync(AuthProfileSelector selector, CancellationToken cancellationToken) =>
        MutateAsync(document =>
        {
            var selected = Resolve(document, selector);
            Activate(document, selected);
            return ToProfile(selected, true);
        }, cancellationToken);

    /// <inheritdoc />
    public async Task SetActiveAsync(string accountKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(accountKey))
        {
            throw new UsageException("An account key or opaque profile id is required.");
        }

        await MutateAsync(document =>
        {
            var matches = document.Profiles
                .Where(p => p.ProfileId == accountKey || p.AccountKey == accountKey)
                .Take(2)
                .ToList();
            if (matches.Count > 1)
            {
                throw new UsageException("The account key or profile id matches multiple profiles. Select a profile by its exact name or index.");
            }

            var selected = matches.SingleOrDefault() ?? throw new AuthenticationException(
                "No AgentHost profile is stored for the specified account key or profile id.",
                ExitCodes.Usage,
                remediation: "Run `msagent auth status` to list stored profiles.");

            Activate(document, selected);
            return ToProfile(selected, true);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<AuthProfile> UpdateAsync(
        AuthProfileSelector selector, AuthProfileUpdate update, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(update);
        if (update.Name is { Length: > 0 })
        {
            ValidateName(update.Name);
        }

        return MutateAsync(document =>
        {
            var selected = Resolve(document, selector);
            return ApplyUpdate(document, selected, update);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AuthProfile> UpdateAsync(
        string profileId, long expectedRevision, AuthProfileUpdate update, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(update);
        if (update.Name is { Length: > 0 }) ValidateName(update.Name);
        return MutateAsync(document =>
        {
            var selected = document.Profiles.SingleOrDefault(p => p.ProfileId == profileId);
            if (selected is null || selected.Revision != expectedRevision)
                throw new UsageException("The captured authentication profile changed. Resolve that exact profile and retry.");
            return ApplyUpdate(document, selected, update);
        }, cancellationToken);
    }

    private static AuthProfile ApplyUpdate(AuthProfilesDocument document, StoredAuthProfile selected, AuthProfileUpdate update)
    {
        if (update.Name is not null)
        {
            string? name = update.Name.Length == 0 ? null : update.Name;
            RequireUniqueName(document, name, selected.ProfileId);
            selected.Name = name;
        }
        if (update.DefaultEnvironment is not null)
            selected.DefaultEnvironment = EnvironmentProfileBinding.BindDefault(ToProfile(selected, selected.ProfileId == document.ActiveProfileId), update.DefaultEnvironment);
        selected.Revision = NextRevision(selected.Revision);
        return ToProfile(selected, selected.ProfileId == document.ActiveProfileId);
    }

    /// <inheritdoc />
    public Task<AuthProfileDeletion> DeleteAsync(AuthProfileSelector selector, CancellationToken cancellationToken) =>
        MutateAsync(document =>
        {
            var selected = Resolve(document, selector);
            bool wasActive = selected.ProfileId == document.ActiveProfileId;
            var deleted = ToProfile(selected, wasActive);
            document.Profiles.Remove(selected);
            if (wasActive)
            {
                document.ActiveProfileId = null;
                if (document.Profiles.Count > 0)
                {
                    Activate(document, document.Profiles[0]);
                }
            }

            return new AuthProfileDeletion(deleted, Active(document));
        }, cancellationToken);

    /// <inheritdoc />
    public Task<AuthProfile> SetDefaultEnvironmentAsync(
        string profileId, long expectedRevision, ActiveEnvironment environment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(environment);
        return MutateAsync(document =>
        {
            var selected = document.Profiles.FirstOrDefault(p => p.ProfileId == profileId);
            if (selected is null || selected.ProfileId != document.ActiveProfileId || selected.Revision != expectedRevision)
            {
                throw new UsageException("The authentication profile changed while selecting the environment. Resolve the active profile and retry.");
            }

            selected.DefaultEnvironment = EnvironmentProfileBinding.BindDefault(ToProfile(selected, true), environment);
            selected.Revision = NextRevision(selected.Revision);
            return ToProfile(selected, true);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long revision;
        using (await AcquireLockAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(_profilesFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw StateIo("cleared", ex);
            }

            revision = _mutationRevision.Advance();
        }

        ObserveActiveProfile(null, revision);
    }

    /// <inheritdoc />
    public async ValueTask<AuthProfileTelemetryContext?> GetActiveTelemetryContextAsync(CancellationToken cancellationToken)
    {
        var active = Active(await ReadDocumentAsync(cancellationToken).ConfigureAwait(false));
        if (active is null || !TryParseNonemptyGuid(active.AuthenticatedTenantId, out Guid tenantId))
        {
            return null;
        }

        Guid? principal = TryParseNonemptyGuid(active.PrincipalObjectId, out Guid parsed) ? parsed : null;
        return new(active.AccountKey, active.ClientId, active.Authority, active.Cloud, tenantId, principal);
    }

    private async Task<T> MutateAsync<T>(Func<AuthProfilesDocument, T> mutate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        T result;
        AuthProfile? active;
        long revision;
        using (await AcquireLockAsync(cancellationToken).ConfigureAwait(false))
        {
            var document = await ReadDocumentAsync(cancellationToken).ConfigureAwait(false);
            result = mutate(document);
            await WriteDocumentAsync(document, cancellationToken).ConfigureAwait(false);
            active = Active(document);
            revision = _mutationRevision.Advance();
        }

        // Observers can re-enter the store or block; never hold a file lock across callbacks.
        ObserveActiveProfile(active, revision);
        return result;
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_profilesFilePath)!);
                // The sibling stays on disk: unlinking a lock file allows two owners of different inodes.
                return OpenOwnerOnly(_profilesFilePath + ".lock", FileMode.OpenOrCreate);
            }
            catch (IOException ex) when (IsLockContention(ex, OperatingSystem.IsMacOS()))
            {
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(5))
                {
                    throw StateIo("locked", ex);
                }

                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw StateIo("locked", ex);
            }
        }
    }

    internal static bool IsLockContention(IOException exception, bool isMacOS) =>
        (exception.HResult & 0xffff) is 32 or 33 or 11 ||
        // Darwin EWOULDBLOCK is raw errno 35; the same errno is EDEADLK on Linux.
        (isMacOS && exception.HResult == 35);

    private async Task<AuthProfilesDocument> ReadDocumentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string json;
        try
        {
            using var stream = new FileStream(_profilesFilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var reader = new StreamReader(stream);
            json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AuthProfilesDocument { SchemaVersion = 2 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw StateIo("read", ex);
        }

        AuthProfilesDocument? document;
        try
        {
            using var parsed = JsonDocument.Parse(json);
            RejectDuplicateProperties(parsed.RootElement, AuthProfileJsonContext.Default.AuthProfilesDocument);
            document = JsonSerializer.Deserialize(json, AuthProfileJsonContext.Default.AuthProfilesDocument);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw Corrupt("the profile store is not valid JSON");
        }

        return ValidateAndMigrate(document);
    }

    private static void RejectDuplicateProperties(JsonElement element, JsonTypeInfo metadata)
    {
        if (element.ValueKind != JsonValueKind.Object || metadata.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var mapped = metadata.Properties.FirstOrDefault(p => string.Equals(p.Name, property.Name, StringComparison.Ordinal));
            if (mapped is null)
            {
                // Unknown fields and their entire payloads are ignored by the persisted schema.
                continue;
            }

            if (!keys.Add(property.Name))
            {
                throw Corrupt("duplicate JSON property");
            }

            if (mapped.PropertyType == typeof(List<StoredAuthProfile>) && property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var profile in property.Value.EnumerateArray())
                {
                    RejectDuplicateProperties(profile, AuthProfileJsonContext.Default.StoredAuthProfile);
                }
            }
            else if (AuthProfileJsonContext.Default.GetTypeInfo(mapped.PropertyType) is { Kind: JsonTypeInfoKind.Object } nested)
            {
                RejectDuplicateProperties(property.Value, nested);
            }
        }
    }

    private static AuthProfilesDocument ValidateAndMigrate(AuthProfilesDocument? document)
    {
        if (document is null)
        {
            throw Corrupt("the profile store deserialized to null");
        }

        if (document.SchemaVersion is not (1 or 2))
        {
            throw Corrupt($"unsupported schema version {document.SchemaVersion}");
        }

        if (document.Profiles is null || document.Profiles.Any(p => p is null || string.IsNullOrWhiteSpace(p.AccountKey)))
        {
            throw Corrupt("a stored profile or account key is missing");
        }

        bool legacy = document.SchemaVersion == 1;
        if (legacy && document.ActiveProfileId is not null)
        {
            throw Corrupt("a version 1 document contains a version 2 active pointer");
        }

        if (legacy && document.Profiles.GroupBy(p => p.AccountKey, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            throw Corrupt("duplicate legacy account keys");
        }

        foreach (var profile in document.Profiles)
        {
            ValidateCoordinates(profile, persisted: true);
            profile.HomeAccountId ??=
                profile.PrincipalType == AuthPrincipalType.User
                && profile.CredentialMethod != AuthCredentialMethod.DefaultAzureCredential
                    ? profile.AccountKey
                    : null;
            profile.CredentialMethod ??= LegacyMethod(profile.FlowType);
            profile.ConnectionUrl ??= ConnectionUrl(profile);
            if (legacy)
            {
                profile.ProfileId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Identity(profile))));
                profile.Revision = 1;
            }
            else if (string.IsNullOrWhiteSpace(profile.ProfileId) || profile.Revision < 1)
            {
                throw Corrupt("a profile identity or revision is missing or invalid");
            }

            if (profile.Name is not null && profile.Name.Length is < 1 or > 30)
            {
                throw Corrupt("a profile name has invalid length");
            }
        }

        if (document.Profiles.GroupBy(p => p.ProfileId, StringComparer.Ordinal).Any(g => g.Count() > 1) ||
            document.Profiles.GroupBy(Identity, StringComparer.Ordinal).Any(g => g.Count() > 1) ||
            document.Profiles.Where(p => p.Name is not null).GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() > 1))
        {
            throw Corrupt("duplicate profile identities or names");
        }

        if (legacy)
        {
            if (document.ActiveAccountKey is not null)
            {
                document.ActiveProfileId = document.Profiles.FirstOrDefault(p => p.AccountKey == document.ActiveAccountKey)?.ProfileId
                    ?? throw Corrupt("the legacy active pointer does not match any stored profile");
            }
            else
            {
                document.ActiveProfileId = null;
            }
        }
        else if (document.ActiveAccountKey is not null)
        {
            throw Corrupt("a version 2 document contains a legacy active pointer");
        }

        if (document.ActiveProfileId is not null && !document.Profiles.Any(p => p.ProfileId == document.ActiveProfileId))
        {
            throw Corrupt("the active pointer does not match any stored profile");
        }

        document.SchemaVersion = 2;
        document.ActiveAccountKey = null;
        return document;
    }

    private async Task WriteDocumentAsync(AuthProfilesDocument document, CancellationToken cancellationToken)
    {
        var tempPath = _profilesFilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = OpenOwnerOnly(tempPath, FileMode.CreateNew))
            {
                await JsonSerializer.SerializeAsync(stream, document, AuthProfileJsonContext.Default.AuthProfilesDocument, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, _profilesFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw StateIo("written", ex);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Preserve the original mutation failure; the unique sibling cannot become active state.
            }
        }
    }

    private static FileStream OpenOwnerOnly(string path, FileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new UnauthorizedAccessException("The current file owner could not be resolved.");
            var security = new FileSecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            return new FileInfo(path).Create(mode, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.Asynchronous, security);
        }

        return new FileStream(path, new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    private static StoredAuthProfile Resolve(AuthProfilesDocument document, AuthProfileSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (selector.Index.HasValue == (selector.Name is not null))
        {
            throw new UsageException("Specify exactly one profile index or name.");
        }

        if (selector.Index is int index)
        {
            return index >= 1 && index <= document.Profiles.Count
                ? document.Profiles[index - 1]
                : throw new UsageException("The profile index is outside the stored profile list.");
        }

        if (string.IsNullOrEmpty(selector.Name))
        {
            throw new UsageException("A profile selector name must not be empty.");
        }

        return document.Profiles.SingleOrDefault(p => string.Equals(p.Name, selector.Name, StringComparison.Ordinal))
            ?? throw new UsageException("No profile matches the exact specified name.");
    }

    private static void Activate(AuthProfilesDocument document, StoredAuthProfile selected, bool incrementSelected = true)
    {
        if (document.ActiveProfileId != selected.ProfileId)
        {
            var previous = document.Profiles.FirstOrDefault(p => p.ProfileId == document.ActiveProfileId);
            if (previous is not null)
            {
                previous.Revision = NextRevision(previous.Revision);
            }
        }

        if (incrementSelected)
        {
            selected.Revision = NextRevision(selected.Revision);
        }

        document.ActiveProfileId = selected.ProfileId;
    }

    private static long NextRevision(long revision) =>
        revision >= 0 && revision < long.MaxValue
            ? revision + 1
            : throw new UsageException("The profile revision cannot be advanced.");

    private static void ValidateName(string? name)
    {
        if (name is not null && name.Length is < 1 or > 30)
        {
            throw new UsageException("Profile names must contain 1 to 30 characters.");
        }
    }

    private static void RequireUniqueName(AuthProfilesDocument document, string? name, string id)
    {
        if (name is not null && document.Profiles.Any(p => p.ProfileId != id && string.Equals(p.Name, name, StringComparison.Ordinal)))
        {
            throw new UsageException("A profile already has the exact specified name.");
        }
    }

    private static void ValidateCoordinates(StoredAuthProfile profile, bool persisted)
    {
        var ring = profile.Ring ?? PowerPlatformApiRing.Production;
        string? invalid = !PowerPlatformApiRingSelection.IsDeclared(ring) ? $"undeclared Power Platform API ring {(int)ring}"
            : !Enum.IsDefined(profile.Cloud) ? "undeclared identity cloud"
            : profile.CredentialMethod.HasValue && !Enum.IsDefined(profile.CredentialMethod.Value) ? "undeclared credential method"
            : !Enum.IsDefined(profile.PrincipalType) ? "undeclared principal type"
            : profile.Cloud != CloudInstance.Public && ring != PowerPlatformApiRing.Production ? "non-public clouds do not support non-production rings"
            : null;
        if (invalid is not null)
        {
            throw persisted ? Corrupt(invalid) : new UsageException(invalid);
        }
    }

    private static string ConnectionUrl(StoredAuthProfile profile)
    {
        var target = PowerPlatformApiRingSelection.TargetFor(profile.Ring ?? PowerPlatformApiRing.Production);
        if (profile.Cloud != CloudInstance.Public)
        {
            target = target with
            {
                Cloud = profile.Cloud,
                GlobalEndpoint = new Uri("https://" + PowerPlatformCloudMap.ForCloud(profile.Cloud).TrustedHostSuffixes[0]),
            };
        }

        return target.GlobalEndpoint.AbsoluteUri.TrimEnd('/');
    }

    private static AuthCredentialMethod LegacyMethod(string? flow) => flow switch
    {
        "interactive" or "browser" => AuthCredentialMethod.Browser,
        "device-code" => AuthCredentialMethod.DeviceCode,
        _ => AuthCredentialMethod.Broker,
    };

    private static string Identity(StoredAuthProfile profile)
    {
        string?[] dimensions =
        [
            profile.PrincipalType == AuthPrincipalType.User ? profile.HomeAccountId ?? profile.AccountKey : profile.AccountKey,
            profile.ClientId, profile.TenantId, profile.Authority, profile.Cloud.ToString(),
            (profile.Ring ?? PowerPlatformApiRing.Production).ToString(),
            (profile.CredentialMethod ?? AuthCredentialMethod.Broker).ToString(),
            profile.PrincipalType.ToString(), profile.CertificateThumbprint, profile.CertificatePath,
        ];
        return string.Join('|', dimensions.Select(value => FormattableString.Invariant($"{value?.Length ?? -1}:{value}")));
    }

    private static AuthProfile? Active(AuthProfilesDocument document) =>
        document.ActiveProfileId is null ? null : ToProfile(document.Profiles.First(p => p.ProfileId == document.ActiveProfileId), true);

    private static AuthProfile ToProfile(StoredAuthProfile stored, bool isActive) => new()
    {
        ProfileId = stored.ProfileId, Name = stored.Name, Revision = stored.Revision,
        HomeAccountId = stored.HomeAccountId, CredentialMethod = stored.CredentialMethod ?? AuthCredentialMethod.Broker,
        PrincipalType = stored.PrincipalType, ConnectionUrl = stored.ConnectionUrl,
        CredentialReference = stored.CredentialReference, CertificatePath = stored.CertificatePath,
        CertificateThumbprint = stored.CertificateThumbprint, DefaultEnvironment = stored.DefaultEnvironment,
        AccountKey = stored.AccountKey, Username = stored.Username, TenantId = stored.TenantId,
        ClientId = stored.ClientId, Authority = stored.Authority, Cloud = stored.Cloud,
        Ring = stored.Ring ?? PowerPlatformApiRing.Production, FlowType = stored.FlowType,
        AuthenticatedTenantId = stored.AuthenticatedTenantId, PrincipalObjectId = stored.PrincipalObjectId,
        IsActive = isActive,
    };

    private static StoredAuthProfile ToStored(AuthProfile profile)
    {
        var stored = new StoredAuthProfile
        {
            Name = profile.Name,
            HomeAccountId = profile.HomeAccountId ??
                (profile.PrincipalType == AuthPrincipalType.User
                && profile.CredentialMethod != AuthCredentialMethod.DefaultAzureCredential
                    ? profile.AccountKey
                    : null),
            CredentialMethod = profile.CredentialMethod, PrincipalType = profile.PrincipalType,
            ConnectionUrl = profile.ConnectionUrl, CredentialReference = profile.CredentialReference,
            CertificatePath = profile.CertificatePath, CertificateThumbprint = profile.CertificateThumbprint,
            DefaultEnvironment = profile.DefaultEnvironment, AccountKey = profile.AccountKey,
            Username = profile.Username, TenantId = profile.TenantId, ClientId = profile.ClientId,
            Authority = profile.Authority, Cloud = profile.Cloud, Ring = profile.Ring, FlowType = profile.FlowType,
            AuthenticatedTenantId = profile.AuthenticatedTenantId, PrincipalObjectId = profile.PrincipalObjectId,
        };
        ValidateCoordinates(stored, persisted: false);
        stored.ConnectionUrl ??= ConnectionUrl(stored);
        return stored;
    }

    private static AuthProfileStateException Corrupt(string reason) =>
        new($"The AgentHost auth profile store is corrupt: {reason}.",
            remediation: "Run `msagent auth clear` to reset the owned local sign-in state.");

    private static AuthProfileStateException StateIo(string operation, Exception ex) =>
        new($"The AgentHost auth profile store could not be {operation}: {ex.Message}",
            remediation: "Verify the AgentHost config directory is writable, or run `msagent auth clear` to reset owned local state.");

    private void ObserveActiveProfile(AuthProfile? profile, long revision)
    {
        if (_stateObserver is null)
        {
            return;
        }

        try
        {
            _stateObserver.ObserveProfileMutation(revision);
        }
        catch (Exception)
        {
            // Optional observation must not change product semantics.
        }

        try
        {
            if (_stateObserver.IsObservationEnabled)
            {
                _stateObserver.ObserveActiveProfile(profile, revision);
            }
        }
        catch (Exception)
        {
            // Optional observation must not change product semantics.
        }
    }

    private static bool TryParseNonemptyGuid(string? value, out Guid parsed) =>
        Guid.TryParse(value, out parsed) && parsed != Guid.Empty;
}
