using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>
/// Atomic, file-backed implementation of <see cref="IAuthProfileStore"/>. It maintains a single active
/// AgentHost profile over the multi-account MSAL cache and is the AgentHost adaptation of PAC's
/// <c>FileBasedAuthProfilesManager</c> and <c>AuthProfilesManagerBase</c>.
///
/// It holds only non-secret account metadata; the MSAL token cache is a separate secure store
/// (<see cref="AgentHostTokenCache"/>). No token is acquired or read here.
///
/// Deliberate adaptations from PAC:
/// <list type="bullet">
///   <item>a single <c>ActiveAccountKey</c> replaces PAC's per-<c>AuthKind</c> "Current" dictionary;</item>
///   <item>writes are atomic (temp file + replace) rather than PAC's in-place <c>File.WriteAllText</c>;</item>
///   <item>invalid JSON, an unsupported schema version, duplicate keys, or an inconsistent active
///   pointer are surfaced as a typed <see cref="AuthProfileStateException"/> — never silently treated
///   as "signed out."</item>
/// </list>
/// </summary>
public sealed class FileAuthProfileStore :
    IAuthProfileStore,
    IAuthProfileTelemetryStore
{
    private const int SupportedSchemaVersion = 1;

    private readonly string _profilesFilePath;
    private readonly IAuthProfileStateObserver? _stateObserver;
    private readonly AuthProfileMutationRevision _mutationRevision;
    private readonly object _gate = new();

    /// <param name="profilesFilePath">
    /// Absolute path to the profile JSON file. The host resolves this under the AgentHost config
    /// directory; Core never reads the environment itself.
    /// </param>
    /// <param name="stateObserver">
    /// Optional observer for already-required active-profile mutations.
    /// </param>
    /// <param name="mutationRevision">
    /// Optional process-local revision shared with authentication observation.
    /// </param>
    public FileAuthProfileStore(
        string profilesFilePath,
        IAuthProfileStateObserver? stateObserver = null,
        AuthProfileMutationRevision? mutationRevision = null)
    {
        if (string.IsNullOrWhiteSpace(profilesFilePath))
        {
            throw new ArgumentException("Profiles file path is required.", nameof(profilesFilePath));
        }

        _profilesFilePath = profilesFilePath;
        _stateObserver = stateObserver;
        _mutationRevision =
            mutationRevision ?? new AuthProfileMutationRevision();
    }

    /// <inheritdoc />
    public Task<AuthProfile?> GetActiveProfileAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var document = ReadDocument();
            if (document.ActiveAccountKey is null)
            {
                return Task.FromResult<AuthProfile?>(null);
            }

            var active = document.Profiles.First(p => p.AccountKey == document.ActiveAccountKey);
            return Task.FromResult<AuthProfile?>(ToProfile(active, isActive: true));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AuthProfile>> GetProfilesAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var document = ReadDocument();
            IReadOnlyList<AuthProfile> profiles = document.Profiles
                .Select(p => ToProfile(p, isActive: p.AccountKey == document.ActiveAccountKey))
                .ToList();
            return Task.FromResult(profiles);
        }
    }

    /// <inheritdoc />
    public Task UpsertAsync(AuthProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (string.IsNullOrWhiteSpace(profile.AccountKey))
        {
            throw new ArgumentException("Profile account key is required.", nameof(profile));
        }

        long revision;
        lock (_gate)
        {
            var document = ReadDocument();

            document.Profiles.RemoveAll(p => p.AccountKey == profile.AccountKey);
            document.Profiles.Add(ToStored(profile));

            // PAC's Create() marks the last-in profile as current; login likewise upserts and activates
            // the matching profile.
            document.ActiveAccountKey = profile.AccountKey;

            WriteDocument(document);
            revision = _mutationRevision.Advance();
        }

        ObserveActiveProfile(profile with { IsActive = true }, revision);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetActiveAsync(string accountKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accountKey))
        {
            throw new ArgumentException("Account key is required.", nameof(accountKey));
        }

        AuthProfile active;
        long revision;
        lock (_gate)
        {
            var document = ReadDocument();

            StoredAuthProfile? stored =
                document.Profiles.FirstOrDefault(p => p.AccountKey == accountKey);
            if (stored is null)
            {
                throw new AuthenticationException(
                    $"No AgentHost profile is stored for account key '{accountKey}'.",
                    ExitCodes.Usage,
                    remediation: "Run `msagent auth status` to list stored accounts, or `msagent auth login` to sign in.");
            }

            document.ActiveAccountKey = accountKey;
            WriteDocument(document);
            active = ToProfile(stored, isActive: true);
            revision = _mutationRevision.Advance();
        }

        ObserveActiveProfile(active, revision);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken)
    {
        long revision;
        lock (_gate)
        {
            if (File.Exists(_profilesFilePath))
            {
                File.Delete(_profilesFilePath);
            }

            revision = _mutationRevision.Advance();
        }

        ObserveActiveProfile(profile: null, revision);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<AuthProfileTelemetryContext?> GetActiveTelemetryContextAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = ReadDocument(optionalSnapshot: true);
        if (document.ActiveAccountKey is null)
        {
            return ValueTask.FromResult<AuthProfileTelemetryContext?>(null);
        }

        StoredAuthProfile active = document.Profiles.First(
            profile => profile.AccountKey == document.ActiveAccountKey);
        if (!TryParseNonemptyGuid(active.AuthenticatedTenantId, out Guid tenantId))
        {
            return ValueTask.FromResult<AuthProfileTelemetryContext?>(null);
        }

        Guid? principalObjectId = null;
        if (!string.IsNullOrWhiteSpace(active.PrincipalObjectId))
        {
            if (TryParseNonemptyGuid(
                    active.PrincipalObjectId,
                    out Guid parsedPrincipal))
            {
                principalObjectId = parsedPrincipal;
            }
        }

        return ValueTask.FromResult<AuthProfileTelemetryContext?>(
            new(
                active.AccountKey,
                active.ClientId,
                active.Authority,
                active.Cloud,
                tenantId,
                principalObjectId));
    }

    private AuthProfilesDocument ReadDocument(bool optionalSnapshot = false)
    {
        if (!File.Exists(_profilesFilePath))
        {
            // A missing file is a clean "signed out" state, not corruption.
            return new AuthProfilesDocument();
        }

        string json;
        try
        {
            json = AtomicFile.ReadAllText(_profilesFilePath);
        }
        catch (FileNotFoundException) when (optionalSnapshot)
        {
            return new AuthProfilesDocument();
        }
        catch (DirectoryNotFoundException) when (optionalSnapshot)
        {
            return new AuthProfilesDocument();
        }
        catch (IOException ex)
        {
            throw new AuthProfileStateException(
                $"The AgentHost auth profile store could not be read: {ex.Message}",
                remediation: "Run `msagent auth logout` to reset the local sign-in state.");
        }

        AuthProfilesDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json, AuthProfileJsonContext.Default.AuthProfilesDocument);
        }
        catch (JsonException ex)
        {
            throw new AuthProfileStateException(
                $"The AgentHost auth profile store is not valid JSON: {ex.Message}",
                remediation: "Run `msagent auth logout` to reset the local sign-in state.");
        }

        return Validate(document);
    }

    private static AuthProfilesDocument Validate(AuthProfilesDocument? document)
    {
        if (document is null)
        {
            throw Corrupt("the profile store deserialized to null");
        }

        if (document.SchemaVersion != SupportedSchemaVersion)
        {
            throw Corrupt($"unsupported schema version {document.SchemaVersion} (expected {SupportedSchemaVersion})");
        }

        document.Profiles ??= [];

        if (document.Profiles.Any(p => string.IsNullOrWhiteSpace(p.AccountKey)))
        {
            throw Corrupt("a stored profile has an empty account key");
        }

        var duplicateKey = document.Profiles
            .GroupBy(p => p.AccountKey, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicateKey is not null)
        {
            throw Corrupt($"duplicate account key '{duplicateKey.Key}'");
        }

        if (document.ActiveAccountKey is not null &&
            !document.Profiles.Any(p => p.AccountKey == document.ActiveAccountKey))
        {
            throw Corrupt($"active pointer '{document.ActiveAccountKey}' does not match any stored profile");
        }

        return document;
    }

    private void WriteDocument(AuthProfilesDocument document)
    {
        var directory = Path.GetDirectoryName(_profilesFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(document, AuthProfileJsonContext.Default.AuthProfilesDocument);

        try
        {
            AtomicFile.WriteAllText(_profilesFilePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AuthProfileStateException(
                $"The AgentHost auth profile store could not be written: {ex.Message}",
                remediation: "Verify the AgentHost config directory is writable, then retry.");
        }
    }

    private static AuthProfileStateException Corrupt(string reason) =>
        new(
            $"The AgentHost auth profile store is corrupt: {reason}.",
            remediation: "Run `msagent auth logout` to reset the local sign-in state.");

    private static AuthProfile ToProfile(StoredAuthProfile stored, bool isActive) => new()
    {
        AccountKey = stored.AccountKey,
        Username = stored.Username,
        TenantId = stored.TenantId,
        ClientId = stored.ClientId,
        Authority = stored.Authority,
        Cloud = stored.Cloud,
        Ring = ReadRing(stored),
        FlowType = stored.FlowType,
        AuthenticatedTenantId = stored.AuthenticatedTenantId,
        PrincipalObjectId = stored.PrincipalObjectId,
        IsActive = isActive,
    };

    /// <summary>
    /// The ring a stored profile signs in against.
    /// </summary>
    /// <remarks>
    /// A profile written before the ring became selectable carries no ring and signed in against
    /// production, so an absent value defaults. A value present but undefined is refused rather than
    /// defaulted: silently reading it as production would point a command at the wrong cloud's
    /// endpoints while the stored state still claims otherwise.
    /// </remarks>
    private static PowerPlatformApiRing ReadRing(StoredAuthProfile stored) => stored.Ring switch
    {
        null => PowerPlatformApiRing.Production,
        var ring when PowerPlatformApiRingSelection.IsDeclared(ring.Value) => ring.Value,
        var ring => throw Corrupt($"profile '{stored.Username}' records '{(int)ring.Value}', which is not a Power Platform API cloud"),
    };

    private static StoredAuthProfile ToStored(AuthProfile profile) => new()
    {
        AccountKey = profile.AccountKey,
        Username = profile.Username,
        TenantId = profile.TenantId,
        ClientId = profile.ClientId,
        Authority = profile.Authority,
        Cloud = profile.Cloud,
        Ring = PowerPlatformApiRingSelection.Require(profile.Ring),
        FlowType = profile.FlowType,
        AuthenticatedTenantId = profile.AuthenticatedTenantId,
        PrincipalObjectId = profile.PrincipalObjectId,
    };

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
            // Metadata-free lifecycle observation must not change product authentication semantics.
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
            // Optional profile observation must not change product authentication semantics.
        }
    }

    private static bool TryParseNonemptyGuid(string? value, out Guid parsed) =>
        Guid.TryParse(value, out parsed) && parsed != Guid.Empty;

}
