using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Telemetry;

internal sealed class CommandTelemetryTenantObserver :
    IAuthenticationResultObserver,
    IAuthProfileStateObserver
{
    private readonly AsyncLocal<ObservationScope?> _current = new();
    private readonly AuthSessionContext? _sessionContext;
    private readonly AuthProfileMutationRevision _mutationRevision;
    private readonly object _sessionsGate = new();
    private readonly Dictionary<CommandTelemetrySession, int> _liveSessions = [];
    private long _observedMutationRevision;

    public CommandTelemetryTenantObserver(
        AuthSessionContext? sessionContext = null,
        AuthProfileMutationRevision? mutationRevision = null)
    {
        _sessionContext = sessionContext;
        _mutationRevision =
            mutationRevision ?? new AuthProfileMutationRevision();
    }

    public bool IsObservationEnabled => _current.Value?.Session is not null;

    public long CaptureGeneration() =>
        _current.Value?.Session is not null
            ? CurrentRevision
            : -1;

    public void Observe(AuthenticatedTenantContext context) =>
        ObserveCurrent(context, CurrentRevision);

    public void Observe(
        AuthenticatedTenantContext context,
        long generation) =>
        ObserveCurrent(context, generation);

    public void ObserveProfileMutation(long revision)
    {
        RecordObservedRevision(revision);
        CommandTelemetrySession[] sessions;
        lock (_sessionsGate)
        {
            sessions = [.. _liveSessions.Keys];
        }

        foreach (CommandTelemetrySession session in sessions)
        {
            session.ObserveProfileMutation(revision);
        }
    }

    public void ObserveActiveProfile(AuthProfile? profile, long revision)
    {
        if (revision != CurrentRevision)
        {
            return;
        }

        _current.Value?.Session?.ObserveActiveProfile(
            CommandTelemetryProfileContext.FromProfile(
                profile,
                _sessionContext),
            revision);
    }

    internal IDisposable BeginSession(CommandTelemetrySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Register(session);
        var scope = new ObservationScope(this, session, _current.Value);
        _current.Value = scope;
        return scope;
    }

    private void ObserveCurrent(
        AuthenticatedTenantContext context,
        long generation)
    {
        if (generation != CurrentRevision)
        {
            return;
        }

        _current.Value?.Session?.ObserveAuthenticatedAccount(
            context,
            generation);
    }

    private long CurrentRevision =>
        Math.Max(
            _mutationRevision.Current,
            Interlocked.Read(ref _observedMutationRevision));

    private void RecordObservedRevision(long revision)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref _observedMutationRevision);
            if (revision <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(
                ref _observedMutationRevision,
                revision,
                current) != current);
    }

    private void Register(CommandTelemetrySession session)
    {
        lock (_sessionsGate)
        {
            _liveSessions.TryGetValue(session, out int count);
            _liveSessions[session] = count + 1;
        }
    }

    private void Unregister(CommandTelemetrySession session)
    {
        lock (_sessionsGate)
        {
            if (!_liveSessions.TryGetValue(session, out int count))
            {
                return;
            }

            if (count == 1)
            {
                _liveSessions.Remove(session);
            }
            else
            {
                _liveSessions[session] = count - 1;
            }
        }
    }

    internal IDisposable BeginSuppression()
    {
        var scope = new ObservationScope(this, session: null, _current.Value);
        _current.Value = scope;
        return scope;
    }

    private sealed class ObservationScope(
        CommandTelemetryTenantObserver owner,
        CommandTelemetrySession? session,
        ObservationScope? previous) : IDisposable
    {
        private int _disposed;
        private readonly CommandTelemetrySession? _session = session;

        internal CommandTelemetrySession? Session =>
            Volatile.Read(ref _disposed) == 0 ? _session : null;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (ReferenceEquals(owner._current.Value, this))
            {
                owner._current.Value = previous;
            }

            if (_session is not null)
            {
                owner.Unregister(_session);
            }
        }
    }
}
