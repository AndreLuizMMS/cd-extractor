using MediaRescue.Core;

namespace MediaRescue.Core.Tests.Fakes;

/// <summary>
/// In-memory destination volume with scriptable faults: capacity (disk full), write errors, silent corruption,
/// removal of the volume, rename failures. Paths are case-insensitive like NTFS.
/// </summary>
public sealed class FakeDestination : IDestinationFileSystem
{
    private sealed class FileEntry
    {
        public MemoryStream Data = new();
        public DateTime ModifiedUtc;
    }

    private sealed class Corruption
    {
        public Func<string, bool> Match = _ => true;
        public long Offset;
        public int Remaining;
    }

    private readonly object _gate = new();
    private Dictionary<string, FileEntry> _files = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Corruption> _corruptions = new();

    public FakeDestination(string root = @"E:\Backup")
    {
        Root = root;
        AddDirectory(root);
    }

    public string Root { get; }
    public string VolumeLabel { get; set; } = "PENDRIVE";
    public string FileSystemName { get; set; } = "NTFS";
    public long CapacityBytes { get; set; } = 64L * 1024 * 1024 * 1024;
    public bool IsRemovable { get; set; } = true;
    public bool IsNetworkOrCloud { get; set; }
    public bool IsOptical { get; set; }
    public bool Writable { get; set; } = true;
    /// <summary>Streams returned by Create cannot seek (and throw on Position), like some network/cloud streams.</summary>
    public bool NonSeekable { get; set; }
    public string? WriteProbeError { get; set; }

    /// <summary>While true the volume is "unplugged": every operation throws DeviceGone and GetInfo returns null.</summary>
    public bool Removed { get; set; }

    public Func<string, long, int, Exception?>? OnWrite { get; set; }
    public Action<string>? OnMoveStart { get; set; }
    public Action<string, string>? OnAtomicWrite { get; set; }
    public Func<string, Exception?>? MoveFault { get; set; }
    public Func<string, Exception?>? FlushFault { get; set; }
    public Func<string, Exception?>? SetTimesFault { get; set; }
    public Func<string, Exception?>? AtomicWriteFault { get; set; }

    public List<string> Created { get; } = new();
    public List<(string From, string To)> Moves { get; } = new();
    public List<string> Deleted { get; } = new();
    public List<string> OpenedForRead { get; } = new();
    public Dictionary<string, DateTime> TimesSet { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int WriteCalls { get; private set; }

    public long UsedBytes { get { lock (_gate) return _files.Values.Sum(f => f.Data.Length); } }
    public long FreeBytes => Math.Max(0, CapacityBytes - UsedBytes);

    public IReadOnlyList<string> AllFiles { get { lock (_gate) return _files.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList(); } }
    public IReadOnlyList<string> PartialFiles => AllFiles.Where(f => f.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)).ToList();

    // ---------------- test helpers ----------------

    public void AddDirectory(string path)
    {
        lock (_gate)
        {
            var p = Norm(path);
            while (!string.IsNullOrEmpty(p))
            {
                _dirs.Add(p);
                var parent = System.IO.Path.GetDirectoryName(p);
                if (string.IsNullOrEmpty(parent) || parent == p) break;
                p = parent;
            }
        }
    }

    public void SeedFile(string path, byte[] data, DateTime? modifiedUtc = null)
    {
        lock (_gate)
        {
            AddDirectory(System.IO.Path.GetDirectoryName(Norm(path))!);
            var f = new FileEntry { ModifiedUtc = modifiedUtc ?? DateTime.UtcNow };
            f.Data.Write(data);
            _files[Norm(path)] = f;
        }
    }

    public byte[] ReadAllBytes(string path)
    {
        lock (_gate) return _files[Norm(path)].Data.ToArray();
    }

    public string ReadText(string path) => System.Text.Encoding.UTF8.GetString(ReadAllBytes(path));

    public DateTime ModifiedOf(string path)
    {
        lock (_gate) return _files[Norm(path)].ModifiedUtc;
    }

    public void Truncate(string path, long length)
    {
        lock (_gate) _files[Norm(path)].Data.SetLength(length);
    }

