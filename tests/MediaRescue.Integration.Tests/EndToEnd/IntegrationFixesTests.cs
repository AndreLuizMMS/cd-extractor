using MediaRescue.Core;
using MediaRescue.Integration.Tests.Support;
using MediaRescue.Windows;
using MediaRescue.Windows.Native;

namespace MediaRescue.Integration.Tests.EndToEnd;

/// <summary>Regression tests for the bugs found while wiring the three layers together.</summary>
public sealed class IntegrationFixesTests : IDisposable
{
    private readonly string _tmp = TestEnv.NewTempDir("mr-fix");
    private readonly List<MountedIso> _mounts = new();

    public void Dispose()
    {
        foreach (var m in _mounts) { try { m.Dispose(); } catch { } }
        for (int i = 0; i < 20; i++)
        {
            try { Directory.Delete(_tmp, true); break; } catch { Thread.Sleep(250); }
        }
    }

    // ------------------------------------------------------------------ Move must not overwrite

    [Fact]
    public void Move_ToAnExistingFile_Throws_AndKeepsBothFiles()
    {
        var fs = new WindowsDestinationFileSystem();
        var source = Path.Combine(_tmp, "video.avi.partial");
        var existing = Path.Combine(_tmp, "video.avi");
        File.WriteAllText(source, "novo");
        File.WriteAllText(existing, "do usuario");

        var act = () => fs.Move(source, existing);

        act.Should().Throw<DestinationException>();
        File.ReadAllText(existing).Should().Be("do usuario", "an existing file is never replaced by a rename");
        File.ReadAllText(source).Should().Be("novo");

        // The normal case still works.
        var free = Path.Combine(_tmp, "livre.avi");
        fs.Move(source, free);
        File.ReadAllText(free).Should().Be("novo");
        File.Exists(source).Should().BeFalse();
    }

    // ------------------------------------------------------------------ stale media-lock handle used to hang the engine

    [MountFact]
    public async Task LockMedia_AfterTheDiscWasReplaced_DoesNotHang_AndCanLockTheNewDisc()
    {
        using var svc = new OpticalDriveService();
        var isoA = new IsoSpec { Label = "A" }.AddRandom("a.bin", 100_000, 1).Build(_tmp, "a.iso");
        var isoB = new IsoSpec { Label = "B" }.AddRandom("b.bin", 120_000, 2).Build(_tmp, "b.iso");
        var a = MountedIso.Mount(isoA);
        _mounts.Add(a);
        await DiscHelpers.AnalyzeOrFailAsync(svc, a.Letter);
        svc.LockMedia(a.Letter, true).Should().BeTrue();

        // E-04: the disc is pulled out while locked and a different one shows up on the same letter.
        a.Dismount();
        var b = MountedIso.Mount(isoB);
        _mounts.Add(b);
        // Windows usually reuses the letter for the next mount, but not always; the no-hang guarantee holds either way.
        var sameLetter = b.Letter == a.Letter;

        // Releasing the old handle used to block forever in DeviceIoControl, freezing the engine's finally block.
        var unlock = Task.Run(() => svc.LockMedia(a.Letter, false));
        var unlocked = await unlock.WaitAsync(TimeSpan.FromSeconds(30));
        if (sameLetter) unlocked.Should().BeTrue();
        // ... and the new disc can be protected again with a fresh handle.
        svc.LockMedia(b.Letter, true).Should().BeTrue();
        svc.LockMedia(b.Letter, true).Should().BeTrue("asking again replaces the handle");
        svc.LockMedia(b.Letter, false).Should().BeTrue();
    }

    // ------------------------------------------------------------------ the tray test ejects a disc: opt-in only

    private sealed class CountingDrives : IOpticalDriveService
    {
        public int Ejects;
        public string Letter = "Z:";
        public event EventHandler? Changed { add { } remove { } }
        public IReadOnlyList<OpticalDrive> GetDrives() => new[] { new OpticalDrive(Letter, "HP", "CDDVDW TS-L633M", "1.0", "USB", false) };
        public DriveState GetState(string letter) => DriveState.Ready;
        public Task<DiscInfo?> AnalyzeAsync(string letter, CancellationToken ct) => Task.FromResult<DiscInfo?>(null);
        public bool Eject(string letter) { Ejects++; return false; }
        public bool CloseTray(string letter) => false;
        public bool LockMedia(string letter, bool locked) => true;
        public bool SetReadSpeed(string letter, int kbPerSec) => true;
        public void Dispose() { }
    }

    [Fact]
    public async Task Diagnostics_DoesNotEjectTheDisc_UnlessTheTrayTestWasRequested()
    {
        var unused = Enumerable.Range('D', 23).Select(c => (char)c).Last(c => !Directory.Exists(c + @":\"));
        var drives = new CountingDrives { Letter = unused + ":" };
        IDriveDiagnostics diag = new DriveDiagnostics(drives, new DiscReader());
        diag.IncludeTrayTest.Should().BeFalse("ejecting the user's disc must be an explicit choice");

        var results = await diag.RunAsync(drives.Letter, false, new Progress<DiagnosticCheck>(), CancellationToken.None);
        results.Single(r => r.Id == "tray").Outcome.Should().Be(CheckOutcome.Skipped);
        drives.Ejects.Should().Be(0, "the default diagnostics run must not eject anything");

        diag.IncludeTrayTest = true;
        var again = await diag.RunAsync(drives.Letter, false, new Progress<DiagnosticCheck>(), CancellationToken.None);
        again.Single(r => r.Id == "tray").Outcome.Should().NotBe(CheckOutcome.Skipped);
        drives.Ejects.Should().Be(1, "the tray test runs only when asked for");
    }

    // ------------------------------------------------------------------ E-08: data + audio tracks

    [Fact]
    public void E08_HasAudioTracks_ComesFromTheToc()
    {
        OpticalDriveService.HasAudioTracks(null).Should().BeFalse("unknown TOC");
        OpticalDriveService.HasAudioTracks(new List<DeviceIo.TocTrack>()).Should().BeFalse();
        OpticalDriveService.HasAudioTracks(new List<DeviceIo.TocTrack> { new(1, true) }).Should().BeFalse("a plain data disc");
        OpticalDriveService.HasAudioTracks(new List<DeviceIo.TocTrack> { new(1, true), new(2, false), new(3, false) }).Should().BeTrue("mixed mode: data first, then audio");
        OpticalDriveService.HasAudioTracks(new List<DeviceIo.TocTrack> { new(1, false), new(2, false), new(3, true) }).Should().BeTrue("CD-Extra: audio session, then a data session");
        OpticalDriveService.HasAudioTracks(new List<DeviceIo.TocTrack> { new(1, false) }).Should().BeTrue("an audio CD");
    }

    [MountFact]
    public async Task E08_ADataDisc_DoesNotReportAudioTracks()
    {
        using var svc = new OpticalDriveService();
        var iso = IsoLayouts.DataDisc().Build(_tmp, "dados.iso");
        var m = MountedIso.Mount(iso);
        _mounts.Add(m);
        var info = await DiscHelpers.AnalyzeOrFailAsync(svc, m.Letter);
        info.HasAudioTracks.Should().BeFalse();
    }
}
