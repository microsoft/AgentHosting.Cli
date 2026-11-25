using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Diagnostics;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.PowerPlatform.Tooling.OneDS;

namespace Microsoft.Agents.Hosting.Cli.Telemetry;

internal enum TelemetryLocation
{
    Global,
    Europe,
}

internal sealed record TelemetryDestination(
    Uri Endpoint,
    string ApiKey,
    string AppName,
    string ClusterCategory,
    string Cloud,
    TelemetryLocation Location);

internal sealed record TelemetryEnvironmentTarget
{
    private TelemetryEnvironmentTarget(
        IReadOnlyList<ResolvedEnvironmentTarget> environments,
        bool isComplete)
    {
        Environments = environments;
        IsComplete = isComplete;
    }

    internal static TelemetryEnvironmentTarget Unknown { get; } =
        new([], isComplete: false);

    internal IReadOnlyList<ResolvedEnvironmentTarget> Environments { get; }

    internal bool IsComplete { get; }

    internal static TelemetryEnvironmentTarget Resolved(
        IEnumerable<ResolvedEnvironmentTarget> environments)
    {
        ArgumentNullException.ThrowIfNull(environments);
        ResolvedEnvironmentTarget[] snapshot = [.. environments];
        if (snapshot.Length == 0
            || snapshot.Any(target =>
                string.IsNullOrWhiteSpace(target.EnvironmentId)
                || string.IsNullOrWhiteSpace(target.TenantId)))
        {
            throw new ArgumentException(
                "Resolved telemetry targets require environment and tenant identifiers.",
                nameof(environments));
        }

        return new(snapshot, isComplete: true);
    }
}

internal interface ITelemetryDestinationResolver
{
    bool ActivationAvailable { get; }

    ValueTask<TelemetryDestination?> ResolveAsync(
        TelemetryEnvironmentTarget target,
        CancellationToken cancellationToken);
}

/// <summary>
/// Sends nothing, for an invocation whose ring could not be read.
/// </summary>
internal sealed class UnclassifiedTelemetryDestinationResolver : ITelemetryDestinationResolver
{
    public bool ActivationAvailable => false;

    public ValueTask<TelemetryDestination?> ResolveAsync(
        TelemetryEnvironmentTarget target,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<TelemetryDestination?>(null);
}

internal sealed class TelemetryDestinationResolver : ITelemetryDestinationResolver{
    private const TelemetryLocation DefaultLocation = TelemetryLocation.Europe;

    private static readonly HashSet<string> EuGeographies = new(StringComparer.Ordinal)
    {
        "europe",
        "france",
        "germany",
        "italy",
        "norway",
        "poland",
        "sweden",
        "switzerland",
        "unitedkingdom",
    };

    private static readonly HashSet<string> NonEuGeographies = new(StringComparer.Ordinal)
    {
        "asia",
        "australia",
        "brazil",
        "canada",
        "chile",
        "india",
        "indonesia",
        "israel",
        "japan",
        "korea",
        "malaysia",
        "mexico",
        "newzealand",
        "qatar",
        "saudiarabia",
        "singapore",
        "southafrica",
        "southamerica",
        "taiwan",
        "unitedarabemirates",
        "unitedstates",
    };

    private static readonly string[] RingSuffixes = ["firstrelease", "euap", "preview"];

    private readonly PowerPlatformApiTargetConfig _target;
    internal static readonly TimeSpan DefaultMetadataTimeout =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumMetadataTimeout =
        TimeSpan.FromSeconds(5);

    private readonly IEnvironmentGeographyClient? _geographyClient;
    private readonly CommandTelemetryTenantObserver? _tenantObserver;
    private readonly string? _productionKey;
    private readonly string? _nonProductionKey;
    private readonly TimeSpan _metadataTimeout;
    private readonly TelemetryRoutingDiagnostics? _diagnostics;
    private readonly FileTelemetryCollectorCache? _collectorCache;

    internal TelemetryDestinationResolver(
        PowerPlatformApiTargetConfig target,
        IEnvironmentGeographyClient? geographyClient)
        : this(
            target,
            geographyClient,
            TelemetryIngestionConfiguration.ProductionKey,
            TelemetryIngestionConfiguration.NonProductionKey)
    {
    }

