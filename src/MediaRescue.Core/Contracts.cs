// Shared contracts between Core, Windows and App. Changing a signature here affects all three
// projects: add members if needed, but do not rename or remove existing ones.
using System.Security.Cryptography;
using System.Text;

namespace MediaRescue.Core;

// ---------- Models ----------

public enum DriveState { NoMedia, TrayOpen, Loading, Ready, Blank, Unfinalized, Unreadable, Protected, AudioCd }
public enum LogicalFormat { Unknown, Empty, DataVideo, Mixed, Vcd, Svcd, DvdVideo, DvdVr, Avchd, AudioCd }
public enum EntryKind { Video, Image, Audio, Other }
public enum FileStatus { Pending, Intact, Recovered, Partial, Failed, Skipped, DestinationFailure }
public enum ExtractionPhase { Preparing, Copying, SecondPass, Verifying, Paused, WaitingForDrive, WaitingForDestination, DestinationFull, Completed, Cancelled }
public enum OverallStatus { Intact, Recovered, Partial, Failed, Cancelled }

/// <param name="Letter">Drive root without trailing slash, e.g. "D:".</param>
public sealed record OpticalDrive(string Letter, string Vendor, string Model, string Firmware, string BusType, bool IsVirtual);

public sealed record VideoProbe(TimeSpan Duration, int Width, int Height, double Fps, string VideoCodec, string AudioCodec, long Bitrate, bool Interlaced);

public sealed record DiscEntry
{
    /// <summary>Path relative to the disc root, backslash separated, no leading slash.</summary>
    public required string RelativePath { get; init; }
    public long Size { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public bool IsDirectory { get; init; }
    public EntryKind Kind { get; init; } = EntryKind.Other;
    /// <summary>DVD-Video title set this VOB belongs to (e.g. "VTS_01"); null otherwise.</summary>
    public string? TitleGroup { get; init; }
    /// <summary>Set when the entry (usually a directory) could not be enumerated.</summary>
    public string? Error { get; init; }
    /// <summary>
    /// Optional physical position on the disc (e.g. start sector). When every selected file has a value, the
    /// extraction engine copies in this order to minimise seeking (RF-05 item 4); otherwise it keeps directory order.
    /// </summary>
    public long? PhysicalOffset { get; init; }
}

public sealed record DiscInfo
{
    public string VolumeLabel { get; init; } = "";
    public string VolumeSerial { get; init; } = "";
    /// <summary>"CD-ROM", "CD-R", "DVD-R", ... or null when unavailable.</summary>
    public string? PhysicalType { get; init; }
    /// <summary>"Finalizado", "Aberto", "Vazio" or null when unavailable.</summary>
    public string? MediaState { get; init; }
    public int? Sessions { get; init; }
    public int? Tracks { get; init; }
    public string FileSystem { get; init; } = "";
    public long UsedBytes { get; init; }
    public long? CapacityBytes { get; init; }
    public DateTime? RecordedAtUtc { get; init; }
    public LogicalFormat LogicalFormat { get; init; }
    public bool IsCopyProtected { get; init; }
    /// <summary>
    /// True when the table of contents also lists audio tracks (E-08: mixed-mode / CD-Extra disc, or an audio CD).
    /// Only the data part is copied; the UI warns about it. False when unknown.
    /// </summary>
    public bool HasAudioTracks { get; init; }
    public IReadOnlyList<DiscEntry> Entries { get; init; } = Array.Empty<DiscEntry>();

    /// <summary>Stable identity of the disc (RF-08).</summary>
    public string Fingerprint => _fingerprint ??= ComputeFingerprint();
    private string? _fingerprint;

