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
        TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan MaximumMetadataTimeout =
        TimeSpan.FromSeconds(1);

    private readonly IEnvironmentGeographyClient? _geographyClient;
    private readonly CommandTelemetryTenantObserver? _tenantObserver;
    private readonly string? _productionKey;
    private readonly string? _nonProductionKey;
    private readonly TimeSpan _metadataTimeout;

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
        TimeSpan? metadataTimeout = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _geographyClient = geographyClient;
        _productionKey = productionKey;
        _nonProductionKey = nonProductionKey;
        _tenantObserver = tenantObserver;
        _metadataTimeout = metadataTimeout ?? DefaultMetadataTimeout;
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
        // Sovereign selections are an explicit no-send boundary. Never fall back to public.
        if (_target.Cloud != CloudInstance.Public
            || !TryValidateKey(SelectedKey(), out string apiKey))
        {
            return null;
        }

        bool production = _target.Ring == PowerPlatformApiRing.Production;
        TelemetryLocation location =
            production ? TelemetryLocation.Europe : TelemetryLocation.Global;
        if (production
            && target.IsComplete
            && target.Environments.Count > 0
            && _geographyClient is not null)
        {
            location = await ResolveProductionLocationAsync(
                target.Environments,
                cancellationToken);
        }

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

    internal static TelemetryLocation ResolveLocation(string? geography)
    {
        if (string.IsNullOrWhiteSpace(geography))
        {
            return TelemetryLocation.Europe;
        }

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

        if (NonEuGeographies.Contains(normalized))
        {
            return TelemetryLocation.Global;
        }

        // Known EU and every unknown value route to EU by policy.
        return EuGeographies.Contains(normalized)
            ? TelemetryLocation.Europe
            : TelemetryLocation.Europe;
    }

    private async Task<TelemetryLocation> ResolveProductionLocationAsync(
        IReadOnlyList<ResolvedEnvironmentTarget> environments,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        budget.CancelAfter(_metadataTimeout);
        CancellationToken budgetToken = budget.Token;
        Task<TelemetryLocation> lookup = Task.Run(
            async () =>
            {
                using IDisposable? suppression =
                    _tenantObserver?.BeginSuppression();
                foreach (ResolvedEnvironmentTarget environment in environments)
                {
                    string? geography = await _geographyClient!
                        .GetGeographyAsync(environment, budgetToken)
                        .ConfigureAwait(false);
                    if (ResolveLocation(geography) != TelemetryLocation.Global)
                    {
                        return TelemetryLocation.Europe;
                    }
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
            ObserveLateLookup(lookup);
            throw;
        }
        catch (OperationCanceledException)
            when (budget.IsCancellationRequested)
        {
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.TelemetryGeographyDeadline);
            ObserveLateLookup(lookup);
            return TelemetryLocation.Europe;
        }
        catch (Exception)
        {
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.TelemetryGeographyLookup);
            return TelemetryLocation.Europe;
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
