using System.Text;
using MediaRescue.Core;
using MediaRescue.Windows.Native;

namespace MediaRescue.Windows;

/// <summary>
/// Destination file system on Windows: volume information (FAT32/exFAT/NTFS, removable, network/cloud, optical),
/// long path support and translation of OS errors into <see cref="DestinationException"/>.
/// </summary>
public sealed class WindowsDestinationFileSystem : IDestinationFileSystem
{
    private static readonly string[] CloudLabels =
    {
        "google drive", "drive file stream", "onedrive", "dropbox", "pcloud", "mega", "icloud", "box drive", "sync.com", "nextcloud", "owncloud",
    };

    public WindowsDestinationFileSystem() { }

    // ------------------------------------------------------------------ info

    public DestinationInfo? GetInfo(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var full = Path.GetFullPath(StripLongPrefix(path));
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return null;

            bool unc = root.StartsWith(@"\\", StringComparison.Ordinal);
            string label = "", fs = "";
            long free = 0, total = 0;
            var type = DriveType.Unknown;

            if (!unc)
            {
                var di = new DriveInfo(root);
                type = di.DriveType;
                if (di.IsReady)
                {
                    label = SafeStr(() => di.VolumeLabel);
                    fs = SafeStr(() => di.DriveFormat);
                    free = SafeLong(() => di.AvailableFreeSpace);
                    total = SafeLong(() => di.TotalSize);
                }
                else if (type == DriveType.CDRom)
                {
                    // Optical drive without media: still report that it is optical.
                    return new DestinationInfo(root, "", "", 0, 0, true, false, true);
                }
                else return null;
            }
            else
            {
                type = DriveType.Network;
                if (NativeMethods.GetDiskFreeSpaceEx(root, out ulong avail, out ulong tot, out _)) { free = (long)avail; total = (long)tot; }
                label = root;
            }

            bool removable = type == DriveType.Removable || (!unc && type == DriveType.Fixed && IsUsbAttached(root));
            bool optical = type == DriveType.CDRom;
            bool cloud = unc || type == DriveType.Network || IsCloudLabel(label) || IsUnderCloudFolder(full);
            return new DestinationInfo(root, label, fs.ToUpperInvariant() == "EXFAT" ? "exFAT" : fs.ToUpperInvariant(), free, total, removable, cloud, optical);
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsCloudLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        var l = label.ToLowerInvariant();
        return CloudLabels.Any(l.Contains);
    }

    private static bool IsUnderCloudFolder(string fullPath)
    {
        foreach (var name in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var root = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(root) && fullPath.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>External USB hard disks report DriveType.Fixed; treat them as removable (can be unplugged mid-copy).</summary>
    private static bool IsUsbAttached(string root)
    {
        try
        {
            using var h = DeviceIo.OpenDevice(root.TrimEnd('\\'), out _);
            if (h == null) return false;
            return DeviceIo.QueryStorageDescriptor(h)?.BusType == 7;
        }
        catch { return false; }
    }

    private static string SafeStr(Func<string> f) { try { return f(); } catch { return ""; } }
    private static long SafeLong(Func<long> f) { try { return f(); } catch { return 0; } }

    // ------------------------------------------------------------------ basic operations

    public bool DirectoryExists(string path) { try { return Directory.Exists(Long(path)); } catch { return false; } }
    public bool FileExists(string path) { try { return File.Exists(Long(path)); } catch { return false; } }

    public void CreateDirectory(string path) => Guard(path, () => Directory.CreateDirectory(Long(path)));

    public Stream Create(string path)
    {
        return Guard(path, () =>
        {
            var fs = new FileStream(Long(path), FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16, FileOptions.None);
            return (Stream)new DestinationStream(fs, path, this);
        });
    }

    public Stream OpenRead(string path)
    {
        return Guard(path, () =>
        {
            var fs = new FileStream(Long(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            return (Stream)new DestinationStream(fs, path, this);
        });
    }

    public void FlushToDisk(Stream stream)
    {
        var inner = stream is DestinationStream ds ? ds.Inner : stream as FileStream;
        if (inner == null) { Guard("", () => stream.Flush()); return; }
        Guard(inner.Name, () => inner.Flush(true));
    }

    /// <summary>Renames <paramref name="source"/> to <paramref name="destination"/>. Never overwrites: when the destination
    /// already exists this throws a <see cref="DestinationException"/> and leaves both files untouched, so a user's file can
    /// not be clobbered by a rename. A caller that owns the destination name deletes it explicitly first.</summary>
    public void Move(string source, string destination) =>
        Guard(destination, () => File.Move(Long(source), Long(destination), overwrite: false));

    public void Delete(string path) => Guard(path, () =>
    {
        var p = Long(path);
        if (File.Exists(p)) File.Delete(p);
    });

    /// <summary>Deletes the directory only when it is empty (non-recursive: <c>Directory.Delete(path, false)</c> refuses otherwise).
    /// Any failure (in use, not empty, no permission, already gone) is ignored: leaving an empty folder is harmless.</summary>
    public void DeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var full = Path.GetFullPath(path);
            if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase)) return; // never a drive root
            var p = Long(path);
            if (!Directory.Exists(p)) return;
            using (var e = Directory.EnumerateFileSystemEntries(p).GetEnumerator())
                if (e.MoveNext()) return; // not empty
            Directory.Delete(p, false);
        }
        catch (Exception) { /* best effort */ }
    }