    private string ComputeFingerprint()
    {
        var sb = new StringBuilder();
        sb.Append(VolumeSerial).Append('\n').Append(VolumeLabel).Append('\n').Append(UsedBytes).Append('\n');
        foreach (var e in Entries.Where(e => !e.IsDirectory).OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase))
            sb.Append(e.RelativePath.ToUpperInvariant()).Append('|').Append(e.Size).Append('|').Append(e.ModifiedUtc.Ticks).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}

public sealed record ByteRange(long Offset, long Length);

public sealed record FileResult
{
    public required string RelativePath { get; init; }
    /// <summary>Path actually written, relative to the disc folder (after sanitization).</summary>
    public required string WrittenPath { get; init; }
    public FileStatus Status { get; init; }
    public long BytesTotal { get; init; }
    public long BytesLost { get; init; }
    public IReadOnlyList<ByteRange> BadRanges { get; init; } = Array.Empty<ByteRange>();
    public string? Sha256 { get; init; }
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
}

public sealed class ExtractionOptions
{
    public int RetriesPerSector { get; set; } = 3;
    public TimeSpan BlockTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan SecondPassBudget { get; set; } = TimeSpan.FromMinutes(20);
    public bool ReduceSpeedOnError { get; set; } = true;
    public bool VerifyAfterCopy { get; set; } = true;
}

public sealed record ExtractionRequest(OpticalDrive Drive, DiscInfo Disc, string DestinationRoot, string FolderName,
    IReadOnlyList<DiscEntry> Entries, ExtractionOptions Options);

public sealed record ExtractionProgress
{
    public ExtractionPhase Phase { get; init; }
    public string? CurrentFile { get; init; }
    public int FileIndex { get; init; }
    public int FileCount { get; init; }
    public long FileBytesDone { get; init; }
    public long FileBytesTotal { get; init; }
    public long TotalBytesDone { get; init; }
    public long TotalBytesTotal { get; init; }
    public double BytesPerSecond { get; init; }
    /// <summary>Null while unknown ("calculando…") or during error recovery ("indeterminado").</summary>
    public TimeSpan? Eta { get; init; }
    public TimeSpan Elapsed { get; init; }
    public int FilesIntact { get; init; }
    public int FilesWithErrors { get; init; }
    public long BytesLost { get; init; }
    /// <summary>Human readable pt-BR message for the log panel; may be null.</summary>
    public string? Message { get; init; }
}

public sealed record ExtractionResult(string DiscFolder, OverallStatus Status, IReadOnlyList<FileResult> Files, TimeSpan Duration);

/// <summary>Names of the bookkeeping files the engine keeps inside each disc folder (RF-08 / RF-10).</summary>
public static class DiscFolderFiles
{
    /// <summary>Resume state, present while a job is unfinished.</summary>
    public const string JobState = ".mediarescue-job.json";
    public const string Manifest = "mediarescue-manifest.json";
    public const string ErrorReadme = "LEIA-ME - ERROS.txt";
    public const string BackupSuffix = ".bak";
}

public sealed record CatalogEntry
{
    public required string Fingerprint { get; init; }
    public required string Name { get; init; }
    public DateTime ExtractedAtUtc { get; init; }
    public required string DestinationPath { get; init; }
    public long TotalBytes { get; init; }
    public long BytesLost { get; init; }
    public int FileCount { get; init; }
    public OverallStatus Status { get; init; }
}

public sealed record DestinationInfo(string Root, string VolumeLabel, string FileSystem, long FreeBytes, long TotalBytes,
    bool IsRemovable, bool IsNetworkOrCloud, bool IsOptical);

public enum ValidationSeverity { Ok, Warning, Blocking }
public sealed record ValidationIssue(ValidationSeverity Severity, string Message, string? RelativePath = null);

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string? LastDestination { get; set; }
    public bool EjectWhenDone { get; set; } = true;
    public bool ConvertToMp4 { get; set; }
    public bool BatchAskName { get; set; } = true;
    public string Theme { get; set; } = "System";
    public ExtractionOptions Extraction { get; set; } = new();
}

// ---------- Errors ----------

public enum ReadErrorKind { BadSector, DeviceGone, Other }

public sealed class DiscReadException : IOException
{
    public ReadErrorKind Kind { get; }
    public int Win32Code { get; }
    public DiscReadException(ReadErrorKind kind, int win32Code, string message, Exception? inner = null) : base(message, inner)
    { Kind = kind; Win32Code = win32Code; }
}

public enum DestinationErrorKind { DiskFull, DeviceGone, AccessDenied, Other }

public sealed class DestinationException : IOException
{
    public DestinationErrorKind Kind { get; }
    public DestinationException(DestinationErrorKind kind, string message, Exception? inner = null) : base(message, inner) { Kind = kind; }
}

// ---------- Hardware / OS abstractions (implemented in MediaRescue.Windows, faked in tests) ----------

public interface IOpticalDriveService : IDisposable
{
    IReadOnlyList<OpticalDrive> GetDrives();
    /// <summary>Fast, non-throwing state query.</summary>
    DriveState GetState(string letter);
    /// <summary>Full analysis (physical info + file listing, no video probing). Null when no readable media.</summary>
    Task<DiscInfo?> AnalyzeAsync(string letter, CancellationToken ct);
    /// <summary>Raised (on any thread) when a drive is added/removed or media changes.</summary>
    event EventHandler? Changed;
    bool Eject(string letter);
    bool CloseTray(string letter);
    bool LockMedia(string letter, bool locked);
    /// <summary>kbPerSec = 0 restores maximum speed. Returns false when unsupported.</summary>
    bool SetReadSpeed(string letter, int kbPerSec);
}

public interface IDiscReader
{
    /// <summary>Opens a file on the disc for random access reads.</summary>
    IDiscFile Open(string driveLetter, string relativePath);
}

