namespace MediaRescue.Core;

/// <summary>Moving-average throughput over the last 10 s (RF-06). Never returns NaN, infinity or negatives.</summary>
internal sealed class SpeedTracker
{
    public const long WindowMs = 10_000;

    private readonly List<(long Ms, long Bytes)> _samples = new();
    private double _last;

    public void Reset()
    {
        _samples.Clear();
        _last = 0;
    }

    /// <summary>Records the cumulative byte count at a monotonic time and returns the current speed in bytes/second.</summary>
    public double Add(long ms, long bytes)
    {
        if (_samples.Count > 0)
        {
            var newest = _samples[^1];
            if (ms < newest.Ms) ms = newest.Ms;               // time never goes backwards
            if (bytes < newest.Bytes) bytes = newest.Bytes;    // nor do the counters
            // Coalesce samples taken in the same millisecond so the list stays bounded with a frozen clock.
            if (newest.Ms == ms) _samples.RemoveAt(_samples.Count - 1);
        }
        _samples.Add((ms, bytes));
        while (_samples.Count > 1 && ms - _samples[0].Ms > WindowMs) _samples.RemoveAt(0);

        var oldest = _samples[0];
        long dt = ms - oldest.Ms;
        if (dt <= 0) return _last;
        double v = (bytes - oldest.Bytes) * 1000.0 / dt;
        if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) v = 0;
        return _last = v;
    }

    public double Current => _last;
}

internal static class EtaMath
{
    public static readonly TimeSpan MinElapsed = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Null while still calculating (first 10 s), during error recovery, or whenever the speed is zero/unknown.
    /// </summary>
    public static TimeSpan? Eta(TimeSpan elapsed, double bytesPerSecond, long remainingBytes, bool recovering)
    {
        if (recovering) return null;
        if (elapsed < MinElapsed) return null;
        if (remainingBytes <= 0) return TimeSpan.Zero;
        if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond) || bytesPerSecond <= 0) return null;
        double seconds = remainingBytes / bytesPerSecond;
        if (double.IsNaN(seconds) || seconds < 0 || seconds > TimeSpan.FromDays(365).TotalSeconds) return null;
        return TimeSpan.FromSeconds(seconds);
    }
}