    public long GetFileSize(string path) => Guard(path, () => new FileInfo(Long(path)).Length);

    public void SetTimes(string path, DateTime modifiedUtc) => Guard(path, () =>
    {
        var p = Long(path);
        File.SetLastWriteTimeUtc(p, modifiedUtc);
        File.SetCreationTimeUtc(p, modifiedUtc);
    });

    public bool ProbeWritable(string directory, out string? error)
    {
        error = null;
        string? probe = null;
        try
        {
            if (!Directory.Exists(Long(directory)))
            {
                error = "A pasta de destino não existe ou o dispositivo não está conectado.";
                return false;
            }
            probe = Path.Combine(directory, ".mediarescue-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var fs = new FileStream(Long(probe), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(new byte[] { 0x4D, 0x52 }, 0, 2);
                fs.Flush(true);
            }
            File.Delete(Long(probe));
            return true;
        }
        catch (Exception ex)
        {
            error = FriendlyMessage(Translate(ex, directory));
            try { if (probe != null && File.Exists(Long(probe))) File.Delete(Long(probe)); } catch { }
            return false;
        }
    }

    public string? ReadAllText(string path)
    {
        try
        {
            var p = Long(path);
            return File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Translate(ex, path);
        }
    }

    /// <summary>Writes to a temp file, flushes it, then File.Replace keeping the previous version as .bak.</summary>
    public void WriteAllTextAtomic(string path, string content) => Guard(path, () =>
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(Long(dir));
        var tmp = path + ".tmp";
        using (var fs = new FileStream(Long(tmp), FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            var bytes = new UTF8Encoding(false).GetBytes(content);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);
        }
        var target = Long(path);
        if (File.Exists(target)) File.Replace(Long(tmp), target, Long(path + ".bak"), ignoreMetadataErrors: true);
        else File.Move(Long(tmp), target);
    });

    // ------------------------------------------------------------------ paths / errors

    internal static string StripLongPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path.Substring(8);
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path.Substring(4);
        return path;
    }

    /// <summary>Adds the \\?\ prefix for long paths so Win32 limits (MAX_PATH) never apply.</summary>
    internal static string Long(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        var full = Path.GetFullPath(path);
        if (full.Length < 240) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full.Substring(2) : @"\\?\" + full;
    }

    private void Guard(string path, Action action)
    {
        try { action(); }
        catch (DestinationException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw Translate(ex, path);
        }
    }

    private T Guard<T>(string path, Func<T> func)
    {
        try { return func(); }
        catch (DestinationException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw Translate(ex, path);
        }
    }

    internal static DestinationException Translate(Exception ex, string path)
    {
        if (ex is DestinationException de) return de;
        int code = Win32Errors.FromException(ex);
        var kind = ClassifyDestination(code, path);
        return new DestinationException(kind, FriendlyMessage(kind, code, path), ex);
    }

    private static string FriendlyMessage(DestinationException ex) => ex.Message;

    internal static DestinationErrorKind ClassifyDestination(int code, string path)
    {
        switch (code)
        {
            case Win32Errors.ERROR_DISK_FULL:
            case Win32Errors.ERROR_HANDLE_DISK_FULL:
                return DestinationErrorKind.DiskFull;
            case Win32Errors.ERROR_ACCESS_DENIED:
            case Win32Errors.ERROR_WRITE_PROTECT:
                return DestinationErrorKind.AccessDenied;
            case Win32Errors.ERROR_NOT_READY:
            case Win32Errors.ERROR_DEVICE_NOT_CONNECTED:
            case 15:   // ERROR_INVALID_DRIVE
            case 53:   // ERROR_BAD_NETPATH
            case 55:   // ERROR_DEV_NOT_EXIST
            case 64:   // ERROR_NETNAME_DELETED
            case 67:   // ERROR_BAD_NET_NAME
            case 433:  // ERROR_NO_SUCH_DEVICE
            case Win32Errors.ERROR_UNRECOGNIZED_VOLUME:
            case Win32Errors.ERROR_IO_DEVICE:
            case 1231: // ERROR_NETWORK_UNREACHABLE
                return DestinationErrorKind.DeviceGone;
            case Win32Errors.ERROR_FILE_NOT_FOUND:
            case Win32Errors.ERROR_PATH_NOT_FOUND:
                return VolumeGone(path) ? DestinationErrorKind.DeviceGone : DestinationErrorKind.Other;
            default:
                return DestinationErrorKind.Other;
        }
    }

    private static bool VolumeGone(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return false;
            var root = Path.GetPathRoot(Path.GetFullPath(StripLongPrefix(path)));
            return !string.IsNullOrEmpty(root) && !Directory.Exists(root);
        }
        catch { return false; }
    }

    private static string FriendlyMessage(DestinationErrorKind kind, int code, string path) => kind switch
    {
        DestinationErrorKind.DiskFull => "O destino ficou sem espaço livre.",
        DestinationErrorKind.DeviceGone => "O destino não está mais disponível (pendrive ou disco removido, ou rede desconectada).",
        DestinationErrorKind.AccessDenied => code == Win32Errors.ERROR_WRITE_PROTECT
            ? "O destino está protegido contra gravação."
            : "Sem permissão para gravar neste destino.",
        _ => code != 0 ? $"Erro ao gravar no destino. Código {code}." : "Erro ao gravar no destino.",
    };

    /// <summary>FileStream wrapper that converts OS failures of every I/O call into <see cref="DestinationException"/>.</summary>
    internal sealed class DestinationStream : Stream
    {
        private readonly string _path;
        public FileStream Inner { get; }

        public DestinationStream(FileStream inner, string path, WindowsDestinationFileSystem owner) { Inner = inner; _path = path; _ = owner; }

        private T Run<T>(Func<T> f)
        {
            try { return f(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Translate(ex, _path); }
        }

        private async ValueTask<T> RunAsync<T>(Func<ValueTask<T>> f)
        {
            try { return await f().ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Translate(ex, _path); }
        }

        public override bool CanRead => Inner.CanRead;
        public override bool CanSeek => Inner.CanSeek;
        public override bool CanWrite => Inner.CanWrite;
        public override long Length => Run(() => Inner.Length);
        public override long Position { get => Inner.Position; set => Run(() => { Inner.Position = value; return 0; }); }
        public override void Flush() => Run(() => { Inner.Flush(); return 0; });
        public override int Read(byte[] buffer, int offset, int count) => Run(() => Inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) { try { return Inner.Read(buffer); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Translate(ex, _path); } }
        public override long Seek(long offset, SeekOrigin origin) => Run(() => Inner.Seek(offset, origin));
        public override void SetLength(long value) => Run(() => { Inner.SetLength(value); return 0; });
        public override void Write(byte[] buffer, int offset, int count) => Run(() => { Inner.Write(buffer, offset, count); return 0; });
        public override void Write(ReadOnlySpan<byte> buffer) { try { Inner.Write(buffer); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Translate(ex, _path); } }
        public override Task FlushAsync(CancellationToken ct) => RunAsync(async () => { await Inner.FlushAsync(ct).ConfigureAwait(false); return 0; }).AsTask();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => RunAsync(() => Inner.ReadAsync(buffer, ct));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => RunAsync(() => Inner.ReadAsync(buffer.AsMemory(offset, count), ct)).AsTask();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            new(RunAsync(async () => { await Inner.WriteAsync(buffer, ct).ConfigureAwait(false); return 0; }).AsTask());
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            RunAsync(async () => { await Inner.WriteAsync(buffer.AsMemory(offset, count), ct).ConfigureAwait(false); return 0; }).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { Inner.Dispose(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Translate(ex, _path); }
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            try { await Inner.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw Translate(ex, _path); }
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