public interface IDiscFile : IDisposable
{
    long Length { get; }
    /// <summary>Preferred alignment for offsets/lengths (2048 on optical media; 1 when buffered).</summary>
    int SectorSize { get; }
    /// <summary>
    /// Reads up to buffer.Length bytes at offset; returns fewer only at end of file.
    /// Throws <see cref="DiscReadException"/> on failure or when <paramref name="timeout"/> elapses (Kind = BadSector).
    /// </summary>
    Task<int> ReadAsync(long offset, Memory<byte> buffer, TimeSpan timeout, CancellationToken ct);
}

/// <summary>All paths are absolute. Implementations translate OS errors into <see cref="DestinationException"/>.</summary>
public interface IDestinationFileSystem
{
    DestinationInfo? GetInfo(string path);
    bool DirectoryExists(string path);
    bool FileExists(string path);
    void CreateDirectory(string path);
    /// <summary>Creates (or truncates) a file for writing. The stream SHOULD be seekable: the engine seeks back to fill
    /// recovered sectors during the second pass (without seeking, unrecovered ranges simply stay zero-filled).</summary>
    Stream Create(string path);
    Stream OpenRead(string path);
    /// <summary>Flushes OS buffers to the device for a stream returned by <see cref="Create"/>.</summary>
    void FlushToDisk(Stream stream);
    /// <summary>Renames a file. MUST NOT overwrite: when <paramref name="destination"/> exists it throws and changes nothing.</summary>
    void Move(string source, string destination);
    void Delete(string path);
    /// <summary>Removes a directory only when it exists and is completely empty (no files, no sub-directories). Never throws and
    /// never deletes anything else; used to leave no empty disc folder behind after "cancel and discard".
    /// Default: does nothing, so existing implementations and fakes keep compiling.</summary>
    void DeleteDirectoryIfEmpty(string path) { }
    long GetFileSize(string path);
    void SetTimes(string path, DateTime modifiedUtc);
    /// <summary>Creates and deletes a small file to prove the directory is writable.</summary>
    bool ProbeWritable(string directory, out string? error);
    string? ReadAllText(string path);
    /// <summary>Atomic replace (temp file + rename), keeping a .bak of the previous content.</summary>
    void WriteAllTextAtomic(string path, string content);
}

public interface IClock
{
    DateTime UtcNow { get; }
    /// <summary>Monotonic milliseconds.</summary>
    long ElapsedMs { get; }
    Task Delay(TimeSpan delay, CancellationToken ct);
}

public interface IMediaProbe
{
    bool IsAvailable { get; }
    Task<VideoProbe?> ProbeAsync(string fullPath, CancellationToken ct);
    /// <summary>PNG/JPEG bytes of one frame, or null.</summary>
    Task<byte[]?> ThumbnailAsync(string fullPath, TimeSpan at, CancellationToken ct);
}

// ---------- Core services (implemented in MediaRescue.Core, consumed by App) ----------

public sealed class ExtractionControl
{
    private volatile bool _paused;
    private volatile bool _skip;
    private long _extraBudgetMs;
    public bool IsPaused => _paused;
    public void Pause() => _paused = true;
    public void Resume() => _paused = false;
    public void SkipCurrentFile() => _skip = true;
    public bool ConsumeSkip() { if (!_skip) return false; _skip = false; return true; }
    public void ExtendSecondPass(TimeSpan extra) => Interlocked.Add(ref _extraBudgetMs, (long)extra.TotalMilliseconds);
    public TimeSpan ExtraSecondPassBudget => TimeSpan.FromMilliseconds(Interlocked.Read(ref _extraBudgetMs));
}

public interface IExtractionEngine
{
    /// <summary>
    /// Copies the requested entries into DestinationRoot\FolderName. Never throws for per-file problems;
    /// cancellation returns a result with Status = Cancelled (keepPartial semantics decided by caller afterwards).
    /// When the drive or destination disappears, reports WaitingForDrive / WaitingForDestination / DestinationFull
    /// and waits until the condition clears, the caller cancels, or a different disc is inserted (then fails the job).
    /// </summary>
    Task<ExtractionResult> RunAsync(ExtractionRequest request, IProgress<ExtractionProgress> progress, ExtractionControl control, CancellationToken ct);
    /// <summary>True when an interrupted job for this disc exists in the destination folder (RF-10).</summary>
    bool CanResume(DiscInfo disc, string destinationRoot, string folderName);
}

public interface ICatalogStore
{
    IReadOnlyList<CatalogEntry> GetAll();
    CatalogEntry? FindByFingerprint(string fingerprint);
    void Add(CatalogEntry entry);
    string ExportCsv();
    /// <summary>Non-null when the catalog had to be recovered from backup or reset.</summary>
    string? LoadWarning { get; }
}

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}

