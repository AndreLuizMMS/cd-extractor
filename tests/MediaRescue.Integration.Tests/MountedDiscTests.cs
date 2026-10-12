using System.Diagnostics;
using MediaRescue.Core;
using MediaRescue.Integration.Tests.Support;
using MediaRescue.Windows;

namespace MediaRescue.Integration.Tests;

/// <summary>
/// SPEC 9.2: ISO images mounted with Mount-DiskImage appear as CD-ROM drives. These tests exercise detection ->
/// analysis -> reading with SHA-256 comparison. They are reported as SKIPPED (never passed) when mounting is unavailable.
/// </summary>
public sealed class MountedDiscTests : IDisposable
{
    private readonly string _tmp = TestEnv.NewTempDir();
    private readonly List<MountedIso> _mounts = new();
    private readonly OpticalDriveService _svc = new();
    private readonly DiscReader _reader = new();
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public MountedDiscTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        _svc.Dispose();
        foreach (var m in _mounts) m.Dispose();
        try { Directory.Delete(_tmp, true); } catch { }
    }

    /// <summary>DiscUtils pads the Joliet volume label with 0x20 bytes instead of UTF-16 spaces, which Windows shows as U+2020; strip that fixture artifact.</summary>
    private static string Label(DiscInfo info) => info.VolumeLabel.TrimEnd('\u2020', ' ');

    private MountedIso MountSpec(IsoSpec spec, string name = "disc.iso")
    {
        var iso = spec.Build(_tmp, Guid.NewGuid().ToString("N") + "-" + name);
        var m = MountedIso.Mount(iso);
        _mounts.Add(m);
        return m;
    }

    private async Task AssertAllFilesMatchAsync(MountedIso m, IsoSpec spec, DiscInfo info)
    {
        var expected = spec.Hashes();
        var files = info.Entries.Where(e => !e.IsDirectory).ToList();
        files.Select(f => f.RelativePath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Should()
            .Equal(spec.Files.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        foreach (var f in files)
        {
            f.Error.Should().BeNull();
            f.Size.Should().Be(spec.Files[f.RelativePath].LongLength, f.RelativePath);
            var actual = await DiscHelpers.HashDiscFileAsync(_reader, m.Letter, f.RelativePath);
            actual.Should().Be(expected[f.RelativePath], $"SHA-256 of {f.RelativePath}");
        }
    }

    // ---------------------------------------------------------------- RF-01 / E-01 / E-02

    [MountFact]
    public async Task E02_MountedIso_IsListedAsVirtualDrive_AndReportsReady()
    {
        using var m = MountSpec(IsoLayouts.DataDisc());
        var drives = _svc.GetDrives();
        var drive = drives.SingleOrDefault(d => d.Letter == m.Letter);
        drive.Should().NotBeNull($"{m.Letter} must be enumerated as an optical drive");
        drive!.IsVirtual.Should().BeTrue("a mounted ISO is a virtual drive");
        drive.Vendor.Should().NotBeNull(); drive.Model.Should().NotBeNull(); drive.Firmware.Should().NotBeNull(); drive.BusType.Should().NotBeNull();
        var ready = await DiscHelpers.WaitUntil(() => _svc.GetState(m.Letter) == DriveState.Ready, TimeSpan.FromSeconds(20));
        ready.Should().BeTrue("the mounted disc must be reported as Ready");
    }

    [MountFact]
    public async Task E02_TwoMountedDiscs_BothListed_EachWithOwnState_AndDifferentFingerprints_SameLabel_E21()
    {
        var specA = IsoLayouts.DataDisc("FERIAS");
        var specB = new IsoSpec { Label = "FERIAS" }.AddRandom("outro.avi", 90_000, 777).AddRandom("x.txt", 12, 778);
        using var a = MountSpec(specA, "a.iso");
        using var b = MountSpec(specB, "b.iso");

        var letters = _svc.GetDrives().Select(d => d.Letter).ToList();
        letters.Should().Contain(new[] { a.Letter, b.Letter });
        var infoA = await DiscHelpers.AnalyzeOrFailAsync(_svc, a.Letter);
        var infoB = await DiscHelpers.AnalyzeOrFailAsync(_svc, b.Letter);
        Label(infoA).Should().Be("FERIAS");
        Label(infoB).Should().Be("FERIAS");
        infoA.Fingerprint.Should().NotBe(infoB.Fingerprint, "E-21: same label, different discs");
        _svc.GetState(a.Letter).Should().Be(DriveState.Ready);
        _svc.GetState(b.Letter).Should().Be(DriveState.Ready);
    }

    [MountFact]
    public async Task Fingerprint_IsStable_ForTheSameDisc_AcrossAnalyses()
    {
        using var m = MountSpec(IsoLayouts.DataDisc());
        var first = await DiscHelpers.AnalyzeOrFailAsync(_svc, m.Letter);
        var second = await DiscHelpers.AnalyzeOrFailAsync(_svc, m.Letter);
        first.Fingerprint.Should().Be(second.Fingerprint);
    }

    // ---------------------------------------------------------------- RF-02 layouts

    [MountFact]
    public async Task DataDisc_AnalyzeThenReadEveryFile_HashesMatch()
    {
        var spec = IsoLayouts.DataDisc("DADOS");
        using var m = MountSpec(spec);
        var info = await DiscHelpers.AnalyzeOrFailAsync(_svc, m.Letter);
        Label(info).Should().Be("DADOS");
        info.VolumeSerial.Should().MatchRegex("^[0-9A-F]{4}-[0-9A-F]{4}$");
        info.FileSystem.Should().BeOneOf("CDFS", "UDF");
        info.UsedBytes.Should().BeGreaterThan(0);
        info.IsCopyProtected.Should().BeFalse();
        info.RecordedAtUtc.Should().NotBeNull("the ISO primary volume descriptor carries a creation date");
        info.RecordedAtUtc!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromDays(1));
        info.LogicalFormat.Should().Be(DiscClassifier.Classify(info.Entries));
        info.Entries.Where(e => e.Kind == EntryKind.Video).Select(e => e.RelativePath).Should().Contain("video1.avi");
        info.Entries.Should().Contain(e => e.IsDirectory && e.RelativePath == "fotos");
        await AssertAllFilesMatchAsync(m, spec, info);
    }

    [MountFact]
    public async Task Vcd_Layout_FilesNotMultipleOfSectorSize_AreReadCorrectly()
    {
        var spec = IsoLayouts.Vcd();
        using var m = MountSpec(spec);
        var info = await DiscHelpers.AnalyzeOrFailAsync(_svc, m.Letter);
        info.LogicalFormat.Should().Be(DiscClassifier.Classify(info.Entries));
        await AssertAllFilesMatchAsync(m, spec, info);
    }

    [MountFact]
    public async Task VideoTs_Layout_TitleGroupsComeFromClassifier_AndAllFilesMatch()
    {
        var spec = IsoLayouts.VideoTs();
        using var m = MountSpec(spec);
        var info = await DiscHelpers.AnalyzeOrFailAsync(_svc, m.Letter);
        foreach (var e in info.Entries.Where(e => !e.IsDirectory))
        {
            e.TitleGroup.Should().Be(DiscClassifier.TitleGroupOf(e.RelativePath), e.RelativePath);
            e.Kind.Should().Be(DiscClassifier.KindOf(e.RelativePath), e.RelativePath);
        }
        info.Entries.Should().Contain(e => e.IsDirectory && e.RelativePath == "AUDIO_TS", "empty folders must be listed (E-24)");
        info.LogicalFormat.Should().Be(DiscClassifier.Classify(info.Entries));
        await AssertAllFilesMatchAsync(m, spec, info);
    }

    [MountFact]
    public async Task DvdRtav_Layout_AllFilesMatch()
    {
        var spec = IsoLayouts.DvdRtav();
        using var m = MountSpec(spec);
        var info = await DiscHelpers.AnalyzeOrFailAsync(_svc, m.Letter);
        info.LogicalFormat.Should().Be(DiscClassifier.Classify(info.Entries));
        await AssertAllFilesMatchAsync(m, spec, info);
    }

    [MountFact]
    public async Task Mixed_UnicodeLongNamesZeroByteAndEmptyFolder_AllFilesMatch()
    {
        var spec = IsoLayouts.Mixed();
        using var m = MountSpec(spec);
        var info = await DiscHelpers.AnalyzeOrFailAsync(_svc, m.Letter);
        Label(info).Should().Be("Álbum da família");
        info.Entries.Should().Contain(e => e.IsDirectory && e.RelativePath == "Pasta vazia");
        info.Entries.Should().Contain(e => !e.IsDirectory && e.Size == 0 && e.RelativePath == "zero.bin");
        info.Entries.Max(e => e.RelativePath.Length).Should().BeGreaterThan(260 - 3, "the fixture contains a path beyond MAX_PATH");
        await AssertAllFilesMatchAsync(m, spec, info);
    }

    [MountFact]
    public async Task E24_ThousandsOfSmallFiles_AreAllListedAndHashed()
    {
        var spec = IsoLayouts.ManySmallFiles(3000);
        using var m = MountSpec(spec);
        var sw = Stopwatch.StartNew();
        var info = await DiscHelpers.AnalyzeOrFailAsync(_svc, m.Letter);
        info.Entries.Count(e => !e.IsDirectory).Should().Be(3000);
        await AssertAllFilesMatchAsync(m, spec, info);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(3));
    }

    [MountFact]
    public async Task E06_EmptyIso_IsReadableButHasNoEntries()
    {
        using var m = MountSpec(IsoLayouts.Empty());
        var info = await DiscHelpers.AnalyzeOrFailAsync(_svc, m.Letter);
        info.Entries.Where(e => !e.IsDirectory).Should().BeEmpty();
        info.LogicalFormat.Should().Be(DiscClassifier.Classify(info.Entries));
        Label(info).Should().Be("VAZIO");
    }

    // ---------------------------------------------------------------- DiscReader behaviour

    [MountFact]
    public async Task DiscReader_RandomAccessUnalignedReads_MatchSourceBytes()
    {
        var spec = new IsoSpec { Label = "RANDOM" }.AddRandom("big.bin", 3_000_000 + 37, 99);
        using var m = MountSpec(spec);
        await DiscHelpers.AnalyzeOrFailAsync(_svc, m.Letter);
        var data = spec.Files["big.bin"];
        using var f = _reader.Open(m.Letter, "big.bin");
        f.Length.Should().Be(data.Length);
        f.SectorSize.Should().BeOneOf(1, 2048);
        var rnd = new Random(5);
        for (int i = 0; i < 60; i++)
        {
            long off = rnd.Next(0, data.Length);
            int len = rnd.Next(1, 200_000);
            var buf = new byte[len];
            int n = await f.ReadAsync(off, buf, TimeSpan.FromSeconds(20), CancellationToken.None);
            int expected = (int)Math.Min(len, data.Length - off);
            n.Should().Be(expected, $"offset {off} length {len}");
            buf.AsSpan(0, n).SequenceEqual(data.AsSpan((int)off, n)).Should().BeTrue($"bytes at {off}+{len}");
        }
        (await f.ReadAsync(data.Length, new byte[10], TimeSpan.FromSeconds(5), CancellationToken.None)).Should().Be(0, "reads at EOF return 0");
        (await f.ReadAsync(data.Length + 4096, new byte[10], TimeSpan.FromSeconds(5), CancellationToken.None)).Should().Be(0);
    }

    [MountFact]
    public async Task E29_AnotherProgramHoldingTheFileOpen_DoesNotPreventReading()
    {
        var spec = IsoLayouts.DataDisc();
        using var m = MountSpec(spec);
        await DiscHelpers.AnalyzeOrFailAsync(_svc, m.Letter);
        // Simulates AutoPlay / Explorer / a player having the same file open for reading.
        using var other = new FileStream($@"{m.Letter}\video1.avi", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var hash = await DiscHelpers.HashDiscFileAsync(_reader, m.Letter, "video1.avi");
        hash.Should().Be(spec.Hashes()["video1.avi"]);
    }

    [MountFact]
    public async Task OpeningAMissingFile_OnAHealthyDisc_IsReportedAsOtherNotDeviceGone()
    {
        using var m = MountSpec(IsoLayouts.DataDisc());
        await DiscHelpers.AnalyzeOrFailAsync(_svc, m.Letter);
        var act = () => _reader.Open(m.Letter, "nao-existe.avi");
        act.Should().Throw<DiscReadException>().Which.Kind.Should().Be(ReadErrorKind.Other);
    }

    [MountFact]
    public async Task E04_DismountDuringRead_ThrowsDeviceGone()
    {
        var spec = new IsoSpec { Label = "GRANDE" }.AddRandom("grande.bin", 24 * 1024 * 1024, 123);
        using var m = MountSpec(spec);
        await DiscHelpers.AnalyzeOrFailAsync(_svc, m.Letter);
        using var file = _reader.Open(m.Letter, "grande.bin");
        var buf = new byte[64 * 1024];
        long offset = 0;
        // Read a few blocks, then pull the "disc" out while the reader is still in the middle of the file.
        for (int i = 0; i < 8; i++) offset += await file.ReadAsync(offset, buf, TimeSpan.FromSeconds(20), CancellationToken.None);
        var dismount = Task.Run(() => m.Dismount());

        DiscReadException? caught = null;
        var sw = Stopwatch.StartNew();
        try
        {
            // Keep reading (wrapping around at EOF) until the disc is pulled out: the removal races with the read loop.
            while (sw.Elapsed < TimeSpan.FromSeconds(60))
            {
                if (offset >= file.Length) offset = 0;
                int n = await file.ReadAsync(offset, buf, TimeSpan.FromSeconds(10), CancellationToken.None);
                if (n <= 0) offset = 0; else offset += n;
                await Task.Delay(2);
            }
        }
        catch (DiscReadException ex) { caught = ex; }
        await dismount;

        caught.Should().NotBeNull("the disc disappeared in the middle of the copy");
        _out.WriteLine($"E-04 observed: Kind={caught!.Kind} Win32Code={caught.Win32Code}");
        caught!.Kind.Should().Be(ReadErrorKind.DeviceGone, $"Win32 code was {caught.Win32Code}");

        // After removal the drive must also report no media.
        (await DiscHelpers.WaitUntil(() => _svc.GetState(m.Letter) == DriveState.NoMedia, TimeSpan.FromSeconds(20))).Should().BeTrue();
    }

    // ---------------------------------------------------------------- Changed event (RF-01)

    [MountFact]
    public async Task E01_Changed_IsRaisedWhenADriveAppearsAndDisappears_WithoutRestart()
    {
        int count = 0;
        _svc.Changed += (_, _) => Interlocked.Increment(ref count);
        var iso = IsoLayouts.DataDisc().Build(_tmp, "evt.iso");
        var m = MountedIso.Mount(iso);
        _mounts.Add(m);
        (await DiscHelpers.WaitUntil(() => Volatile.Read(ref count) >= 1, TimeSpan.FromSeconds(15))).Should().BeTrue("mounting a disc must raise Changed");
        _svc.GetDrives().Select(d => d.Letter).Should().Contain(m.Letter);

        int before = Volatile.Read(ref count);
        m.Dismount();
        (await DiscHelpers.WaitUntil(() => Volatile.Read(ref count) > before, TimeSpan.FromSeconds(15))).Should().BeTrue("removing a disc must raise Changed");
        _svc.GetDrives().Select(d => d.Letter).Should().NotContain(m.Letter);
    }

    [MountFact]
    public async Task E01_DeviceNotificationAlone_RaisesChanged_WithoutPolling()
    {
        using var svc = new OpticalDriveService(pollInterval: null);
        int count = 0;
        svc.Changed += (_, _) => Interlocked.Increment(ref count);
        var iso = IsoLayouts.DataDisc().Build(_tmp, "evt2.iso");
        var m = MountedIso.Mount(iso);
        _mounts.Add(m);
        (await DiscHelpers.WaitUntil(() => Volatile.Read(ref count) >= 1, TimeSpan.FromSeconds(10)))
            .Should().BeTrue("WM_DEVICECHANGE must be delivered to the hidden window even if polling is disabled");
    }

    // ---------------------------------------------------------------- E-33 graceful degradation / robustness

    [MountFact]
    public async Task E33_PhysicalInfoDegradesGracefully_OnVirtualDrive()
    {
        using var m = MountSpec(IsoLayouts.DataDisc());
        var info = await DiscHelpers.AnalyzeOrFailAsync(_svc, m.Letter);
        _out.WriteLine($"virtual drive: type={info.PhysicalType ?? "null"} state={info.MediaState ?? "null"} sessions={info.Sessions?.ToString() ?? "null"} tracks={info.Tracks?.ToString() ?? "null"} capacity={info.CapacityBytes?.ToString() ?? "null"} recorded={info.RecordedAtUtc:u} fs={info.FileSystem} drive={string.Join("|", _svc.GetDrives().Where(d => d.Letter == m.Letter).Select(d => $"{d.Vendor}/{d.Model}/{d.Firmware}/{d.BusType}"))}");
        // Whatever the virtual drive does not report must be null (shown as "—"), never an exception.
        if (info.Sessions != null) info.Sessions.Should().BeGreaterThan(0);
        if (info.Tracks != null) info.Tracks.Should().BeGreaterThan(0);
        if (info.PhysicalType != null) info.PhysicalType.Should().NotBeNullOrWhiteSpace();
        if (info.MediaState != null) info.MediaState.Should().BeOneOf("Finalizado", "Aberto", "Vazio");
    }

    [Fact]
    public async Task InvalidLettersAndMissingDrives_NeverThrow()
    {
        _svc.GetDrives().Should().NotBeNull();
        foreach (var letter in new[] { "", "  ", "1", "?:", "Q", "q:", @"Q:\", "ZZ", @"\\?\Q:" })
        {
            var act = () => _svc.GetState(letter);
            act.Should().NotThrow(letter);
            var state = _svc.GetState(letter);
            state.Should().BeOneOf(DriveState.NoMedia, DriveState.Unreadable, DriveState.Ready, DriveState.TrayOpen);
            (await _svc.AnalyzeAsync(letter, CancellationToken.None)).Should().BeNull();
            _svc.Eject(letter).Should().BeFalse();
            _svc.CloseTray(letter).Should().BeFalse();
        }
        _svc.LockMedia("Q:", true).Should().BeFalse();
        _svc.SetReadSpeed("Q:", 1000).Should().BeFalse();
    }

    [MountFact]
    public async Task AnalyzeAsync_CanBeCancelled()
    {
        using var m = MountSpec(IsoLayouts.ManySmallFiles(1500));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = async () => await _svc.AnalyzeAsync(m.Letter, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [MountFact]
    public async Task LockMedia_AndSetReadSpeed_NeverThrow_OnVirtualDrive()
    {
        using var m = MountSpec(IsoLayouts.DataDisc());
        await DiscHelpers.AnalyzeOrFailAsync(_svc, m.Letter);
        var lockedAct = () => _svc.LockMedia(m.Letter, true);
        lockedAct.Should().NotThrow();
        var unlockAct = () => _svc.LockMedia(m.Letter, false);
        unlockAct.Should().NotThrow();
        _svc.LockMedia(m.Letter, false).Should().BeTrue("unlocking when not locked is a no-op");
        var speedAct = () => _svc.SetReadSpeed(m.Letter, 0);
        speedAct.Should().NotThrow();
    }

    // ---------------------------------------------------------------- RF-07 diagnostics on a virtual drive

    [MountFact]
    public async Task Diagnostics_OnMountedIso_RunsAllSixChecks_InOrder_WithoutFailures()
    {
        var spec = new IsoSpec { Label = "DIAG" }.AddRandom("a.bin", 5 * 1024 * 1024, 5).AddRandom("b.bin", 3 * 1024 * 1024, 6);
        using var m = MountSpec(spec);
        await DiscHelpers.AnalyzeOrFailAsync(_svc, m.Letter);

        var diag = new DriveDiagnostics(_svc, _reader) { IncludeTrayTest = true };
        var progress = new List<DiagnosticCheck>();
        var results = await diag.RunAsync(m.Letter, fullSurfaceScan: true, new Progress<DiagnosticCheck>(c => { lock (progress) progress.Add(c); }), CancellationToken.None);

        results.Select(r => r.Id).Should().Equal("detected", "responds", "tray", "media", "sampling", "surface");
        foreach (var r in results)
        {
            r.Title.Should().NotBeNullOrWhiteSpace();
            r.Detail.Should().NotBeNullOrWhiteSpace();
            r.Outcome.Should().NotBe(CheckOutcome.Fail, $"{r.Id}: {r.Detail}");
        }
        results[0].Outcome.Should().Be(CheckOutcome.Pass);
        results[2].Outcome.Should().Be(CheckOutcome.NotSupported, "virtual drives have no tray");
        results[3].Outcome.Should().Be(CheckOutcome.Pass);
        results[4].Outcome.Should().BeOneOf(CheckOutcome.Pass, CheckOutcome.Warning, CheckOutcome.NotSupported);
        results[5].Outcome.Should().BeOneOf(CheckOutcome.Pass, CheckOutcome.Warning, CheckOutcome.NotSupported);
        await DiscHelpers.WaitUntil(() => { lock (progress) return progress.Count == 6; }, TimeSpan.FromSeconds(5));
        progress.Should().HaveCount(6);
    }

    [Fact]
    public async Task Diagnostics_OnMissingDrive_FailsDetectionAndSkipsTheRest()
    {
        var diag = new DriveDiagnostics(_svc, _reader);
        var results = await diag.RunAsync("Q:", false, new Progress<DiagnosticCheck>(), CancellationToken.None);
        results.Should().HaveCount(6);
        results[0].Outcome.Should().BeOneOf(CheckOutcome.Fail);
        results[0].Tip.Should().NotBeNullOrWhiteSpace();
        results[1].Outcome.Should().BeOneOf(CheckOutcome.Fail, CheckOutcome.NotSupported);
        results[4].Outcome.Should().Be(CheckOutcome.Skipped);
    }
}
