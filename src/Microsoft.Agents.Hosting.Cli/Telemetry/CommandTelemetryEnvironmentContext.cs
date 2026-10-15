using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Telemetry;

internal sealed class CommandTelemetryEnvironmentObserver :
    IResolvedEnvironmentObserver
{
    private readonly AsyncLocal<ObservationScope?> _current = new();

    public bool IsObservationEnabled => _current.Value?.Session is not null;

    public void Observe(ResolvedEnvironmentTarget target) =>
        _current.Value?.Session?.ObserveResolvedEnvironment(target);

    internal IDisposable BeginSession(CommandTelemetrySession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var scope = new ObservationScope(this, session, _current.Value);
        _current.Value = scope;
        return scope;
    }

    internal IDisposable BeginSuppression()
    {
        var scope = new ObservationScope(this, session: null, _current.Value);
        _current.Value = scope;
        return scope;
    }

    private sealed class ObservationScope(
        CommandTelemetryEnvironmentObserver owner,
        CommandTelemetrySession? session,
        ObservationScope? previous) : IDisposable
    {
        private int _disposed;

        internal CommandTelemetrySession? Session { get; } = session;

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
        }
    }
}
