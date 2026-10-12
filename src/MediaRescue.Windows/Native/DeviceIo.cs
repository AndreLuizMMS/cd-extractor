using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MediaRescue.Windows.Native;

/// <summary>Synchronous IOCTL helpers against optical drive handles. Nothing here throws.</summary>
internal static unsafe class DeviceIo
{
    /// <summary>"D", "D:", "D:\", "d:\foo" -> "D:". Null when not a drive letter.</summary>
    public static string? NormalizeLetter(string? letter)
    {
        if (string.IsNullOrWhiteSpace(letter)) return null;
        var c = letter.Trim()[0];
        if (!char.IsAsciiLetter(c)) return null;
        return char.ToUpperInvariant(c) + ":";
    }

    public static string DevicePath(string letter) => @"\\.\" + letter;
    public static string VolumeRoot(string letter) => letter + @"\";

    /// <summary>Opens \\.\X: for IOCTLs. Tries read access first, then no access. Null (with error) when denied/missing.</summary>
    public static SafeFileHandle? OpenDevice(string letter, out int error)
    {
        var path = DevicePath(letter);
        var h = NativeMethods.CreateFile(path, NativeMethods.GENERIC_READ, NativeMethods.FILE_SHARE_ALL, IntPtr.Zero,
            NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
        if (!h.IsInvalid) { error = 0; return h; }
        error = Marshal.GetLastWin32Error();
        h.Dispose();
        if (error != Win32Errors.ERROR_ACCESS_DENIED) return null;
        h = NativeMethods.CreateFile(path, 0, NativeMethods.FILE_SHARE_ALL, IntPtr.Zero,
            NativeMethods.OPEN_EXISTING, 0, IntPtr.Zero);
        if (!h.IsInvalid) { error = 0; return h; }
        h.Dispose();
        return null;
    }

    /// <summary>Returns 0 on success, otherwise the Win32 error.</summary>
    public static int Ioctl(SafeFileHandle h, uint code, byte[]? input, byte[]? output, out int returned)
    {
        returned = 0;
        try
        {
            fixed (byte* pin = input)
            fixed (byte* pout = output)
            {
                bool ok = NativeMethods.DeviceIoControl(h, code, input == null ? null : pin, input?.Length ?? 0,
                    output == null ? null : pout, output?.Length ?? 0, out returned, IntPtr.Zero);
                return ok ? 0 : Marshal.GetLastWin32Error();
            }
        }
        catch (ObjectDisposedException)
        {
            return Win32Errors.ERROR_INVALID_HANDLE;
        }
    }

    /// <summary>
    /// An IOCTL that cannot hang the caller. On a stale handle (the disc was removed and another one took the drive letter)
    /// DeviceIoControl can block forever; after <paramref name="timeoutMs"/> the request is cancelled with CancelIoEx and, if it
    /// still does not return, abandoned (the worker thread stays blocked, the caller moves on). Returns the Win32 error, 0 on success.
    /// </summary>
    public static int IoctlBounded(SafeFileHandle h, uint code, byte[]? input, int timeoutMs = 3000)
    {
        var task = Task.Run(() => Ioctl(h, code, input, null, out _));
        try { if (task.Wait(timeoutMs)) return task.Result; }
        catch (Exception) { return Win32Errors.ERROR_INVALID_HANDLE; }
        try { NativeMethods.CancelIoEx(h, null); } catch (Exception) { /* handle already closed */ }
        try { if (task.Wait(1000)) return task.Result; } catch (Exception) { return Win32Errors.ERROR_INVALID_HANDLE; }
        return Win32Errors.ERROR_SEM_TIMEOUT;
    }

    /// <summary>Closes a handle without risking a hang on a stale device: gives up waiting after <paramref name="timeoutMs"/>.</summary>
    public static void CloseBounded(SafeFileHandle h, int timeoutMs = 2000)
    {
        try { Task.Run(h.Dispose).Wait(timeoutMs); } catch (Exception) { /* nothing more to do */ }
    }

    public static int CheckVerify(SafeFileHandle h) => Ioctl(h, NativeMethods.IOCTL_STORAGE_CHECK_VERIFY, null, null, out _);

    public sealed record StorageDescriptor(string Vendor, string Product, string Revision, string Serial, int BusType);

    public static StorageDescriptor? QueryStorageDescriptor(SafeFileHandle h)
    {
        var query = new byte[12]; // PropertyId=StorageDeviceProperty(0), QueryType=PropertyStandardQuery(0)
        var buf = new byte[1024];
        if (Ioctl(h, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY, query, buf, out int ret) != 0 || ret < 36) return null;
        static string Str(byte[] b, int offset)
        {
            if (offset <= 0 || offset >= b.Length) return "";
            int end = offset;
            while (end < b.Length && b[end] != 0) end++;
            return Encoding.ASCII.GetString(b, offset, end - offset).Trim();
        }
        return new StorageDescriptor(
            Str(buf, BitConverter.ToInt32(buf, 12)),
            Str(buf, BitConverter.ToInt32(buf, 16)),
            Str(buf, BitConverter.ToInt32(buf, 20)),
            Str(buf, BitConverter.ToInt32(buf, 24)),
            BitConverter.ToInt32(buf, 28));
    }

    public static string BusTypeName(int bus) => bus switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE 1394",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "Virtual (arquivo)",
        17 => "NVMe",
        _ => "",
    };

    public sealed record VolumeInfo(string Label, uint Serial, string FileSystem, uint Flags);

