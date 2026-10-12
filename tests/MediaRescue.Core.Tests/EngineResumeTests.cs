using System.Text.Json;
using FluentAssertions;
using MediaRescue.Core;
using MediaRescue.Core.Tests.Fakes;

namespace MediaRescue.Core.Tests;

/// <summary>RF-10 resume and E-27 (power loss / forced close).</summary>
public class EngineResumeTests
{
    private const int Block = 64 * 1024;

    private static Rig FourFileRig()
    {
        var rig = new Rig();
        for (int i = 1; i <= 4; i++) rig.AddFile($"{i}.avi", 150_000);
        return rig;
    }

    /// <summary>Runs until reading file number <paramref name="n"/>, then cancels (like closing the app).</summary>
    private static async Task<ExtractionResult> InterruptAt(Rig rig, int n)
    {
        using var cts = new CancellationTokenSource();
        rig.Reader.File($"{n}.avi").OnRead = (offset, _) => { if (offset >= Block) cts.Cancel(); };
        var result = await rig.RunWithTimeout(cts.Token);
        rig.Reader.File($"{n}.avi").OnRead = null;
        return result;
    }

    [Fact]
    public async Task RF10_InterruptedJob_ResumesWithoutRecopyingFinishedFiles()
    {
        var rig = FourFileRig();
        var first = await InterruptAt(rig, 3);
        first.Status.Should().Be(OverallStatus.Cancelled);
        rig.Engine.CanResume(rig.Disc, Rig.DestRoot, Rig.Folder).Should().BeTrue();
        // as after a crash: a stale partial of the file that was in flight
        rig.Dest.SeedFile(rig.DestPath("3.avi.partial"), new byte[] { 9, 9, 9 });
        rig.Reader.Opened.Clear();

        var result = await rig.RunWithTimeout();

        rig.Reader.Opened.Should().Equal("3.avi", "4.avi");
        result.Status.Should().Be(OverallStatus.Intact);
        result.Files.Select(f => f.RelativePath).Should().BeEquivalentTo(new[] { "1.avi", "2.avi", "3.avi", "4.avi" });
        result.Files.Should().OnlyContain(f => f.Status == FileStatus.Intact && f.Sha256 != null);
        foreach (var (path, bytes) in rig.Contents)
        {
            rig.Written(path).Should().Equal(bytes);
            result.Files.Single(f => f.RelativePath == path).Sha256.Should().Be(TestData.Sha(bytes));
        }
        rig.Dest.PartialFiles.Should().BeEmpty();
        rig.Dest.FileExists(rig.DestPath(".mediarescue-job.json")).Should().BeFalse("the state file is removed on success");
        rig.Engine.CanResume(rig.Disc, Rig.DestRoot, Rig.Folder).Should().BeFalse();
    }

    [Fact]
    public async Task RF10_ResumedRun_ReportsTheFinishedFilesInProgressAndTotals()
    {
        var rig = FourFileRig();
        await InterruptAt(rig, 3);
        rig.Progress.OnReport = null;

        await rig.RunWithTimeout();

        var last = rig.Progress.Last;
        last.FileCount.Should().Be(4);
        last.FilesIntact.Should().Be(4);
        last.TotalBytesDone.Should().Be(4 * 150_000);
    }

    [Fact]
    public async Task RF10_StaleStateOfAnotherDisc_IsNotResumed_AndNothingIsOverwritten()
    {
        var rig = FourFileRig();
        await InterruptAt(rig, 3);
        var originalOne = rig.Written("1.avi");
        rig.Serial = "9999-ZZZZ"; // a different disc with the same file names
        rig.Engine.CanResume(rig.Disc, Rig.DestRoot, Rig.Folder).Should().BeFalse();

        var result = await rig.RunWithTimeout();

        result.Files.Should().OnlyContain(f => f.Status == FileStatus.Intact);
        rig.Dest.ReadAllBytes(rig.DestPath("1.avi")).Should().Equal(originalOne, "files of the other job are never overwritten");
        result.Files.Single(f => f.RelativePath == "1.avi").WrittenPath.Should().Be("1 (2).avi");
    }

    [Fact]
    public async Task RF10_FinishedFileWhoseSizeChanged_IsCopiedAgain()
    {
        var rig = FourFileRig();
        await InterruptAt(rig, 3);
        rig.Dest.Truncate(rig.DestPath("2.avi"), 100);
        rig.Reader.Opened.Clear();

        var result = await rig.RunWithTimeout();

        rig.Reader.Opened.Should().Equal("2.avi", "3.avi", "4.avi");
        rig.Written("2.avi").Should().Equal(rig.Contents["2.avi"]);
        result.Status.Should().Be(OverallStatus.Intact);
    }

    [Fact]
    public async Task RF10_FinishedFileThatWasDeleted_IsCopiedAgain()
    {
        var rig = FourFileRig();
        await InterruptAt(rig, 3);
        rig.Dest.Delete(rig.DestPath("1.avi"));
        rig.Reader.Opened.Clear();

        await rig.RunWithTimeout();

        rig.Reader.Opened.Should().Contain("1.avi");
        rig.Written("1.avi").Should().Equal(rig.Contents["1.avi"]);
    }