    internal TelemetryDestinationResolver(
        PowerPlatformApiTargetConfig target,
        IEnvironmentGeographyClient? geographyClient,
        string? productionKey)
        : this(
            target,
            geographyClient,
            productionKey,
            TelemetryIngestionConfiguration.NonProductionKey)
    {
    }

    internal TelemetryDestinationResolver(
        PowerPlatformApiTargetConfig target,
        IEnvironmentGeographyClient? geographyClient,
        string? productionKey,
        string? nonProductionKey,
        CommandTelemetryTenantObserver? tenantObserver = null,
        TimeSpan? metadataTimeout = null,
        TelemetryRoutingDiagnostics? diagnostics = null,
        FileTelemetryCollectorCache? collectorCache = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _geographyClient = geographyClient;
        _productionKey = productionKey;
        _nonProductionKey = nonProductionKey;
        _tenantObserver = tenantObserver;
        _metadataTimeout = metadataTimeout ?? DefaultMetadataTimeout;
        _diagnostics = diagnostics;
        _collectorCache = collectorCache;
        if (_metadataTimeout <= TimeSpan.Zero
            || _metadataTimeout > MaximumMetadataTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(metadataTimeout),
                $"The telemetry metadata timeout must be positive and no greater than {MaximumMetadataTimeout}.");
        }
    }

    public bool ActivationAvailable =>
        _target.Cloud == CloudInstance.Public
        && TryValidateKey(SelectedKey(), out _);

    public async ValueTask<TelemetryDestination?> ResolveAsync(
        TelemetryEnvironmentTarget target,
        CancellationToken cancellationToken)
    {
        try
        {
            _diagnostics?.Write("routing-start", FormattableString.Invariant(
                $"cloud={_target.Cloud} ring={_target.Ring} targetComplete={target.IsComplete} environmentCount={target.Environments.Count}"));
            // Sovereign selections are an explicit no-send boundary. Never fall back to public.
            if (_target.Cloud != CloudInstance.Public)
            {
                _diagnostics?.Write("routing-skipped", "reason=cloud-no-send collector=none");
                return null;
            }

            if (!TryValidateKey(SelectedKey(), out string apiKey))
            {
                _diagnostics?.Write("routing-skipped", "reason=ingestion-key-unavailable collector=none");
                return null;
            }

            bool production = _target.Ring == PowerPlatformApiRing.Production;
            TelemetryLocation location =
                production ? DefaultLocation : TelemetryLocation.Global;
            if (production
                && target.IsComplete
                && target.Environments.Count > 0
                && _geographyClient is not null)
            {
                location = await ResolveProductionLocationAsync(
                    target.Environments,
                    cancellationToken);
            }
            else
            {
                _diagnostics?.Write("lookup-skipped", "reason=" + (!production
                    ? "nonproduction"
                    : !target.IsComplete
                        ? "target-incomplete"
                        : target.Environments.Count == 0
                            ? "no-environment-targets"
                            : "geography-client-unavailable"));
            }

            _diagnostics?.Write("destination", location == TelemetryLocation.Europe
                ? "collector=EU" : "collector=Global");
            return new TelemetryDestination(
                location == TelemetryLocation.Europe
                    ? OneDSCollectorEndpoints.RegionalPublicCloud.EU
                    : OneDSCollectorEndpoints.RegionalPublicCloud.US,
                apiKey,
                production ? "powerappsclient" : "powerappsclientnonprod",
                production ? "prod" : "test",
                production ? "prod" : "test",
                location);
        }
        finally
        {
            _diagnostics?.Complete();
        }
    }

    internal static TelemetryLocation ResolveLocation(string? geography)
        => TryResolveKnownLocation(geography, out var location) ? location : DefaultLocation;

    private static bool TryResolveKnownLocation(string? geography, out TelemetryLocation location)
    {
        location = DefaultLocation;
        if (string.IsNullOrWhiteSpace(geography))
        {
            return false;
        }

        string normalized = NormalizeGeography(geography);
        if (NonEuGeographies.Contains(normalized))
        {
            location = TelemetryLocation.Global;
            return true;
        }

        if (!EuGeographies.Contains(normalized))
            return false;
        location = TelemetryLocation.Europe;
        return true;
    }