    /// <summary>Silently flips one bit of the stored copy when a write covers <paramref name="offset"/> (bad flash cell).</summary>
    public void CorruptOnWrite(long offset, int times = int.MaxValue, Func<string, bool>? pathMatch = null) =>
        _corruptions.Add(new Corruption { Offset = offset, Remaining = times, Match = pathMatch ?? (p => p.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) });

    public void FlipBitNow(string path, long offset)
    {
        lock (_gate)
        {
            var d = _files[Norm(path)].Data;
            var b = d.GetBuffer();
            b[offset] ^= 0x01;
        }
    }

    /// <summary>Copy of the whole volume state (a "power loss" snapshot).</summary>
    public FakeDestination Clone()
    {
        lock (_gate)
        {
            var c = new FakeDestination(Root)
            {
                VolumeLabel = VolumeLabel, FileSystemName = FileSystemName, CapacityBytes = CapacityBytes, IsRemovable = IsRemovable,
                Writable = Writable,
            };
            c._dirs = new HashSet<string>(_dirs, StringComparer.OrdinalIgnoreCase);
            c._files = _files.ToDictionary(kv => kv.Key, kv =>
            {
                var e = new FileEntry { ModifiedUtc = kv.Value.ModifiedUtc };
                e.Data.Write(kv.Value.Data.ToArray());
                return e;
            }, StringComparer.OrdinalIgnoreCase);
            return c;
        }
    }

    private static string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\');

    private void ThrowIfRemoved()
    {
        if (Removed) throw new DestinationException(DestinationErrorKind.DeviceGone, "O volume foi removido.");
    }

    // ---------------- IDestinationFileSystem ----------------

    public DestinationInfo? GetInfo(string path)
    {
        if (Removed) return null;
        return new DestinationInfo(Root, VolumeLabel, FileSystemName, FreeBytes, CapacityBytes, IsRemovable, IsNetworkOrCloud, IsOptical);
    }

    public bool DirectoryExists(string path)
    {
        if (Removed) return false;
        lock (_gate) return _dirs.Contains(Norm(path));
    }

    public bool FileExists(string path)
    {
        ThrowIfRemoved();
        lock (_gate) return _files.ContainsKey(Norm(path));
    }

    public void CreateDirectory(string path)
    {
        ThrowIfRemoved();
        AddDirectory(path);
    }

    public Stream Create(string path)
    {
        ThrowIfRemoved();
        lock (_gate)
        {
            var p = Norm(path);
            if (!_dirs.Contains(System.IO.Path.GetDirectoryName(p)!))
                throw new DestinationException(DestinationErrorKind.Other, "Diretório não existe: " + p, new DirectoryNotFoundException());
            Created.Add(p);
            var f = new FileEntry { ModifiedUtc = DateTime.UtcNow };
            _files[p] = f;
            return new WriteStream(this, p, f);
        }
    }

    public Stream OpenRead(string path)
    {
        ThrowIfRemoved();
        lock (_gate)
        {
            var p = Norm(path);
            if (!_files.TryGetValue(p, out var f)) throw new FileNotFoundException(p);
            OpenedForRead.Add(p);
            return new MemoryStream(f.Data.ToArray(), writable: false);
        }
    }

    public void FlushToDisk(Stream stream)
    {
        ThrowIfRemoved();
        var ws = stream as WriteStream;
        var fault = ws == null ? null : FlushFault?.Invoke(ws.Path);
        if (fault != null) throw fault;
        stream.Flush();
    }

    public void Move(string source, string destination)
    {
        ThrowIfRemoved();
        OnMoveStart?.Invoke(destination);
        var fault = MoveFault?.Invoke(destination);
        if (fault != null) throw fault;
        lock (_gate)
        {
            var s = Norm(source);
            var d = Norm(destination);
            if (!_files.TryGetValue(s, out var f)) throw new FileNotFoundException(s);
            if (_files.ContainsKey(d)) throw new IOException("O arquivo já existe: " + d);
            _files.Remove(s);
            _files[d] = f;
            Moves.Add((s, d));
        }
    }

    public void Delete(string path)
    {
        ThrowIfRemoved();
        lock (_gate)
        {
            var p = Norm(path);
            if (_files.Remove(p)) Deleted.Add(p);
        }
    }

