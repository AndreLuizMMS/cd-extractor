using FluentAssertions;
using MediaRescue.Core;
using MediaRescue.Core.Tests.Fakes;

namespace MediaRescue.Core.Tests;

/// <summary>RF-05 two-pass tolerant reading: E-10, E-11, E-12, timeouts, budget, skip.</summary>
public class EngineErrorToleranceTests
{
    private const int Sector = 2048;
    private const int Block = 64 * 1024;

    [Fact]
    public async Task E10_TransientError_ClearsOnTheImmediateRetry_AndIsReportedAsRecovered()
    {
        var rig = new Rig();
        var disc = rig.AddFile("a.avi", 300_000);
        disc.FailTimes(Block * 2, Block, times: 1);

        var result = await rig.RunWithTimeout();

        var f = result.Files.Single();
        f.Status.Should().Be(FileStatus.Recovered);
        f.BadRanges.Should().BeEmpty();
        f.Sha256.Should().Be(TestData.Sha(rig.Contents["a.avi"]));
        rig.Written("a.avi").Should().Equal(rig.Contents["a.avi"]);
        disc.Log.Count(r => r.Offset == Block * 2).Should().Be(2, "the failed block is retried once");
        result.Status.Should().Be(OverallStatus.Recovered);
    }

    [Fact]
    public async Task E10_TransientError_NeedingTheSecondPass_IsRecovered_WithTheOriginalHash()
    {
        var rig = new Rig();
        var disc = rig.AddFile("a.avi", 400_000);
        disc.FailTimes(Block * 2, Block, times: 3); // pass 1 (read + retry) and the first pass-2 attempt fail

        var result = await rig.RunWithTimeout();

        var f = result.Files.Single();
        f.Status.Should().Be(FileStatus.Recovered);
        f.BytesLost.Should().Be(0);
        f.BadRanges.Should().BeEmpty();
        f.Sha256.Should().Be(TestData.Sha(rig.Contents["a.avi"]));
        rig.Written("a.avi").Should().Equal(rig.Contents["a.avi"]);
        rig.Progress.SawPhase(ExtractionPhase.SecondPass).Should().BeTrue();
    }

    [Fact]
    public async Task E10_PermanentBadSectors_YieldPartial_WithExactRanges_AndZerosOnlyThere()
    {
        var rig = new Rig();
        const int size = 1_048_576;
        var disc = rig.AddFile("video.mpg", size);
        disc.BadRange(200 * Sector, 2 * Sector);   // 409600..413696
        disc.BadRange(440 * Sector, 1 * Sector);   // 901120..903168

        var result = await rig.RunWithTimeout();

        var f = result.Files.Single();
        f.Status.Should().Be(FileStatus.Partial);
        f.BadRanges.Should().Equal(new ByteRange(200 * Sector, 2 * Sector), new ByteRange(440 * Sector, Sector));
        f.BytesLost.Should().Be(3 * Sector);
        f.BytesTotal.Should().Be(size);
        result.Status.Should().Be(OverallStatus.Partial);

        var written = rig.Written("video.mpg");
        written.Length.Should().Be(size, "the output keeps the original size");
        var original = rig.Contents["video.mpg"];
        for (int i = 0; i < size; i++)
        {
            bool lost = (i >= 200 * Sector && i < 202 * Sector) || (i >= 440 * Sector && i < 441 * Sector);
            if (lost) written[i].Should().Be(0, $"byte {i} is inside a lost range");
            else if (written[i] != original[i]) throw new Xunit.Sdk.XunitException($"byte {i} differs outside the lost ranges");
        }
        f.Sha256.Should().Be(TestData.Sha(written), "the hash of a partial file is that of the recovered content");
        f.Sha256.Should().NotBe(TestData.Sha(original));
    }

    [Fact]
    public async Task E10_BadRangeAtTheEndOfTheFile_IsClampedToTheFileLength()
    {
        var rig = new Rig();
        const int size = 100_000; // last sector starts at 98304, 1696 bytes long
        rig.AddFile("a.avi", size).BadRange(98_304, Sector);

        var result = await rig.RunWithTimeout();

        var f = result.Files.Single();
        f.Status.Should().Be(FileStatus.Partial);
        f.BadRanges.Should().Equal(new ByteRange(98_304, 1_696));
        f.BytesLost.Should().Be(1_696);
        rig.Written("a.avi").Length.Should().Be(size);
    }

