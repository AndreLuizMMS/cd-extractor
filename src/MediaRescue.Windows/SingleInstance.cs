using System.Diagnostics;
using MediaRescue.Windows.Native;

namespace MediaRescue.Windows;

/// <summary>
/// Single-instance guard: a session-local named mutex plus a named event used to ask the running instance to come to
/// the front. Typical use in App startup:
/// <code>
/// var single = new SingleInstance();
/// if (!single.IsFirstInstance) { single.SignalExisting(); Shutdown(); return; }
/// single.ActivationRequested += () => Dispatcher.Invoke(BringMainWindowToFront);
/// </code>
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activate;
    private readonly Thread? _listener;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly string _eventName;
    private int _disposed;

    public bool IsFirstInstance { get; }

    /// <summary>Raised on a background thread when another process asked this instance to show its window.</summary>
    public event Action? ActivationRequested;

    public SingleInstance(string name = "MediaRescue")
    {
        var safe = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-').ToArray());
        _eventName = @"Local\" + safe + ".Activate";
        try
        {
            _mutex = new Mutex(true, @"Local\" + safe + ".SingleInstance", out bool created);
            IsFirstInstance = created;
            if (!created) { _mutex.Dispose(); _mutex = null; }
        }
        catch
        {
            // If the mutex cannot be created, never block the user from running the app.
            IsFirstInstance = true;
        }

        if (IsFirstInstance)
        {
            try
            {
                _activate = new EventWaitHandle(false, EventResetMode.AutoReset, _eventName);
                _listener = new Thread(Listen) { IsBackground = true, Name = "MediaRescue.SingleInstance" };
                _listener.Start();
            }
            catch { _activate = null; }
        }
    }

    private void Listen()
    {
        if (_activate == null) return;
        var handles = new WaitHandle[] { _activate, _stop.WaitHandle };
        while (WaitHandle.WaitAny(handles) == 0)
        {
            try { ActivationRequested?.Invoke(); } catch { }
        }
    }

    /// <summary>
    /// Asks the running instance to show itself (named event) and, as a fallback, restores and foregrounds its main window
    /// directly. Returns true when something was signalled.
    /// </summary>
    public bool SignalExisting()
    {
        bool signalled = false;
        try
        {
            if (EventWaitHandle.TryOpenExisting(_eventName, out var ev))
            {
                using (ev) { ev.Set(); signalled = true; }
            }
        }
        catch { }

        try
        {
            using var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
            {
                using (p)
                {
                    if (p.Id == me.Id) continue;
                    var h = p.MainWindowHandle;
                    if (h == IntPtr.Zero) continue;
                    if (NativeMethods.IsIconic(h)) NativeMethods.ShowWindow(h, 9 /* SW_RESTORE */);
                    NativeMethods.SetForegroundWindow(h);
                    signalled = true;
                }
            }
        }
        catch { }
        return signalled;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _stop.Set(); } catch { }
        try { _listener?.Join(500); } catch { }
        try { _activate?.Dispose(); } catch { }
        try { if (IsFirstInstance) _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
    }
}
