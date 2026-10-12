using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using MediaRescue.Core;
using MediaRescue.Integration.Tests.Support;
using MediaRescue.Windows;

namespace MediaRescue.Integration.Tests.EndToEnd;

/// <summary>Synchronous <see cref="IProgress{T}"/> that records every report (and can react to it on the engine thread).</summary>
internal sealed class RecordingProgress : IProgress<ExtractionProgress>
{
    private readonly object _gate = new();
    private readonly List<ExtractionProgress> _items = new();

    /// <summary>Invoked synchronously on the engine's thread for every report.</summary>
    public Action<ExtractionProgress>? OnReport { get; set; }

    public IReadOnlyList<ExtractionProgress> Items { get { lock (_gate) return _items.ToList(); } }

    public void Report(ExtractionProgress value)
    {
        lock (_gate) _items.Add(value);
        OnReport?.Invoke(value);
    }

    public bool SawPhase(ExtractionPhase phase) => Items.Any(i => i.Phase == phase);
    public IEnumerable<string> Messages => Items.Select(i => i.Message).Where(m => m != null)!;
}

/// <summary>Delegates everything to the real service and records calls (lock/unlock sequence, calling threads).</summary>
internal sealed class RecordingDriveService : IOpticalDriveService
{
    private readonly IOpticalDriveService _inner;
    private readonly object _gate = new();
    private readonly List<bool> _locks = new();
    private readonly List<(string Method, int ThreadId)> _calls = new();

    public RecordingDriveService(IOpticalDriveService inner) => _inner = inner;

    public IReadOnlyList<bool> LockCalls { get { lock (_gate) return _locks.ToList(); } }
    public IReadOnlyList<(string Method, int ThreadId)> Calls { get { lock (_gate) return _calls.ToList(); } }

    private void Note(string method) { lock (_gate) _calls.Add((method, Environment.CurrentManagedThreadId)); }

    public event EventHandler? Changed { add => _inner.Changed += value; remove => _inner.Changed -= value; }

    public IReadOnlyList<OpticalDrive> GetDrives() { Note(nameof(GetDrives)); return _inner.GetDrives(); }
    public DriveState GetState(string letter) { Note(nameof(GetState)); return _inner.GetState(letter); }
    public Task<DiscInfo?> AnalyzeAsync(string letter, CancellationToken ct) { Note(nameof(AnalyzeAsync)); return _inner.AnalyzeAsync(letter, ct); }
    public bool Eject(string letter) { Note(nameof(Eject)); return _inner.Eject(letter); }
    public bool CloseTray(string letter) { Note(nameof(CloseTray)); return _inner.CloseTray(letter); }

    public bool LockMedia(string letter, bool locked)
    {
        lock (_gate) _locks.Add(locked);
        return _inner.LockMedia(letter, locked);
    }

    public bool SetReadSpeed(string letter, int kbPerSec) => _inner.SetReadSpeed(letter, kbPerSec);
    public void Dispose() => _inner.Dispose();
}

/// <summary>The real <see cref="DiscReader"/> underneath; counts opens per file and offers a hook before each read.</summary>
internal sealed class CountingDiscReader : IDiscReader
{
    private readonly IDiscReader _inner;
    private readonly ConcurrentDictionary<string, int> _opens = new(StringComparer.OrdinalIgnoreCase);

    public CountingDiscReader(IDiscReader inner) => _inner = inner;

    /// <summary>(relative path, offset) -> awaited before the real read happens. Lets a test pull the disc out at an exact point.</summary>
    public Func<string, long, Task>? BeforeRead { get; set; }

    public int OpenCount(string relativePath) => _opens.TryGetValue(relativePath, out var n) ? n : 0;
    public IReadOnlyDictionary<string, int> Opens => _opens;
    public void ResetCounts() => _opens.Clear();

    public IDiscFile Open(string driveLetter, string relativePath)
    {
        var file = _inner.Open(driveLetter, relativePath);
        _opens.AddOrUpdate(relativePath, 1, (_, n) => n + 1);
        return new CountingFile(file, relativePath, this);
    }

    private sealed class CountingFile : IDiscFile
    {
        private readonly IDiscFile _file;
        private readonly string _path;
        private readonly CountingDiscReader _owner;

        public CountingFile(IDiscFile file, string path, CountingDiscReader owner) { _file = file; _path = path; _owner = owner; }

        public long Length => _file.Length;
        public int SectorSize => _file.SectorSize;

        public async Task<int> ReadAsync(long offset, Memory<byte> buffer, TimeSpan timeout, CancellationToken ct)
        {
            var hook = _owner.BeforeRead;
            if (hook != null) await hook(_path, offset).ConfigureAwait(false);
            return await _file.ReadAsync(offset, buffer, timeout, ct).ConfigureAwait(false);
        }

        public void Dispose() => _file.Dispose();
    }
}

