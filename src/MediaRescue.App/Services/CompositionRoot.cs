using MediaRescue.App.Demo;
using MediaRescue.App.Infrastructure;

namespace MediaRescue.App.Services;

/// <summary>Builds the object graph. Real services live behind <c>#if !DEMO_ONLY</c> until the other layers exist.</summary>
public static class CompositionRoot
{
    /// <param name="dataDirectory">Where catalog.json and settings.json live; null = %LOCALAPPDATA%\MediaRescue. Tests pass a temp folder.</param>
    public static AppServices Create(StartupOptions options, string? dataDirectory = null)
    {
#if DEMO_ONLY
        return DemoServices.Create(options);
#else
        return options.Demo ? DemoServices.Create(options) : CreateReal(dataDirectory);
#endif
    }

#if !DEMO_ONLY
    private static AppServices CreateReal(string? dataDirectory)
    {
        var appData = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaRescue");
        Directory.CreateDirectory(appData);

        var drives = new MediaRescue.Windows.OpticalDriveService();
        var reader = new MediaRescue.Windows.DiscReader();
        var fs = new MediaRescue.Windows.WindowsDestinationFileSystem();
        var toolsDir = Path.Combine(AppContext.BaseDirectory, "tools");
        var probe = new MediaRescue.Windows.FfprobeMediaProbe(toolsDir);
        var mp4 = new MediaRescue.Windows.FfmpegMp4Converter(toolsDir, probe);
        var diagnostics = new MediaRescue.Windows.DriveDiagnostics(drives, reader);
        var clock = new MediaRescue.Core.SystemClock();
        var engine = new MediaRescue.Core.ExtractionEngine(reader, fs, drives, clock);
        var catalog = new MediaRescue.Core.CatalogStore(fs, Path.Combine(appData, "catalog.json"));
        var settings = new MediaRescue.Core.SettingsStore(fs, Path.Combine(appData, "settings.json"));
        var validator = new MediaRescue.Core.DestinationValidator(fs);

        return new AppServices
        {
            Drives = drives,
            Engine = engine,
            Catalog = catalog,
            SettingsStore = settings,
            Validator = validator,
            Fs = fs,
            Probe = probe,
            Mp4 = mp4,
            Diagnostics = diagnostics,
            Clock = clock,
            DestinationDrives = new DriveInfoDestinationLister(),
            // PowerGuard is IDisposable and keeps the system awake from construction until Dispose (assumed parameterless ctor).
            KeepAwake = () => new MediaRescue.Windows.PowerGuard(),
        };
    }
#endif
}
