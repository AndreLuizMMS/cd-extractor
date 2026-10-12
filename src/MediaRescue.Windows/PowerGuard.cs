using MediaRescue.Windows.Native;

namespace MediaRescue.Windows;

/// <summary>
/// Prevents the system from sleeping while alive (SetThreadExecutionState ES_CONTINUOUS | ES_SYSTEM_REQUIRED).
/// The execution state belongs to a thread, so it is held by a dedicated background thread: the guard can be created
/// and disposed from any thread (UI, worker, finalizer) and is always released on Dispose.
/// </summary>
public sealed class PowerGuard : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _release = new(false);
    private readonly ManualResetEventSlim _started = new(false);
    private int _disposed;

    /// <summary>True when the OS accepted the request.</summary>
    public bool IsActive { get; private set; }

    public PowerGuard(bool keepDisplayOn = false)
    {
        _thread = new Thread(() => Hold(keepDisplayOn)) { IsBackground = true, Name = "MediaRescue.PowerGuard" };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(2));
    }

    private void Hold(bool keepDisplayOn)
    {
        try
        {
            uint flags = NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED | (keepDisplayOn ? NativeMethods.ES_DISPLAY_REQUIRED : 0);
            IsActive = NativeMethods.SetThreadExecutionState(flags) != 0;
        }
        catch { IsActive = false; }
        finally { _started.Set(); }

        try { _release.Wait(); } catch { }
        try { NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _release.Set(); } catch { }
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
        IsActive = false;
    }
}
