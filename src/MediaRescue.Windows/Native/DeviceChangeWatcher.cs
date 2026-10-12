using System.Runtime.InteropServices;
using System.Threading;

namespace MediaRescue.Windows.Native;

/// <summary>
/// Hidden, never-shown window on its own STA thread that receives WM_DEVICECHANGE. A top-level (not message-only) window
/// is required because the system broadcasts volume arrival/removal only to top-level windows; additionally the window
/// registers for CD-ROM and volume interface notifications. Raises <see cref="Notified"/> on the watcher thread.
/// </summary>
internal sealed unsafe class DeviceChangeWatcher : IDisposable
{
    private static readonly Guid GuidDevInterfaceCdrom = new("53f56308-b6bf-11d0-94f2-00a0c91efb8b");
    private static readonly Guid GuidDevInterfaceVolume = new("53f5630d-b6bf-11d0-94f2-00a0c91efb8b");

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly NativeMethods.WndProc _wndProc;
    private readonly string _className = "MediaRescueDeviceWatcher_" + Guid.NewGuid().ToString("N");
    private IntPtr _hwnd;
    private int _disposed;

    public event Action? Notified;

    /// <summary>True when the hidden window was created (false means only polling is available).</summary>
    public bool IsRunning => _hwnd != IntPtr.Zero;

    public DeviceChangeWatcher()
    {
        _wndProc = WindowProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "MediaRescue.DeviceWatcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        var notifyHandles = new List<IntPtr>();
        IntPtr namePtr = IntPtr.Zero;
        bool classRegistered = false;
        var hInstance = NativeMethods.GetModuleHandle(null);
        try
        {
            namePtr = Marshal.StringToHGlobalUni(_className);
            var cls = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)sizeof(NativeMethods.WNDCLASSEX),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = namePtr,
            };
            if (NativeMethods.RegisterClassEx(ref cls) == 0) return;
            classRegistered = true;

            _hwnd = NativeMethods.CreateWindowEx(NativeMethods.WS_EX_TOOLWINDOW, _className, "MediaRescue device watcher",
                NativeMethods.WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) return;

            foreach (var guid in new[] { GuidDevInterfaceCdrom, GuidDevInterfaceVolume })
            {
                var filter = new NativeMethods.DEV_BROADCAST_DEVICEINTERFACE
                {
                    dbcc_size = sizeof(NativeMethods.DEV_BROADCAST_DEVICEINTERFACE),
                    dbcc_devicetype = NativeMethods.DBT_DEVTYP_DEVICEINTERFACE,
                    dbcc_classguid = guid,
                };
                var n = NativeMethods.RegisterDeviceNotification(_hwnd, &filter, 0);
                if (n != IntPtr.Zero) notifyHandles.Add(n);
            }
        }
        catch
        {
            _hwnd = IntPtr.Zero;
        }
        finally
        {
            _ready.Set();
        }

        if (_hwnd != IntPtr.Zero)
        {
            try
            {
                while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    NativeMethods.TranslateMessage(ref msg);
                    NativeMethods.DispatchMessage(ref msg);
                }
            }
            catch { /* thread must never crash the process */ }
        }

        foreach (var n in notifyHandles) { try { NativeMethods.UnregisterDeviceNotification(n); } catch { } }
        if (classRegistered) { try { NativeMethods.UnregisterClass(_className, hInstance); } catch { } }
        if (namePtr != IntPtr.Zero) Marshal.FreeHGlobal(namePtr);
        _hwnd = IntPtr.Zero;
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case NativeMethods.WM_DEVICECHANGE:
                    int code = (int)(long)wParam;
                    if (code is NativeMethods.DBT_DEVICEARRIVAL or NativeMethods.DBT_DEVICEREMOVECOMPLETE or NativeMethods.DBT_DEVNODES_CHANGED)
                    {
                        try { Notified?.Invoke(); } catch { }
                    }
                    return (IntPtr)1; // TRUE: grant the request
                case NativeMethods.WM_CLOSE:
                    NativeMethods.DestroyWindow(hwnd);
                    return IntPtr.Zero;
                case NativeMethods.WM_DESTROY:
                    NativeMethods.PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch { }
        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var h = _hwnd;
        if (h != IntPtr.Zero) NativeMethods.PostMessage(h, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }
}
