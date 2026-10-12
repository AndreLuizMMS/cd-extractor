using FluentAssertions;
using MediaRescue.Core;
using MediaRescue.Core.Tests.Fakes;

namespace MediaRescue.Core.Tests;

/// <summary>Drive disappearing / disc swapped during the copy: E-03, E-04, E-05, E-26.</summary>
public class EngineDeviceTests
{
    private static Rig NewRig(int files = 2, int size = 300_000)
    {
        var rig = new Rig();
        for (int i = 1; i <= files; i++) rig.AddFile($"{i}.avi", size);
        return rig;
    }

    /// <summary>The n-th poll brings the original disc back (and reconnects the cable).</summary>
    private static void ReturnsOnPoll(Rig rig, int poll)
    {
        rig.Drives.AnalyzeScript = n =>
        {
            if (n < poll) return null;
            rig.Reader.DeviceGone = false;
            return rig.Disc;
        };
    }

    [Fact]
    public async Task E03_DriveDisconnected_WaitsForTheSameDisc_ThenRedoesTheCurrentFile()
    {
        var rig = NewRig();
        rig.Reader.File("2.avi").DeviceGoneAt(150_000);
        ReturnsOnPoll(rig, 3);
        var partialsWhileWaiting = new List<int>();
        rig.Clock.OnDelay = _ => partialsWhileWaiting.Add(rig.Dest.PartialFiles.Count);

        var result = await rig.RunWithTimeout();

        rig.Progress.SawPhase(ExtractionPhase.WaitingForDrive).Should().BeTrue();
        rig.Progress.Messages.Should().Contain(m => m.Contains("Leitor desconectado") && m.Contains("reconecte"));
        result.Status.Should().Be(OverallStatus.Intact);
        result.Files.Should().OnlyContain(f => f.Status == FileStatus.Intact);
        foreach (var (path, bytes) in rig.Contents) rig.Written(path).Should().Equal(bytes);
        rig.Reader.Opened.Should().Equal("1.avi", "2.avi", "2.avi");
        rig.Drives.AnalyzeCalls.Should().Be(3);
        partialsWhileWaiting.Should().OnlyContain(c => c == 0, "the interrupted .partial is discarded and redone");
        rig.Dest.PartialFiles.Should().BeEmpty();
        rig.Drives.LockCalls.Should().Equal(true, true, false);
    }

    [Fact]
    public async Task E03_DriveGoneAlreadyWhenOpeningTheFile_IsHandledTheSameWay()
    {
        var rig = NewRig(files: 1);
        rig.Reader.DeviceGone = true;
        ReturnsOnPoll(rig, 2);

        var result = await rig.RunWithTimeout();

        result.Files.Single().Status.Should().Be(FileStatus.Intact);
        rig.Written("1.avi").Should().Equal(rig.Contents["1.avi"]);
    }

    [Fact]
    public async Task E03_TimeSpentWaitingForTheDrive_IsNotCountedInTheElapsedTime()
    {
        var rig = NewRig(files: 1);
        rig.Reader.File("1.avi").DeviceGoneAt(100_000);
        ReturnsOnPoll(rig, 6); // five polls of 2 s = 10 s of waiting

        var result = await rig.RunWithTimeout();

        result.Duration.Should().BeLessThan(rig.Clock.Elapsed - TimeSpan.FromSeconds(9));
    }

    [Fact]
    public async Task E04_DifferentDiscInserted_FailsTheJobCleanly_AndKeepsTheStateForTheOriginalDisc()
    {
        var rig = NewRig(files: 3);
        rig.Reader.File("2.avi").DeviceGoneAt(100_000);
        var other = new DiscInfo { VolumeLabel = "OUTRO DISCO", VolumeSerial = "FFFF-0000", Entries = new[] { new DiscEntry { RelativePath = "x.avi", Size = 5 } } };
        rig.Drives.AnalyzeScript = n => n == 1 ? null : other;

        var result = await rig.RunWithTimeout();

        result.Status.Should().Be(OverallStatus.Failed);
        result.Files.Single(f => f.RelativePath == "1.avi").Status.Should().Be(FileStatus.Intact);
        var two = result.Files.Single(f => f.RelativePath == "2.avi");
        two.Status.Should().Be(FileStatus.Failed);
        two.Error.Should().Contain("diferente");
        result.Files.Single(f => f.RelativePath == "3.avi").Status.Should().Be(FileStatus.Failed);
        rig.Dest.PartialFiles.Should().BeEmpty();
        rig.Drives.LockCalls.Last().Should().BeFalse();
        rig.Engine.CanResume(rig.Disc, Rig.DestRoot, Rig.Folder).Should().BeTrue("inserting the original disc later can continue");
        rig.Dest.ReadText(rig.DestPath("LEIA-ME - ERROS.txt")).Should().Contain("diferente");
    }

