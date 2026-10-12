namespace MediaRescue.App.Services;

/// <summary>
/// E-05: counts drive reconnections; three within two minutes mean the USB drive probably lacks power.
/// </summary>
public sealed class ReconnectTracker
{
    private readonly Queue<DateTime> _events = new();
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(2);
    public int Threshold { get; init; } = 3;

    public void Record(DateTime utcNow)
    {
        _events.Enqueue(utcNow);
        Trim(utcNow);
    }

    public bool IsTripped(DateTime utcNow)
    {
        Trim(utcNow);
        return _events.Count >= Threshold;
    }

    private void Trim(DateTime now)
    {
        while (_events.Count > 0 && now - _events.Peek() > Window) _events.Dequeue();
    }
}
