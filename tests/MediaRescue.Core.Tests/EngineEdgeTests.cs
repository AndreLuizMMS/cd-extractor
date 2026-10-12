using System.Text.Json;
using FluentAssertions;
using MediaRescue.Core;
using MediaRescue.Core.Tests.Fakes;

namespace MediaRescue.Core.Tests;

/// <summary>Less common engine paths: abandoned reads, non-seekable destinations, big jobs, resume with a changed selection.</summary>
public class EngineEdgeTests
{
    private const int Block = 64 * 1024;
    private const int Sector = 2048;

    [Fact]
    public async Task AbandonedRead_ThatCompletesLater_CannotCorruptTheDataOfLaterReads()
    {
        var rig = new Rig();
        var data = TestData.Bytes(4 * Block, 77);
        rig.Contents["a.avi"] = data;
        rig.Entries.Add(new DiscEntry { RelativePath = "a.avi", Size = data.Length, ModifiedUtc = Rig.DefaultModified });
        var zombie = new ZombieDisc(data, hangAtOffset: Block);
        var engine = new ExtractionEngine(new SingleFileReader(zombie), rig.Dest, rig.Drives, rig.Clock);

        var result = await engine.RunAsync(rig.Request(), rig.Progress, rig.Control, default);

        zombie.HangsServed.Should().BeGreaterThan(0);
        zombie.GarbageWrites.Should().BeGreaterThan(0, "the abandoned read did 'complete' later and scribble over its old buffer");
        result.Files.Single().Status.Should().Be(FileStatus.Recovered);
        rig.Written("a.avi").Should().Equal(data);
    }

    [Fact]
    public async Task E12_ReadThatHonoursCancellationButNotItsTimeout_IsAlsoAbandoned()
    {
        var rig = new Rig();
        rig.Options.SecondPassBudget = TimeSpan.Zero;
        var disc = rig.AddFile("a.avi", 300_000);
        disc.HangRange(Block, Block, ignoreCancellation: false);

        var result = await rig.RunWithTimeout();

        result.Files.Single().Status.Should().Be(FileStatus.Partial);
        result.Files.Single().BadRanges[0].Offset.Should().Be(Block);
    }

    [Fact]
    public async Task Cancel_DuringTheSecondPass_StopsRecovering_AndCleansUp()
    {
        var rig = new Rig();
        var disc = rig.AddFile("a.avi", 600_000);
        disc.BadRange(100 * Sector, 20 * Sector);
        using var cts = new CancellationTokenSource();
        int sectorReads = 0;
        disc.OnRead = (_, len) => { if (len == Sector && ++sectorReads == 5) cts.Cancel(); };

        var result = await rig.RunWithTimeout(cts.Token);

        result.Status.Should().Be(OverallStatus.Cancelled);
        sectorReads.Should().Be(5);
        rig.Dest.PartialFiles.Should().BeEmpty();
        rig.Dest.FileExists(rig.DestPath("a.avi")).Should().BeFalse();
        rig.Drives.SpeedCalls.Should().Equal(600, 0);
    }

    [Fact]
    public async Task Skip_DuringTheSecondPass_AbandonsTheRecoveryOfThatFile()
    {
        var rig = new Rig();
        var disc = rig.AddFile("ruim.avi", 600_000);
        disc.BadRange(100 * Sector, 20 * Sector);
        rig.AddFile("bom.avi", 50_000);
        int sectorReads = 0;
        disc.OnRead = (_, len) => { if (len == Sector && ++sectorReads == 3) rig.Control.SkipCurrentFile(); };

        var result = await rig.RunWithTimeout();

        result.Files.Single(f => f.RelativePath == "ruim.avi").Status.Should().Be(FileStatus.Skipped);
        result.Files.Single(f => f.RelativePath == "bom.avi").Status.Should().Be(FileStatus.Intact);
        rig.Dest.PartialFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task ZeroRetriesPerSector_IsTreatedAsOne_AndStillTerminates()
    {
        var rig = new Rig();
        rig.Options.RetriesPerSector = 0;
        var disc = rig.AddFile("a.avi", 300_000);
        disc.BadRange(Block, Sector);

        var result = await rig.RunWithTimeout();

        result.Files.Single().BadRanges.Should().Equal(new ByteRange(Block, Sector));
        disc.Log.Count(r => r.Offset == Block && r.Length == Sector).Should().Be(1);
    }

    [Fact]
    public async Task TheRealFileLength_WinsOverTheSizeListedInTheDirectory()
    {
        var rig = new Rig();
        var data = TestData.Bytes(5000, 3);
        rig.Contents["a.avi"] = data;
        rig.Entries.Add(new DiscEntry { RelativePath = "a.avi", Size = 1000, ModifiedUtc = Rig.DefaultModified }); // stale listing
        rig.Reader.Add("a.avi", data);

        var result = await rig.RunWithTimeout();

        result.Files.Single().BytesTotal.Should().Be(5000);
        rig.Written("a.avi").Should().Equal(data);
        rig.Progress.Last.TotalBytesTotal.Should().Be(5000);
        rig.Progress.Items.Should().OnlyContain(i => i.TotalBytesDone <= i.TotalBytesTotal);
    }

    [Fact]
    public async Task DuplicateAndBlankEntries_AreIgnored()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 1000);
        var entries = new List<DiscEntry>(rig.Entries)
        {
            rig.Entries[0], new() { RelativePath = "" }, new() { RelativePath = "   " },
        };