    private static string DiagnosticGeography(string? geography)
    {
        if (string.IsNullOrWhiteSpace(geography)) return "missing";
        string normalized = NormalizeGeography(geography);
        return EuGeographies.Contains(normalized) || NonEuGeographies.Contains(normalized)
            ? normalized : "unrecognized";
    }

    private static string NormalizeGeography(string geography)
    {
        string normalized = new(
            geography
                .Trim()
                .ToLowerInvariant()
                .Where(character => character is not (' ' or '_' or '-'))
                .ToArray());
        foreach (string suffix in RingSuffixes)
        {
            if (normalized.Length > suffix.Length
                && normalized.EndsWith(suffix, StringComparison.Ordinal))
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }

        return normalized;
    }

    private async Task<TelemetryLocation> ResolveProductionLocationAsync(
        IReadOnlyList<ResolvedEnvironmentTarget> environments,
        CancellationToken cancellationToken)
    {
        _diagnostics?.Write("lookup-start", FormattableString.Invariant(
            $"budgetMs={(long)_metadataTimeout.TotalMilliseconds}"));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        budget.CancelAfter(_metadataTimeout);
        CancellationToken budgetToken = budget.Token;
        Task<TelemetryLocation> lookup = Task.Run(
            async () =>
            {
                using IDisposable? suppression =
                    _tenantObserver?.BeginSuppression();
                int environmentIndex = 0;
                foreach (ResolvedEnvironmentTarget environment in environments)
                {
                    budgetToken.ThrowIfCancellationRequested();
                    _diagnostics?.Write("environment-lookup", FormattableString.Invariant(
                        $"environmentIndex={++environmentIndex}"));
                    if (_collectorCache is not null
                        && await _collectorCache.ReadAsync(_target, environment, budgetToken).ConfigureAwait(false)
                            is { } cached)
                    {
                        if (cached == TelemetryLocation.Europe)
                            return TelemetryLocation.Europe;
                        continue;
                    }
                    string? geography = await _geographyClient!
                        .GetGeographyAsync(environment, budgetToken)
                        .ConfigureAwait(false);
                    budgetToken.ThrowIfCancellationRequested();
                    _diagnostics?.Write("geography-result", "geography=" + DiagnosticGeography(geography));
                    bool known = TryResolveKnownLocation(geography, out var resolved);
                    if (known && _collectorCache is not null)
                        await _collectorCache.WriteAsync(_target, environment, resolved, budgetToken).ConfigureAwait(false);
                    if (resolved == TelemetryLocation.Europe)
                        return TelemetryLocation.Europe;
                }

                return TelemetryLocation.Global;
            },
            CancellationToken.None);

        try
        {
            return await lookup.WaitAsync(budgetToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            _diagnostics?.Write("lookup-cancelled");
            ObserveLateLookup(lookup);
            throw;
        }
        catch (OperationCanceledException)
            when (budget.IsCancellationRequested)
        {
            _diagnostics?.WriteDeadline();
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.TelemetryGeographyDeadline);
            ObserveLateLookup(lookup);
            return DefaultLocation;
        }
        catch (Exception)
        {
            _diagnostics?.Write("lookup-failed");
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.TelemetryGeographyLookup);
            return DefaultLocation;
        }
    }

    private static void ObserveLateLookup(Task lookup)
    {
        _ = lookup.ContinueWith(
            static completed =>
            {
                if (completed.IsFaulted)
                {
                    _ = completed.Exception;
                    OptionalMeasurementDiagnostics.Report(
                        OptionalMeasurementFailureCategory.TelemetryGeographyLookup);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private string? SelectedKey() =>
        _target.Ring == PowerPlatformApiRing.Production
            ? _productionKey
            : _nonProductionKey;

    private static bool TryValidateKey(string? key, out string apiKey)
    {
        apiKey = string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        try
        {
            var configuration =
                new OneDSEndpointConfiguration(
                    OneDSCollectorEndpoints.RegionalPublicCloud.EU,
                    key);
            apiKey = configuration.ApiKey;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
