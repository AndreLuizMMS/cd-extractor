using FluentAssertions;
using MediaRescue.Core;
using MediaRescue.Core.Tests.Fakes;

namespace MediaRescue.Core.Tests;

/// <summary>Destination-side behaviour: verification, write errors, disk full, removal, rename retries (E-15, E-16, E-19, E-25).</summary>
public class EngineDestinationTests
{
    private const int Block = 64 * 1024;
    private const int Sector = 2048;

    // ------------------------------------------------------------------ verification (RF-05.2)

    [Fact]
    public async Task Verification_DetectsPersistentSilentCorruption_AsDestinationFailure()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 200_000);
        var good = rig.AddFile("b.avi", 50_000);
        rig.Dest.CorruptOnWrite(70_000, pathMatch: p => p.Contains("a.avi"));

        var result = await rig.RunWithTimeout();

        var a = result.Files.Single(f => f.RelativePath == "a.avi");
        a.Status.Should().Be(FileStatus.DestinationFailure);
        a.Error.Should().NotBeNullOrEmpty();
        rig.Dest.FileExists(rig.DestPath("a.avi")).Should().BeFalse("a corrupted copy must never get the final name");
        rig.Dest.PartialFiles.Should().BeEmpty();
        rig.Reader.File("a.avi").Log.Count(r => r.Offset == 0).Should().Be(2, "one new attempt, reading the disc again");
        rig.Progress.Messages.Should().Contain(m => m.Contains("defeito"));
        result.Files.Single(f => f.RelativePath == "b.avi").Status.Should().Be(FileStatus.Intact);
        result.Status.Should().Be(OverallStatus.Partial);
        rig.Dest.ReadText(rig.DestPath("LEIA-ME - ERROS.txt")).Should().Contain("a.avi").And.Contain("defeito");
        good.Log.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Verification_TransientCorruption_IsFixedByTheSingleRetry()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 200_000);
        rig.Dest.CorruptOnWrite(70_000, times: 1);

        var result = await rig.RunWithTimeout();

        var a = result.Files.Single();
        a.Status.Should().Be(FileStatus.Intact);
        a.Sha256.Should().Be(TestData.Sha(rig.Contents["a.avi"]));
        rig.Written("a.avi").Should().Equal(rig.Contents["a.avi"]);
        rig.Dest.OpenedForRead.Should().HaveCount(2);
    }

    [Fact]
    public async Task Verification_DetectsTruncatedDestinationFile()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 200_000);
        int flushes = 0;
        rig.Dest.FlushFault = path => { if (++flushes == 1) rig.Dest.Truncate(path, 1000); return null; };

        var result = await rig.RunWithTimeout();

        result.Files.Single().Status.Should().Be(FileStatus.Intact, "the second attempt is fine");
        flushes.Should().Be(2);
    }

    [Fact]
    public async Task Verification_OfAPartialFile_StillDetectsCorruptionInTheRecoveredData()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 600_000).BadRange(100 * Sector, Sector);
        rig.Dest.CorruptOnWrite(500_000); // persistent, in a region that was read fine

        var result = await rig.RunWithTimeout();

        result.Files.Single().Status.Should().Be(FileStatus.DestinationFailure);
        rig.Dest.FileExists(rig.DestPath("a.avi")).Should().BeFalse();
    }

    [Fact]
    public async Task Verification_OfAPartialFile_PassesWhenTheDestinationIsHealthy()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 600_000).BadRange(100 * Sector, Sector);

        var result = await rig.RunWithTimeout();

        result.Files.Single().Status.Should().Be(FileStatus.Partial);
        rig.Dest.FileExists(rig.DestPath("a.avi")).Should().BeTrue();
    }

    [Fact]
    public async Task Verification_OfARecoveredFile_UsesTheHashOfTheRecoveredContent()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 400_000).FailTimes(Block, Block, 3);

        var result = await rig.RunWithTimeout();

        result.Files.Single().Status.Should().Be(FileStatus.Recovered);
        result.Files.Single().Sha256.Should().Be(TestData.Sha(rig.Contents["a.avi"]));
    }

    [Fact]
    public async Task NoFileEverAppearsWithItsFinalNameBeforeItIsComplete()
    {
        var rig = new Rig();
        for (int i = 1; i <= 3; i++) rig.AddFile($"f{i}.avi", 150_000);
        var violations = new List<string>();
        rig.Dest.OnWrite = (path, _, _) =>
        {
            foreach (var name in rig.Contents.Keys)
                if (rig.Dest.FileExists(rig.DestPath(name)) && !rig.Dest.ReadAllBytes(rig.DestPath(name)).SequenceEqual(rig.Contents[name]))
                    violations.Add(name);
            return null;
        };

        await rig.RunWithTimeout();

        violations.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ write errors (E-19)

    [Fact]
    public async Task E19_WriteError_IsRetriedOnce_AndThenSucceeds()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 200_000);
        int writes = 0;
        rig.Dest.OnWrite = (_, _, _) => ++writes == 2 ? new IOException("erro de E/S no pendrive") : null;

        var result = await rig.RunWithTimeout();

        result.Files.Single().Status.Should().Be(FileStatus.Intact);
        rig.Written("a.avi").Should().Equal(rig.Contents["a.avi"]);
        rig.Dest.PartialFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task E19_PersistentWriteError_ReportsDestinationFailure_AndContinuesWithOtherFiles()
    {
        var rig = new Rig();
        rig.AddFile("ruim.avi", 200_000);
        rig.AddFile("bom.avi", 50_000);
        rig.Dest.OnWrite = (path, _, _) => path.Contains("ruim") ? new IOException("erro de E/S") : null;

        var result = await rig.RunWithTimeout();

        var ruim = result.Files.Single(f => f.RelativePath == "ruim.avi");
        ruim.Status.Should().Be(FileStatus.DestinationFailure);
        ruim.Error.Should().Contain("destino");
        result.Files.Single(f => f.RelativePath == "bom.avi").Status.Should().Be(FileStatus.Intact);
        rig.Dest.PartialFiles.Should().BeEmpty();
        rig.Dest.FileExists(rig.DestPath("ruim.avi")).Should().BeFalse();
        rig.Progress.Last.FilesWithErrors.Should().Be(1);
    }

    [Fact]
    public async Task E19_AccessDeniedWhileWriting_IsAlsoADestinationFailure()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 100_000);
        rig.Dest.OnWrite = (_, _, _) => new UnauthorizedAccessException("protegido contra gravação");

        var result = await rig.RunWithTimeout();

        result.Files.Single().Status.Should().Be(FileStatus.DestinationFailure);
        result.Status.Should().Be(OverallStatus.Failed);
    }

    // ------------------------------------------------------------------ E-15 disk full

    [Fact]
    public async Task E15_DestinationFillsUp_PausesAndCleansPartial()
    {
        var rig = new Rig();
        rig.Dest.CapacityBytes = 500_000;
        for (int i = 1; i <= 3; i++) rig.AddFile($"{i}.avi", 200_000);
        int polls = 0;
        int partialsAtFirstPoll = -1;
        rig.Clock.OnDelay = _ =>
        {
            polls++;
            if (polls == 1) partialsAtFirstPoll = rig.Dest.PartialFiles.Count;
            if (polls == 3) rig.Dest.CapacityBytes = 50_000_000; // the user freed space
        };

        var result = await rig.RunWithTimeout();

        partialsAtFirstPoll.Should().Be(0, "the half-written .partial is removed while waiting");
        polls.Should().BeGreaterThanOrEqualTo(3, "it keeps waiting until there is room");
        rig.Progress.SawPhase(ExtractionPhase.DestinationFull).Should().BeTrue();
        rig.Progress.Messages.Should().Contain(m => m.Contains("sem espaço"));
        result.Files.Should().HaveCount(3).And.OnlyContain(f => f.Status == FileStatus.Intact);
        foreach (var (path, bytes) in rig.Contents) rig.Written(path).Should().Equal(bytes);
        rig.Dest.PartialFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task E15_DiskFull_CanBeCancelledWhileWaiting()
    {
        var rig = new Rig();
        rig.Dest.CapacityBytes = 100_000;
        rig.AddFile("a.avi", 300_000);
        using var cts = new CancellationTokenSource();
        int polls = 0;
        rig.Clock.OnDelay = _ => { if (++polls == 4) cts.Cancel(); };

        var result = await rig.RunWithTimeout(cts.Token);

        result.Status.Should().Be(OverallStatus.Cancelled);
        rig.Dest.PartialFiles.Should().BeEmpty();
        rig.Drives.LockCalls.Should().Equal(true, false);
    }

    // ------------------------------------------------------------------ E-16 destination removed

    [Fact]
    public async Task E16_DestinationRemoved_WaitsThenContinues_WithoutRecopyingFinishedFiles()
    {
        var rig = new Rig();
        for (int i = 1; i <= 3; i++) rig.AddFile($"{i}.avi", 200_000);
        bool pulled = false;
        rig.Dest.OnWrite = (path, offset, _) =>
        {
            if (!pulled && path.Contains("2.avi") && offset >= Block)
            {
                pulled = true;
                rig.Dest.Removed = true;
                return new DestinationException(DestinationErrorKind.DeviceGone, "pendrive removido");
            }
            return null;
        };
        int polls = 0;
        rig.Clock.OnDelay = _ => { if (++polls == 3) rig.Dest.Removed = false; };

        var result = await rig.RunWithTimeout();

        rig.Progress.SawPhase(ExtractionPhase.WaitingForDestination).Should().BeTrue();
        rig.Progress.Messages.Should().Contain(m => m.Contains("reconecte"));
        result.Files.Should().HaveCount(3).And.OnlyContain(f => f.Status == FileStatus.Intact);
        rig.Reader.Opened.Count(p => p == "1.avi").Should().Be(1, "finished files are validated by size, not copied again");
        rig.Reader.Opened.Count(p => p == "2.avi").Should().Be(2);
        foreach (var (path, bytes) in rig.Contents) rig.Written(path).Should().Equal(bytes);
        rig.Dest.PartialFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task E16_FinishedFileMissingWhenTheDestinationReturns_IsCopiedAgain()
    {
        var rig = new Rig();
        for (int i = 1; i <= 2; i++) rig.AddFile($"{i}.avi", 200_000);
        bool pulled = false;
        rig.Dest.OnWrite = (path, offset, _) =>
        {
            if (!pulled && path.Contains("2.avi") && offset >= Block)
            {
                pulled = true;
                rig.Dest.Removed = true;
                return new DestinationException(DestinationErrorKind.DeviceGone, "pendrive removido");
            }
            return null;
        };
        int polls = 0;
        rig.Clock.OnDelay = _ =>
        {
            if (++polls == 2) { rig.Dest.Removed = false; rig.Dest.Truncate(rig.DestPath("1.avi"), 10); }
        };

        var result = await rig.RunWithTimeout();

        result.Files.Should().OnlyContain(f => f.Status == FileStatus.Intact);
        rig.Reader.Opened.Count(p => p == "1.avi").Should().Be(2, "its size no longer matched");
        rig.Written("1.avi").Should().Equal(rig.Contents["1.avi"]);
    }

    [Fact]
    public async Task E16_AnotherVolumeComesBack_StopsTheJob_AndKeepsTheStateForALaterResume()
    {
        var rig = new Rig();
        for (int i = 1; i <= 2; i++) rig.AddFile($"{i}.avi", 200_000);
        bool pulled = false;
        rig.Dest.OnWrite = (path, offset, _) =>
        {
            if (!pulled && path.Contains("2.avi") && offset >= Block)
            {
                pulled = true;
                rig.Dest.Removed = true;
                return new DestinationException(DestinationErrorKind.DeviceGone, "pendrive removido");
            }
            return null;
        };
        int polls = 0;
        rig.Clock.OnDelay = _ => { if (++polls == 2) { rig.Dest.Removed = false; rig.Dest.VolumeLabel = "OUTRO PENDRIVE"; } };

        var result = await rig.RunWithTimeout();

        result.Status.Should().Be(OverallStatus.Failed);
        result.Files.Single(f => f.RelativePath == "1.avi").Status.Should().Be(FileStatus.Intact);
        result.Files.Single(f => f.RelativePath == "2.avi").Status.Should().Be(FileStatus.Failed);
        rig.Engine.CanResume(rig.Disc, Rig.DestRoot, Rig.Folder).Should().BeTrue();
        rig.Drives.LockCalls.Should().Equal(true, false);
    }

    [Fact]
    public async Task E16_DestinationVanishesAndNeverReturns_CancelWorks()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 200_000);
        rig.Dest.OnWrite = (_, _, _) => { rig.Dest.Removed = true; return new DestinationException(DestinationErrorKind.DeviceGone, "removido"); };
        using var cts = new CancellationTokenSource();
        int polls = 0;
        rig.Clock.OnDelay = _ => { if (++polls == 5) cts.Cancel(); };

        var result = await rig.RunWithTimeout(cts.Token);

        result.Status.Should().Be(OverallStatus.Cancelled);
        rig.Drives.LockCalls.Should().Equal(true, false);
    }

    // ------------------------------------------------------------------ E-25 rename retries

    [Fact]
    public async Task E25_RenameBlockedByAntivirus_IsRetriedWithBackoff_ThenSucceeds()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 50_000);
        int failures = 0;
        rig.Dest.MoveFault = _ => failures++ < 3 ? new IOException("O arquivo está sendo usado por outro processo") : null;

        var result = await rig.RunWithTimeout();

        result.Files.Single().Status.Should().Be(FileStatus.Intact);
        rig.Dest.FileExists(rig.DestPath("a.avi")).Should().BeTrue();
        rig.Dest.PartialFiles.Should().BeEmpty();
        rig.Clock.Delays.Should().Equal(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public async Task E25_RenameAlwaysBlocked_GivesUpAfterTenSeconds_AsDestinationFailure()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 50_000);
        rig.Dest.MoveFault = _ => new IOException("O arquivo está sendo usado por outro processo");

        var result = await rig.RunWithTimeout();

        var f = result.Files.Single();
        f.Status.Should().Be(FileStatus.DestinationFailure);
        f.Error.Should().Contain("em uso");
        rig.Clock.Delays.Sum(d => d.TotalMilliseconds).Should().Be(10_000);
        rig.Clock.Delays.Should().BeInAscendingOrder().And.HaveCountGreaterThan(4);
        rig.Dest.FileExists(rig.DestPath("a.avi")).Should().BeFalse();
        rig.Dest.PartialFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task ExistingForeignFile_IsNeverOverwritten_TheCopyGetsASuffixInstead()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 50_000);
        rig.Dest.SeedFile(rig.DestPath("a.avi"), new byte[] { 1, 2, 3 });
        var result = await rig.Engine.RunAsync(rig.Request(), rig.Progress, rig.Control, default);

        rig.Dest.ReadAllBytes(rig.DestPath("a.avi")).Should().Equal(1, 2, 3);
        result.Files.Single().WrittenPath.Should().Be("a (2).avi");
        rig.Written("a (2).avi").Should().Equal(rig.Contents["a.avi"]);
    }

    [Fact]
    public async Task SetTimesFailure_DoesNotFailTheFile()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 50_000);
        rig.Dest.SetTimesFault = _ => new IOException("em uso");

        var result = await rig.RunWithTimeout();

        result.Files.Single().Status.Should().Be(FileStatus.Intact);
        rig.Dest.FileExists(rig.DestPath("a.avi")).Should().BeTrue();
    }
}
