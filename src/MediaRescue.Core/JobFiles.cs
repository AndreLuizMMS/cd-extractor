using System.Reflection;
using System.Text.Json;

namespace MediaRescue.Core;

/// <summary>Names and (de)serialization of the files the engine keeps in each disc folder.</summary>
internal static class JobFiles
{
    public const int SchemaVersion = 1;
    public const string StateName = DiscFolderFiles.JobState;
    public const string ManifestName = DiscFolderFiles.Manifest;
    public const string ReadmeName = DiscFolderFiles.ErrorReadme;
    public const string PartialSuffix = ".partial";

    public static string AppVersion { get; } =
        typeof(JobFiles).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(JobFiles).Assembly.GetName().Version?.ToString() ?? "0.0";

    /// <summary>Loads the resume state from the main file or its ".bak"; null when absent or unreadable.</summary>
    public static JobStateDto? LoadState(IDestinationFileSystem fs, string discFolder)
    {
        var path = Path.Combine(discFolder, StateName);
        return RecoverableJsonFile.Load(fs, path, "O estado do trabalho", Parse, out _);
    }

    private static JobStateDto Parse(string text)
    {
        var dto = JsonSerializer.Deserialize<JobStateDto>(text, JsonDefaults.Options) ?? throw new JsonException("empty state");
        if (string.IsNullOrEmpty(dto.Fingerprint) || dto.Files == null) throw new JsonException("incomplete state");
        return dto;
    }
}

internal sealed class RangeDto
{
    public long Offset { get; set; }
    public long Length { get; set; }
}

internal sealed class JobFileDto
{
    public string RelativePath { get; set; } = "";
    public string WrittenPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    /// <summary>True only after the file was renamed to its final name and verified.</summary>
    public bool Completed { get; set; }
    public FileStatus Status { get; set; }
    public long BytesTotal { get; set; }
    public long BytesLost { get; set; }
    public List<RangeDto> BadRanges { get; set; } = new();
    public string? Sha256 { get; set; }
    public string? Error { get; set; }
    public double DurationSeconds { get; set; }
}

internal sealed class JobStateDto
{
    public int SchemaVersion { get; set; } = JobFiles.SchemaVersion;
    public string JobId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string? VolumeLabel { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? AppVersion { get; set; }
    public List<JobFileDto> Files { get; set; } = new();
}

// ---- manifest ----

internal sealed class ManifestDto
{
    public int SchemaVersion { get; set; } = JobFiles.SchemaVersion;
    public string? AppVersion { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public ManifestJob Job { get; set; } = new();
    public ManifestDisc Disc { get; set; } = new();
    public ManifestDrive Drive { get; set; } = new();
    public ManifestTotals Totals { get; set; } = new();
    /// <summary>Original path on the disc to the path actually written (after sanitization).</summary>
    public Dictionary<string, string> PathMap { get; set; } = new();
    public List<ManifestFile> Files { get; set; } = new();
}

internal sealed class ManifestJob
{
    public string JobId { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
    public double DurationSeconds { get; set; }
    public OverallStatus OverallStatus { get; set; }
    public string? Note { get; set; }
}

internal sealed class ManifestDisc
{
    public string Fingerprint { get; set; } = "";
    public string VolumeLabel { get; set; } = "";
    public string VolumeSerial { get; set; } = "";
    public string? PhysicalType { get; set; }
    public string? MediaState { get; set; }
    public string FileSystem { get; set; } = "";
    public LogicalFormat LogicalFormat { get; set; }
    public DateTime? RecordedAtUtc { get; set; }
    public int? Sessions { get; set; }
    public int? Tracks { get; set; }
    public long UsedBytes { get; set; }
}

internal sealed class ManifestDrive
{
    public string Letter { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Model { get; set; } = "";
    public string Firmware { get; set; } = "";
    public string BusType { get; set; } = "";
}

internal sealed class ManifestTotals
{
    public int Files { get; set; }
    public long Bytes { get; set; }
    public long BytesLost { get; set; }
    public int Intact { get; set; }
    public int Recovered { get; set; }
    public int Partial { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public int DestinationFailure { get; set; }
}

internal sealed class ManifestFile
{
    public string OriginalPath { get; set; } = "";
    public string WrittenPath { get; set; } = "";
    public FileStatus Status { get; set; }
    public string StatusLabel { get; set; } = "";
    public long BytesTotal { get; set; }
    public long BytesLost { get; set; }
    public double LostPercent { get; set; }
    public List<RangeDto> BadRanges { get; set; } = new();
    public string? Sha256 { get; set; }
    /// <summary>"original" (hash of the data as stored on the disc) or "recovered-content" (partial files).</summary>
    public string? Sha256Scope { get; set; }
    public string? Error { get; set; }
    public double DurationSeconds { get; set; }
    public DateTime? ModifiedUtc { get; set; }
}
