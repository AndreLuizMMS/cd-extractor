using System.Collections.Concurrent;
using System.Diagnostics;
using MediaRescue.Core;
using MediaRescue.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace MediaRescue.Windows;

/// <summary>
/// Windows implementation of <see cref="IOpticalDriveService"/>: drive/media detection (IOCTL + IMAPI2 + WMI fallback),
/// device-change notifications (hidden window + 2 s polling) and disc analysis. No administrator rights required;
/// every query degrades to "unavailable" instead of throwing.
/// </summary>
public sealed class OpticalDriveService : IOpticalDriveService
{
    private sealed record MediaClass(DriveState State, string Label, uint Serial);

    private static readonly TimeSpan DriveInfoTtl = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SpinUpGrace = TimeSpan.FromSeconds(15);

    private readonly DeviceChangeWatcher? _watcher;
    private readonly Timer _pollTimer;
    private readonly Timer _debounceTimer;
    private readonly ConcurrentDictionary<string, MediaClass> _mediaCache = new();
    private readonly ConcurrentDictionary<string, (DateTime At, OpticalDrive Info)> _driveCache = new();
    private readonly ConcurrentDictionary<string, Task<DriveState>> _inflight = new();
    private readonly ConcurrentDictionary<string, long> _inflightStart = new();
    private const int StateBudgetMs = 4000;
    private readonly ConcurrentDictionary<string, DateTime> _presentSince = new();
    private readonly ConcurrentDictionary<string, DateTime> _notReadySince = new();
    private readonly ConcurrentDictionary<string, bool> _trayOpen = new();
    private readonly ConcurrentDictionary<string, (DateTime At, ImapiMediaInfo? Info)> _imapiCache = new();
    private readonly Dictionary<string, SafeFileHandle> _lockHandles = new();
    private readonly object _lockGate = new();
    private string? _lastSnapshot;
    private int _pollRunning;
    private volatile bool _disposed;

    public event EventHandler? Changed;

    public OpticalDriveService() : this(PollInterval) { }

    /// <summary>Test hook: a null interval disables the polling fallback so only WM_DEVICECHANGE can raise Changed.</summary>
    internal OpticalDriveService(TimeSpan? pollInterval)
    {
        try { _watcher = new DeviceChangeWatcher(); _watcher.Notified += OnRawDeviceChange; }
        catch { _watcher = null; }
        _debounceTimer = new Timer(_ => FireChanged(), null, Timeout.Infinite, Timeout.Infinite);
        // Baseline snapshot off the caller's thread: a drive stuck on a bad disc must not delay construction.
        _ = Task.Run(() => { if (_lastSnapshot == null) _lastSnapshot = SafeSnapshot(); });
        _pollTimer = new Timer(_ => Poll(), null, pollInterval ?? Timeout.InfiniteTimeSpan, pollInterval ?? Timeout.InfiniteTimeSpan);
    }

    // ------------------------------------------------------------------ drives

    public IReadOnlyList<OpticalDrive> GetDrives()
    {
        try
        {
            var result = new List<OpticalDrive>();
            foreach (var letter in EnumerateCdromLetters())
            {
                try { result.Add(GetDriveInfoCached(letter)); }
                catch { result.Add(new OpticalDrive(letter, "", "", "", "", false)); }
            }
            return result;
        }
        catch
        {
            return Array.Empty<OpticalDrive>();
        }
    }

    private static List<string> EnumerateCdromLetters()
    {
        var list = new List<string>();
        uint mask = NativeMethods.GetLogicalDrives();
        for (int i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) == 0) continue;
            var letter = ((char)('A' + i)) + ":";
            if (NativeMethods.GetDriveType(letter + @"\") == NativeMethods.DRIVE_CDROM) list.Add(letter);
        }
        return list;
    }

    private OpticalDrive GetDriveInfoCached(string letter)
    {
        if (_driveCache.TryGetValue(letter, out var c) && DateTime.UtcNow - c.At < DriveInfoTtl) return c.Info;
        var info = BuildDriveInfo(letter);
        _driveCache[letter] = (DateTime.UtcNow, info);
        return info;
    }