/// <summary>
/// The real object graph of the extraction path (NOT fakes): OpticalDriveService, DiscReader, WindowsDestinationFileSystem,
/// SystemClock, ExtractionEngine, CatalogStore, DestinationValidator, with a temporary NTFS destination and mounted ISOs.
/// The only wrappers only observe (counting opens, recording lock calls). Dismounts and deletes everything on Dispose.
/// </summary>
internal sealed class E2eRig : IDisposable
{

    private readonly List<MountedIso> _mounts = new();

    public string Work { get; } = TestEnv.NewTempDir("mr-e2e");
    public string DestRoot { get; }
    public string IsoDir { get; }
    public string CatalogPath { get; }

    public OpticalDriveService Real { get; } = new();
    public RecordingDriveService Drives { get; }
    public CountingDiscReader Reader { get; } = new(new DiscReader());
    public WindowsDestinationFileSystem Fs { get; } = new();
    public SystemClock Clock { get; } = new();
    public ExtractionEngine Engine { get; }
    public CatalogStore Catalog { get; }
    public DestinationValidator Validator { get; }

    public E2eRig()
    {
        DestRoot = Path.Combine(Work, "destino");
        IsoDir = Path.Combine(Work, "iso");
        CatalogPath = Path.Combine(Work, "appdata", "catalog.json");
        Directory.CreateDirectory(DestRoot);
        Directory.CreateDirectory(IsoDir);
        Drives = new RecordingDriveService(Real);
        Engine = new ExtractionEngine(Reader, Fs, Drives, Clock);
        Catalog = new CatalogStore(Fs, CatalogPath);
        Validator = new DestinationValidator(Fs);
    }

    public string BuildIso(IsoSpec spec, string name = "disc.iso") => spec.Build(IsoDir, Guid.NewGuid().ToString("N") + "-" + name);

    public MountedIso Mount(IsoSpec spec, string name = "disc.iso") => MountIso(BuildIso(spec, name));

    public MountedIso MountIso(string isoPath)
    {
        var m = MountedIso.Mount(isoPath);
        lock (_mounts) _mounts.Add(m);
        return m;
    }

    public async Task<(OpticalDrive Drive, DiscInfo Disc)> AnalyzeAsync(string letter)
    {
        var disc = await DiscHelpers.AnalyzeOrFailAsync(Real, letter);
        var drive = Real.GetDrives().Single(d => d.Letter == letter);
        return (drive, disc);
    }

    public ExtractionRequest Request(OpticalDrive drive, DiscInfo disc, string folderName, string? destRoot = null,
        IReadOnlyList<DiscEntry>? entries = null, ExtractionOptions? options = null) =>
        new(drive, disc, destRoot ?? DestRoot, folderName, entries ?? disc.Entries,
            options ?? new ExtractionOptions { BlockTimeout = TimeSpan.FromMinutes(2) });

    public async Task<ExtractionResult> RunAsync(ExtractionRequest request, RecordingProgress? progress = null,
        ExtractionControl? control = null, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        progress ??= new RecordingProgress();
        control ??= new ExtractionControl();
        return await Engine.RunAsync(request, progress, control, ct).WaitAsync(timeout ?? TimeSpan.FromMinutes(6));
    }

    /// <summary>Same record the App adds after a finished extraction (ExtractViewModel.AddToCatalogAsync).</summary>
    public CatalogEntry AddToCatalog(string name, DiscInfo disc, ExtractionResult result)
    {
        var entry = new CatalogEntry
        {
            Fingerprint = disc.Fingerprint,
            Name = name,
            ExtractedAtUtc = Clock.UtcNow,
            DestinationPath = result.DiscFolder,
            TotalBytes = result.Files.Sum(f => f.BytesTotal),
            BytesLost = result.Files.Sum(f => f.BytesLost),
            FileCount = result.Files.Count,
            Status = result.Status,
        };
        Catalog.Add(entry);
        return entry;
    }

    // ------------------------------------------------------------------ assertions

