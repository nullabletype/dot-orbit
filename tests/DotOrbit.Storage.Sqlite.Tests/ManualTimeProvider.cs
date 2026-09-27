namespace DotOrbit.Storage.Sqlite.Tests;

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = start;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    public void Advance(TimeSpan elapsed)
    {
        _utcNow += elapsed;
        while (true)
        {
            var due = _timers
                .Where(timer => timer.IsDue(_utcNow))
                .OrderBy(timer => timer.DueAtUtc)
                .FirstOrDefault();
            if (due is null)
            {
                return;
            }

            due.Fire(_utcNow);
        }
    }

    public void SetUtcNow(DateTimeOffset value) => _utcNow = value;

    private sealed class ManualTimer : ITimer
    {
        private readonly TimerCallback _callback;
        private readonly ManualTimeProvider _owner;
        private readonly object? _state;
        private bool _disposed;
        private TimeSpan _period;

        public ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            Change(dueTime, period);
        }

        public DateTimeOffset DueAtUtc { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
            {
                return false;
            }

            _period = period;
            DueAtUtc = dueTime == Timeout.InfiniteTimeSpan
                ? DateTimeOffset.MaxValue
                : _owner.GetUtcNow() + dueTime;
            return true;
        }

        public void Dispose() => _disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public bool IsDue(DateTimeOffset now) => !_disposed && DueAtUtc <= now;

        public void Fire(DateTimeOffset now)
        {
            if (_period == Timeout.InfiniteTimeSpan)
            {
                _disposed = true;
            }
            else
            {
                DueAtUtc = now + _period;
            }

            _callback(_state);
        }
    }
}
