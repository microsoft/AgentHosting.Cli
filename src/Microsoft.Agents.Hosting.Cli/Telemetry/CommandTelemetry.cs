using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Diagnostics;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.PowerPlatform.Tooling.OneDS;

namespace Microsoft.Agents.Hosting.Cli.Telemetry;

internal enum CommandTelemetryOutcome
{
    Success,
    Usage,
    NotSignedIn,
    Failure,
    Timeout,
    Cancellation,
}

internal enum CommandTelemetryRecordKind
{
    Host,
    CommandAttempt,
    CommandOutcome,
}

internal enum CommandTelemetryHostCategory
{
    Help,
    Version,
    ParserRejection,
    GroupUsage,
}

internal enum CommandTelemetryDiagnosticStatus
{
    None,
    InvalidTransition,
    BufferCapacity,
    ClockFailure,
}

internal sealed record CommandTelemetryPath
{
    private CommandTelemetryPath(string value) => Value = value;

    internal string Value { get; }

    internal bool IsPlayground =>
        string.Equals(Value, "test playground", StringComparison.Ordinal);

    internal string OperationName =>
        "AgentHostingCLI." + Value.Replace(' ', '.');

    internal static CommandTelemetryPath FromMetadata(IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count < 2
            || segments.Any(segment =>
                string.IsNullOrEmpty(segment)
                || segment[0] is not (>= 'a' and <= 'z')
                || segment[^1] is not (>= 'a' and <= 'z' or >= '0' and <= '9')
                || segment.Any(character =>
                    character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-'))))
        {
            throw new ArgumentException(
                "Command paths must contain lowercase command metadata names.",
                nameof(segments));
        }

        return new CommandTelemetryPath(string.Join(' ', segments));
    }
}

internal sealed record CommandTelemetryRecord
{
    private CommandTelemetryRecord(
        CommandTelemetryRecordKind kind,
        CommandTelemetryPath? path,
        CommandTelemetryHostCategory? hostCategory,
        CommandTelemetryOutcome? outcome,
        DateTimeOffset timestamp,
        Guid? tenantId,
        Guid? principalObjectId,
        PlaygroundTelemetryDetails? playgroundDetails,
        int? errorCode = null)
    {
        Kind = kind;
        Path = path;
        HostCategory = hostCategory;
        Outcome = outcome;
        ErrorCode = errorCode;
        Timestamp = timestamp;
        PlaygroundDetails = ValidatePlaygroundDetails(path, playgroundDetails);
        TenantId = ValidateTenantId(tenantId);
        PrincipalObjectId = ValidatePrincipalObjectId(
            tenantId,
            principalObjectId);
    }

    public CommandTelemetryRecordKind Kind { get; }

    public CommandTelemetryPath? Path { get; }

    public CommandTelemetryHostCategory? HostCategory { get; }

    public CommandTelemetryOutcome? Outcome { get; }

    public int? ErrorCode { get; }

    public DateTimeOffset Timestamp { get; }

    internal PlaygroundTelemetryDetails? PlaygroundDetails { get; }

    public Guid? TenantId { get; }

    public Guid? PrincipalObjectId { get; }

    internal static CommandTelemetryRecord Host(
        CommandTelemetryHostCategory category,
        DateTimeOffset timestamp,
        Guid? tenantId)
    {
        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        return new(
            CommandTelemetryRecordKind.Host,
            path: null,
            category,
            outcome: null,
            timestamp,
            tenantId,
            principalObjectId: null,
            playgroundDetails: null,
            errorCode: category is CommandTelemetryHostCategory.ParserRejection or CommandTelemetryHostCategory.GroupUsage
                ? ErrorCodes.General.Usage
                : null);
    }

    internal static CommandTelemetryRecord Attempt(
        CommandTelemetryPath path,
        DateTimeOffset timestamp,
        Guid? tenantId,
        Guid? principalObjectId = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new(
            CommandTelemetryRecordKind.CommandAttempt,
            path,
            hostCategory: null,
            outcome: null,
            timestamp,
            tenantId,
            principalObjectId,
            playgroundDetails: null);
    }

    internal static CommandTelemetryRecord OutcomeRecord(
        CommandTelemetryPath path,
        CommandTelemetryOutcome outcome,
        DateTimeOffset timestamp,
        Guid? tenantId,
        PlaygroundTelemetryDetails? playgroundDetails = null,
        int? errorCode = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        errorCode = outcome switch
        {
            CommandTelemetryOutcome.Success or CommandTelemetryOutcome.Cancellation => null,
            CommandTelemetryOutcome.Usage => errorCode ?? ErrorCodes.General.Usage,
            CommandTelemetryOutcome.Timeout => errorCode ?? ErrorCodes.General.TransportTimeout,
            _ => errorCode ?? ErrorCodes.General.Unknown,
        };

        return new(
            CommandTelemetryRecordKind.CommandOutcome,
            path,
            hostCategory: null,
            outcome,
            timestamp,
            tenantId,
            principalObjectId: null,
            playgroundDetails,
            errorCode);
    }

    internal CommandTelemetryRecord WithAttribution(
        Guid? tenantId,
        Guid? principalObjectId) =>
        new(
            Kind,
            Path,
            HostCategory,
            Outcome,
            Timestamp,
            tenantId,
            Kind == CommandTelemetryRecordKind.CommandAttempt
                ? principalObjectId
                : null,
            PlaygroundDetails,
            ErrorCode);

    private static PlaygroundTelemetryDetails? ValidatePlaygroundDetails(
        CommandTelemetryPath? path,
        PlaygroundTelemetryDetails? playgroundDetails)
    {
        if (playgroundDetails is not null
            && (path is null || !path.IsPlayground))
        {
            throw new ArgumentException(
                "Playground telemetry details require the test playground path.",
                nameof(playgroundDetails));
        }

        return playgroundDetails;
    }

    private static Guid? ValidateTenantId(Guid? tenantId) =>
        tenantId == Guid.Empty
            ? throw new ArgumentException(
                "The tenant id cannot be empty.",
                nameof(tenantId))
            : tenantId;

    private static Guid? ValidatePrincipalObjectId(
        Guid? tenantId,
        Guid? principalObjectId)
    {
        if (principalObjectId == Guid.Empty)
        {
            throw new ArgumentException(
                "The principal object id cannot be empty.",
                nameof(principalObjectId));
        }

        if (principalObjectId is not null && tenantId is null)
        {
            throw new ArgumentException(
                "A principal object id requires its issuer tenant id.",
                nameof(principalObjectId));
        }

        return principalObjectId;
    }
}

internal sealed class CommandTelemetryEvent : IOneDSTelemetry
{
    internal const string EnvelopeName = "event";
    internal const string ClientType = "AgentHostingCLI";
    internal const string Scenario = "AgentHosting_CLI_Authoring";

    internal CommandTelemetryEvent(
        CommandTelemetryRecord record,
        TelemetryDestination destination,
        string cliVersion,
        string sessionId,
        int sequenceNumber)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(destination);
        if (string.IsNullOrWhiteSpace(cliVersion))
        {
            throw new ArgumentException(
                "CLI version provenance is required.",
                nameof(cliVersion));
        }

        if (!Guid.TryParseExact(sessionId, "D", out Guid parsedSessionId)
            || parsedSessionId == Guid.Empty)
        {
            throw new ArgumentException(
                "A nonempty D-format session id is required.",
                nameof(sessionId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequenceNumber);

        TelemetryName = EnvelopeName;
        Timestamp = record.Timestamp;
        Flags = UtcFlags.Microsoft_IsPII_Hash;
        Data = CommandTelemetryData.Create(
            record,
            destination,
            cliVersion,
            sessionId,
            sequenceNumber);
    }

    public string TelemetryName { get; }

    public string? InstrumentationKey { get; set; }

    public DateTimeOffset Timestamp { get; }

    public CommandTelemetryData Data { get; }

    public UtcFlags Flags { get; }

    object IOneDSTelemetry.Data => Data;
}

internal sealed class CommandTelemetryData
{
    private CommandTelemetryData()
    {
    }

    [JsonPropertyName("app_Name")]
    public required string AppName { get; init; }

    [JsonPropertyName("clientType")]
    public string ClientType { get; init; } = CommandTelemetryEvent.ClientType;

    [JsonPropertyName("clusterCategory")]
    public required string ClusterCategory { get; init; }

    [JsonPropertyName("device_Id")]
    public string DeviceId { get; init; } = "browser";

    [JsonPropertyName("event_Name")]
    public required string EventName { get; init; }

    [JsonPropertyName("sequenceNumber")]
    public required int SequenceNumber { get; init; }

    [JsonPropertyName("session_Id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "info";

    [JsonPropertyName("timestamp")]
    public required string Timestamp { get; init; }

    [JsonPropertyName("operation_Name")]
    public required string OperationName { get; init; }

    [JsonPropertyName("customDimensions")]
    public required string CustomDimensions { get; init; }

    [JsonPropertyName("telemetryLocation")]
    public required string TelemetryLocation { get; init; }

    [JsonPropertyName("tenantId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TenantId { get; init; }

    [JsonPropertyName("principalObjectId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrincipalObjectId { get; init; }

    internal static CommandTelemetryData Create(
        CommandTelemetryRecord record,
        TelemetryDestination destination,
        string cliVersion,
        string sessionId,
        int sequenceNumber)
    {
        string operationName =
            record.Path?.OperationName ?? "AgentHostingCLI.Host";
        string telemetryLocation =
            destination.Location == Telemetry.TelemetryLocation.Europe ? "eu" : "global";
        var dimensions = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["IsFailure"] = record.Outcome is not (null
                or CommandTelemetryOutcome.Success
                or CommandTelemetryOutcome.Cancellation)
                || record.HostCategory is CommandTelemetryHostCategory.ParserRejection
                    or CommandTelemetryHostCategory.GroupUsage,
            ["operationName"] = operationName,
            ["telemetryLocation"] = telemetryLocation,
            ["isOnePlayer"] = "false",
            ["webPlayerScenario"] = CommandTelemetryEvent.Scenario,
            ["cloud"] = destination.Cloud,
            ["station"] = "Unknown",
            ["cliVersion"] = cliVersion,
        };
        if (record.TenantId is { } tenantId)
        {
            if (record.Path?.IsPlayground != true)
            {
                dimensions["tenantId"] = tenantId.ToString("D");
            }
        }

        if (record.Kind == CommandTelemetryRecordKind.CommandAttempt
            && record.Path?.IsPlayground != true
            && record.PrincipalObjectId is { } principalObjectId)
        {
            dimensions["principalObjectId"] = principalObjectId.ToString("D");
        }

        if (record.HostCategory is { } hostCategory)
        {
            dimensions["hostCategory"] = HostCategoryToken(hostCategory);
            dimensions["exitClass"] = hostCategory is
                CommandTelemetryHostCategory.Help or CommandTelemetryHostCategory.Version
                ? "success"
                : "usage";
        }

        if (record.Outcome is { } outcome)
        {
            dimensions["outcomeCategory"] = OutcomeToken(outcome);
            if (outcome != CommandTelemetryOutcome.Cancellation)
            {
                dimensions["exitClass"] = ExitClass(outcome);
            }
        }

        if (record.ErrorCode is { } errorCode)
        {
            dimensions["errorCode"] =
                errorCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (record.PlaygroundDetails is { } playgroundDetails)
        {
            dimensions["durationMs"] =
                playgroundDetails.DurationMs.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            if (playgroundDetails.PlaygroundVersion is { } playgroundVersion)
            {
                dimensions["playgroundVersion"] = playgroundVersion;
            }

            dimensions["installResult"] =
                InstallResultToken(playgroundDetails.InstallResult);
            dimensions["errorCategory"] =
                ErrorCategoryToken(playgroundDetails.ErrorCategory);
        }

        return new CommandTelemetryData
        {
            AppName = destination.AppName,
            ClusterCategory = destination.ClusterCategory,
            EventName = record.Kind switch
            {
                CommandTelemetryRecordKind.Host => "HostCase",
                CommandTelemetryRecordKind.CommandAttempt => "CommandAttempt",
                CommandTelemetryRecordKind.CommandOutcome => "CommandOutcome",
                _ => throw new ArgumentOutOfRangeException(nameof(record)),
            },
            SequenceNumber = sequenceNumber,
            SessionId = sessionId,
            Timestamp = record.Timestamp.ToString("O"),
            OperationName = operationName,
            CustomDimensions = JsonSerializer.Serialize(dimensions),
            TelemetryLocation = telemetryLocation,
            TenantId =
                record.Kind == CommandTelemetryRecordKind.CommandAttempt
                    && record.Path?.IsPlayground != true
                    && record.PrincipalObjectId is not null
                    ? record.TenantId?.ToString("D")
                    : null,
            PrincipalObjectId =
                record.Kind == CommandTelemetryRecordKind.CommandAttempt
                    && record.Path?.IsPlayground != true
                    ? record.PrincipalObjectId?.ToString("D")
                    : null,
        };
    }

    private static string HostCategoryToken(CommandTelemetryHostCategory category) => category switch
    {
        CommandTelemetryHostCategory.Help => "help",
        CommandTelemetryHostCategory.Version => "version",
        CommandTelemetryHostCategory.ParserRejection => "parser-rejection",
        CommandTelemetryHostCategory.GroupUsage => "group-usage",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };

    private static string OutcomeToken(CommandTelemetryOutcome outcome) => outcome switch
    {
        CommandTelemetryOutcome.Success => "success",
        CommandTelemetryOutcome.Usage => "usage",
        CommandTelemetryOutcome.NotSignedIn => "not-signed-in",
        CommandTelemetryOutcome.Failure => "failure",
        CommandTelemetryOutcome.Timeout => "timeout",
        CommandTelemetryOutcome.Cancellation => "cancellation",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private static string InstallResultToken(PlaygroundInstallResult result) => result switch
    {
        PlaygroundInstallResult.NotNeeded => "not-needed",
        PlaygroundInstallResult.Installed => "installed",
        PlaygroundInstallResult.Declined => "declined",
        PlaygroundInstallResult.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };

    private static string ErrorCategoryToken(PlaygroundErrorCategory category) =>
        category switch
        {
            PlaygroundErrorCategory.None => "none",
            PlaygroundErrorCategory.Unsupported => "unsupported",
            PlaygroundErrorCategory.ToolNotFound => "tool-not-found",
            PlaygroundErrorCategory.VersionTooOld => "version-too-old",
            PlaygroundErrorCategory.InstallDeclined => "install-declined",
            PlaygroundErrorCategory.InstallFailed => "install-failed",
            PlaygroundErrorCategory.LaunchFailed => "launch-failed",
            PlaygroundErrorCategory.Timeout => "timeout",
            PlaygroundErrorCategory.Cancellation => "cancellation",
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };

    private static string ExitClass(CommandTelemetryOutcome outcome) => outcome switch
    {
        CommandTelemetryOutcome.Success => "success",
        CommandTelemetryOutcome.Usage => "usage",
        CommandTelemetryOutcome.NotSignedIn => "not-signed-in",
        CommandTelemetryOutcome.Failure or CommandTelemetryOutcome.Timeout => "failure",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };
}

internal interface ICommandTelemetryTransport
{
    ValueTask<HttpStatusCode> SendAsync(
        string payload,
        TelemetryDestination destination,
        CancellationToken cancellationToken);
}

internal sealed class OneDsCommandTelemetryTransport : ICommandTelemetryTransport
{
    internal static OneDsCommandTelemetryTransport Instance { get; } = new();

    public async ValueTask<HttpStatusCode> SendAsync(
        string payload,
        TelemetryDestination destination,
        CancellationToken cancellationToken)
    {
        var transmission =
            new OneDSTransmission(payload, destination.Endpoint, destination.ApiKey);
        var response = await transmission.SendAsync(cancellationToken);
        return response.StatusCode;
    }
}

internal sealed class CommandTelemetrySession : IAsyncDisposable
{
    private const int MaximumDiagnosticCount = 4;
    private const int MaximumRecordCount = 2;
    private const int MaximumEnvironmentTargetCount = 4;
    private static readonly TimeSpan DefaultFlushTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumFlushTimeout = TimeSpan.FromMinutes(1);

    private readonly ITelemetryDestinationResolver? _destinationResolver;
    private readonly IClock? _clock;
    private readonly string? _cliVersion;
    private readonly TimeSpan _flushTimeout;
    private readonly TimeProvider _timeProvider = TimeProvider.System;
    private readonly ICommandTelemetryTransport? _transport;
    private readonly CommandTelemetryTenantObserver? _tenantObserver;
    private readonly CommandTelemetryEnvironmentObserver? _environmentObserver;
    private readonly object _gate = new();
    private readonly List<CommandTelemetryRecord> _records = [];
    private readonly Dictionary<string, string> _environmentTargets =
        new(StringComparer.OrdinalIgnoreCase);
    private CommandTelemetryPath? _path;
    private bool _environmentTargetConflict;
    private PlaygroundTelemetryDetails? _playgroundDetails;
    private Guid? _tenantId;
    private Guid? _principalObjectId;
    private bool _principalConflict;
    private bool _tenantConflict;
    private bool _hasFreshObservation;
    private Guid? _freshTenantId;
    private Guid? _freshPrincipalObjectId;
    private string? _freshAccountKey;
    private long _latestFreshRevision = -1;
    private long _lastMutationRevision;
    private long _lastProfileRevision;
    private SessionState _state;
    private bool _completed;
    private int _invalidTransitionCount;
    private int _failedOperationCount;
    private CommandTelemetryDiagnosticStatus _lastDiagnosticStatus;

    private CommandTelemetrySession(
        CommandTelemetryTenantObserver? tenantObserver = null,
        CommandTelemetryEnvironmentObserver? environmentObserver = null)
    {
        _tenantObserver = tenantObserver;
        _environmentObserver = environmentObserver;
    }

    internal CommandTelemetrySession(
        ITelemetryDestinationResolver destinationResolver,
        IClock clock,
        string cliVersion,
        CommandTelemetryTenantObserver? tenantObserver = null,
        CommandTelemetryEnvironmentObserver? environmentObserver = null,
        CommandTelemetryProfileContext? selectedProfile = null,
        TimeSpan? flushTimeout = null,
        ICommandTelemetryTransport? transport = null,
        TimeProvider? timeProvider = null)
    {
        _destinationResolver = destinationResolver
            ?? throw new ArgumentNullException(nameof(destinationResolver));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cliVersion = !string.IsNullOrWhiteSpace(cliVersion)
            ? cliVersion
            : throw new ArgumentException("CLI version is required.", nameof(cliVersion));
        _flushTimeout = flushTimeout ?? DefaultFlushTimeout;
        ValidateFlushTimeout(_flushTimeout, nameof(flushTimeout));

        _transport = transport ?? OneDsCommandTelemetryTransport.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _tenantObserver = tenantObserver;
        _environmentObserver = environmentObserver;
        ApplySelectedProfile(selectedProfile);
    }

    internal static CommandTelemetrySession Disabled(
        CommandTelemetryTenantObserver? tenantObserver = null,
        CommandTelemetryEnvironmentObserver? environmentObserver = null) =>
        new(tenantObserver, environmentObserver);

    internal static void ValidateFlushTimeout(
        TimeSpan flushTimeout,
        string parameterName)
    {
        if (flushTimeout <= TimeSpan.Zero
            || flushTimeout > MaximumFlushTimeout)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"The telemetry flush timeout must be positive and no greater than {MaximumFlushTimeout}.");
        }
    }

    internal int InvalidTransitionCount
    {
        get
        {
            lock (_gate)
            {
                return _invalidTransitionCount;
            }
        }
    }

    internal int FailedOperationCount
    {
        get
        {
            lock (_gate)
            {
                return _failedOperationCount;
            }
        }
    }

    internal CommandTelemetryDiagnosticStatus LastDiagnosticStatus
    {
        get
        {
            lock (_gate)
            {
                return _lastDiagnosticStatus;
            }
        }
    }

    internal IDisposable? EnterObservationScope()
    {
        if (_tenantObserver is null && _environmentObserver is null)
        {
            return null;
        }

        IDisposable? tenantScope = _tenantObserver is null
            ? null
            : _destinationResolver is not null
                ? _tenantObserver.BeginSession(this)
                : _tenantObserver.BeginSuppression();
        IDisposable? environmentScope = _environmentObserver is null
            ? null
            : _destinationResolver is not null
                ? _environmentObserver.BeginSession(this)
                : _environmentObserver.BeginSuppression();
        return new ObservationScopes(tenantScope, environmentScope);
    }

    internal void RecordHost(CommandTelemetryHostCategory category)
    {
        if (_destinationResolver is null)
        {
            return;
        }

        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        lock (_gate)
        {
            if (_completed || _state != SessionState.Empty)
            {
                ObserveInvalidTransition();
                return;
            }

            if (!TryGetTimestamp(out DateTimeOffset timestamp))
            {
                return;
            }

            CommandTelemetryRecord record =
                CommandTelemetryRecord.Host(category, timestamp, _tenantId);
            if (!TryAddRecord(record))
            {
                return;
            }

            _state = SessionState.Host;
        }
    }

    internal void RecordAttempt(CommandTelemetryPath path)
    {
        if (_destinationResolver is null)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(path);
        lock (_gate)
        {
            if (_completed
                || _state != SessionState.Empty)
            {
                ObserveInvalidTransition();
                return;
            }

            if (!TryGetTimestamp(out DateTimeOffset timestamp))
            {
                return;
            }

            CommandTelemetryRecord record =
                CommandTelemetryRecord.Attempt(
                    path,
                    timestamp,
                    _tenantId,
                    _principalObjectId);
            if (!TryAddRecord(record))
            {
                return;
            }

            _path = path;
            _state = SessionState.Attempt;
        }
    }

    internal void RecordOutcome(CommandTelemetryOutcome outcome, int? errorCode = null)
    {
        if (_destinationResolver is null)
        {
            return;
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        lock (_gate)
        {
            if (_completed
                || _path is null
                || _state != SessionState.Attempt)
            {
                ObserveInvalidTransition();
                return;
            }

            if (!TryGetTimestamp(out DateTimeOffset timestamp))
            {
                return;
            }

            CommandTelemetryRecord record =
                CommandTelemetryRecord.OutcomeRecord(
                    _path,
                    outcome,
                    timestamp,
                    _tenantId,
                    _playgroundDetails,
                    errorCode);
            if (!TryAddRecord(record))
            {
                return;
            }

            _state = SessionState.Outcome;
        }
    }

    internal void RecordPlaygroundDetails(PlaygroundTelemetryDetails details)
    {
        if (_destinationResolver is null)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(details);
        lock (_gate)
        {
            if (_completed
                || _state != SessionState.Attempt
                || _path is null
                || !_path.IsPlayground
                || _playgroundDetails is not null)
            {
                ObserveInvalidTransition();
                return;
            }

            _playgroundDetails = details;
        }
    }

    internal void ObserveResolvedEnvironment(
        ResolvedEnvironmentTarget environmentTarget)
    {
        if (_destinationResolver is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_completed || _environmentTargetConflict)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(environmentTarget.EnvironmentId)
                || string.IsNullOrWhiteSpace(environmentTarget.TenantId))
            {
                _environmentTargets.Clear();
                _environmentTargetConflict = true;
                return;
            }

            if (_environmentTargets.TryGetValue(
                    environmentTarget.EnvironmentId,
                    out string? existingTenant))
            {
                if (!string.Equals(
                        existingTenant,
                        environmentTarget.TenantId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _environmentTargets.Clear();
                    _environmentTargetConflict = true;
                }

                return;
            }

            if (_environmentTargets.Count >= MaximumEnvironmentTargetCount)
            {
                _environmentTargets.Clear();
                _environmentTargetConflict = true;
                return;
            }

            _environmentTargets.Add(
                environmentTarget.EnvironmentId,
                environmentTarget.TenantId);
        }
    }

    internal void ObserveAuthenticatedTenant(Guid tenantId)
        => ObserveAuthenticatedAccount(
            new AuthenticatedTenantContext(
                tenantId,
                PrincipalObjectId: null,
                AccountKey: null,
                ClientId: null));

    internal void ObserveAuthenticatedAccount(
        AuthenticatedTenantContext context)
    {
        long revision;
        lock (_gate)
        {
            revision = _lastMutationRevision;
        }

        ObserveAuthenticatedAccount(context, revision);
    }

    internal void ObserveAuthenticatedAccount(
        AuthenticatedTenantContext context,
        long generation)
    {
        if (_destinationResolver is null)
        {
            return;
        }

        if (context.TenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "The tenant id cannot be empty.",
                nameof(context));
        }

        if (context.PrincipalObjectId == Guid.Empty)
        {
            throw new ArgumentException(
                "The principal object id cannot be empty.",
                nameof(context));
        }

        lock (_gate)
        {
            if (_completed || generation < _lastMutationRevision)
            {
                return;
            }

            if (!_hasFreshObservation)
            {
                _hasFreshObservation = true;
                _freshTenantId = context.TenantId;
                _freshPrincipalObjectId = context.PrincipalObjectId;
                _freshAccountKey = context.AccountKey;
            }
            else
            {
                if (!_tenantConflict
                    && _freshTenantId != context.TenantId)
                {
                    _freshTenantId = null;
                    _tenantConflict = true;
                }

                if (_tenantConflict
                    || _freshPrincipalObjectId != context.PrincipalObjectId
                    || !string.Equals(
                        _freshAccountKey,
                        context.AccountKey,
                        StringComparison.Ordinal))
                {
                    _principalConflict = true;
                }
            }

            _latestFreshRevision =
                Math.Max(_latestFreshRevision, generation);
            ApplyFreshContext(context);
            ApplyAttributionToRecords();
        }
    }

    internal void ObserveProfileMutation(long revision)
    {
        if (_destinationResolver is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_completed || revision <= _lastMutationRevision)
            {
                return;
            }

            _lastMutationRevision = revision;
            if (_latestFreshRevision >= revision)
            {
                return;
            }

            ClearCurrentAttribution();
            ApplyAttributionToRecords();
        }
    }

    internal void ObserveActiveProfile(
        CommandTelemetryProfileContext? profile,
        long revision)
    {
        if (_destinationResolver is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_completed
                || revision < _lastMutationRevision
                || revision <= _lastProfileRevision)
            {
                return;
            }

            _lastProfileRevision = revision;
            if (_latestFreshRevision >= revision)
            {
                return;
            }

            ApplySelectedProfile(profile);
            ApplyAttributionToRecords();
        }
    }

    public async ValueTask DisposeAsync()
    {
        CommandTelemetryRecord[] records;
        TelemetryEnvironmentTarget target;
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            records = [.. _records];
            target = _environmentTargetConflict || _environmentTargets.Count == 0
                ? TelemetryEnvironmentTarget.Unknown
                : TelemetryEnvironmentTarget.Resolved(
                    _environmentTargets.Select(pair =>
                        new ResolvedEnvironmentTarget(pair.Key, pair.Value)));
            _records.Clear();
            _environmentTargets.Clear();
        }

        if (_destinationResolver is null || records.Length == 0)
        {
            return;
        }

        var budget = new CancellationTokenSource(_flushTimeout, _timeProvider);
        Task flush;
        try
        {
            flush = Task.Run(
                () => FlushAsync(
                    records,
                    target,
                    budget.Token),
                CancellationToken.None);
        }
        catch (Exception)
        {
            budget.Dispose();
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.TelemetryFlush);
            return;
        }

        bool disposeBudgetHere = true;
        try
        {
            await flush.WaitAsync(budget.Token);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.TelemetryDeadline);
            disposeBudgetHere = false;
            ObserveLateCompletion(flush, budget);
        }
        catch (Exception)
        {
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.TelemetryFlush);
        }
        finally
        {
            if (disposeBudgetHere)
            {
                budget.Dispose();
            }
        }
    }

    private async Task FlushAsync(
        CommandTelemetryRecord[] records,
        TelemetryEnvironmentTarget target,
        CancellationToken cancellationToken)
    {
        OptionalMeasurementFailureCategory failureCategory =
            OptionalMeasurementFailureCategory.TelemetryDestinationResolution;
        try
        {
            TelemetryDestination? destination =
                await _destinationResolver!.ResolveAsync(target, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (destination is not null)
            {
                failureCategory = OptionalMeasurementFailureCategory.TelemetrySerialization;
                string sessionId = Guid.NewGuid().ToString("D");
                IOneDSTelemetry[] events = records
                    .Select((record, index) =>
                    {
                        var item = new CommandTelemetryEvent(
                            record,
                            destination,
                            _cliVersion!,
                            sessionId,
                            index + 1);
                        var endpoint =
                            new OneDSEndpointConfiguration(
                                destination.Endpoint,
                                destination.ApiKey);
                        item.InstrumentationKey = endpoint.InstrumentationKey;
                        return (IOneDSTelemetry)item;
                    })
                    .ToArray();
                string payload =
                    CommonSchemaV4Serializer.SerializeToJsonStream(events);
                cancellationToken.ThrowIfCancellationRequested();

                failureCategory =
                    OptionalMeasurementFailureCategory.TelemetryTransport;
                HttpStatusCode status =
                    await _transport!.SendAsync(
                        payload,
                        destination,
                        cancellationToken);
                if (status is < HttpStatusCode.OK
                    or >= HttpStatusCode.MultipleChoices)
                {
                    OptionalMeasurementDiagnostics.Report(
                        OptionalMeasurementFailureCategory.TelemetryTransportResponse);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            OptionalMeasurementDiagnostics.Report(failureCategory);
        }
    }

    private static void ObserveLateCompletion(
        Task flush,
        CancellationTokenSource budget)
    {
        _ = flush.ContinueWith(
            static (completed, state) =>
            {
                try
                {
                    if (completed.IsFaulted)
                    {
                        _ = completed.Exception;
                        OptionalMeasurementDiagnostics.Report(
                            OptionalMeasurementFailureCategory.TelemetryFlush);
                    }
                }
                finally
                {
                    ((CancellationTokenSource)state!).Dispose();
                }
            },
            budget,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool TryGetTimestamp(out DateTimeOffset timestamp)
    {
        try
        {
            timestamp = _clock!.UtcNow;
            return true;
        }
        catch (Exception)
        {
            _failedOperationCount = Increment(_failedOperationCount);
            _lastDiagnosticStatus = CommandTelemetryDiagnosticStatus.ClockFailure;
            timestamp = default;
            return false;
        }
    }

    private void ObserveInvalidTransition()
    {
        _invalidTransitionCount = Increment(_invalidTransitionCount);
        _lastDiagnosticStatus =
            CommandTelemetryDiagnosticStatus.InvalidTransition;
    }

    private bool TryAddRecord(CommandTelemetryRecord record)
    {
        if (_records.Count >= MaximumRecordCount)
        {
            _invalidTransitionCount = Increment(_invalidTransitionCount);
            _lastDiagnosticStatus =
                CommandTelemetryDiagnosticStatus.BufferCapacity;
            return false;
        }

        _records.Add(record);
        return true;
    }

    private void ApplySelectedProfile(
        CommandTelemetryProfileContext? profile)
    {
        if (profile is null)
        {
            ClearCurrentAttribution();
        }
        else if (_tenantConflict)
        {
            _tenantId = null;
            _principalObjectId = null;
        }
        else if (_principalConflict)
        {
            _tenantId = _freshTenantId;
            _principalObjectId = null;
        }
        else
        {
            CommandTelemetryProfileContext selected = profile.Value;
            _tenantId = selected.TenantId;
            _principalObjectId = selected.PrincipalObjectId;
        }
    }

    private void ApplyFreshContext(AuthenticatedTenantContext context)
    {
        _tenantId = _tenantConflict ? null : _freshTenantId;
        _principalObjectId =
            _principalConflict ? null : context.PrincipalObjectId;
    }

    private void ClearCurrentAttribution()
    {
        _tenantId = null;
        _principalObjectId = null;
    }

    private void ApplyAttributionToRecords()
    {
        for (var index = 0; index < _records.Count; index++)
        {
            _records[index] = _records[index].WithAttribution(
                _tenantId,
                _principalObjectId);
        }
    }

    private static int Increment(int count) =>
        Math.Min(count + 1, MaximumDiagnosticCount);

    private enum SessionState
    {
        Empty,
        Host,
        Attempt,
        Outcome,
    }

    private sealed class ObservationScopes(
        IDisposable? tenantScope,
        IDisposable? environmentScope) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            environmentScope?.Dispose();
            tenantScope?.Dispose();
        }
    }
}

internal sealed class CommandTelemetrySessionFactory
{
    internal static readonly TimeSpan DefaultPolicyTimeout =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumPolicyTimeout =
        TimeSpan.FromMinutes(1);

    private readonly TelemetryControlService? _controls;
    private readonly ITelemetryDestinationResolver? _destinationResolver;
    private readonly IClock? _clock;
    private readonly string? _cliVersion;
    private readonly CommandTelemetryTenantObserver? _tenantObserver;
    private readonly CommandTelemetryEnvironmentObserver? _environmentObserver;
    private readonly ICommandTelemetryProfileContextSource? _profileSource;
    private readonly TimeSpan _policyTimeout;
    private readonly TimeSpan? _flushTimeout;
    private readonly ICommandTelemetryTransport? _transport;

    private CommandTelemetrySessionFactory()
    {
    }

    internal CommandTelemetrySessionFactory(
        TelemetryControlService controls,
        ITelemetryDestinationResolver destinationResolver,
        IClock clock,
        Assembly assembly,
        CommandTelemetryTenantObserver? tenantObserver = null,
        CommandTelemetryEnvironmentObserver? environmentObserver = null,
        ICommandTelemetryProfileContextSource? profileSource = null,
        TimeSpan? policyTimeout = null,
        TimeSpan? flushTimeout = null,
        ICommandTelemetryTransport? transport = null)
    {
        _controls = controls ?? throw new ArgumentNullException(nameof(controls));
        _destinationResolver = destinationResolver
            ?? throw new ArgumentNullException(nameof(destinationResolver));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _cliVersion = assembly
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? throw new ArgumentException(
                "The CLI assembly must have an informational version.",
                nameof(assembly));
        _tenantObserver = tenantObserver;
        _environmentObserver = environmentObserver;
        _profileSource = profileSource;
        _policyTimeout = policyTimeout ?? DefaultPolicyTimeout;
        ValidatePolicyTimeout(_policyTimeout, nameof(policyTimeout));
        if (flushTimeout is { } timeout)
        {
            CommandTelemetrySession.ValidateFlushTimeout(
                timeout,
                nameof(flushTimeout));
        }

        _flushTimeout = flushTimeout;
        _transport = transport;
    }

    internal static CommandTelemetrySessionFactory Disabled { get; } = new();

    internal async ValueTask<CommandTelemetrySession> CreateAsync(
        bool suppressTelemetry,
        CancellationToken cancellationToken)
    {
        if (suppressTelemetry || _controls is null)
        {
            return CommandTelemetrySession.Disabled(
                _tenantObserver,
                _environmentObserver);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var budget = new CancellationTokenSource(_policyTimeout);
        var workerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                budget.Token);
        Task<bool> policy;
        try
        {
            policy = Task.Run(
                async () =>
                    await _controls.IsCollectionEnabledAsync(
                        workerCancellation.Token),
                CancellationToken.None);
        }
        catch (Exception)
        {
            workerCancellation.Dispose();
            budget.Dispose();
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.TelemetrySessionCreation);
            return CommandTelemetrySession.Disabled(
                _tenantObserver,
                _environmentObserver);
        }

        bool disposeCancellationHere = true;
        try
        {
            bool enabled = await policy.WaitAsync(workerCancellation.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.IsCancellationRequested)
            {
                OptionalMeasurementDiagnostics.Report(
                    OptionalMeasurementFailureCategory.TelemetryPolicyDeadline);
                return CommandTelemetrySession.Disabled(
                    _tenantObserver,
                    _environmentObserver);
            }

            if (!enabled)
            {
                return CommandTelemetrySession.Disabled(
                    _tenantObserver,
                    _environmentObserver);
            }

            return new CommandTelemetrySession(
                _destinationResolver!,
                _clock!,
                _cliVersion!,
                _tenantObserver,
                _environmentObserver,
                await TryLoadSelectedProfileAsync(
                    budget,
                    cancellationToken,
                    workerCancellation.Token),
                _flushTimeout,
                _transport);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            disposeCancellationHere = false;
            ObserveLatePolicyCompletion(policy, workerCancellation, budget);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.TelemetryPolicyDeadline);
            disposeCancellationHere = false;
            ObserveLatePolicyCompletion(policy, workerCancellation, budget);
            return CommandTelemetrySession.Disabled(
                _tenantObserver,
                _environmentObserver);
        }
        catch (Exception)
        {
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.TelemetrySessionCreation);
            return CommandTelemetrySession.Disabled(
                _tenantObserver,
                _environmentObserver);
        }
        finally
        {
            if (disposeCancellationHere)
            {
                workerCancellation.Dispose();
                budget.Dispose();
            }
        }
    }

    private static void ValidatePolicyTimeout(
        TimeSpan timeout,
        string parameterName)
    {
        if (timeout <= TimeSpan.Zero || timeout > MaximumPolicyTimeout)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"The telemetry policy timeout must be positive and no greater than {MaximumPolicyTimeout}.");
        }
    }

    private async ValueTask<CommandTelemetryProfileContext?>
        TryLoadSelectedProfileAsync(
            CancellationTokenSource budget,
            CancellationToken callerCancellation,
            CancellationToken workerCancellation)
    {
        if (_profileSource is null)
        {
            return null;
        }

        if (budget.IsCancellationRequested)
        {
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.TelemetryProfileDeadline);
            return null;
        }

        Task<CommandTelemetryProfileContext?> read;
        try
        {
            read = Task.Run(
                async () =>
                    await _profileSource
                        .GetSelectedAsync(workerCancellation)
                        .ConfigureAwait(false),
                CancellationToken.None);
        }
        catch (Exception)
        {
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.TelemetryProfileRead);
            return null;
        }

        try
        {
            return await read.WaitAsync(workerCancellation);
        }
        catch (OperationCanceledException)
            when (callerCancellation.IsCancellationRequested)
        {
            ObserveLateProfileRead(read);
            callerCancellation.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException)
            when (budget.IsCancellationRequested)
        {
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.TelemetryProfileDeadline);
            ObserveLateProfileRead(read);
            return null;
        }
        catch (Exception)
        {
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.TelemetryProfileRead);
            return null;
        }
    }

    private static void ObserveLateProfileRead(
        Task<CommandTelemetryProfileContext?> read)
    {
        _ = read.ContinueWith(
            static completed =>
            {
                if (completed.IsFaulted)
                {
                    _ = completed.Exception;
                    OptionalMeasurementDiagnostics.Report(
                        OptionalMeasurementFailureCategory.TelemetryProfileRead);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ObserveLatePolicyCompletion(
        Task<bool> policy,
        CancellationTokenSource workerCancellation,
        CancellationTokenSource budget)
    {
        _ = policy.ContinueWith(
            static (completed, state) =>
            {
                var cancellation =
                    ((CancellationTokenSource Worker, CancellationTokenSource Budget))state!;
                try
                {
                    if (completed.IsFaulted)
                    {
                        _ = completed.Exception;
                        OptionalMeasurementDiagnostics.Report(
                            OptionalMeasurementFailureCategory.TelemetrySessionCreation);
                    }
                }
                finally
                {
                    cancellation.Worker.Dispose();
                    cancellation.Budget.Dispose();
                }
            },
            (workerCancellation, budget),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}

internal sealed class CommandTelemetrySessionAccessor
{
    private readonly AsyncLocal<Scope?> _current = new();

    internal CommandTelemetrySession Current
    {
        get
        {
            Scope? current = Scope.FirstActive(_current.Value);
            if (!ReferenceEquals(current, _current.Value))
            {
                _current.Value = current;
            }

            return current?.Session ?? CommandTelemetrySession.Disabled();
        }
    }

    internal IDisposable Push(CommandTelemetrySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var scope = new Scope(this, session, Scope.FirstActive(_current.Value));
        _current.Value = scope;
        return scope;
    }

    private sealed class Scope(
        CommandTelemetrySessionAccessor owner,
        CommandTelemetrySession session,
        Scope? previous) : IDisposable
    {
        private int _disposed;

        internal CommandTelemetrySession Session { get; } = session;

        internal Scope? Previous { get; } = previous;

        internal bool IsActive => Volatile.Read(ref _disposed) == 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (ReferenceEquals(owner._current.Value, this))
            {
                owner._current.Value = FirstActive(Previous);
            }
        }

        internal static Scope? FirstActive(Scope? scope)
        {
            while (scope is not null && !scope.IsActive)
            {
                scope = scope.Previous;
            }

            return scope;
        }
    }
}