    public static VolumeInfo? GetVolumeInfo(string letter, out int error)
    {
        var label = new StringBuilder(261);
        var fs = new StringBuilder(261);
        if (!NativeMethods.GetVolumeInformation(VolumeRoot(letter), label, label.Capacity, out var serial, out _, out var flags, fs, fs.Capacity))
        {
            error = Marshal.GetLastWin32Error();
            return null;
        }
        error = 0;
        return new VolumeInfo(label.ToString(), serial, fs.ToString(), flags);
    }

    public static string FormatSerial(uint serial) => $"{serial >> 16:X4}-{serial & 0xFFFF:X4}";

    public sealed record TocTrack(int Number, bool IsData);

    /// <summary>Tracks from IOCTL_CDROM_READ_TOC; null when the drive/medium does not provide a TOC.</summary>
    public static List<TocTrack>? ReadToc(SafeFileHandle h)
    {
        var buf = new byte[4 + 100 * 8];
        if (Ioctl(h, NativeMethods.IOCTL_CDROM_READ_TOC, null, buf, out int ret) != 0 || ret < 4) return null;
        int length = (buf[0] << 8) | buf[1]; // bytes following the length field
        int entries = Math.Max(0, (length - 2) / 8);
        entries = Math.Min(entries, 100);
        var list = new List<TocTrack>();
        for (int i = 0; i < entries; i++)
        {
            int o = 4 + i * 8;
            if (o + 8 > ret) break;
            int track = buf[o + 2];
            if (track == 0xAA || track == 0) continue; // lead-out
            bool data = (buf[o + 1] & 0x04) != 0;
            list.Add(new TocTrack(track, data));
        }
        return list;
    }

    /// <summary>Number of closed sessions (READ_TOC_EX session format); null when unavailable.</summary>
    public static int? ReadSessionCount(SafeFileHandle h)
    {
        var input = new byte[4];
        input[0] = 1; // CDROM_READ_TOC_EX_FORMAT_SESSION
        var buf = new byte[4 + 8];
        if (Ioctl(h, NativeMethods.IOCTL_CDROM_READ_TOC_EX, input, buf, out int ret) != 0 || ret < 4) return null;
        int first = buf[2], last = buf[3];
        if (first <= 0 || last < first) return null;
        return last - first + 1;
    }

    /// <summary>MMC current profile via IOCTL_CDROM_GET_CONFIGURATION; null when unsupported.</summary>
    public static int? ReadCurrentProfile(SafeFileHandle h)
    {
        var input = new byte[24]; // Feature = FeatureProfileList (0), RequestType = CURRENT (1)
        input[4] = 1;
        var buf = new byte[512];
        if (Ioctl(h, NativeMethods.IOCTL_CDROM_GET_CONFIGURATION, input, buf, out int ret) != 0 || ret < 8) return null;
        return (buf[6] << 8) | buf[7];
    }

    public static string? ProfileName(int profile) => profile switch
    {
        0x08 => "CD-ROM",
        0x09 => "CD-R",
        0x0A => "CD-RW",
        0x10 => "DVD-ROM",
        0x11 => "DVD-R",
        0x12 => "DVD-RAM",
        0x13 => "DVD-RW",
        0x14 => "DVD-RW",
        0x15 => "DVD-R DL",
        0x16 => "DVD-R DL",
        0x1A => "DVD+RW",
        0x1B => "DVD+R",
        0x2A => "DVD+RW DL",
        0x2B => "DVD+R DL",
        0x40 => "BD-ROM",
        0x41 => "BD-R",
        0x42 => "BD-R",
        0x43 => "BD-RE",
        0x50 => "HD DVD-ROM",
        0x51 => "HD DVD-R",
        0x52 => "HD DVD-RAM",
        _ => null,
    };

    public static bool IsDvdProfile(int profile) => profile is >= 0x10 and <= 0x2B;

    public static bool IsRomProfile(int profile) => profile is 0x08 or 0x10 or 0x40 or 0x50;

    /// <summary>True=protected, False=not protected, null=undetectable (treated as not protected by callers).</summary>
    public static bool? ReadDvdCopyrightProtection(SafeFileHandle h)
    {
        var session = new byte[4];
        if (Ioctl(h, NativeMethods.IOCTL_DVD_START_SESSION, null, session, out int sret) != 0 || sret < 4) return null;
        try
        {
            // DVD_READ_STRUCTURE: BlockByteOffset(8) Format(4) SessionId(4) LayerNumber(1) -> padded to 24
            var input = new byte[24];
            BitConverter.GetBytes(1).CopyTo(input, 8);          // DvdCopyrightDescriptor
            Array.Copy(session, 0, input, 12, 4);               // SessionId
            var output = new byte[4 + 4];                        // DVD_DESCRIPTOR_HEADER + DVD_COPYRIGHT_DESCRIPTOR
            if (Ioctl(h, NativeMethods.IOCTL_DVD_READ_STRUCTURE, input, output, out int rret) != 0 || rret < 5) return null;
            return output[4] != 0;
        }
        finally
        {
            Ioctl(h, NativeMethods.IOCTL_DVD_END_SESSION, session, null, out _);
        }
    }

    public static long? ReadLengthInfo(SafeFileHandle h)
    {
        var buf = new byte[8];
        if (Ioctl(h, NativeMethods.IOCTL_DISK_GET_LENGTH_INFO, null, buf, out int ret) != 0 || ret < 8) return null;
        return BitConverter.ToInt64(buf, 0);
    }
}