public interface IDestinationValidator
{
    /// <summary>RF-03: existence, writability, space, FAT32 4 GB limit, cloud/network, not the optical drive.</summary>
    IReadOnlyList<ValidationIssue> Validate(string destinationRoot, string folderName, IReadOnlyList<DiscEntry> entries, string opticalDriveLetter);
}

// ---------- Diagnostics (implemented in MediaRescue.Windows, RF-07) ----------

public enum CheckOutcome { Pass, Warning, Fail, NotSupported, Skipped }

/// <param name="Tip">Plain-language pt-BR advice when the check did not pass; null otherwise.</param>
public sealed record DiagnosticCheck(string Id, string Title, CheckOutcome Outcome, string Detail, string? Tip = null);

public interface IDriveDiagnostics
{
    /// <summary>
    /// When true, check 3 ejects the tray and waits for it to close, which throws a disc out of the drive. Must be opt-in:
    /// implementations default to false and the UI only sets it when the user asks for the tray test explicitly.
    /// Implementations without a tray test ignore it.
    /// </summary>
    bool IncludeTrayTest { get => false; set { } }

    /// <summary>Runs the RF-07 checks in order, reporting each as it completes.</summary>
    Task<IReadOnlyList<DiagnosticCheck>> RunAsync(string letter, bool fullSurfaceScan, IProgress<DiagnosticCheck> progress, CancellationToken ct);
}

// ---------- MP4 conversion (RF-11; planned in Core, run by MediaRescue.Windows, driven by the App) ----------

public enum Mp4JobStatus { Converted, Failed, Cancelled, Skipped }

/// <summary>Outcome of one conversion job (one DVD title set, or one video file).</summary>
public sealed record Mp4JobResult
{
    /// <summary>Display name (pt-BR friendly): a title like "Título 01 (VTS_01)" or the relative path of the file.</summary>
    public required string Name { get; init; }
    /// <summary>Source files, relative to the disc folder (the copies in the destination, never the disc).</summary>
    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();
    /// <summary>Output, relative to the disc folder (e.g. "MP4\Título 01 (VTS_01).mp4"); null when no output was kept.</summary>
    public string? OutputPath { get; init; }
    public Mp4JobStatus Status { get; init; }
    /// <summary>True when the source copy had unrecovered ranges (Partial) or missing parts: the MP4 is derived from damaged data.</summary>
    public bool FromDamagedSource { get; init; }
    /// <summary>ffmpeg encoder that produced the output (e.g. "h264_mf"); null when none ran.</summary>
    public string? Encoder { get; init; }
    public long OutputBytes { get; init; }
    public TimeSpan SourceDuration { get; init; }
    public TimeSpan OutputDuration { get; init; }
    /// <summary>pt-BR text: the failure reason, or a warning about a converted file. Null when there is nothing to say.</summary>
    public string? Message { get; init; }
}

public sealed record Mp4ConversionResult
{
    /// <summary>Preferred working encoder found at run time ("libx264", "h264_mf", "libopenh264" or "mpeg4"); empty when none was selected.</summary>
    public string Encoder { get; init; } = "";
    public IReadOnlyList<Mp4JobResult> Jobs { get; init; } = Array.Empty<Mp4JobResult>();
    public bool WasCancelled { get; init; }
    /// <summary>pt-BR text when the whole conversion could not run (no tools, no space, no encoder). Null otherwise.</summary>
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }

    public int ConvertedCount => Jobs.Count(j => j.Status == Mp4JobStatus.Converted);
    public int FailedCount => Jobs.Count(j => j.Status == Mp4JobStatus.Failed);
}

public sealed record Mp4ConversionProgress
{
    /// <summary>1-based index of the job being converted.</summary>
    public int JobIndex { get; init; }
    public int JobCount { get; init; }
    public string CurrentName { get; init; } = "";
    /// <summary>0..1 for the current job; null when its duration is unknown.</summary>
    public double? JobFraction { get; init; }
    /// <summary>0..1 over all jobs (weighted by source size).</summary>
    public double OverallFraction { get; init; }
    public TimeSpan Elapsed { get; init; }
    public TimeSpan? Eta { get; init; }
    /// <summary>Human readable pt-BR message for the log panel; may be null.</summary>
    public string? Message { get; init; }
}

public interface IMp4Converter
{
    /// <summary>True when the conversion tools (ffmpeg/ffprobe) are present.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Converts the copied videos listed in <paramref name="files"/> (paths relative to <paramref name="discFolder"/>) into
    /// <c>discFolder\MP4\</c>. Never touches the originals, never throws for per-file problems (they become failed jobs);
    /// cancellation returns a result with <c>WasCancelled</c> and leaves no incomplete output.
    /// </summary>
    Task<Mp4ConversionResult> ConvertAsync(string discFolder, IReadOnlyList<FileResult> files, IProgress<Mp4ConversionProgress> progress, CancellationToken ct);
}