    private static OpticalDrive BuildDriveInfo(string letter)
    {
        string vendor = "", model = "", firmware = "", bus = "";
        int busType = 0;
        using (var h = DeviceIo.OpenDevice(letter, out _))
        {
            if (h != null)
            {
                var d = DeviceIo.QueryStorageDescriptor(h);
                if (d != null)
                {
                    vendor = d.Vendor; model = d.Product; firmware = d.Revision;
                    busType = d.BusType;
                    bus = DeviceIo.BusTypeName(d.BusType);
                }
            }
        }

        string caption = "";
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrEmpty(bus))
        {
            // IOCTL unavailable (E-33) or incomplete: use WMI Win32_CDROMDrive as inventory.
            var w = WmiInventory.Query(TimeSpan.FromSeconds(6)).FirstOrDefault(x => x.Letter.Equals(letter, StringComparison.OrdinalIgnoreCase));
            if (w != null)
            {
                caption = w.Caption;
                if (string.IsNullOrWhiteSpace(model)) model = w.Caption;
                if (string.IsNullOrWhiteSpace(vendor) && !w.Manufacturer.StartsWith("(", StringComparison.Ordinal)) vendor = w.Manufacturer;
                if (string.IsNullOrWhiteSpace(firmware)) firmware = w.Revision;
                if (string.IsNullOrEmpty(bus)) bus = w.Bus;
            }
        }

