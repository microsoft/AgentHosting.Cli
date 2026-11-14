using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Telemetry;

internal enum TelemetryRememberedPreference
{
    NotSet,
    Enabled,
    Disabled,
    Invalid,
}

internal enum TelemetryEnvironmentOverride
{
    NotSet,
    OptedOut,
    OptedIn,
    Invalid,
}

internal interface ITelemetryPreferenceStore
{
    ValueTask<TelemetryRememberedPreference> ReadAsync(CancellationToken cancellationToken);

    ValueTask WriteAsync(bool enabled, CancellationToken cancellationToken);
}

internal sealed class FileTelemetryPreferenceStore : ITelemetryPreferenceStore
{
    private const int SupportedSchemaVersion = 1;
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal FileTelemetryPreferenceStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("A telemetry preference file path is required.", nameof(filePath));
        }

        _filePath = Path.GetFullPath(filePath);
    }

    internal string FilePath => _filePath;

    internal static FileTelemetryPreferenceStore CreateForAhConfigDirectory(string configDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDirectory))
        {
            throw new ArgumentException(
                "The AgentHost config directory cannot be empty.",
                nameof(configDirectory));
        }

        return new FileTelemetryPreferenceStore(
            Path.Combine(
                Path.GetFullPath(configDirectory),
                "telemetry",
                "collection-preference.json"));
    }

    public async ValueTask<TelemetryRememberedPreference> ReadAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string json;
            try
            {
                json = await File.ReadAllTextAsync(_filePath, cancellationToken);
            }
            catch (FileNotFoundException)
            {
                return TelemetryRememberedPreference.NotSet;
            }
            catch (DirectoryNotFoundException)
            {
                return TelemetryRememberedPreference.NotSet;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                throw Unavailable("read");
            }

            try
            {
                return ParseDocument(json);
            }
            catch (JsonException)
            {
                return TelemetryRememberedPreference.Invalid;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask WriteAsync(bool enabled, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                try
                {
                    Directory.CreateDirectory(directory);
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
                {
                    throw Unavailable("write");
                }
            }

            try
            {
                AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(new TelemetryPreferenceDocument { SchemaVersion = SupportedSchemaVersion, Enabled = enabled }, TelemetryPreferenceJsonContext.Default.TelemetryPreferenceDocument));
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            {
                throw Unavailable("write");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TelemetryRememberedPreference ParseDocument(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return TelemetryRememberedPreference.Invalid;
        }

        JsonProperty[] properties = [.. document.RootElement.EnumerateObject()];
        JsonProperty[] schemaVersions =
            [.. properties.Where(property => property.NameEquals("schemaVersion"))];
        JsonProperty[] enabledValues =
            [.. properties.Where(property => property.NameEquals("enabled"))];
        if (schemaVersions.Length != 1
            || enabledValues.Length != 1
            || properties.Length != 2
            || schemaVersions[0].Value.ValueKind != JsonValueKind.Number
            || !schemaVersions[0].Value.TryGetInt32(out int schemaVersion)
            || schemaVersion != SupportedSchemaVersion)
        {
            return TelemetryRememberedPreference.Invalid;
        }

        return enabledValues[0].Value.ValueKind switch
        {
            JsonValueKind.True => TelemetryRememberedPreference.Enabled,
            JsonValueKind.False => TelemetryRememberedPreference.Disabled,
            _ => TelemetryRememberedPreference.Invalid,
        };
    }

    private static AgentHostingStateException Unavailable(string operation) =>
        new(
            $"The AgentHost telemetry preference could not be {(operation == "write" ? "written" : "read")}.",
            "Verify that the AgentHost config directory is accessible, then retry.");
}

internal sealed class TelemetryControlService :
    ICommandHandler<TelemetryEnableRequest, TelemetryControlReport>,
    ICommandHandler<TelemetryDisableRequest, TelemetryControlReport>,
    ICommandHandler<TelemetryStatusRequest, TelemetryControlReport>
{
    internal const string OptOutEnvironmentVariable = "AH_CLI_TELEMETRY_OPTOUT";

    private readonly ITelemetryPreferenceStore _preferenceStore;
    private readonly Func<string?> _readEnvironmentOverride;
    private readonly bool _compositionFallbackEnabled;
    private readonly bool _collectionActivationAvailable;

    internal TelemetryControlService(
        ITelemetryPreferenceStore preferenceStore,
        Func<string?> readEnvironmentOverride,
        bool compositionFallbackEnabled,
        bool collectionActivationAvailable)
    {
        _preferenceStore = preferenceStore
            ?? throw new ArgumentNullException(nameof(preferenceStore));
        _readEnvironmentOverride = readEnvironmentOverride
            ?? throw new ArgumentNullException(nameof(readEnvironmentOverride));
        _compositionFallbackEnabled = compositionFallbackEnabled;
        _collectionActivationAvailable = collectionActivationAvailable;
    }

    async Task<TelemetryControlReport> ICommandHandler<TelemetryEnableRequest, TelemetryControlReport>.HandleAsync(
        TelemetryEnableRequest request,
        CancellationToken cancellationToken)
    {
        await _preferenceStore.WriteAsync(enabled: true, cancellationToken);
        return await InspectAsync("preference-enabled", cancellationToken);
    }

    async Task<TelemetryControlReport> ICommandHandler<TelemetryDisableRequest, TelemetryControlReport>.HandleAsync(
        TelemetryDisableRequest request,
        CancellationToken cancellationToken)
    {
        await _preferenceStore.WriteAsync(enabled: false, cancellationToken);
        return await InspectAsync("preference-disabled", cancellationToken);
    }

    Task<TelemetryControlReport> ICommandHandler<TelemetryStatusRequest, TelemetryControlReport>.HandleAsync(
        TelemetryStatusRequest request,
        CancellationToken cancellationToken) =>
        InspectAsync("status", cancellationToken).AsTask();

    internal async ValueTask<bool> IsCollectionEnabledAsync(CancellationToken cancellationToken)
    {
        TelemetryEnvironmentOverride environment = ParseEnvironmentOverride(
            _readEnvironmentOverride());
        TelemetryPolicyResolution resolution = Resolve(environment, preference: null);
        if (!resolution.PreferenceRequired)
        {
            return resolution.Enabled;
        }

        TelemetryRememberedPreference preference =
            await _preferenceStore.ReadAsync(cancellationToken);
        return Resolve(environment, preference).Enabled;
    }

    internal async ValueTask<TelemetryControlReport> InspectAsync(
        string status,
        CancellationToken cancellationToken)
    {
        TelemetryEnvironmentOverride environment = ParseEnvironmentOverride(
            _readEnvironmentOverride());
        TelemetryRememberedPreference preference =
            await _preferenceStore.ReadAsync(cancellationToken);
        TelemetryPolicyResolution resolution = Resolve(environment, preference);

        return new TelemetryControlReport
        {
            Status = status,
            RememberedPreference = Describe(preference),
            EnvironmentOverride = Describe(environment),
            CompositionFallbackEnabled = _compositionFallbackEnabled,
            CollectionActivationAvailable = _collectionActivationAvailable,
            EffectiveCollectionEnabled = resolution.Enabled,
            EffectiveReason = resolution.Reason,
        };
    }

    internal static TelemetryEnvironmentOverride ParseEnvironmentOverride(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return TelemetryEnvironmentOverride.NotSet;
        }

        if (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return TelemetryEnvironmentOverride.OptedOut;
        }

        if (value == "0" || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return TelemetryEnvironmentOverride.OptedIn;
        }

        return TelemetryEnvironmentOverride.Invalid;
    }

    private TelemetryPolicyResolution Resolve(
        TelemetryEnvironmentOverride environment,
        TelemetryRememberedPreference? preference)
    {
        if (environment == TelemetryEnvironmentOverride.OptedOut)
        {
            return TelemetryPolicyResolution.Disabled("environment-opt-out");
        }

        if (environment == TelemetryEnvironmentOverride.OptedIn)
        {
            return _collectionActivationAvailable
                ? TelemetryPolicyResolution.EnabledBy("environment-opt-in")
                : TelemetryPolicyResolution.Disabled("collection-activation-unavailable");
        }

        if (environment == TelemetryEnvironmentOverride.Invalid)
        {
            return TelemetryPolicyResolution.Disabled("invalid-environment-override");
        }

        if (!_collectionActivationAvailable)
        {
            return TelemetryPolicyResolution.Disabled("collection-activation-unavailable");
        }

        if (preference is null)
        {
            return TelemetryPolicyResolution.RequiresPreference();
        }

        if (preference == TelemetryRememberedPreference.Invalid)
        {
            return TelemetryPolicyResolution.Disabled("invalid-remembered-preference");
        }

        bool requested = preference switch
        {
            TelemetryRememberedPreference.Enabled => true,
            TelemetryRememberedPreference.Disabled => false,
            TelemetryRememberedPreference.NotSet => _compositionFallbackEnabled,
            _ => false,
        };

        return requested
            ? TelemetryPolicyResolution.EnabledBy("enabled")
            : TelemetryPolicyResolution.Disabled(
                preference == TelemetryRememberedPreference.Disabled
                    ? "remembered-disabled"
                    : "composition-default-disabled");
    }

    private static string Describe(TelemetryRememberedPreference preference) =>
        preference switch
        {
            TelemetryRememberedPreference.NotSet => "not-set",
            TelemetryRememberedPreference.Enabled => "enabled",
            TelemetryRememberedPreference.Disabled => "disabled",
            TelemetryRememberedPreference.Invalid => "invalid",
            _ => "invalid",
        };

    private static string Describe(TelemetryEnvironmentOverride environment) =>
        environment switch
        {
            TelemetryEnvironmentOverride.NotSet => "not-set",
            TelemetryEnvironmentOverride.OptedOut => "opted-out",
            TelemetryEnvironmentOverride.OptedIn => "opted-in",
            TelemetryEnvironmentOverride.Invalid => "invalid",
            _ => "invalid",
        };
}

internal readonly record struct TelemetryPolicyResolution(
    bool Enabled,
    bool PreferenceRequired,
    string Reason)
{
    internal static TelemetryPolicyResolution Disabled(string reason) =>
        new(Enabled: false, PreferenceRequired: false, Reason: reason);

    internal static TelemetryPolicyResolution EnabledBy(string reason) =>
        new(Enabled: true, PreferenceRequired: false, Reason: reason);

    internal static TelemetryPolicyResolution RequiresPreference() =>
        new(Enabled: false, PreferenceRequired: true, Reason: "preference-required");
}

internal sealed class TelemetryPreferenceDocument
{
    public int SchemaVersion { get; set; }

    public bool? Enabled { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TelemetryPreferenceDocument))]
internal sealed partial class TelemetryPreferenceJsonContext : JsonSerializerContext;
