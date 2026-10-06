// tests/Ecr.Application.Tests/Integration/ManualTimeProvider.cs
namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Керований годинник для меж очікування: таймер спрацьовує лише в <see cref="Advance"/> — синхронно, у потоці
/// тесту, без пулу потоків і без справжнього часу.
/// </summary>
/// <remarks>
/// ⚠ Навіщо: межа на справжньому таймері (200 мс) під навантаженням CI спрацьовувала із запізненням у секунди, і
/// тести «джерело мовчить → 503» падали за власним запобіжником <c>WaitAsync</c> (прогін 37296823643), хоча
/// обробник поводився правильно. Підтримано лише одноразові таймери — саме такі створює
/// <see cref="CancellationTokenSource"/>.
/// </remarks>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _pending = [];
    private DateTimeOffset _now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Скільки таймерів чекають свого часу.</summary>
    public int PendingTimers
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);

        return timer;
    }

    /// <summary>Зсуває годинник і синхронно запускає таймери, чий час настав.</summary>
    public void Advance(TimeSpan by)
    {
        ManualTimer[] due;
        lock (_gate)
        {
            _now += by;
            due = [.. _pending.Where(t => t.DueAt <= _now)];
            _pending.RemoveAll(t => t.DueAt <= _now);
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan && period != TimeSpan.Zero)
        {
            throw new NotSupportedException("Керований годинник підтримує лише одноразові таймери.");
        }

        lock (_gate)
        {
            _pending.Remove(timer);
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                timer.DueAt = _now + dueTime;
                _pending.Add(timer);
            }
        }
    }

    private void Cancel(ManualTimer timer)
    {
        lock (_gate)
        {
            _pending.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Schedule(this, dueTime, period);

            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner.Cancel(this);

        public ValueTask DisposeAsync()
        {
            Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