    [Fact]
    public async Task E04_ADiscStillSpinningUp_WithAMomentaryDifferentListing_DoesNotAbortTheJob()
    {
        var rig = NewRig(files: 1);
        rig.Reader.File("1.avi").DeviceGoneAt(100_000);
        var halfListed = new DiscInfo { VolumeLabel = rig.Label, VolumeSerial = rig.Serial, Entries = Array.Empty<DiscEntry>() };
        rig.Drives.AnalyzeScript = n =>
        {
            if (n == 1) return null;
            if (n == 2) return halfListed;
            rig.Reader.DeviceGone = false;
            return rig.Disc;
        };

        var result = await rig.RunWithTimeout();

        result.Status.Should().Be(OverallStatus.Intact);
    }

    [Fact]
    public async Task E03_AnalysisThatThrowsWhileTheDriveIsMissing_IsJustAnotherPoll()
    {
        var rig = NewRig(files: 1);
        rig.Reader.File("1.avi").DeviceGoneAt(100_000);
        rig.Drives.AnalyzeScript = n =>
        {
            if (n < 3) throw new IOException("drive not ready");
            rig.Reader.DeviceGone = false;
            return rig.Disc;
        };

        var result = await rig.RunWithTimeout();

        result.Status.Should().Be(OverallStatus.Intact);
    }

    [Fact]
    public async Task E03_Cancel_WhileWaitingForTheDrive_Works()
    {
        var rig = NewRig(files: 1);
        rig.Reader.File("1.avi").DeviceGoneAt(100_000);
        using var cts = new CancellationTokenSource();
        int polls = 0;
        rig.Clock.OnDelay = _ => { if (++polls == 4) cts.Cancel(); };

        var result = await rig.RunWithTimeout(cts.Token);

        result.Status.Should().Be(OverallStatus.Cancelled);
        rig.Dest.PartialFiles.Should().BeEmpty();
        rig.Drives.LockCalls.Last().Should().BeFalse();
    }

    [Fact]
    public async Task E05_ThreeDisconnectionsInTwoMinutes_ShowAUsbPowerHint()
    {
        var rig = NewRig(files: 4);
        for (int i = 1; i <= 3; i++) rig.Reader.File($"{i}.avi").DeviceGoneAt(100_000);
        ReturnsOnPoll(rig, 1);
        rig.Drives.AnalyzeScript = _ => { rig.Reader.DeviceGone = false; return rig.Disc; };

        var result = await rig.RunWithTimeout();

        result.Status.Should().Be(OverallStatus.Intact);
        var waiting = rig.Progress.Items.Where(p => p.Phase == ExtractionPhase.WaitingForDrive && p.Message != null).Select(p => p.Message!).ToList();
        waiting.Should().HaveCount(3);
        waiting[0].Should().NotContain("energia");
        waiting[1].Should().NotContain("energia");
        waiting[2].Should().Contain("energia").And.Contain("hub");
    }

    [Fact]
    public async Task E05_DisconnectionsFarApart_DoNotTriggerTheHint()
    {
        var rig = NewRig(files: 4);
        for (int i = 1; i <= 3; i++) rig.Reader.File($"{i}.avi").DeviceGoneAt(100_000);
        rig.Drives.AnalyzeScript = _ => { rig.Reader.DeviceGone = false; return rig.Disc; };
        // five minutes pass between the disconnections
        foreach (var name in new[] { "1.avi", "2.avi", "3.avi" })
        {
            var f = rig.Reader.File(name);
            f.OnRead = (_, _) => rig.Clock.Advance(TimeSpan.FromMinutes(5));
        }

        await rig.RunWithTimeout();

        rig.Progress.Messages.Should().NotContain(m => m.Contains("energia"));
    }

    [Fact]
    public async Task E26_SuspendAndResume_LooksLikeADriveLoss_AndTheFileIsRedone()
    {
        // after resuming from sleep the first read reports "media changed / not ready": same as E-03
        var rig = NewRig(files: 1);
        int reads = 0;
        rig.Reader.File("1.avi").OnRead = (_, _) =>
        {
            if (++reads == 3) rig.Reader.DeviceGone = true;
        };
        ReturnsOnPoll(rig, 2);

        var result = await rig.RunWithTimeout();

        result.Files.Single().Status.Should().Be(FileStatus.Intact);
        rig.Written("1.avi").Should().Equal(rig.Contents["1.avi"]);
        rig.Reader.Opened.Count.Should().Be(2);
    }

    [Fact]
    public async Task DriveLoss_InTheSecondPass_IsAlsoRecovered()
    {
        var rig = NewRig(files: 1, size: 400_000);
        var disc = rig.Reader.File("1.avi");
        disc.BadRange(65_536, Sector);
        rig.Options.RetriesPerSector = 1;
        bool gone = false;
        disc.OnRead = (offset, len) =>
        {
            // the cable is pulled the first time pass 2 reads the single sector
            if (!gone && len == Sector && offset == 65_536) { gone = true; rig.Reader.DeviceGone = true; }
        };
        ReturnsOnPoll(rig, 2);

        var result = await rig.RunWithTimeout();

        result.Files.Single().Status.Should().Be(FileStatus.Partial);
        result.Files.Single().BadRanges.Should().Equal(new ByteRange(65_536, Sector));
    }

    private const int Sector = 2048;
}