    [Fact]
    public async Task RF10_PartialFilesFromThePreviousRun_KeepTheirStatusAndBadRanges()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 300_000).BadRange(64 * 2048, 2048);
        rig.AddFile("b.avi", 100_000);
        rig.AddFile("c.avi", 100_000);
        using var cts = new CancellationTokenSource();
        rig.Reader.File("c.avi").OnRead = (_, _) => cts.Cancel();
        await rig.RunWithTimeout(cts.Token);
        rig.Reader.File("c.avi").OnRead = null;
        rig.Reader.Opened.Clear();

        var result = await rig.RunWithTimeout();

        rig.Reader.Opened.Should().Equal("c.avi");
        var a = result.Files.Single(f => f.RelativePath == "a.avi");
        a.Status.Should().Be(FileStatus.Partial);
        a.BadRanges.Should().Equal(new ByteRange(64 * 2048, 2048));
        result.Status.Should().Be(OverallStatus.Partial);
    }

    [Fact]
    public async Task RF10_ACorruptedStateFile_FallsBackToItsBackup()
    {
        var rig = FourFileRig();
        await InterruptAt(rig, 4);
        var statePath = rig.DestPath(".mediarescue-job.json");
        rig.Dest.SeedFile(statePath, System.Text.Encoding.UTF8.GetBytes("{ not json"));
        rig.Dest.AllFiles.Should().Contain(statePath + ".bak");

        rig.Engine.CanResume(rig.Disc, Rig.DestRoot, Rig.Folder).Should().BeTrue();
        rig.Reader.Opened.Clear();
        var result = await rig.RunWithTimeout();

        result.Status.Should().Be(OverallStatus.Intact);
        rig.Reader.Opened.Count.Should().BeLessThan(4, "the files recorded in the backup are not copied again");
    }

    [Fact]
    public void CanResume_IsFalse_WithoutAJobState()
    {
        var rig = FourFileRig();
        rig.Engine.CanResume(rig.Disc, Rig.DestRoot, Rig.Folder).Should().BeFalse();
    }

    [Fact]
    public void CanResume_IsFalse_AndDoesNotThrow_WhenTheDestinationIsGone()
    {
        var rig = FourFileRig();
        rig.Dest.Removed = true;
        rig.Engine.CanResume(rig.Disc, Rig.DestRoot, Rig.Folder).Should().BeFalse();
    }

    [Fact]
    public async Task ResumeState_IsUpdatedAfterEveryFinishedFile()
    {
        var rig = FourFileRig();
        var completedCounts = new List<int>();
        rig.Dest.OnAtomicWrite = (path, content) =>
        {
            if (!path.EndsWith(".mediarescue-job.json")) return;
            using var doc = JsonDocument.Parse(content);
            doc.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(1);
            completedCounts.Add(doc.RootElement.GetProperty("files").EnumerateArray().Count(f => f.GetProperty("completed").GetBoolean()));
        };

        await rig.RunWithTimeout();

        completedCounts.Should().Equal(0, 1, 2, 3, 4);
    }

    [Fact]
    public async Task ResumeState_SurvivesCancellation_AndRecordsTheFingerprint()
    {
        var rig = FourFileRig();
        await InterruptAt(rig, 2);

        using var doc = JsonDocument.Parse(rig.Dest.ReadText(rig.DestPath(".mediarescue-job.json")));
        doc.RootElement.GetProperty("fingerprint").GetString().Should().Be(rig.Disc.Fingerprint);
        doc.RootElement.GetProperty("files").EnumerateArray().Count(f => f.GetProperty("completed").GetBoolean()).Should().Be(1);
    }

    // ------------------------------------------------------------------ E-27: crash at any point

    public static IEnumerable<object[]> CrashPoints() => Enumerable.Range(1, 30).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public async Task E27_PowerLossAtAnyPoint_NeverLeavesAnIncompleteFileWithAFinalName_AndTheJobResumes(int crashAtOperation)
    {
        var rig = FourFileRig();
        rig.AddFile(@"Sub\5.avi", 90_000);
        rig.AddFile("vazio.bin", 0);
        FakeDestination? snapshot = null;
        int ops = 0;
        void Tick() { if (++ops == crashAtOperation) snapshot = rig.Dest.Clone(); }
        rig.Dest.OnWrite = (_, _, _) => { Tick(); return null; };
        rig.Dest.OnMoveStart = _ => Tick();
        rig.Dest.OnAtomicWrite = (_, _) => Tick();

        await rig.RunWithTimeout();

        if (snapshot == null) return; // the run had fewer operations than this crash point

        // 1. at the instant of the "crash" every file with a final name is complete and correct
        foreach (var (path, bytes) in rig.Contents)
        {
            var full = rig.DestPath(path);
            if (snapshot.AllFiles.Contains(full, StringComparer.OrdinalIgnoreCase))
                snapshot.ReadAllBytes(full).Should().Equal(bytes, $"{path} must be complete if it has its final name");
        }

        // 2. the app restarts against that disk state: it finishes the job without redoing verified files
        var rig2 = new Rig(snapshot);
        foreach (var (path, bytes) in rig.Contents) rig2.AddFile(path, bytes);
        var result = await rig2.RunWithTimeout();

        result.Status.Should().Be(OverallStatus.Intact);
        result.Files.Should().HaveCount(rig.Contents.Count);
        foreach (var (path, bytes) in rig.Contents) rig2.Written(path).Should().Equal(bytes);
        rig2.Dest.PartialFiles.Should().BeEmpty();
        rig2.Dest.FileExists(rig2.DestPath(".mediarescue-job.json")).Should().BeFalse();
    }
}