    public static string Sha256Of(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    /// <summary>Nothing the engine uses internally may be left in a finished disc folder.</summary>
    public static void AssertNoLeftovers(string discFolder)
    {
        var all = Directory.EnumerateFiles(discFolder, "*", SearchOption.AllDirectories).ToList();
        all.Where(p => p.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty("no .partial file may be left behind");
        all.Where(p => Path.GetFileName(p).StartsWith(".mediarescue-job", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty("the resume state is removed when the job completes");
        all.Where(p => p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)).Should().BeEmpty("no temp/backup files");
    }

    /// <summary>
    /// RF-05 / RF-08 / E-24 for a finished, fully successful extraction: every file Intact, SHA-256 of the destination equals the
    /// source, modified times preserved, empty folders recreated, manifest present and consistent, no leftovers.
    /// </summary>
    public static void AssertExtractedCorrectly(IsoSpec spec, DiscInfo disc, ExtractionResult result, string? expectedLetter = null)
    {
        result.Status.Should().Be(OverallStatus.Intact, string.Join("; ", result.Files.Where(f => f.Status != FileStatus.Intact).Take(5).Select(f => $"{f.RelativePath}={f.Status} {f.Error}")));
        var expected = spec.Hashes();
        result.Files.Should().HaveCount(spec.Files.Count);
        result.Files.Should().OnlyContain(f => f.Status == FileStatus.Intact, "every file must be Intact");
        Directory.Exists(result.DiscFolder).Should().BeTrue();

        foreach (var f in result.Files)
        {
            var dst = Path.Combine(result.DiscFolder, f.WrittenPath);
            File.Exists(dst).Should().BeTrue($"{f.RelativePath} must exist at {f.WrittenPath}");
            new FileInfo(dst).Length.Should().Be(spec.Files[f.RelativePath].LongLength, f.RelativePath);
            Sha256Of(dst).Should().Be(expected[f.RelativePath], $"SHA-256 of the destination copy of {f.RelativePath}");
            f.Sha256.Should().Be(expected[f.RelativePath], $"the hash the engine reports for {f.RelativePath}");
            f.BytesLost.Should().Be(0);
            f.BadRanges.Should().BeEmpty();

            var entry = disc.Entries.Single(e => !e.IsDirectory && e.RelativePath == f.RelativePath);
            if (entry.ModifiedUtc.Ticks != 0)
            {
                File.GetLastWriteTimeUtc(dst).Should().BeCloseTo(entry.ModifiedUtc, TimeSpan.FromMilliseconds(5), $"modified time of {f.RelativePath}");
                File.GetCreationTimeUtc(dst).Should().BeCloseTo(entry.ModifiedUtc, TimeSpan.FromMilliseconds(5), $"creation time of {f.RelativePath}");
            }
        }

        foreach (var dir in disc.Entries.Where(e => e.IsDirectory))
            Directory.Exists(Path.Combine(result.DiscFolder, dir.RelativePath)).Should().BeTrue($"folder {dir.RelativePath} (even when empty) must be recreated");

        AssertManifest(spec, disc, result, expectedLetter);
        AssertNoLeftovers(result.DiscFolder);
        File.Exists(Path.Combine(result.DiscFolder, "LEIA-ME - ERROS.txt")).Should().BeFalse("a clean extraction has no error report");
    }

    public static void AssertManifest(IsoSpec spec, DiscInfo disc, ExtractionResult result, string? expectedLetter)
    {
        var path = Path.Combine(result.DiscFolder, "mediarescue-manifest.json");
        File.Exists(path).Should().BeTrue("RF-08: every disc folder gets mediarescue-manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        root.GetProperty("schemaVersion").GetInt32().Should().BeGreaterThan(0);
        root.GetProperty("appVersion").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("job").GetProperty("overallStatus").GetString().Should().Be("Intact");
        var d = root.GetProperty("disc");
        d.GetProperty("fingerprint").GetString().Should().Be(disc.Fingerprint);
        d.GetProperty("volumeSerial").GetString().Should().Be(disc.VolumeSerial);
        d.GetProperty("logicalFormat").GetString().Should().Be(disc.LogicalFormat.ToString());
        if (expectedLetter != null) root.GetProperty("drive").GetProperty("letter").GetString().Should().Be(expectedLetter);

        var totals = root.GetProperty("totals");
        totals.GetProperty("files").GetInt32().Should().Be(spec.Files.Count);
        totals.GetProperty("intact").GetInt32().Should().Be(spec.Files.Count);
        totals.GetProperty("bytesLost").GetInt64().Should().Be(0);
        totals.GetProperty("bytes").GetInt64().Should().Be(spec.Files.Values.Sum(v => (long)v.Length));

        var expected = spec.Hashes();
        var files = root.GetProperty("files").EnumerateArray().ToList();
        files.Should().HaveCount(spec.Files.Count);
        var pathMap = root.GetProperty("pathMap");
        foreach (var f in files)
        {
            var original = f.GetProperty("originalPath").GetString()!;
            f.GetProperty("status").GetString().Should().Be("Intact", original);
            f.GetProperty("sha256").GetString().Should().Be(expected[original], original);
            f.GetProperty("sha256Scope").GetString().Should().Be("original", original);
            f.GetProperty("bytesTotal").GetInt64().Should().Be(spec.Files[original].LongLength, original);
            f.GetProperty("bytesLost").GetInt64().Should().Be(0, original);
            var written = f.GetProperty("writtenPath").GetString()!;
            pathMap.GetProperty(original).GetString().Should().Be(written, "the path map must agree with the file list");
            File.Exists(Path.Combine(result.DiscFolder, written)).Should().BeTrue(original);
        }
        files.Select(f => f.GetProperty("originalPath").GetString()).Should().BeEquivalentTo(spec.Files.Keys);
    }

    public void Dispose()
    {
        try { Real.Dispose(); } catch { }
        List<MountedIso> mounts;
        lock (_mounts) mounts = _mounts.ToList();
        foreach (var m in mounts) { try { m.Dispose(); } catch { } }
        // Mount-DiskImage releases the ISO file asynchronously: retry the delete for a short while.
        for (int i = 0; i < 20; i++)
        {
            try { Directory.Delete(@"\\?\" + Work, true); break; }
            catch { Thread.Sleep(250); }
        }
    }
}
