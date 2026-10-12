using MediaRescue.Core;

namespace MediaRescue.Core.Tests.Fakes;

public readonly record struct ReadRecord(long Offset, int Length, bool Ok);

/// <summary>In-memory "disc". Files are <see cref="FaultInjectingDiscFile"/>s so every test can script failures.</summary>
public sealed class FakeDiscReader : IDiscReader
{
    private readonly Dictionary<string, FaultInjectingDiscFile> _files = new(StringComparer.Ordinal);

    public FakeDiscReader(FakeClock clock) => Clock = clock;

    public FakeClock Clock { get; }
    /// <summary>While true every Open/Read fails with DeviceGone (USB cable pulled).</summary>
    public bool DeviceGone { get; set; }
    public List<string> Opened { get; } = new();
    public Func<string, Exception?>? OpenFault { get; set; }

    public FaultInjectingDiscFile Add(string relativePath, byte[] data, int sectorSize = 2048)
    {
        var f = new FaultInjectingDiscFile(this, relativePath, data, sectorSize);
        _files[Normalize(relativePath)] = f;
        return f;
    }

    public FaultInjectingDiscFile File(string relativePath) => _files[Normalize(relativePath)];

    public IDiscFile Open(string driveLetter, string relativePath)
    {
        if (DeviceGone) throw new DiscReadException(ReadErrorKind.DeviceGone, 1167, "device not connected");
        var fault = OpenFault?.Invoke(relativePath);
        if (fault != null) throw fault;
        if (!_files.TryGetValue(Normalize(relativePath), out var f))
            throw new DiscReadException(ReadErrorKind.Other, 2, "file not found on disc: " + relativePath);
        Opened.Add(relativePath);
        return f;
    }

    private static string Normalize(string p) => p.Replace('/', '\\').TrimStart('\\');
}

/// <summary>
/// A disc file with scriptable faults: permanent bad ranges, N transient failures, delays (on the fake clock),
/// timeouts, hangs, and "device disappears at byte X". A read that overlaps a faulty byte fails as a whole, like a real drive.
/// </summary>
public sealed class FaultInjectingDiscFile : IDiscFile
{
    private sealed class Transient { public long Start, End; public int Remaining; }

    private readonly FakeDiscReader _reader;
    private readonly byte[] _data;
    private readonly List<(long Start, long End)> _permanent = new();
    private readonly List<Transient> _transient = new();
    private readonly List<(long Start, long End)> _timeouts = new();
    private readonly List<(long Start, long End, bool IgnoreCancel)> _hangs = new();
    private long? _goneAt;
    private bool _goneFired;
    private int _failFirstReads;

    public FaultInjectingDiscFile(FakeDiscReader reader, string path, byte[] data, int sectorSize)
    {
        _reader = reader; _data = data; Path = path; SectorSize = sectorSize;
    }

    public string Path { get; }
    public byte[] Data => _data;
    public long Length => _data.Length;
    public int SectorSize { get; }

    /// <summary>Virtual time every read takes (success or failure).</summary>
    public TimeSpan ReadDelay { get; set; }
    /// <summary>Extra virtual time a failing read takes (a drive retrying internally).</summary>
    public TimeSpan FailDelay { get; set; }
    /// <summary>Invoked at the start of every read (offset, length); may cancel tokens, pause the control, etc.</summary>
    public Action<long, int>? OnRead { get; set; }
    public List<ReadRecord> Log { get; } = new();
    public List<TimeSpan> Timeouts { get; } = new();
    public TaskCompletionSource HangStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int ReadCalls => Log.Count;

    public FaultInjectingDiscFile BadRange(long offset, long length) { _permanent.Add((offset, offset + length)); return this; }
    public FaultInjectingDiscFile FailTimes(long offset, long length, int times) { _transient.Add(new Transient { Start = offset, End = offset + length, Remaining = times }); return this; }
    public FaultInjectingDiscFile FailFirstReads(int count) { _failFirstReads = count; return this; }
    public FaultInjectingDiscFile TimeoutRange(long offset, long length) { _timeouts.Add((offset, offset + length)); return this; }
    public FaultInjectingDiscFile HangRange(long offset, long length, bool ignoreCancellation = false) { _hangs.Add((offset, offset + length, ignoreCancellation)); return this; }
    /// <summary>The first read reaching byte <paramref name="offset"/> makes the whole device disappear (once).</summary>
    public FaultInjectingDiscFile DeviceGoneAt(long offset) { _goneAt = offset; return this; }

    public int DisposeCount { get; private set; }
    public void Dispose() => DisposeCount++;

    public async Task<int> ReadAsync(long offset, Memory<byte> buffer, TimeSpan timeout, CancellationToken ct)
    {
        Timeouts.Add(timeout);
        OnRead?.Invoke(offset, buffer.Length);
        ct.ThrowIfCancellationRequested();

        int count = (int)Math.Max(0, Math.Min(buffer.Length, _data.Length - offset));
        long end = offset + Math.Max(count, 1);

        if (_reader.DeviceGone)
        {
            Log.Add(new(offset, buffer.Length, false));
            throw new DiscReadException(ReadErrorKind.DeviceGone, 1167, "device not connected");
        }
        if (_goneAt is long g && !_goneFired && end > g)
        {
            _goneFired = true;
            _reader.DeviceGone = true;
            Log.Add(new(offset, buffer.Length, false));
            throw new DiscReadException(ReadErrorKind.DeviceGone, 1167, "device removed during read");
        }

        foreach (var h in _hangs)
        {
            if (!Overlaps(offset, end, h.Start, h.End)) continue;
            Log.Add(new(offset, buffer.Length, false));
            HangStarted.TrySetResult();
            if (h.IgnoreCancel) await new TaskCompletionSource<int>().Task;
            else await Task.Delay(Timeout.Infinite, ct);
        }

        if (ReadDelay > TimeSpan.Zero) await _reader.Clock.Delay(ReadDelay, ct);

        bool fail = _failFirstReads > 0 && Log.Count < _failFirstReads;
        fail |= _permanent.Any(p => Overlaps(offset, end, p.Start, p.End));
        foreach (var t in _transient)
            if (t.Remaining > 0 && Overlaps(offset, end, t.Start, t.End)) { t.Remaining--; fail = true; }
        if (fail)
        {
            if (FailDelay > TimeSpan.Zero) await _reader.Clock.Delay(FailDelay, ct);
            Log.Add(new(offset, buffer.Length, false));
            throw new DiscReadException(ReadErrorKind.BadSector, 23, $"CRC error at {offset}");
        }
        if (_timeouts.Any(p => Overlaps(offset, end, p.Start, p.End)))
        {
            await _reader.Clock.Delay(timeout, ct); // the "driver" waits for the whole timeout, then gives up
            Log.Add(new(offset, buffer.Length, false));
            throw new DiscReadException(ReadErrorKind.BadSector, 121, "semaphore timeout");
        }

        if (count > 0) _data.AsMemory((int)offset, count).CopyTo(buffer);
        Log.Add(new(offset, buffer.Length, true));
        return count;
    }

    private static bool Overlaps(long a0, long a1, long b0, long b1) => a0 < b1 && b0 < a1;
}