        var result = await rig.Engine.RunAsync(rig.Request(entries), rig.Progress, rig.Control, default);

        result.Files.Should().ContainSingle();
        rig.Reader.Opened.Should().ContainSingle();
    }

    [Fact]
    public async Task OnlyDirectoriesSelected_JustCreatesThem()
    {
        var rig = new Rig();
        rig.AddDirectory(@"A\B");
        rig.AddDirectory("C");

        var result = await rig.RunWithTimeout();

        result.Status.Should().Be(OverallStatus.Intact);
        result.Files.Should().BeEmpty();
        rig.Dest.DirectoryExists(rig.DestPath(@"A\B")).Should().BeTrue();
        rig.Dest.DirectoryExists(rig.DestPath("C")).Should().BeTrue();
    }

    [Fact]
    public async Task NonSeekableDestination_StillCopiesCleanFiles_AndZeroFillsUnreadableRanges()
    {
        var rig = new Rig();
        rig.Dest.NonSeekable = true;
        rig.AddFile("bom.avi", 300_000);
        rig.AddFile("ruim.avi", 300_000).BadRange(Block, Sector);

        var result = await rig.RunWithTimeout();

        result.Files.Single(f => f.RelativePath == "bom.avi").Status.Should().Be(FileStatus.Intact);
        var ruim = result.Files.Single(f => f.RelativePath == "ruim.avi");
        ruim.Status.Should().Be(FileStatus.Partial);
        // without seeking the second pass cannot patch the file: pass 1 zero-filled the failed block plus the 64 KiB skipped after it
        ruim.BadRanges.Should().Equal(new ByteRange(Block, 2 * Block));
        rig.Written("ruim.avi").Length.Should().Be(300_000);
    }

    [Fact]
    public async Task EveryDiscFileOpened_IsDisposed()
    {
        var rig = new Rig();
        var a = rig.AddFile("a.avi", 10_000);
        var b = rig.AddFile("b.avi", 10_000);
        var c = rig.AddFile("c.avi", 10_000).BadRange(0, 10_000);

        await rig.RunWithTimeout();

        a.DisposeCount.Should().Be(1);
        b.DisposeCount.Should().Be(1);
        c.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task BigJobs_DoNotRewriteTheStateAfterEverySmallFile_ButCancelStillSavesEverything()
    {
        var rig = new Rig();
        for (int i = 0; i < 300; i++) rig.AddFile($"f{i:000}.jpg", 50, seed: i + 1);
        using var cts = new CancellationTokenSource();
        rig.Reader.File("f250.jpg").OnRead = (_, _) => cts.Cancel();
        int stateWrites = 0;
        rig.Dest.OnAtomicWrite = (p, _) => { if (p.EndsWith(".mediarescue-job.json")) stateWrites++; };

        var first = await rig.RunWithTimeout(cts.Token);

        first.Status.Should().Be(OverallStatus.Cancelled);
        stateWrites.Should().BeLessThan(20);
        using var doc = JsonDocument.Parse(rig.Dest.ReadText(rig.DestPath(".mediarescue-job.json")));
        doc.RootElement.GetProperty("files").EnumerateArray().Count(f => f.GetProperty("completed").GetBoolean()).Should().Be(250);

        rig.Reader.File("f250.jpg").OnRead = null;
        rig.Reader.Opened.Clear();
        var second = await rig.RunWithTimeout();

        rig.Reader.Opened.Should().HaveCount(50);
        second.Files.Should().HaveCount(300).And.OnlyContain(f => f.Status == FileStatus.Intact);
    }

    [Fact]
    public async Task Resume_WithALargerSelection_CopiesTheNewFilesToo()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 100_000);
        rig.AddFile("b.avi", 100_000);
        rig.AddFile("c.avi", 100_000);
        using var cts = new CancellationTokenSource();
        rig.Reader.File("b.avi").OnRead = (_, _) => cts.Cancel();
        await rig.Engine.RunAsync(rig.Request(rig.Entries.Take(2).ToList()), rig.Progress, rig.Control, cts.Token);
        rig.Reader.File("b.avi").OnRead = null;
        rig.Reader.Opened.Clear();

        var result = await rig.RunWithTimeout();

        rig.Reader.Opened.Should().Equal("b.avi", "c.avi");
        result.Files.Should().HaveCount(3).And.OnlyContain(f => f.Status == FileStatus.Intact);
    }

    [Fact]
    public async Task Resume_WithANewFileThatCollidesWithAnExistingName_NeverOverwritesIt()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 100_000);
        rig.AddFile("b.avi", 100_000);
        using var cts = new CancellationTokenSource();
        rig.Reader.File("b.avi").OnRead = (_, _) => cts.Cancel();
        await rig.RunWithTimeout(cts.Token);
        rig.Reader.File("b.avi").OnRead = null;
        // somebody put an unrelated file where the second one would go
        rig.Dest.SeedFile(rig.DestPath("zzz.avi"), new byte[] { 1, 2, 3 });
        rig.AddFile("zzz.avi", 5_000);

        var result = await rig.RunWithTimeout();

        rig.Dest.ReadAllBytes(rig.DestPath("zzz.avi")).Should().Equal(1, 2, 3);
        result.Files.Single(f => f.RelativePath == "zzz.avi").WrittenPath.Should().Be("zzz (2).avi");
    }

    [Fact]
    public void Engine_DoesNotCaptureTheCallersSynchronizationContext()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 200_000);
        rig.AddFile("b.avi", 200_000);
        var ctx = new CountingContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ctx);
        try
        {
            // block instead of await: awaiting would post the TEST's own continuation to this context
            var run = rig.Run();
            ((IAsyncResult)run).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(30)).Should().BeTrue();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        ctx.Posts.Should().Be(0, "the UI thread must never be needed for the copy to progress");
    }

    [Fact]
    public async Task TheEngineCanBeReusedForConsecutiveJobs()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 10_000);
        var first = await rig.RunWithTimeout();

        var rig2 = new Rig(rig.Dest);
        rig2.AddFile("a.avi", 10_000, seed: 99);
        var request = new ExtractionRequest(rig2.Drive, rig2.Disc, Rig.DestRoot, "Disco 2", rig2.Entries, rig2.Options);
        var second = await rig.Engine.RunAsync(request, rig2.Progress, rig2.Control, default);

        first.Status.Should().Be(OverallStatus.Intact);
        second.Status.Should().Be(OverallStatus.Intact, "no state leaks from one run to the next");
    }

    [Fact]
    public async Task TheOperationCount_IsLargeEnoughForTheCrashPointTheory()
    {
        // guards E27_PowerLossAtAnyPoint...: it only means something if the run has many write/rename/state operations
        var rig = new Rig();
        for (int i = 1; i <= 4; i++) rig.AddFile($"{i}.avi", 150_000);
        rig.AddFile(@"Sub\5.avi", 90_000);
        rig.AddFile("vazio.bin", 0);
        int ops = 0;
        rig.Dest.OnWrite = (_, _, _) => { ops++; return null; };
        rig.Dest.OnMoveStart = _ => ops++;
        rig.Dest.OnAtomicWrite = (_, _) => ops++;

        await rig.RunWithTimeout();

        ops.Should().BeGreaterThanOrEqualTo(25);
    }

    // ------------------------------------------------------------------ helpers

    private sealed class CountingContext : SynchronizationContext
    {
        private int _posts;
        public int Posts => _posts;
        public override void Post(SendOrPostCallback d, object? state) { Interlocked.Increment(ref _posts); base.Post(d, state); }
        public override void Send(SendOrPostCallback d, object? state) { Interlocked.Increment(ref _posts); base.Send(d, state); }
    }

    private sealed class SingleFileReader : IDiscReader
    {
        private readonly IDiscFile _f;
        public SingleFileReader(IDiscFile f) => _f = f;
        public IDiscFile Open(string driveLetter, string relativePath) => _f;
    }

    /// <summary>
    /// A drive whose first read of one block never returns by itself. When the next read arrives, the old hung
    /// request "completes" and scribbles garbage into the buffer it was given (a real driver can do this).
    /// </summary>
    private sealed class ZombieDisc : IDiscFile
    {
        private readonly byte[] _d;
        private readonly long _hangAt;
        private TaskCompletionSource<int>? _zombie;
        private Memory<byte> _zombieBuffer;

        public ZombieDisc(byte[] d, long hangAtOffset) { _d = d; _hangAt = hangAtOffset; }
        public int HangsServed { get; private set; }
        public int GarbageWrites { get; private set; }
        public long Length => _d.Length;
        public int SectorSize => 2048;
        public void Dispose() { }

        public Task<int> ReadAsync(long offset, Memory<byte> buffer, TimeSpan timeout, CancellationToken ct)
        {
            if (offset == _hangAt && HangsServed == 0)
            {
                HangsServed++;
                _zombie = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                _zombieBuffer = buffer;
                return _zombie.Task; // ignores ct on purpose
            }
            int n = (int)Math.Min(buffer.Length, _d.Length - offset);
            _d.AsMemory((int)offset, n).CopyTo(buffer);
            if (_zombie != null && !_zombie.Task.IsCompleted)
            {
                _zombieBuffer.Span.Fill(0xEE);
                GarbageWrites++;
                _zombie.TrySetResult(_zombieBuffer.Length);
            }
            return Task.FromResult(n);
        }
    }
}
