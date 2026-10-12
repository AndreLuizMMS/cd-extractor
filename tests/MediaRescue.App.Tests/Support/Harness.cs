using MediaRescue.App.Infrastructure;
using MediaRescue.App.Services;
using MediaRescue.App.ViewModels;
using MediaRescue.Core;

namespace MediaRescue.App.Tests.Support;

public static class TestDiscs
{
    private static readonly DateTime T = new(2009, 12, 12, 18, 0, 0, DateTimeKind.Utc);

    public static DiscEntry File(string path, long size = 1_000_000, string? group = null) => new()
    {
        RelativePath = path, Size = size, ModifiedUtc = T, Kind = DiscClassifier.KindOf(path), TitleGroup = group,
    };

    public static DiscEntry Dir(string path) => new() { RelativePath = path, IsDirectory = true, ModifiedUtc = T };

    public static DiscInfo Dvd(string label = "FORMATURA", string serial = "AAAA-0001", bool isProtected = false, LogicalFormat format = LogicalFormat.DvdVideo,
        IReadOnlyList<DiscEntry>? entries = null, int? sessions = 1)
    {
        entries ??= new[]
        {
            Dir("VIDEO_TS"),
            File(@"VIDEO_TS\VIDEO_TS.IFO", 20_000),
            File(@"VIDEO_TS\VTS_01_0.VOB", 3_000_000),
            File(@"VIDEO_TS\VTS_01_1.VOB", 600_000_000, "VTS_01"),
            File(@"VIDEO_TS\VTS_01_2.VOB", 400_000_000, "VTS_01"),
            Dir("Fotos"),
            File(@"Fotos\a.jpg", 2_000_000),
            File(@"Fotos\b.jpg", 2_000_000),
        };
        return new DiscInfo
        {
            VolumeLabel = label, VolumeSerial = serial, PhysicalType = "DVD-R", MediaState = "Finalizado", Sessions = sessions, Tracks = 1,
            FileSystem = "UDF", UsedBytes = entries.Where(e => !e.IsDirectory).Sum(e => e.Size), CapacityBytes = 4_700_000_000,
            RecordedAtUtc = T, LogicalFormat = format, IsCopyProtected = isProtected, Entries = entries,
        };
    }
}

public sealed class CallbackDisposable : IDisposable
{
    private readonly Action _onDispose;
    public CallbackDisposable(Action onDispose) => _onDispose = onDispose;
    public void Dispose() => _onDispose();
}

/// <summary>Builds the whole ViewModel graph on fakes. Dispatching is inline, so no window is needed.</summary>
public sealed class Harness
{
    public FakeDrives Drives { get; } = new();
    public FakeEngine Engine { get; } = new();
    public FakeCatalog Catalog { get; } = new();
    public FakeSettingsStore SettingsStore { get; } = new();
    public FakeFs Fs { get; } = new();
    public FakeValidator Validator { get; } = new();
    public FakeProbe Probe { get; } = new();
    public FakeDiagnostics Diagnostics { get; } = new();
    public FakeClock Clock { get; } = new();
    public FakeLister Lister { get; } = new();
    public FakeDialogs Dialogs { get; } = new();
    public FakeShell Shell { get; } = new();
    public FakeTheme Theme { get; } = new();
    public int AwakeBegun { get; private set; }
    public int AwakeReleased { get; private set; }
    public AppServices Services { get; }
    public MainViewModel Main { get; }

    public ExtractViewModel Extract => Main.Extract;
    public BatchViewModel Batch => Main.Extract.Batch;

    public Harness()
    {
        Services = new AppServices
        {
            Drives = Drives, Engine = Engine, Catalog = Catalog, SettingsStore = SettingsStore, Validator = Validator, Fs = Fs,
            Probe = Probe, Diagnostics = Diagnostics, Clock = Clock, DestinationDrives = Lister, KeepAwake = () => { AwakeBegun++; return new CallbackDisposable(() => AwakeReleased++); },
        };
        Main = new MainViewModel(Services, new ImmediateUiDispatcher(), Dialogs, Shell, Theme, StartupOptions.Parse(Array.Empty<string>()));
        Extract.ValidationDelay = TimeSpan.Zero;
        Extract.AutoValidate = false; // deterministic: tests call ValidateNowAsync explicitly
    }

    /// <summary>Loads settings and reads the readers once (like app start, without the polling loop).</summary>
    public async Task StartAsync(string? destination = @"E:\Videos")
    {
        await Main.Settings.LoadAsync();
        await Extract.RefreshQuickDrivesAsync();
        if (destination != null) Extract.DestinationRoot = destination;
        await Extract.RefreshAsync(true);
    }

    /// <summary>Puts a disc in D: and waits for the analysis and validation to finish.</summary>
    public async Task InsertAsync(DiscInfo disc, string letter = "D:", DriveState state = DriveState.Ready)
    {
        Drives.Discs[letter] = disc;
        Drives.States[letter] = state;
        await Extract.RefreshAsync();
        await Extract.ValidateNowAsync();
    }

    public async Task RemoveDiscAsync(string letter = "D:")
    {
        Drives.States[letter] = DriveState.NoMedia;
        Drives.Discs[letter] = null;
        await Extract.RefreshAsync();
    }

    public static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(10);
        }
    }
}


