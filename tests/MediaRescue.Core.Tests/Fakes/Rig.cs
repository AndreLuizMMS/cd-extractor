using System.Security.Cryptography;
using MediaRescue.Core;

namespace MediaRescue.Core.Tests.Fakes;

public static class TestData
{
    /// <summary>Deterministic pseudo-random content (never all zeros, so "zero-filled" is detectable).</summary>
    public static byte[] Bytes(int length, int seed = 1)
    {
        var rnd = new Random(seed);
        var b = new byte[length];
        rnd.NextBytes(b);
        for (int i = 0; i < b.Length; i++) if (b[i] == 0) b[i] = 1;
        return b;
    }

    public static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}

/// <summary>Wires a complete engine with fakes around one fake disc and one fake destination.</summary>
public sealed class Rig
{
    public const string DestRoot = @"E:\Backup";
    public const string Folder = "Disco 1";

    public FakeClock Clock { get; } = new();
    public FakeDiscReader Reader { get; }
    public FakeDestination Dest { get; }
    public FakeDriveService Drives { get; } = new();
    public ListProgress Progress { get; } = new();
    public ExtractionControl Control { get; } = new();
    public ExtractionEngine Engine { get; }
    public ExtractionOptions Options { get; } = new();
    public OpticalDrive Drive => Drives.Drives[0];
    public List<DiscEntry> Entries { get; } = new();
    public Dictionary<string, byte[]> Contents { get; } = new(StringComparer.Ordinal);
    public string Label { get; set; } = "FORMATURA";
    public string Serial { get; set; } = "1234-ABCD";
    public bool Protected { get; set; }

    public Rig(FakeDestination? dest = null)
    {
        Reader = new FakeDiscReader(Clock);
        Dest = dest ?? new FakeDestination(DestRoot);
        Engine = new ExtractionEngine(Reader, Dest, Drives, Clock);
        Drives.AnalyzeScript = _ => Disc;
    }

    public string DiscFolder => Path.Combine(DestRoot, Folder);
    public string DestPath(string relative) => Path.Combine(DiscFolder, relative);

    public static readonly DateTime DefaultModified = new(2009, 12, 12, 15, 30, 0, DateTimeKind.Utc);

    public FaultInjectingDiscFile AddFile(string relativePath, int length, int seed = 0, DateTime? modified = null, int sectorSize = 2048, long? physicalOffset = null)
    {
        var data = TestData.Bytes(length, seed == 0 ? Contents.Count + 1 : seed);
        return AddFile(relativePath, data, modified, sectorSize, physicalOffset);
    }

    public FaultInjectingDiscFile AddFile(string relativePath, byte[] data, DateTime? modified = null, int sectorSize = 2048, long? physicalOffset = null)
    {
        Contents[relativePath] = data;
        Entries.Add(new DiscEntry
        {
            RelativePath = relativePath, Size = data.Length, ModifiedUtc = modified ?? DefaultModified,
            Kind = DiscClassifier.KindOf(relativePath), PhysicalOffset = physicalOffset,
        });
        return Reader.Add(relativePath, data, sectorSize);
    }

    public void AddDirectory(string relativePath, string? error = null) =>
        Entries.Add(new DiscEntry { RelativePath = relativePath, IsDirectory = true, Error = error, ModifiedUtc = DefaultModified });

    public DiscInfo Disc => new()
    {
        VolumeLabel = Label, VolumeSerial = Serial, PhysicalType = "DVD-R", FileSystem = "UDF",
        UsedBytes = Entries.Sum(e => e.Size), Entries = Entries.ToList(), IsCopyProtected = Protected,
        RecordedAtUtc = new DateTime(2009, 12, 12, 0, 0, 0, DateTimeKind.Utc),
    };

    public ExtractionRequest Request(IReadOnlyList<DiscEntry>? entries = null) =>
        new(Drive, Disc, DestRoot, Folder, entries ?? Entries.ToList(), Options);

    public Task<ExtractionResult> Run(CancellationToken ct = default) => Engine.RunAsync(Request(), Progress, Control, ct);

    public async Task<ExtractionResult> RunWithTimeout(CancellationToken ct = default)
    {
        var task = Run(ct);
        // Real-time guard only: a healthy run finishes immediately because the fake clock never sleeps.
        return await task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>Final destination content of a file as written by the engine.</summary>
    public byte[] Written(string relativePath) => Dest.ReadAllBytes(DestPath(relativePath));

    public ExtractionEngine NewEngine() => new(Reader, Dest, Drives, Clock);
}
