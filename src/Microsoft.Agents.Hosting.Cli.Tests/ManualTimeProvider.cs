namespace Microsoft.Agents.Hosting.Cli.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private TimeSpan _elapsed;

    public int ActiveTimers => _timers.Count(timer => !timer.Disposed);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _elapsed.Ticks;

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + _elapsed;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, _elapsed + dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan duration)
    {
        var target = _elapsed + duration;

        while (_timers.Where(timer => !timer.Disposed && timer.DueAt <= target).MinBy(timer => timer.DueAt) is { } due)
        {
            _elapsed = due.DueAt;
            due.Fire();
        }

        _elapsed = target > _elapsed ? target : _elapsed;
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueAt, TimeSpan period) : ITimer
    {
        public TimeSpan DueAt { get; private set; } = dueAt;

        public bool Disposed { get; private set; }

        public void Fire()
        {
            DueAt = period > TimeSpan.Zero ? DueAt + period : TimeSpan.MaxValue;
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
