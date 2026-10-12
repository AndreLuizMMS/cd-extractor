using Serilog;

namespace MediaRescue.App.Infrastructure;

/// <summary>Serilog file sink: %LOCALAPPDATA%\MediaRescue\logs, 10 files x 5 MB. Never logs file contents.</summary>
public static class AppLogging
{
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaRescue", "logs");

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(
                    Path.Combine(LogDirectory, "mediarescue-.log"),
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: 5 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: 10,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
        }
        catch (Exception)
        {
            // Logging is best effort: the app must still start if the folder is not writable.
            Log.Logger = new LoggerConfiguration().CreateLogger();
        }
    }

    public static string? LatestLogFile()
    {
        try
        {
            return Directory.Exists(LogDirectory)
                ? new DirectoryInfo(LogDirectory).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName
                : null;
        }
        catch (Exception) { return null; }
    }

    public static void Shutdown()
    {
        try { Log.CloseAndFlush(); } catch (Exception) { /* exiting */ }
    }
}