        bool virt = IsVirtualDrive(busType, vendor, model, caption);
        return new OpticalDrive(letter, vendor, model, firmware, bus, virt);
    }

    internal static bool IsVirtualDrive(int busType, string vendor, string model, string caption)
    {
        if (busType is 14 or 15) return true;
        var text = $"{vendor} {model} {caption}".ToLowerInvariant();
        string[] markers = { "virtual", "msft", "daemon", "elby", "clonedrive", "vmware", "vbox", "qemu", "gsi ", "alcohol", "passmark" };
        return markers.Any(text.Contains);
    }

    // ------------------------------------------------------------------ state

    public DriveState GetState(string letter)
    {
        try
        {
            var l = DeviceIo.NormalizeLetter(letter);
            if (l == null || _disposed) return DriveState.NoMedia;

            Task<DriveState> task;
            long startedAt;
            lock (_inflight)
            {
                if (!_inflight.TryGetValue(l, out task!) || task.IsCompleted)
                {
                    task = Task.Run(() => ComputeStateSafe(l));
                    _inflight[l] = task;
                    _inflightStart[l] = Environment.TickCount64;
                }
                startedAt = _inflightStart.GetValueOrDefault(l, Environment.TickCount64);
            }
            // A drive stuck on a bad disc must not freeze the caller: after the budget report "Loading" and keep the worker running.
            long remaining = StateBudgetMs - (Environment.TickCount64 - startedAt);
            return task.Wait(TimeSpan.FromMilliseconds(Math.Max(100, remaining))) ? task.Result : DriveState.Loading;
        }
        catch
        {
            return DriveState.NoMedia;
        }
    }

    private DriveState ComputeStateSafe(string l)
    {
        try { return ComputeState(l); }
        catch { return DriveState.Unreadable; }
    }

    private DriveState ComputeState(string l)
    {
        if (NativeMethods.GetDriveType(l + @"\") != NativeMethods.DRIVE_CDROM) { ClearMedia(l); return DriveState.NoMedia; }

        using var h = DeviceIo.OpenDevice(l, out int openError);
        if (h == null && openError is Win32Errors.ERROR_FILE_NOT_FOUND or Win32Errors.ERROR_PATH_NOT_FOUND)
        {
            ClearMedia(l);
            return DriveState.NoMedia;
        }

        int cv;
        DeviceIo.VolumeInfo? vol = null;
        if (h != null)
        {
            cv = DeviceIo.CheckVerify(h);
            if (cv == Win32Errors.ERROR_MEDIA_CHANGED) { _mediaCache.TryRemove(l, out _); cv = DeviceIo.CheckVerify(h); }
            if (cv != 0 && cv is not (Win32Errors.ERROR_NOT_READY or Win32Errors.ERROR_NO_MEDIA_IN_DRIVE or Win32Errors.ERROR_MEDIA_CHANGED
                or Win32Errors.ERROR_DEVICE_NOT_CONNECTED or 1785 or 1129 or 1005))
                cv = 0; // IOCTL unsupported by this driver: decide from the volume instead
        }
        else
        {
            // E-33: device cannot be opened (access denied) -> decide from the file system only.
            vol = DeviceIo.GetVolumeInfo(l, out int ve);
            cv = vol != null ? 0 : (ve == 0 ? Win32Errors.ERROR_NO_MEDIA_IN_DRIVE : ve);
        }

        if (cv != 0) return StateWithoutMedia(l, cv, h != null ? DeviceIo.ReadCurrentProfile(h) : null);

        _notReadySince.TryRemove(l, out _);
        _trayOpen.TryRemove(l, out _);
        var since = _presentSince.GetOrAdd(l, _ => DateTime.UtcNow);

        vol ??= DeviceIo.GetVolumeInfo(l, out _);
        if (vol != null)
        {
            if (_mediaCache.TryGetValue(l, out var cached) && cached.Label == vol.Label && cached.Serial == vol.Serial)
                return cached.State;

            bool prot = h != null && DeviceIo.ReadDvdCopyrightProtection(h) == true;
            var state = prot ? DriveState.Protected : DriveState.Ready;
            _mediaCache[l] = new MediaClass(state, vol.Label, vol.Serial);
            return state;
        }

        // Media present but no readable file system.
        _mediaCache.TryRemove(l, out _);
        if (h != null)
        {
            var toc = DeviceIo.ReadToc(h);
            if (toc is { Count: > 0 } && toc.All(t => !t.IsData)) return DriveState.AudioCd;
        }
        var im = GetImapi(l);
        if (im != null)
        {
            if (im.IsBlank) return DriveState.Blank;
            if (im.IsAppendable) return DriveState.Unfinalized;
        }
        return DateTime.UtcNow - since < SpinUpGrace ? DriveState.Loading : DriveState.Unreadable;
    }

    private DriveState StateWithoutMedia(string l, int error, int? profile)
    {
        _mediaCache.TryRemove(l, out _);
        _presentSince.TryRemove(l, out _);
        switch (error)
        {
            case Win32Errors.ERROR_NO_MEDIA_IN_DRIVE:
            case Win32Errors.ERROR_DEVICE_NOT_CONNECTED:
                _notReadySince.TryRemove(l, out _);
                return _trayOpen.ContainsKey(l) ? DriveState.TrayOpen : DriveState.NoMedia;
            default:
                // ERROR_NOT_READY & friends: spinning up, loading, or blank media that the driver reports as not ready.
                var im = GetImapi(l);
                if (im != null)
                {
                    if (im.IsBlank) return DriveState.Blank;
                    if (im.IsAppendable) return DriveState.Unfinalized;
                }
                // Many USB drives answer NOT_READY (21) instead of NO_MEDIA (1112) when the tray is empty. MMC "current profile"
                // 0 means no medium has been identified, so that case is NoMedia/TrayOpen; a non-zero profile means a medium
                // is being spun up (Loading) and only becomes Unreadable after a long time.
                if (profile == 0)
                {
                    _notReadySince.TryRemove(l, out _);
                    return _trayOpen.ContainsKey(l) ? DriveState.TrayOpen : DriveState.NoMedia;
                }
                var since = _notReadySince.GetOrAdd(l, _ => DateTime.UtcNow);
                if (DateTime.UtcNow - since > TimeSpan.FromSeconds(90)) return DriveState.Unreadable;
                return _trayOpen.ContainsKey(l) ? DriveState.TrayOpen : DriveState.Loading;
        }
    }

    private void ClearMedia(string l)
    {
        _mediaCache.TryRemove(l, out _);
        _presentSince.TryRemove(l, out _);
        _notReadySince.TryRemove(l, out _);
    }

    private ImapiMediaInfo? GetImapi(string l)
    {
        if (_imapiCache.TryGetValue(l, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(4)) return c.Info;
        var info = Imapi2Info.Query(l, TimeSpan.FromSeconds(8));
        _imapiCache[l] = (DateTime.UtcNow, info);
        return info;
    }

    // ------------------------------------------------------------------ analysis

    public async Task<DiscInfo?> AnalyzeAsync(string letter, CancellationToken ct)
    {
        var l = DeviceIo.NormalizeLetter(letter);
        if (l == null || _disposed) return null;
        try
        {
            return await Task.Run(() => AnalyzeCore(l, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static bool Wait(CancellationToken ct, int ms)
    {
        return ct.WaitHandle.WaitOne(ms);
    }

    /// <summary>RF-01: waits until two consecutive reads of the volume descriptor succeed. Returns the stable state.</summary>
    private DriveState WaitForStableMedia(string l, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int consecutive = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var st = GetState(l);
            switch (st)
            {
                case DriveState.NoMedia:
                case DriveState.TrayOpen:
                case DriveState.Blank:
                case DriveState.Unfinalized:
                case DriveState.AudioCd:
                    return st;
                case DriveState.Ready:
                case DriveState.Protected:
                    if (ProbeVolumeDescriptor(l, ct)) { if (++consecutive >= 2) return st; }
                    else consecutive = 0;
                    if (sw.Elapsed > TimeSpan.FromSeconds(60)) return DriveState.Unreadable;
                    break;
                case DriveState.Loading:
                    consecutive = 0;
                    if (sw.Elapsed > TimeSpan.FromSeconds(60)) return DriveState.Unreadable;
                    break;
                default: // Unreadable
                    consecutive = 0;
                    if (sw.Elapsed > TimeSpan.FromSeconds(20)) return DriveState.Unreadable;
                    break;
            }
            if (Wait(ct, 400)) ct.ThrowIfCancellationRequested();
        }
    }

    private static bool ProbeVolumeDescriptor(string l, CancellationToken ct)
    {
        if (DeviceIo.GetVolumeInfo(l, out _) == null) return false;
        using var raw = OverlappedHandle.Open(DeviceIo.DevicePath(l), NativeMethods.GENERIC_READ, NativeMethods.FILE_SHARE_ALL, NativeMethods.FILE_FLAG_NO_BUFFERING, out _);
        if (raw == null) return true; // E-33: no raw access, volume info alone is the best evidence
        try
        {
            var buf = new byte[Iso9660.SectorSize];
            int n = raw.ReadAsync((long)Iso9660.PrimaryDescriptorSector * Iso9660.SectorSize, Iso9660.SectorSize, 0, buf, TimeSpan.FromSeconds(15), ct).GetAwaiter().GetResult();
            return n == Iso9660.SectorSize;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private DiscInfo? AnalyzeCore(string l, CancellationToken ct)
    {
        var stable = WaitForStableMedia(l, ct);
        if (stable is DriveState.NoMedia or DriveState.TrayOpen or DriveState.Blank or DriveState.Unfinalized or DriveState.Unreadable or DriveState.Loading)
            return null;

        using var dev = DeviceIo.OpenDevice(l, out _);
        var tracks = dev != null ? DeviceIo.ReadToc(dev) : null;
        int? sessions = dev != null ? DeviceIo.ReadSessionCount(dev) : null;
        int? profile = dev != null ? DeviceIo.ReadCurrentProfile(dev) : null;
        var imapi = GetImapi(l);

        string? physical = imapi?.PhysicalType ?? (profile is { } p ? DeviceIo.ProfileName(p) : null);
        string? mediaState = null;
        if (imapi != null)
        {
            if (imapi.IsBlank) mediaState = "Vazio";
            else if (imapi.IsAppendable) mediaState = "Aberto";
            else if (imapi.Status != null) mediaState = "Finalizado";
        }
        if (mediaState == null && profile is { } pr && DeviceIo.IsRomProfile(pr)) mediaState = "Finalizado";

        long? capacity = imapi?.TotalSectors is > 0 ? imapi.TotalSectors * 2048L : null;

        if (stable == DriveState.AudioCd)
        {
            return new DiscInfo
            {
                PhysicalType = physical,
                MediaState = mediaState,
                Sessions = sessions,
                Tracks = tracks?.Count,
                CapacityBytes = capacity,
                LogicalFormat = LogicalFormat.AudioCd,
                HasAudioTracks = true,
            };
        }

        var vol = DeviceIo.GetVolumeInfo(l, out _);
        if (vol == null) return null;

        bool protectedDisc = stable == DriveState.Protected || (dev != null && DeviceIo.ReadDvdCopyrightProtection(dev) == true);

        DateTime? recorded = ReadRecordedAt(l, ct);
        var entries = EnumerateEntries(l, ct, out bool rootReadable);
        if (!rootReadable) return null;

        long used = 0;
        if (NativeMethods.GetDiskFreeSpaceEx(DeviceIo.VolumeRoot(l), out _, out ulong total, out _) && total > 0) used = (long)total;
        else used = entries.Where(e => !e.IsDirectory).Sum(e => e.Size);

        recorded ??= SafeRootTime(l);

        return new DiscInfo
        {
            VolumeLabel = vol.Label,
            VolumeSerial = DeviceIo.FormatSerial(vol.Serial),
            PhysicalType = physical,
            MediaState = mediaState,
            Sessions = sessions,
            Tracks = tracks?.Count,
            FileSystem = vol.FileSystem,
            UsedBytes = used,
            CapacityBytes = capacity,
            RecordedAtUtc = recorded,
            LogicalFormat = DiscClassifier.Classify(entries),
            IsCopyProtected = protectedDisc,
            HasAudioTracks = HasAudioTracks(tracks),
            Entries = entries,
        };
    }

    /// <summary>E-08: true when the TOC lists at least one audio track (mixed-mode / CD-Extra: data plus audio).</summary>
    internal static bool HasAudioTracks(IReadOnlyList<DeviceIo.TocTrack>? toc) => toc != null && toc.Any(t => !t.IsData);

    private static DateTime? SafeRootTime(string l)
    {
        try
        {
            var t = Directory.GetCreationTimeUtc(@"\\?\" + l + @"\");
            return t.Year > 1601 ? t : null;
        }
        catch { return null; }
    }

    private static DateTime? ReadRecordedAt(string l, CancellationToken ct)
    {
        try
        {
            using var raw = OverlappedHandle.Open(DeviceIo.DevicePath(l), NativeMethods.GENERIC_READ, NativeMethods.FILE_SHARE_ALL, NativeMethods.FILE_FLAG_NO_BUFFERING, out _);
            if (raw == null) return null;
            var buf = new byte[Iso9660.SectorSize];
            int n = raw.ReadAsync((long)Iso9660.PrimaryDescriptorSector * Iso9660.SectorSize, Iso9660.SectorSize, 0, buf, TimeSpan.FromSeconds(15), ct).GetAwaiter().GetResult();
            return n == Iso9660.SectorSize ? Iso9660.ParseRecordedAtUtc(buf) : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // ------------------------------------------------------------------ file listing (E-13)

    private const int MaxDepth = 128;

    internal static List<DiscEntry> EnumerateEntries(string l, CancellationToken ct, out bool rootReadable)
    {
        var list = new List<DiscEntry>();
        rootReadable = Walk(@"\\?\" + l + @"\", "", list, ct, 0);
        return list;
    }

    private static bool Walk(string dirFull, string rel, List<DiscEntry> list, CancellationToken ct, int depth)
    {
        IEnumerator<FileSystemInfo> en;
        try
        {
            var opts = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
            en = new DirectoryInfo(dirFull).EnumerateFileSystemInfos("*", opts).GetEnumerator();
        }
        catch (Exception ex)
        {
            MarkError(list, rel, ex);
            return false;
        }

        using (en)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                FileSystemInfo fi;
                try
                {
                    if (!en.MoveNext()) break;
                    fi = en.Current;
                }
                catch (Exception ex)
                {
                    MarkError(list, rel, ex);
                    return !string.IsNullOrEmpty(rel); // a failing root listing means nothing usable
                }

                var childRel = rel.Length == 0 ? fi.Name : rel + "\\" + fi.Name;
                DateTime modified = SafeTime(fi);
                if (fi is DirectoryInfo di)
                {
                    list.Add(new DiscEntry { RelativePath = childRel, IsDirectory = true, ModifiedUtc = modified, Kind = EntryKind.Other });
                    bool reparse = (fi.Attributes & FileAttributes.ReparsePoint) != 0;
                    if (reparse) continue;
                    if (depth >= MaxDepth)
                    {
                        MarkError(list, childRel, new IOException("Profundidade máxima de pastas excedida."));
                        continue;
                    }
                    Walk(dirFull.TrimEnd('\\') + "\\" + fi.Name, childRel, list, ct, depth + 1);
                }
                else
                {
                    long size = 0;
                    string? error = null;
                    try { size = ((FileInfo)fi).Length; } catch (Exception ex) { error = ex.Message; }
                    list.Add(new DiscEntry
                    {
                        RelativePath = childRel,
                        Size = size,
                        ModifiedUtc = modified,
                        IsDirectory = false,
                        Kind = DiscClassifier.KindOf(childRel),
                        TitleGroup = DiscClassifier.TitleGroupOf(childRel),
                        Error = error,
                    });
                }
            }
        }
        return true;
    }

    private static DateTime SafeTime(FileSystemInfo fi)
    {
        try
        {
            var t = fi.LastWriteTimeUtc;
            return t.Year < 1601 ? DateTime.MinValue : t;
        }
        catch { return DateTime.MinValue; }
    }

    private static void MarkError(List<DiscEntry> list, string rel, Exception ex)
    {
        var message = $"Pasta ilegível: {ex.Message}";
        if (rel.Length == 0)
        {
            list.Add(new DiscEntry { RelativePath = ".", IsDirectory = true, Error = message });
            return;
        }
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].IsDirectory && string.Equals(list[i].RelativePath, rel, StringComparison.Ordinal))
            {
                list[i] = list[i] with { Error = message };
                return;
            }
        }
        list.Add(new DiscEntry { RelativePath = rel, IsDirectory = true, Error = message });
    }

    // ------------------------------------------------------------------ tray / lock / speed

    public bool Eject(string letter)
    {
        try
        {
            var l = DeviceIo.NormalizeLetter(letter);
            if (l == null) return false;
            // Release our own removal lock first (the engine may still hold it on failure paths).
            LockMedia(l, false);
            using var h = DeviceIo.OpenDevice(l, out _);
            if (h == null) return false;
            DeviceIo.Ioctl(h, NativeMethods.IOCTL_STORAGE_MEDIA_REMOVAL, new byte[] { 0 }, null, out _);
            bool ok = DeviceIo.Ioctl(h, NativeMethods.IOCTL_STORAGE_EJECT_MEDIA, null, null, out _) == 0;
            if (ok) { _trayOpen[l] = true; ClearMedia(l); }
            return ok;
        }
        catch { return false; }
    }

    public bool CloseTray(string letter)
    {
        try
        {
            var l = DeviceIo.NormalizeLetter(letter);
            if (l == null) return false;
            using var h = DeviceIo.OpenDevice(l, out _);
            if (h == null) return false;
            bool ok = DeviceIo.Ioctl(h, NativeMethods.IOCTL_STORAGE_LOAD_MEDIA, null, null, out _) == 0;
            if (ok) _trayOpen.TryRemove(l, out _);
            return ok;
        }
        catch { return false; }
    }

    /// <summary>
    /// Prevents (or allows again) removal of the medium. The lock lives as long as the handle that took it, so the service keeps
    /// that handle. After a drive loss (E-03/E-04) a held handle can be stale: it may hang or even "succeed" without protecting the
    /// disc that came back, so a new lock request always replaces it with a fresh handle, and every call on a possibly stale
    /// handle is time-bounded (a blocked DeviceIoControl must never freeze the copy or the UI).
    /// </summary>
    public bool LockMedia(string letter, bool locked)
    {
        try
        {
            var l = DeviceIo.NormalizeLetter(letter);
            if (l == null) return false;
            lock (_lockGate)
            {
                if (_lockHandles.Remove(l, out var held))
                {
                    DeviceIo.IoctlBounded(held, NativeMethods.IOCTL_STORAGE_MEDIA_REMOVAL, new byte[] { 0 });
                    DeviceIo.CloseBounded(held);
                }
                if (!locked) return true;

                var h = DeviceIo.OpenDevice(l, out _);
                if (h == null) return false;
                if (DeviceIo.IoctlBounded(h, NativeMethods.IOCTL_STORAGE_MEDIA_REMOVAL, new byte[] { 1 }) != 0)
                {
                    DeviceIo.CloseBounded(h);
                    return false;
                }
                _lockHandles[l] = h;
                return true;
            }
        }
        catch { return false; }
    }
    public bool SetReadSpeed(string letter, int kbPerSec)
    {
        try
        {
            var l = DeviceIo.NormalizeLetter(letter);
            if (l == null) return false;
            using var h = DeviceIo.OpenDevice(l, out _);
            if (h == null) return false;
            ushort speed = kbPerSec <= 0 ? (ushort)0xFFFF : (ushort)Math.Min(kbPerSec, 0xFFFE);
            var input = new byte[12]; // CDROM_SET_SPEED { RequestType=CdromSetSpeed, ReadSpeed, WriteSpeed, RotationControl=Default }
            BitConverter.GetBytes(speed).CopyTo(input, 4);
            BitConverter.GetBytes((ushort)0xFFFF).CopyTo(input, 6);
            return DeviceIo.Ioctl(h, NativeMethods.IOCTL_CDROM_SET_SPEED, input, null, out _) == 0;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------ change notification (debounce + polling)

    private void OnRawDeviceChange()
    {
        if (_disposed) return;
        try { _debounceTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
    }

    private string SafeSnapshot()
    {
        try
        {
            var parts = new List<string>();
            foreach (var letter in EnumerateCdromLetters())
                parts.Add(letter + "=" + GetState(letter));
            return string.Join(";", parts);
        }
        catch { return ""; }
    }

    private void Poll()
    {
        if (_disposed || Interlocked.Exchange(ref _pollRunning, 1) != 0) return;
        try
        {
            var snap = SafeSnapshot();
            var previous = _lastSnapshot;
            _lastSnapshot = snap;
            if (previous != null && snap != previous)
            {
                _driveCache.Clear();
                RaiseChanged();
            }
        }
        catch { }
        finally { Volatile.Write(ref _pollRunning, 0); }
    }

    private void FireChanged()
    {
        if (_disposed) return;
        _driveCache.Clear();
        _imapiCache.Clear();
        _lastSnapshot = SafeSnapshot();
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (_disposed) return;
        try { Changed?.Invoke(this, EventArgs.Empty); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _pollTimer.Dispose(); } catch { }
        try { _debounceTimer.Dispose(); } catch { }
        try { _watcher?.Dispose(); } catch { }
        lock (_lockGate)
        {
            foreach (var h in _lockHandles.Values) { try { h.Dispose(); } catch { } }
            _lockHandles.Clear();
        }
    }
}

