using MediaRescue.Core;

namespace MediaRescue.Core.Tests.Fakes;

/// <summary>
/// Virtual clock. By default every <see cref="Delay"/> completes immediately and advances virtual time, so tests never
/// sleep. With <see cref="AutoAdvance"/> off, delays stay pending until <see cref="Advance"/> is called.
/// </summary>
public sealed class FakeClock : IClock
{
    private readonly object _gate = new();
    private long _ms = 1_000;
    private readonly List<(long Due, TaskCompletionSource Tcs)> _pending = new();

    public DateTime StartUtc { get; } = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
    public bool AutoAdvance { get; set; } = true;
    /// <summary>Called (before time advances) for every delay request: use it to script "events happen while waiting".</summary>
    public Action<TimeSpan>? OnDelay { get; set; }
    public List<TimeSpan> Delays { get; } = new();
    public int DelayCount => Delays.Count;

    public DateTime UtcNow { get { lock (_gate) return StartUtc.AddMilliseconds(_ms - 1_000); } }
    public long ElapsedMs { get { lock (_gate) return _ms; } }
    public TimeSpan Elapsed => TimeSpan.FromMilliseconds(ElapsedMs - 1_000);

    public Task Delay(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Delays.Add(delay);
            if (Delays.Count > 400_000) throw new InvalidOperationException("Runaway wait loop in test (more than 400k delays).");
        }
        OnDelay?.Invoke(delay);
        ct.ThrowIfCancellationRequested();
        if (AutoAdvance)
        {
            lock (_gate) _ms += (long)delay.TotalMilliseconds;
            return Task.CompletedTask;
        }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _pending.Add((_ms + (long)delay.TotalMilliseconds, tcs));
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    /// <summary>Moves time forward and completes pending delays that became due.</summary>
    public void Advance(TimeSpan by)
    {
        List<TaskCompletionSource> due;
        lock (_gate)
        {
            _ms += (long)by.TotalMilliseconds;
            due = _pending.Where(p => p.Due <= _ms).Select(p => p.Tcs).ToList();
            _pending.RemoveAll(p => p.Due <= _ms);
        }
        foreach (var t in due) t.TrySetResult();
    }
}
