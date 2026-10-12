using MediaRescue.App.Infrastructure;
using MediaRescue.App.Services;
using MediaRescue.Core;

namespace MediaRescue.App.Tests.Support;

public sealed class FakeDrives : IOpticalDriveService
{
    public static readonly OpticalDrive D = new("D:", "hp", "CDDVDW TS-L633M", "0301", "USB", false);
    public static readonly OpticalDrive F = new("F:", "ISO", "Virtual DVD-ROM", "1.0", "Virtual", true);

    public List<OpticalDrive> DriveList { get; } = new() { D };
    public Dictionary<string, DriveState> States { get; } = new(StringComparer.OrdinalIgnoreCase) { ["D:"] = DriveState.NoMedia, ["F:"] = DriveState.NoMedia };
    public Dictionary<string, DiscInfo?> Discs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Exception? AnalyzeThrows { get; set; }
    public TaskCompletionSource<bool>? AnalyzeGate { get; set; }
    public int AnalyzeCalls { get; private set; }
    public List<string> Ejected { get; } = new();
    public bool EjectResult { get; set; } = true;
    public List<(string Letter, bool Locked)> Locks { get; } = new();

    public event EventHandler? Changed;
    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public IReadOnlyList<OpticalDrive> GetDrives() => DriveList.ToList();
    public DriveState GetState(string letter) => States.TryGetValue(letter, out var s) ? s : DriveState.NoMedia;

    public async Task<DiscInfo?> AnalyzeAsync(string letter, CancellationToken ct)
    {
        AnalyzeCalls++;
        if (AnalyzeGate != null) await AnalyzeGate.Task.WaitAsync(ct);
        if (AnalyzeThrows != null) throw AnalyzeThrows;
        return Discs.TryGetValue(letter, out var d) ? d : null;
    }

    public bool Eject(string letter)
    {
        Ejected.Add(letter);
        if (EjectResult) States[letter] = DriveState.NoMedia;
        return EjectResult;
    }

    public bool CloseTray(string letter) => false;
    public bool LockMedia(string letter, bool locked) { Locks.Add((letter, locked)); return true; }
    public bool SetReadSpeed(string letter, int kbPerSec) => true;
    public void Dispose() { }
}

public sealed class FakeEngine : IExtractionEngine
{
    public bool Resumable { get; set; }
    public List<ExtractionRequest> Requests { get; } = new();
    public Func<ExtractionRequest, IProgress<ExtractionProgress>, ExtractionControl, CancellationToken, Task<ExtractionResult>>? Behavior { get; set; }

    public bool CanResume(DiscInfo disc, string destinationRoot, string folderName) => Resumable;

    public Task<ExtractionResult> RunAsync(ExtractionRequest request, IProgress<ExtractionProgress> progress, ExtractionControl control, CancellationToken ct)
    {
        Requests.Add(request);
        if (Behavior != null) return Behavior(request, progress, control, ct);
        return Task.FromResult(Intact(request));
    }

    public static ExtractionResult Intact(ExtractionRequest r) => Result(r, OverallStatus.Intact, FileStatus.Intact);

    public static ExtractionResult Result(ExtractionRequest r, OverallStatus overall, FileStatus fileStatus, long lost = 0) =>
        new(Path.Combine(r.DestinationRoot, r.FolderName), overall,
            r.Entries.Where(e => !e.IsDirectory).Select(e => new FileResult
            {
                RelativePath = e.RelativePath, WrittenPath = e.RelativePath, Status = fileStatus, BytesTotal = e.Size, BytesLost = lost,
            }).ToList(),
            TimeSpan.FromSeconds(30));
}

public sealed class FakeCatalog : ICatalogStore
{
    public List<CatalogEntry> Entries { get; } = new();
    public string? LoadWarning { get; set; }
    public IReadOnlyList<CatalogEntry> GetAll() => Entries.ToList();
    public CatalogEntry? FindByFingerprint(string fingerprint) => Entries.FirstOrDefault(e => e.Fingerprint == fingerprint);
    public void Add(CatalogEntry entry) => Entries.Add(entry);
    public string ExportCsv() => "Nome;Data\n" + string.Join("\n", Entries.Select(e => e.Name));
}

