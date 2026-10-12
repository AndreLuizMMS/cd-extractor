using MediaRescue.App.Infrastructure;
using MediaRescue.Core;

namespace MediaRescue.App.Services;

/// <summary>The object graph handed to the ViewModels (built by <see cref="CompositionRoot"/>).</summary>
public sealed class AppServices : IDisposable
{
    public required IOpticalDriveService Drives { get; init; }
    public required IExtractionEngine Engine { get; init; }
    public required ICatalogStore Catalog { get; init; }
    public required ISettingsStore SettingsStore { get; init; }
    public required IDestinationValidator Validator { get; init; }
    public required IDestinationFileSystem Fs { get; init; }
    public required IMediaProbe Probe { get; init; }
    public required IDriveDiagnostics Diagnostics { get; init; }
    public required IClock Clock { get; init; }
    public required IDestinationDriveLister DestinationDrives { get; init; }
    /// <summary>Starts keeping the computer awake; dispose the result when the copy ends.</summary>
    public required Func<IDisposable> KeepAwake { get; init; }
    /// <summary>MP4 converter (RF-11). Optional: defaults to an unavailable converter.</summary>
    public IMp4Converter Mp4 { get; set; } = NullMp4Converter.Instance;

    public void Dispose()
    {
        try { Drives.Dispose(); } catch { /* shutting down */ }
    }
}

/// <summary>Enumerates destination candidates with <see cref="DriveInfo"/> (blocking calls: run off the UI thread).</summary>
public sealed class DriveInfoDestinationLister : IDestinationDriveLister
{
    public IReadOnlyList<string> ListRoots()
    {
        var roots = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
                    roots.Add(d.Name);
            }
        }
        catch { /* enumerating drives can fail while devices change */ }
        return roots;
    }
}
