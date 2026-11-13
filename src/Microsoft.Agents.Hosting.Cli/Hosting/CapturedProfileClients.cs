using System.Collections.Concurrent;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Telemetry;

namespace Microsoft.Agents.Hosting.Cli.Hosting;

internal sealed class CapturedConnectionConsentFlow(
    Func<CopilotEnvironmentContext, ConnectionConsentLadder> createFlow) : IConnectionConsentFlow
{
    public async Task<ConnectionConsentResult> CreateConnectionAsync(CopilotEnvironmentContext environment, string connectorId,
        bool openBrowser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        cancellationToken.ThrowIfCancellationRequested();
        if (environment.Context is { } context)
        {
            PowerAppsServiceTarget.RequireContext(context);
            if (context.Profile.PrincipalType != AuthPrincipalType.User)
                throw new UserPrincipalRequiredException();
        }
        using var flow = createFlow(environment);
        return await flow.CreateConnectionAsync(environment, connectorId, openBrowser, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class CapturedProfileClients(
    HttpClient http, IProfileResourceTokenProvider tokens, AuthProfile? bootstrapProfile) : IProfileEnvironmentClientFactory
{
    private readonly ProfilePowerPlatformEnvironmentClientFactory _factory = new(http, tokens);
    private sealed record ContextKey(string Profile, long Revision, PowerPlatformApiTargetConfig Target);
    private readonly ConcurrentDictionary<(string Environment, string Tenant),
        ConcurrentDictionary<ContextKey, EnvironmentResolutionContext>> _contexts = new();
    private EnvironmentResolutionContext? _current = bootstrapProfile is null ? null
        : new(bootstrapProfile, PowerPlatformApiTargetSelection.TargetFor(bootstrapProfile.Cloud, bootstrapProfile.Ring));

    internal EnvironmentResolutionContext? Current => Volatile.Read(ref _current);
    internal EnvironmentResolutionContext? For(ResolvedEnvironmentTarget target)
    {
        if (_contexts.TryGetValue((target.EnvironmentId.ToUpperInvariant(), target.TenantId.ToUpperInvariant()), out var matches))
        {
            var snapshot = matches.Values.Take(2).ToArray();
            return snapshot.Length == 1 ? snapshot[0] : null;
        }
        return null;
    }

    internal void Capture(ResolvedEnvironmentTarget target, EnvironmentResolutionContext context)
    {
        Volatile.Write(ref _current, context);
        _contexts.GetOrAdd((target.EnvironmentId.ToUpperInvariant(), target.TenantId.ToUpperInvariant()),
            _ => new())[new(context.Profile.ProfileId, context.Profile.Revision, context.Target)] = context;
    }

    public IEnvironmentClient CreateClient(EnvironmentResolutionContext context)
    {
        Volatile.Write(ref _current, context);
        return new CapturedInventory(_factory.CreateClient(context), context, _contexts);
    }

    private sealed class CapturedInventory(IEnvironmentClient inner, EnvironmentResolutionContext context,
        ConcurrentDictionary<(string Environment, string Tenant),
            ConcurrentDictionary<ContextKey, EnvironmentResolutionContext>> contexts) : IEnvironmentClient
    {
        public async Task<IReadOnlyList<PowerPlatformEnvironment>> ListEnvironmentsAsync(CancellationToken cancellationToken)
        {
            var environments = await inner.ListEnvironmentsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var environment in environments)
                contexts.GetOrAdd((environment.EnvironmentId.ToUpperInvariant(), environment.TenantId.ToUpperInvariant()),
                    _ => new())[new(context.Profile.ProfileId, context.Profile.Revision, context.Target)] = context;
            return environments;
        }
    }
}

internal sealed class CapturedProfileEnvironmentObserver(
    CapturedProfileClients contexts, IResolvedEnvironmentObserver inner) : IProfileResolvedEnvironmentObserver
{
    public bool IsObservationEnabled => inner.IsObservationEnabled;
    public void Observe(ResolvedEnvironmentTarget target) => inner.Observe(target);
    public void Observe(ResolvedEnvironmentTarget target, EnvironmentResolutionContext context)
    {
        contexts.Capture(target, context);
        inner.Observe(target);
    }
}

internal sealed class CapturedProfileGeographyClient(
    HttpClient http, CapturedProfileClients contexts, MsalSilentTokenProvider selectedTokens) : IEnvironmentGeographyClient
{
    public Task<string?> GetGeographyAsync(ResolvedEnvironmentTarget target, CancellationToken cancellationToken)
    {
        var context = contexts.For(target);
        if (context is null) return Task.FromResult<string?>(null);
        return new PowerPlatformApiEnvironmentGeographyClient(http, new CapturedSelectedTokens(selectedTokens, context.Profile),
            context.Target, new()).GetGeographyAsync(target, cancellationToken);
    }

    private sealed class CapturedSelectedTokens(MsalSilentTokenProvider tokens, AuthProfile profile) : ISelectedAccountTokenProvider
    {
        public Task<TokenAcquisitionResult?> TryAcquireTokenSilentAsync(string resource, string targetTenantId, CancellationToken cancellationToken) =>
            tokens.TryAcquireForProfileAsync(profile, resource, targetTenantId, cancellationToken);
    }
}

internal sealed class CapturedProfileTelemetryDestinationResolver(
    CapturedProfileClients contexts, IEnvironmentGeographyClient geography,
    PowerPlatformApiTargetConfig bootstrapTarget, bool targetKnown,
    CommandTelemetryTenantObserver? tenantObserver) : ITelemetryDestinationResolver
{
    public bool ActivationAvailable => targetKnown &&
        new TelemetryDestinationResolver(contexts.Current?.Target ?? bootstrapTarget, geography).ActivationAvailable;

    public ValueTask<TelemetryDestination?> ResolveAsync(TelemetryEnvironmentTarget target, CancellationToken cancellationToken)
    {
        if (!targetKnown) return ValueTask.FromResult<TelemetryDestination?>(null);
        var captured = target.Environments.Select(contexts.For).ToArray();
        if (captured.Any(context => context is null)
            || captured.Select(context => (context!.Profile.ProfileId, context.Profile.Revision, context.Target)).Distinct().Skip(1).Any())
            return ValueTask.FromResult<TelemetryDestination?>(null);
        var resourceTarget = captured.FirstOrDefault()?.Target ?? contexts.Current?.Target ?? bootstrapTarget;
        return new TelemetryDestinationResolver(resourceTarget, geography,
            TelemetryIngestionConfiguration.ProductionKey, TelemetryIngestionConfiguration.NonProductionKey, tenantObserver)
            .ResolveAsync(target, cancellationToken);
    }
}