    [Fact]
    public async Task E10_AdjacentBadSectors_AreMergedIntoOneRange()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 600_000).BadRange(100 * Sector, 5 * Sector);

        var result = await rig.RunWithTimeout();

        result.Files.Single().BadRanges.Should().Equal(new ByteRange(100 * Sector, 5 * Sector));
    }

    [Fact]
    public async Task E10_GoodSectorsInsideASkippedRegion_AreRecoveredInPassTwo()
    {
        var rig = new Rig();
        var disc = rig.AddFile("a.avi", 2_000_000);
        disc.BadRange(Block * 4, Sector); // a single bad sector: pass 1 zero-fills and skips ahead, pass 2 gets the rest back

        var result = await rig.RunWithTimeout();

        var f = result.Files.Single();
        f.BadRanges.Should().Equal(new ByteRange(Block * 4, Sector));
        f.BytesLost.Should().Be(Sector);
        var written = rig.Written("a.avi");
        var original = rig.Contents["a.avi"];
        written.AsSpan(0, Block * 4).SequenceEqual(original.AsSpan(0, Block * 4)).Should().BeTrue();
        written.AsSpan(Block * 4 + Sector).SequenceEqual(original.AsSpan(Block * 4 + Sector)).Should().BeTrue();
    }

    [Fact]
    public async Task E10_FullyUnreadableFile_Fails_AndLeavesNothingBehind()
    {
        var rig = new Rig();
        rig.AddFile("ruim.avi", 200_000).BadRange(0, 200_000);
        rig.AddFile("bom.avi", 50_000);

        var result = await rig.RunWithTimeout();

        var bad = result.Files.Single(f => f.RelativePath == "ruim.avi");
        bad.Status.Should().Be(FileStatus.Failed);
        bad.BytesLost.Should().Be(200_000);
        bad.Sha256.Should().BeNull();
        bad.Error.Should().NotBeNullOrEmpty();
        rig.Dest.FileExists(rig.DestPath("ruim.avi")).Should().BeFalse();
        rig.Dest.PartialFiles.Should().BeEmpty();
        result.Files.Single(f => f.RelativePath == "bom.avi").Status.Should().Be(FileStatus.Intact);
        result.Status.Should().Be(OverallStatus.Partial);
    }

    [Fact]
    public async Task E10_AllFilesUnreadable_GivesOverallFailed()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 10_000).BadRange(0, 10_000);

        var result = await rig.RunWithTimeout();

        result.Status.Should().Be(OverallStatus.Failed);
    }

    [Fact]
    public async Task ProgressiveSkip_Doubles_From64KiB_To4MiB_AndResetsAfterAGoodRead()
    {
        var rig = new Rig();
        rig.Options.SecondPassBudget = TimeSpan.Zero; // look at pass 1 only
        const int mib = 1 << 20;
        var disc = rig.AddFile("big.vob", 24 * mib);
        disc.BadRange(0, 16 * mib);
        disc.BadRange(22 * mib, 1 * mib);

        var result = await rig.RunWithTimeout();

        // distinct read positions, with the retry (same offset twice) collapsed
        var reads = new List<ReadRecord>();
        foreach (var r in disc.Log) if (reads.Count == 0 || reads[^1].Offset != r.Offset || reads[^1].Ok) reads.Add(r);
        var gaps = new List<long>();
        var firstGapOfEachStreak = new List<long>();
        for (int i = 0; i + 1 < reads.Count; i++)
        {
            if (reads[i].Ok) continue;
            long gap = reads[i + 1].Offset - (reads[i].Offset + Block);
            gaps.Add(gap);
            if (i == 0 || reads[i - 1].Ok) firstGapOfEachStreak.Add(gap);
        }

        gaps.Take(9).Should().Equal(64 * 1024, 128 * 1024, 256 * 1024, 512 * 1024, mib, 2 * mib, 4 * mib, 4 * mib, 4 * mib);
        gaps.Should().OnlyContain(g => g <= 4 * mib);
        firstGapOfEachStreak.Should().Equal(new long[] { 64 * 1024, 64 * 1024 }); // starts over at 64 KiB after good reads
        disc.Log.Where(r => !r.Ok).Select(r => r.Offset).Distinct().Count().Should().BeLessThan(40, "skipping avoids reading every bad block");
        result.Files.Single().Status.Should().Be(FileStatus.Partial);
        result.Files.Single().BytesLost.Should().BeGreaterThan(16 * mib);
    }

    [Fact]
    public async Task EveryBadBlock_IsReadTwice_BeforeSkippingAhead()
    {
        var rig = new Rig();
        rig.Options.SecondPassBudget = TimeSpan.Zero;
        var disc = rig.AddFile("a.avi", 10 * Block);
        disc.BadRange(3 * Block, Block);

        await rig.RunWithTimeout();

        disc.Log.Where(r => r.Offset == 3 * Block).Should().HaveCount(2).And.OnlyContain(r => !r.Ok);
    }

    [Fact]
    public async Task Reads_AreBlocksOf64KiB_AlignedToTheSectorSize()
    {
        var rig = new Rig();
        var disc = rig.AddFile("a.avi", 5 * Block + 1234);

        await rig.RunWithTimeout();

        disc.Log.Should().OnlyContain(r => r.Ok && r.Offset % Sector == 0);
        disc.Log.Select(r => r.Length).Distinct().Should().BeEquivalentTo(new[] { Block, 1234 });
    }

    [Fact]
    public async Task SectorSizeOfOne_BufferedReads_StillUses64KiBBlocks()
    {
        var rig = new Rig();
        var disc = rig.AddFile("a.dat", 3 * Block + 7, sectorSize: 1);

        var result = await rig.RunWithTimeout();

        result.Files.Single().Status.Should().Be(FileStatus.Intact);
        disc.Log.Take(3).Select(r => r.Length).Should().OnlyContain(l => l == Block);
    }

    [Fact]
    public async Task BlockTimeout_FromTheOptions_IsPassedToTheReader()
    {
        var rig = new Rig();
        rig.Options.BlockTimeout = TimeSpan.FromSeconds(7);
        var disc = rig.AddFile("a.avi", 200_000);

        await rig.RunWithTimeout();

        disc.Timeouts.Should().OnlyContain(t => t == TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task Timeout_IsTreatedAsABadSector()
    {
        var rig = new Rig();
        rig.Options.BlockTimeout = TimeSpan.FromSeconds(10);
        rig.Options.SecondPassBudget = TimeSpan.FromMinutes(10);
        var disc = rig.AddFile("a.avi", 400_000);
        disc.TimeoutRange(2 * Block, Sector);

        var result = await rig.RunWithTimeout();

        var f = result.Files.Single();
        f.Status.Should().Be(FileStatus.Partial);
        f.BadRanges.Should().Equal(new ByteRange(2 * Block, Sector));
        rig.Clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(20), "each timed-out attempt costs the whole timeout");
    }

    [Fact]
    public async Task E12_ReadThatNeverReturns_IsAbandonedByTheEngine_AfterTimeoutPlusGrace()
    {
        var rig = new Rig();
        rig.Options.BlockTimeout = TimeSpan.FromSeconds(10);
        rig.Options.SecondPassBudget = TimeSpan.Zero;
        var disc = rig.AddFile("a.avi", 300_000);
        disc.HangRange(2 * Block, Block, ignoreCancellation: true);

        var result = await rig.RunWithTimeout();

        var f = result.Files.Single();
        f.Status.Should().Be(FileStatus.Partial);
        f.BadRanges.Should().NotBeEmpty();
        f.BadRanges[0].Offset.Should().Be(2 * Block);
        rig.Clock.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(30), "two attempts of timeout (10 s) + grace (5 s)");
        var written = rig.Written("a.avi");
        written.AsSpan(0, 2 * Block).SequenceEqual(rig.Contents["a.avi"].AsSpan(0, 2 * Block)).Should().BeTrue();
    }

    [Fact]
    public async Task E12_Cancel_WhileTheDeviceIsHung_ReturnsPromptly_AndCleansUp()
    {
        var rig = new Rig();
        rig.Clock.AutoAdvance = false; // nothing advances time: only the cancel can end the hung read
        var disc = rig.AddFile("a.avi", 300_000);
        disc.HangRange(Block, Block, ignoreCancellation: true);
        using var cts = new CancellationTokenSource();

        var run = rig.Run(cts.Token);
        await disc.HangStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        result.Status.Should().Be(OverallStatus.Cancelled);
        rig.Dest.PartialFiles.Should().BeEmpty();
        rig.Dest.FileExists(rig.DestPath("a.avi")).Should().BeFalse();
        rig.Drives.LockCalls.Should().Equal(true, false);
        rig.Progress.Last.Phase.Should().Be(ExtractionPhase.Cancelled);
    }

    [Fact]
    public async Task Cancel_MidCopy_ReturnsCancelled_RemovesThePartial_AndKeepsFinishedFiles()
    {
        var rig = new Rig();
        rig.AddFile("um.avi", 100_000);
        var second = rig.AddFile("dois.avi", 300_000);
        rig.AddFile("tres.avi", 100_000);
        using var cts = new CancellationTokenSource();
        second.OnRead = (offset, _) => { if (offset >= Block) cts.Cancel(); };

        var result = await rig.RunWithTimeout(cts.Token);

        result.Status.Should().Be(OverallStatus.Cancelled);
        result.Files.Should().ContainSingle(f => f.RelativePath == "um.avi");
        rig.Written("um.avi").Should().Equal(rig.Contents["um.avi"]);
        rig.Dest.FileExists(rig.DestPath("dois.avi")).Should().BeFalse();
        rig.Dest.PartialFiles.Should().BeEmpty();
        rig.Reader.Opened.Should().NotContain("tres.avi");
        rig.Drives.LockCalls.Should().Equal(true, false);
    }

    [Fact]
    public async Task Cancel_BeforeTheFirstByte_StillReturnsCancelled()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 100_000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await rig.RunWithTimeout(cts.Token);

        result.Status.Should().Be(OverallStatus.Cancelled);
        rig.Dest.FileExists(rig.DestPath("a.avi")).Should().BeFalse();
    }

    [Fact]
    public async Task Pause_StopsAllReads_UntilResumed()
    {
        var rig = new Rig();
        var disc = rig.AddFile("a.avi", 10 * Block);
        int readsWhenPaused = -1;
        int pausePolls = 0;
        disc.OnRead = (offset, _) => { if (offset == 3L * Block) rig.Control.Pause(); };
        rig.Clock.OnDelay = _ =>
        {
            pausePolls++;
            if (pausePolls == 1) readsWhenPaused = disc.Log.Count;
            if (pausePolls == 6)
            {
                disc.Log.Count.Should().Be(readsWhenPaused, "no disc reads happen while paused");
                rig.Control.Resume();
            }
        };

        var result = await rig.RunWithTimeout();

        pausePolls.Should().BeGreaterThanOrEqualTo(6);
        rig.Progress.SawPhase(ExtractionPhase.Paused).Should().BeTrue();
        result.Files.Single().Status.Should().Be(FileStatus.Intact);
        rig.Written("a.avi").Should().Equal(rig.Contents["a.avi"]);
        rig.Progress.Items.Last(i => i.Phase != ExtractionPhase.Completed).Phase.Should().NotBe(ExtractionPhase.Paused);
    }

    [Fact]
    public async Task Pause_BeforeStart_WaitsBeforeTheFirstFile()
    {
        var rig = new Rig();
        var disc = rig.AddFile("a.avi", 3 * Block);
        rig.Control.Pause();
        int polls = 0;
        rig.Clock.OnDelay = _ =>
        {
            if (++polls == 4) { disc.Log.Should().BeEmpty(); rig.Control.Resume(); }
        };

        var result = await rig.RunWithTimeout();

        result.Status.Should().Be(OverallStatus.Intact);
    }

    [Fact]
    public async Task SkipCurrentFile_SkipsOnlyThatFile_AndRemovesItsPartial()
    {
        var rig = new Rig();
        var first = rig.AddFile("grande.avi", 10 * Block);
        rig.AddFile("outro.avi", 50_000);
        first.OnRead = (offset, _) => { if (offset == 2L * Block) rig.Control.SkipCurrentFile(); };

        var result = await rig.RunWithTimeout();

        result.Files.Single(f => f.RelativePath == "grande.avi").Status.Should().Be(FileStatus.Skipped);
        result.Files.Single(f => f.RelativePath == "outro.avi").Status.Should().Be(FileStatus.Intact);
        rig.Dest.FileExists(rig.DestPath("grande.avi")).Should().BeFalse();
        rig.Dest.PartialFiles.Should().BeEmpty();
        result.Status.Should().Be(OverallStatus.Partial, "something was not copied");
        rig.Dest.ReadText(rig.DestPath("LEIA-ME - ERROS.txt")).Should().Contain("grande.avi");
    }

    [Fact]
    public async Task SecondPassBudget_IsRespected_UsingTheClock()
    {
        var withSmall = await RunDamagedWithBudget(TimeSpan.FromSeconds(20), extra: TimeSpan.Zero);
        var withLarge = await RunDamagedWithBudget(TimeSpan.FromHours(1), extra: TimeSpan.Zero);

        withSmall.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(20)).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
        withLarge.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(150));
        // either way the unreadable part is reported exactly
        withSmall.Result.Files.Single().BadRanges.Should().Equal(withLarge.Result.Files.Single().BadRanges);
        withSmall.Result.Files.Single().BadRanges.Should().Equal(new ByteRange(Block, 2 * Block));
    }

    [Fact]
    public async Task ExtendSecondPass_GivesTheRecoveryMoreTime()
    {
        var plain = await RunDamagedWithBudget(TimeSpan.FromSeconds(20), extra: TimeSpan.Zero);
        var extended = await RunDamagedWithBudget(TimeSpan.FromSeconds(20), extra: TimeSpan.FromHours(1));

        extended.Elapsed.Should().BeGreaterThan(plain.Elapsed * 3);
    }

    [Fact]
    public async Task ZeroSecondPassBudget_SkipsPassTwo_AndKeepsPendingRangesAsLost()
    {
        var rig = new Rig();
        rig.Options.SecondPassBudget = TimeSpan.Zero;
        var disc = rig.AddFile("a.avi", 5 * Block);
        disc.BadRange(Block, Sector);

        var result = await rig.RunWithTimeout();

        var f = result.Files.Single();
        f.Status.Should().Be(FileStatus.Partial);
        // pass 1 zero-filled the failed block and the 64 KiB skipped after it\n        f.BadRanges.Should().Equal(new ByteRange(Block, 2 * Block));
        rig.Drives.SpeedCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task SecondPass_ReducesTheReadSpeed_AndRestoresItAfterwards()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 400_000).BadRange(Block, Sector);

        await rig.RunWithTimeout();

        rig.Drives.SpeedCalls.Should().HaveCount(2);
        rig.Drives.SpeedCalls[0].Should().BeGreaterThan(0);
        rig.Drives.SpeedCalls[1].Should().Be(0);
    }

    [Fact]
    public async Task SecondPass_DoesNotTouchTheReadSpeed_WhenDisabledInTheOptions()
    {
        var rig = new Rig();
        rig.Options.ReduceSpeedOnError = false;
        rig.AddFile("a.avi", 400_000).BadRange(Block, Sector);

        await rig.RunWithTimeout();

        rig.Drives.SpeedCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task SecondPass_UnsupportedSpeedChange_IsHarmless()
    {
        var rig = new Rig();
        rig.Drives.SpeedSupported = false;
        rig.AddFile("a.avi", 400_000).BadRange(Block, Sector);

        var result = await rig.RunWithTimeout();

        result.Files.Single().Status.Should().Be(FileStatus.Partial);
        rig.Drives.SpeedCalls.Should().ContainSingle(s => s > 0, "no restore call when the reduction was not applied");
    }

    [Fact]
    public async Task RetriesPerSector_ControlsHowOftenAnUnreadableSectorIsTried()
    {
        var rig = new Rig();
        rig.Options.RetriesPerSector = 5;
        var disc = rig.AddFile("a.avi", 400_000);
        disc.BadRange(Block, Sector);

        await rig.RunWithTimeout();

        // the sector at 65536 is attempted individually exactly 5 times in pass 2
        disc.Log.Count(r => r.Offset == Block && r.Length == Sector).Should().Be(5);
    }

    [Fact]
    public async Task E11_MostlyUnreadableFile_FinishesWithinTheBudget_AndWarns()
    {
        var rig = new Rig();
        rig.Options.SecondPassBudget = TimeSpan.FromSeconds(60);
        var disc = rig.AddFile("a.avi", 4 * 1024 * 1024);
        disc.BadRange(Block, 4 * 1024 * 1024 - Block);
        disc.FailDelay = TimeSpan.FromSeconds(1);

        var result = await rig.RunWithTimeout();

        var f = result.Files.Single();
        f.Status.Should().Be(FileStatus.Partial);
        f.BytesLost.Should().Be(4 * 1024 * 1024 - Block);
        rig.Progress.Messages.Should().Contain(m => m.Contains("metade"));
        rig.Clock.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(5), "bounded by the second-pass budget, never an endless loop");
    }

    [Fact]
    public async Task UnexpectedReaderException_IsTreatedLikeABadRead_NotACrash()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 50_000);
        rig.AddFile("b.avi", 50_000);
        rig.Reader.OpenFault = p => p == "a.avi" ? new InvalidOperationException("driver bug") : null;

        var result = await rig.RunWithTimeout();

        result.Files.Single(f => f.RelativePath == "a.avi").Status.Should().Be(FileStatus.Failed);
        result.Files.Single(f => f.RelativePath == "a.avi").Error.Should().Contain("driver bug");
        result.Files.Single(f => f.RelativePath == "b.avi").Status.Should().Be(FileStatus.Intact);
    }

    [Fact]
    public async Task E13_UnreadableDirectory_IsReported_AndTheRestIsCopied()
    {
        var rig = new Rig();
        rig.AddDirectory("Pasta Boa");
        rig.AddDirectory("Pasta Ruim", error: "erro de E/S");
        rig.AddFile(@"Pasta Boa\a.avi", 20_000);

        var result = await rig.RunWithTimeout();

        result.Files.Should().Contain(f => f.RelativePath == "Pasta Ruim" && f.Status == FileStatus.Failed && f.Error!.Contains("ilegível"));
        result.Files.Single(f => f.RelativePath == @"Pasta Boa\a.avi").Status.Should().Be(FileStatus.Intact);
        rig.Dest.DirectoryExists(rig.DestPath("Pasta Ruim")).Should().BeFalse();
        rig.Dest.ReadText(rig.DestPath("LEIA-ME - ERROS.txt")).Should().Contain("Pasta Ruim");
    }

    [Fact]
    public async Task ShortReadInTheMiddle_IsContinuedFromWhereItStopped()
    {
        var rig = new Rig();
        var data = TestData.Bytes(3 * Block, 9);
        rig.Contents["a.avi"] = data;
        rig.Entries.Add(new DiscEntry { RelativePath = "a.avi", Size = data.Length, ModifiedUtc = Rig.DefaultModified });
        rig.Reader.Add("a.avi", data);
        var shortFile = new ShortReadDisc(data);
        var reader = new SingleFileReader(shortFile);
        var engine = new ExtractionEngine(reader, rig.Dest, rig.Drives, rig.Clock);

        var result = await engine.RunAsync(rig.Request(), rig.Progress, rig.Control, default);

        result.Files.Single().Status.Should().Be(FileStatus.Intact);
        rig.Written("a.avi").Should().Equal(data);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(ExtractionResult Result, TimeSpan Elapsed)> RunDamagedWithBudget(TimeSpan budget, TimeSpan extra)
    {
        var rig = new Rig();
        rig.Options.SecondPassBudget = budget;
        var disc = rig.AddFile("a.avi", 4 * Block);
        disc.BadRange(Block, 2 * Block);
        disc.FailDelay = TimeSpan.FromSeconds(1);
        if (extra > TimeSpan.Zero) rig.Control.ExtendSecondPass(extra);
        var result = await rig.RunWithTimeout();
        return (result, rig.Clock.Elapsed);
    }

    private sealed class SingleFileReader : IDiscReader
    {
        private readonly IDiscFile _f;
        public SingleFileReader(IDiscFile f) => _f = f;
        public IDiscFile Open(string driveLetter, string relativePath) => _f;
    }

    /// <summary>Returns at most 30000 bytes per call (not a multiple of the sector size): legal short reads mid-file.</summary>
    private sealed class ShortReadDisc : IDiscFile
    {
        private readonly byte[] _d;
        public ShortReadDisc(byte[] d) => _d = d;
        public long Length => _d.Length;
        public int SectorSize => 1;
        public void Dispose() { }
        public Task<int> ReadAsync(long offset, Memory<byte> buffer, TimeSpan timeout, CancellationToken ct)
        {
            int n = (int)Math.Min(Math.Min(buffer.Length, 30_000), _d.Length - offset);
            _d.AsMemory((int)offset, n).CopyTo(buffer);
            return Task.FromResult(n);
        }
    }
}