    public long GetFileSize(string path)
    {
        ThrowIfRemoved();
        lock (_gate)
        {
            if (!_files.TryGetValue(Norm(path), out var f)) throw new FileNotFoundException(path);
            return f.Data.Length;
        }
    }

    public void SetTimes(string path, DateTime modifiedUtc)
    {
        ThrowIfRemoved();
        var fault = SetTimesFault?.Invoke(path);
        if (fault != null) throw fault;
        lock (_gate)
        {
            if (!_files.TryGetValue(Norm(path), out var f)) throw new FileNotFoundException(path);
            f.ModifiedUtc = modifiedUtc;
            TimesSet[Norm(path)] = modifiedUtc;
        }
    }

    public bool ProbeWritable(string directory, out string? error)
    {
        error = Writable ? null : (WriteProbeError ?? "Acesso negado.");
        return Writable;
    }

    public string? ReadAllText(string path)
    {
        ThrowIfRemoved();
        lock (_gate) return _files.TryGetValue(Norm(path), out var f) ? System.Text.Encoding.UTF8.GetString(f.Data.ToArray()) : null;
    }

    public void WriteAllTextAtomic(string path, string content)
    {
        ThrowIfRemoved();
        var fault = AtomicWriteFault?.Invoke(path);
        if (fault != null) throw fault;
        OnAtomicWrite?.Invoke(path, content);
        lock (_gate)
        {
            var p = Norm(path);
            var dir = System.IO.Path.GetDirectoryName(p)!;
            if (!_dirs.Contains(dir)) throw new DestinationException(DestinationErrorKind.Other, "Diretório não existe: " + dir);
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            if (_files.TryGetValue(p, out var old))
            {
                var bak = new FileEntry { ModifiedUtc = old.ModifiedUtc };
                bak.Data.Write(old.Data.ToArray());
                _files[p + ".bak"] = bak;
            }
            var f = new FileEntry { ModifiedUtc = DateTime.UtcNow };
            f.Data.Write(bytes);
            if (UsedBytes - (old?.Data.Length ?? 0) + bytes.Length > CapacityBytes)
                throw new DestinationException(DestinationErrorKind.DiskFull, "Sem espaço no disco.");
            _files[p] = f;
        }
    }

    // ---------------- write stream ----------------

    private sealed class WriteStream : Stream
    {
        private readonly FakeDestination _fs;
        private readonly FileEntry _file;
        public string Path { get; }
        private bool _disposed;

        public WriteStream(FakeDestination fs, string path, FileEntry file) { _fs = fs; Path = path; _file = file; }

        public override bool CanRead => false;
        public override bool CanSeek => !_fs.NonSeekable;
        public override bool CanWrite => true;
        public override long Length => _file.Data.Length;
        public override long Position
        {
            get => _fs.NonSeekable ? throw new NotSupportedException() : _file.Data.Position;
            set => _file.Data.Position = value;
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) { _fs.ThrowIfRemoved(); return _file.Data.Seek(offset, origin); }
        public override void SetLength(long value) => _file.Data.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_disposed) throw new ObjectDisposedException(Path);
            _fs.ThrowIfRemoved();
            long offset = _file.Data.Position;
            lock (_fs._gate) _fs.WriteCalls++;
            var fault = _fs.OnWrite?.Invoke(Path, offset, buffer.Length);
            if (fault != null) throw fault;

            long growth = Math.Max(0, offset + buffer.Length - _file.Data.Length);
            long free = _fs.FreeBytes;
            if (growth > free)
            {
                // Real volumes accept what fits and then fail.
                int fits = (int)Math.Max(0, free - Math.Max(0, offset - _file.Data.Length));
                if (fits > 0) _file.Data.Write(buffer[..Math.Min(fits, buffer.Length)]);
                throw new DestinationException(DestinationErrorKind.DiskFull, "Não há espaço suficiente no disco.");
            }
            _file.Data.Write(buffer);

            foreach (var c in _fs._corruptions)
            {
                if (c.Remaining > 0 && c.Match(Path) && c.Offset >= offset && c.Offset < offset + buffer.Length)
                {
                    c.Remaining--;
                    _file.Data.GetBuffer()[c.Offset] ^= 0x40; // silent corruption of the stored copy only
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }
    }
}