public sealed class FakeSettingsStore : ISettingsStore
{
    public AppSettings Stored { get; set; } = new();
    public int SaveCount { get; private set; }
    public bool LoadThrows { get; set; }
    public AppSettings Load() => LoadThrows ? throw new IOException("corrupted") : SettingsService.Clone(Stored);
    public void Save(AppSettings settings) { SaveCount++; Stored = SettingsService.Clone(settings); }
}

public sealed class FakeFs : IDestinationFileSystem
{
    public HashSet<string> Folders { get; } = new(StringComparer.OrdinalIgnoreCase) { @"E:\", @"C:\", @"E:\Videos" };
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Deleted { get; } = new();
    public Dictionary<string, DestinationInfo> Roots { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"E:\"] = new(@"E:\", "KINGSTON", "exFAT", 20_000_000_000, 30_000_000_000, true, false, false),
        [@"C:\"] = new(@"C:\", "Windows", "NTFS", 8_000_000_000, 500_000_000_000, false, false, false),
    };

    public DestinationInfo? GetInfo(string path) => Roots.TryGetValue(Path.GetPathRoot(path) ?? "", out var i) ? i : null;
    public bool DirectoryExists(string path) => Folders.Contains(path.Length > 3 ? path.TrimEnd('\\') : path);
    public bool FileExists(string path) => Files.Contains(path);
    public void CreateDirectory(string path) => Folders.Add(path);
    public Stream Create(string path) => new MemoryStream();
    public Stream OpenRead(string path) => new MemoryStream();
    public void FlushToDisk(Stream stream) { }
    public void Move(string source, string destination) { }
    public void Delete(string path) { Deleted.Add(path); Files.Remove(path); }
    /// <summary>Folders the app asked to remove "if empty", in call order (the fake removes the folder when no known file is inside).</summary>
    public List<string> EmptyFolderRequests { get; } = new();
    public void DeleteDirectoryIfEmpty(string path)
    {
        EmptyFolderRequests.Add(path);
        var prefix = path.TrimEnd('\\') + "\\";
        if (!Files.Any(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) && !Folders.Any(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            Folders.Remove(path);
    }
    public long GetFileSize(string path) => 0;
    public void SetTimes(string path, DateTime modifiedUtc) { }
    public bool ProbeWritable(string directory, out string? error) { error = null; return true; }
    public string? ReadAllText(string path) => null;
    public void WriteAllTextAtomic(string path, string content) { }
}

public sealed class FakeValidator : IDestinationValidator
{
    public Func<string, string, IReadOnlyList<DiscEntry>, string, IReadOnlyList<ValidationIssue>>? Rule { get; set; }
    public List<(string Root, string Folder, int EntryCount)> Calls { get; } = new();

    public IReadOnlyList<ValidationIssue> Validate(string destinationRoot, string folderName, IReadOnlyList<DiscEntry> entries, string opticalDriveLetter)
    {
        Calls.Add((destinationRoot, folderName, entries.Count));
        return Rule?.Invoke(destinationRoot, folderName, entries, opticalDriveLetter) ?? Array.Empty<ValidationIssue>();
    }
}

public sealed class FakeProbe : IMediaProbe
{
    public bool Available { get; set; } = true;
    public bool IsAvailable => Available;
    public List<string> Probed { get; } = new();
    public Func<string, CancellationToken, Task<VideoProbe?>>? Handler { get; set; }

    public async Task<VideoProbe?> ProbeAsync(string fullPath, CancellationToken ct)
    {
        lock (Probed) Probed.Add(fullPath);
        if (Handler != null) return await Handler(fullPath, ct);
        return new VideoProbe(TimeSpan.FromMinutes(10), 720, 480, 29.97, "mpeg2video", "ac3", 6_000_000, true);
    }

    public Task<byte[]?> ThumbnailAsync(string fullPath, TimeSpan at, CancellationToken ct) => Task.FromResult<byte[]?>(null);
}

public sealed class FakeDiagnostics : IDriveDiagnostics
{
    public List<DiagnosticCheck> Checks { get; } = new();
    public Exception? Throws { get; set; }
    public bool LastFullScan { get; private set; }
    /// <summary>Value of IncludeTrayTest at the time of the last run (the tray test ejects the disc).</summary>
    public bool LastIncludeTrayTest { get; private set; }
    public bool IncludeTrayTest { get; set; }

    public Task<IReadOnlyList<DiagnosticCheck>> RunAsync(string letter, bool fullSurfaceScan, IProgress<DiagnosticCheck> progress, CancellationToken ct)
    {
        LastFullScan = fullSurfaceScan;
        LastIncludeTrayTest = IncludeTrayTest;
        if (Throws != null) throw Throws;
        foreach (var c in Checks) progress.Report(c);
        return Task.FromResult<IReadOnlyList<DiagnosticCheck>>(Checks.ToList());
    }
}

/// <summary>Fixed clock; Delay never completes on its own (so background loops stay idle in tests).</summary>
public sealed class FakeClock : IClock
{
    public DateTime Now { get; set; } = new(2026, 10, 12, 15, 0, 0, DateTimeKind.Utc);
    public DateTime UtcNow => Now;
    public long ElapsedMs => (long)(Now - new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
    public Task Delay(TimeSpan delay, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
}

public sealed class FakeLister : IDestinationDriveLister
{
    public List<string> Roots { get; } = new() { @"C:\", @"E:\" };
    public IReadOnlyList<string> ListRoots() => Roots.ToList();
}

public sealed class FakeTheme : IThemeService
{
    public List<string> Applied { get; } = new();
    public void Apply(string theme) => Applied.Add(theme);
}

public sealed class FakeShell : IShellService
{
    public List<string> OpenedFolders { get; } = new();
    public bool OpenFolderResult { get; set; } = true;
    public string? PickFolderResult { get; set; }
    public string? SaveFileResult { get; set; }
    public List<(string Path, string Text)> Written { get; } = new();
    public string? Clipboard { get; private set; }
    public List<SoundKind> Sounds { get; } = new();
    public int LogOpened { get; private set; }

    public bool OpenFolder(string path) { OpenedFolders.Add(path); return OpenFolderResult; }
    public string? PickFolder(string? initialPath) => PickFolderResult;
    public string? PickSaveFile(string suggestedName, string filter) => SaveFileResult;
    public Task WriteAllTextAsync(string path, string text) { Written.Add((path, text)); return Task.CompletedTask; }
    public void CopyToClipboard(string text) => Clipboard = text;
    public void PlaySound(SoundKind kind) => Sounds.Add(kind);
    public void OpenLog() => LogOpened++;
}

/// <summary>Scripted dialogs: every shown request is recorded; the responder decides which button is pressed.</summary>
public sealed class FakeDialogs : IDialogService
{
    public List<DialogRequest> Shown { get; } = new();
    public List<NamePromptRequest> NamePrompts { get; } = new();
    /// <summary>Default answer: the button flagged IsCancel, or "close".</summary>
    public Func<DialogRequest, string>? Responder { get; set; }
    public Queue<string> Script { get; } = new();
    public Func<NamePromptRequest, NamePromptResult?>? NameResponder { get; set; }

    public Task<DialogResponse> ShowAsync(DialogRequest request)
    {
        Shown.Add(request);
        string key;
        if (Script.Count > 0) key = Script.Dequeue();
        else if (Responder != null) key = Responder(request);
        else key = request.Buttons.FirstOrDefault(b => b.IsCancel)?.Key ?? "close";
        return Task.FromResult(new DialogResponse(key));
    }

    public Task<NamePromptResult?> PromptNameAsync(NamePromptRequest request)
    {
        NamePrompts.Add(request);
        return Task.FromResult(NameResponder != null ? NameResponder(request) : new NamePromptResult(request.SuggestedName, false));
    }

    public bool WasShown(string titleFragment) => Shown.Any(d => d.Title.Contains(titleFragment, StringComparison.OrdinalIgnoreCase));
}

public sealed class FailingCatalog : ICatalogStore
{
    public string? LoadWarning => null;
    public IReadOnlyList<CatalogEntry> GetAll() => Array.Empty<CatalogEntry>();
    public CatalogEntry? FindByFingerprint(string fingerprint) => null;
    public void Add(CatalogEntry entry) => throw new IOException("disk full");
    public string ExportCsv() => "";
}

