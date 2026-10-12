using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MediaRescue.Windows.Native;

/// <summary>Raw Win32 declarations used by the hardware layer. Internal on purpose.</summary>
internal static unsafe class NativeMethods
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1;
    public const uint FILE_SHARE_WRITE = 2;
    public const uint FILE_SHARE_DELETE = 4;
    public const uint FILE_SHARE_ALL = 7;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    public const int DRIVE_CDROM = 5;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    public static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
    public static extern bool DeviceIoControl(SafeFileHandle handle, uint code, void* inBuffer, int inSize,
        void* outBuffer, int outSize, out int bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "ReadFile")]
    public static extern bool ReadFile(SafeFileHandle handle, void* buffer, uint bytesToRead, IntPtr bytesRead,
        System.Threading.NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "CancelIoEx")]
    public static extern bool CancelIoEx(SafeFileHandle handle, System.Threading.NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetFileSizeEx")]
    public static extern bool GetFileSizeEx(SafeFileHandle handle, out long size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetDriveTypeW")]
    public static extern int GetDriveType(string rootPath);

    [DllImport("kernel32.dll", EntryPoint = "GetLogicalDrives")]
    public static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetVolumeInformationW")]
    public static extern bool GetVolumeInformation(string rootPath, StringBuilder volumeName, int volumeNameSize,
        out uint serial, out uint maxComponent, out uint flags, StringBuilder fsName, int fsNameSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetDiskFreeSpaceExW")]
    public static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeAvailable, out ulong total, out ulong totalFree);

    [DllImport("kernel32.dll", EntryPoint = "SetThreadExecutionState")]
    public static extern uint SetThreadExecutionState(uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    public static extern IntPtr GetModuleHandle(string? name);

    // ---- window / device notification (hidden watcher window) ----

    public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        public char dbcc_name;
    }

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "RegisterClassExW")]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX cls);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "UnregisterClassW")]
    public static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW")]
    public static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow")]
    public static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "PostQuitMessage")]
    public static extern void PostQuitMessage(int code);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll", EntryPoint = "TranslateMessage")]
    public static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "RegisterDeviceNotificationW")]
    public static extern IntPtr RegisterDeviceNotification(IntPtr recipient, void* filter, uint flags);

    [DllImport("user32.dll", EntryPoint = "UnregisterDeviceNotification")]
    public static extern bool UnregisterDeviceNotification(IntPtr handle);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    public static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll", EntryPoint = "IsIconic")]
    public static extern bool IsIconic(IntPtr hwnd);

    public const uint WM_CLOSE = 0x0010;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_DEVICECHANGE = 0x0219;
    public const int DBT_DEVNODES_CHANGED = 0x0007;
    public const int DBT_DEVICEARRIVAL = 0x8000;
    public const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    public const int DBT_DEVTYP_DEVICEINTERFACE = 5;
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TOOLWINDOW = 0x80;

    // ---- IOCTL codes ----

    public static uint CtlCode(uint deviceType, uint function, uint method, uint access) =>
        (deviceType << 16) | (access << 14) | (function << 2) | method;

    private const uint METHOD_BUFFERED = 0;
    private const uint FILE_ANY_ACCESS = 0;
    private const uint FILE_READ_ACCESS = 1;
    private const uint FILE_DEVICE_CD_ROM = 0x02;
    private const uint FILE_DEVICE_DISK = 0x07;
    private const uint FILE_DEVICE_DVD = 0x33;
    private const uint IOCTL_STORAGE_BASE = 0x2D;

    public static readonly uint IOCTL_STORAGE_CHECK_VERIFY = CtlCode(IOCTL_STORAGE_BASE, 0x0200, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static readonly uint IOCTL_STORAGE_MEDIA_REMOVAL = CtlCode(IOCTL_STORAGE_BASE, 0x0201, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static readonly uint IOCTL_STORAGE_EJECT_MEDIA = CtlCode(IOCTL_STORAGE_BASE, 0x0202, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static readonly uint IOCTL_STORAGE_LOAD_MEDIA = CtlCode(IOCTL_STORAGE_BASE, 0x0203, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static readonly uint IOCTL_STORAGE_QUERY_PROPERTY = CtlCode(IOCTL_STORAGE_BASE, 0x0500, METHOD_BUFFERED, FILE_ANY_ACCESS);
    public static readonly uint IOCTL_CDROM_READ_TOC = CtlCode(FILE_DEVICE_CD_ROM, 0x0000, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static readonly uint IOCTL_CDROM_READ_TOC_EX = CtlCode(FILE_DEVICE_CD_ROM, 0x0015, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static readonly uint IOCTL_CDROM_GET_CONFIGURATION = CtlCode(FILE_DEVICE_CD_ROM, 0x0016, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static readonly uint IOCTL_CDROM_SET_SPEED = CtlCode(FILE_DEVICE_CD_ROM, 0x0018, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static readonly uint IOCTL_DVD_START_SESSION = CtlCode(FILE_DEVICE_DVD, 0x0400, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static readonly uint IOCTL_DVD_END_SESSION = CtlCode(FILE_DEVICE_DVD, 0x0403, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static readonly uint IOCTL_DVD_READ_STRUCTURE = CtlCode(FILE_DEVICE_DVD, 0x0450, METHOD_BUFFERED, FILE_READ_ACCESS);
    public static readonly uint IOCTL_DISK_GET_LENGTH_INFO = CtlCode(FILE_DEVICE_DISK, 0x0017, METHOD_BUFFERED, FILE_READ_ACCESS);

    public const uint ES_CONTINUOUS = 0x80000000;
    public const uint ES_SYSTEM_REQUIRED = 0x00000001;
    public const uint ES_DISPLAY_REQUIRED = 0x00000002;
}
