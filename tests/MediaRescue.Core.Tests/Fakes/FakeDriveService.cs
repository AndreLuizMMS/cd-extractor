using MediaRescue.Core;

namespace MediaRescue.Core.Tests.Fakes;

public sealed class FakeDriveService : IOpticalDriveService
{
    public List<OpticalDrive> Drives { get; } = new() { new OpticalDrive("D:", "HP", "CDDVDW TS-L633M", "1.0", "USB", false) };
    public DriveState State { get; set; } = DriveState.Ready;

    /// <summary>Result of the n-th (1-based) AnalyzeAsync call. Default: no media.</summary>
    public Func<int, DiscInfo?> AnalyzeScript { get; set; } = _ => null;
    public int AnalyzeCalls { get; private set; }
    public List<bool> LockCalls { get; } = new();
    public List<int> SpeedCalls { get; } = new();
    public bool SpeedSupported { get; set; } = true;
    public bool LockSupported { get; set; } = true;
    public Exception? LockFault { get; set; }

    public event EventHandler? Changed { add { } remove { } }

    public IReadOnlyList<OpticalDrive> GetDrives() => Drives;
    public DriveState GetState(string letter) => State;

    public Task<DiscInfo?> AnalyzeAsync(string letter, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        AnalyzeCalls++;
        return Task.FromResult(AnalyzeScript(AnalyzeCalls));
    }

    public bool Eject(string letter) => true;
    public bool CloseTray(string letter) => true;

    public bool LockMedia(string letter, bool locked)
    {
        LockCalls.Add(locked);
        if (LockFault != null) throw LockFault;
        return LockSupported;
    }

    public bool SetReadSpeed(string letter, int kbPerSec)
    {
        SpeedCalls.Add(kbPerSec);
        return SpeedSupported;
    }

    public void Dispose() { }
}

/// <summary>Synchronous <see cref="IProgress{T}"/> (unlike Progress&lt;T&gt;, which posts to the thread pool) so tests are deterministic.</summary>
public sealed class ListProgress : IProgress<ExtractionProgress>
{
    private readonly object _gate = new();
    private readonly List<ExtractionProgress> _items = new();

    public Action<ExtractionProgress>? OnReport { get; set; }

    public IReadOnlyList<ExtractionProgress> Items { get { lock (_gate) return _items.ToList(); } }
    public ExtractionProgress Last { get { lock (_gate) return _items[^1]; } }

    public void Report(ExtractionProgress value)
    {
        lock (_gate) _items.Add(value);
        OnReport?.Invoke(value);
    }

    public IEnumerable<string> Messages => Items.Select(i => i.Message).Where(m => m != null)!;
    public bool SawPhase(ExtractionPhase phase) => Items.Any(i => i.Phase == phase);
}
