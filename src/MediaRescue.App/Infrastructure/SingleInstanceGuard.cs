namespace MediaRescue.App.Infrastructure;

/// <summary>
/// Single instance via a named mutex; a second launch signals a named event so the first window comes to the front
/// (E-34). Implemented here because the Windows layer's SingleInstance API was not specified.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = @"Local\MediaRescue.SingleInstance";
    private const string EventName = @"Local\MediaRescue.Activate";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstanceGuard(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
        var thread = new Thread(Listen) { IsBackground = true, Name = "SingleInstanceListener" };
        thread.Start();
    }

    public event Action? ActivationRequested;

    /// <summary>Returns null when another instance is already running (it has been asked to show itself).</summary>
    public static SingleInstanceGuard? TryAcquire()
    {
        var mutex = new Mutex(true, MutexName, out var created);
        var evt = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        if (!created)
        {
            try { evt.Set(); } catch (Exception) { /* the other instance is closing */ }
            mutex.Dispose();
            evt.Dispose();
            return null;
        }
        return new SingleInstanceGuard(mutex, evt);
    }

    private void Listen()
    {
        var handles = new WaitHandle[] { _activate, _cts.Token.WaitHandle };
        while (true)
        {
            int i;
            try { i = WaitHandle.WaitAny(handles); } catch (Exception) { return; }
            if (i != 0) return;
            ActivationRequested?.Invoke();
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (Exception) { /* disposing */ }
        try { _mutex.ReleaseMutex(); } catch (Exception) { /* not owned by this thread */ }
        _mutex.Dispose();
        _activate.Dispose();
    }
}
