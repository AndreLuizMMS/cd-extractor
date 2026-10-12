using System.Diagnostics;

namespace MediaRescue.Core;

/// <summary>Real clock. The only place in Core that touches wall-clock time, <see cref="Stopwatch"/> and <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;

    public long ElapsedMs => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

    public Task Delay(TimeSpan delay, CancellationToken ct) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, ct);
}
