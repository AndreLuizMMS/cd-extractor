using FluentAssertions;
using MediaRescue.Core;
using MediaRescue.Core.Tests.Fakes;

namespace MediaRescue.Core.Tests;

public class EngineBasicTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2047)]
    [InlineData(2048)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(65537)]
    [InlineData(300_000)]
    public async Task IntactFile_IsCopiedByteForByte_WithMatchingSha256(int size)
    {
        var rig = new Rig();
        rig.AddFile(@"Videos\festa.avi", size);

        var result = await rig.RunWithTimeout();

        result.Status.Should().Be(OverallStatus.Intact);
        var f = result.Files.Should().ContainSingle().Subject;
        f.Status.Should().Be(FileStatus.Intact);
        f.BytesTotal.Should().Be(size);
        f.BytesLost.Should().Be(0);
        f.BadRanges.Should().BeEmpty();
        f.Sha256.Should().Be(TestData.Sha(rig.Contents[@"Videos\festa.avi"]));
        rig.Written(@"Videos\festa.avi").Should().Equal(rig.Contents[@"Videos\festa.avi"]);
        rig.Dest.PartialFiles.Should().BeEmpty();
        result.DiscFolder.Should().Be(rig.DiscFolder);
    }

    [Fact]
    public async Task MultipleFiles_KeepTheirFolderStructure()
    {
        var rig = new Rig();
        rig.AddFile(@"VIDEO_TS\VTS_01_1.VOB", 100_000);
        rig.AddFile(@"VIDEO_TS\VIDEO_TS.IFO", 3_000);
        rig.AddFile(@"Fotos\2009\IMG_0001.JPG", 5_000);
        rig.AddFile("LEIAME.TXT", 10);

        var result = await rig.RunWithTimeout();

        result.Status.Should().Be(OverallStatus.Intact);
        result.Files.Should().HaveCount(4).And.OnlyContain(f => f.Status == FileStatus.Intact);
        foreach (var (path, bytes) in rig.Contents)
            rig.Written(path).Should().Equal(bytes);
        rig.Dest.DirectoryExists(rig.DestPath(@"Fotos\2009")).Should().BeTrue();
    }

    [Fact]
    public async Task E24_ZeroByteFile_AndEmptyFolder_AreRecreated()
    {
        var rig = new Rig();
        rig.AddDirectory(@"Pasta Vazia");
        rig.AddFile("vazio.txt", 0);

        var result = await rig.RunWithTimeout();

        result.Status.Should().Be(OverallStatus.Intact);
        rig.Dest.DirectoryExists(rig.DestPath("Pasta Vazia")).Should().BeTrue();
        rig.Dest.FileExists(rig.DestPath("vazio.txt")).Should().BeTrue();
        rig.Dest.GetFileSize(rig.DestPath("vazio.txt")).Should().Be(0);
        result.Files.Single().Sha256.Should().Be("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855");
    }

    [Fact]
    public async Task E24_ThousandsOfSmallFiles_AreAllCopied_AndQuickly()
    {
        var rig = new Rig();
        for (int i = 0; i < 1500; i++) rig.AddFile($@"Fotos\img{i:0000}.jpg", 100 + i % 7, seed: i + 1);

        var result = await rig.RunWithTimeout();

        result.Status.Should().Be(OverallStatus.Intact);
        result.Files.Should().HaveCount(1500);
        rig.Progress.Last.FileCount.Should().Be(1500);
        rig.Progress.Last.FilesIntact.Should().Be(1500);
        // the job state is not rewritten 1500 times (it would be quadratic); it is gone after success anyway
        rig.Dest.FileExists(rig.DestPath(".mediarescue-job.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Copy_WritesToPartialFirst_AndNeverCreatesAFinalNameDirectly()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 200_000);
        var seenFinalDuringWrite = false;
        rig.Dest.OnWrite = (path, _, _) =>
        {
            if (rig.Dest.FileExists(rig.DestPath("a.avi"))) seenFinalDuringWrite = true;
            return null;
        };

        await rig.RunWithTimeout();

        seenFinalDuringWrite.Should().BeFalse();
        rig.Dest.Created.Should().OnlyContain(p => p.EndsWith(".partial"));
        rig.Dest.Moves.Should().ContainSingle(m => m.To.EndsWith("a.avi") && m.From.EndsWith("a.avi.partial"));
    }

    [Fact]
    public async Task ModifiedTime_IsPreserved()
    {
        var rig = new Rig();
        var when = new DateTime(2008, 5, 17, 10, 20, 30, DateTimeKind.Utc);
        rig.AddFile("a.avi", 5000, modified: when);

        await rig.RunWithTimeout();

        rig.Dest.ModifiedOf(rig.DestPath("a.avi")).Should().Be(when);
    }

    [Fact]
    public async Task PhysicalOrder_IsUsed_WhenEveryFileKnowsIt()
    {
        var rig = new Rig();
        rig.AddFile("c.avi", 3000, physicalOffset: 300);
        rig.AddFile("a.avi", 3000, physicalOffset: 900);
        rig.AddFile("b.avi", 3000, physicalOffset: 100);

        await rig.RunWithTimeout();

        rig.Reader.Opened.Should().Equal("b.avi", "c.avi", "a.avi");
    }

    [Fact]
    public async Task DirectoryOrder_IsKept_WhenPhysicalOffsetsAreUnknown()
    {
        var rig = new Rig();
        rig.AddFile("c.avi", 3000);
        rig.AddFile("a.avi", 3000);
        rig.AddFile("b.avi", 3000, physicalOffset: 5);

        await rig.RunWithTimeout();

        rig.Reader.Opened.Should().Equal("c.avi", "a.avi", "b.avi");
    }

    [Fact]
    public async Task E09_ProtectedDisc_IsRefused_WithoutTouchingDiscOrDestination()
    {
        var rig = new Rig { Protected = true };
        rig.AddFile("a.avi", 5000);

        var result = await rig.RunWithTimeout();

        result.Status.Should().Be(OverallStatus.Failed);
        rig.Reader.Opened.Should().BeEmpty();
        rig.Dest.AllFiles.Should().BeEmpty();
        rig.Dest.DirectoryExists(rig.DiscFolder).Should().BeFalse();
        rig.Progress.Messages.Should().Contain(m => m.Contains("protegido"));
        rig.Drives.LockCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task EntriesNotRequested_AreNotCopied()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 5000);
        rig.AddFile("b.avi", 5000);

        var result = await rig.Engine.RunAsync(rig.Request(rig.Entries.Take(1).ToList()), rig.Progress, rig.Control, default);

        result.Files.Should().ContainSingle(f => f.RelativePath == "a.avi");
        rig.Dest.FileExists(rig.DestPath("b.avi")).Should().BeFalse();
    }

    [Fact]
    public async Task Verification_CanBeDisabled_AndTheDestinationIsThenNotReRead()
    {
        var rig = new Rig();
        rig.Options.VerifyAfterCopy = false;
        rig.AddFile("a.avi", 80_000);

        var result = await rig.RunWithTimeout();

        rig.Dest.OpenedForRead.Should().BeEmpty();
        result.Files.Single().Sha256.Should().Be(TestData.Sha(rig.Contents["a.avi"]));
    }

    [Fact]
    public async Task Verification_Enabled_ReReadsTheDestination()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 80_000);

        await rig.RunWithTimeout();

        rig.Dest.OpenedForRead.Should().ContainSingle(p => p.EndsWith("a.avi.partial"));
    }

    [Fact]
    public async Task UnexpectedFailureWhilePreparing_ReturnsFailed_AndStillUnlocksTheMedia()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 3000);
        var engine = new ExtractionEngine(rig.Reader, new ThrowingDestination(rig.Dest), rig.Drives, rig.Clock);

        var result = await engine.RunAsync(rig.Request(), rig.Progress, rig.Control, default);

        result.Status.Should().Be(OverallStatus.Failed);
        rig.Drives.LockCalls.Should().Equal(true, false);
    }

    [Fact]
    public async Task Lock_IsHeldDuringTheCopy_AndReleasedAfterwards()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 3000);
        var lockedWhileReading = false;
        rig.Reader.File("a.avi").OnRead = (_, _) => lockedWhileReading = rig.Drives.LockCalls.SequenceEqual(new[] { true });

        await rig.RunWithTimeout();

        lockedWhileReading.Should().BeTrue();
        rig.Drives.LockCalls.Should().Equal(true, false);
    }

    [Fact]
    public async Task Lock_IsReleased_EvenWhenTheLockCallItselfThrows()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 3000);
        rig.Drives.LockFault = new InvalidOperationException("ioctl failed");

        var result = await rig.RunWithTimeout();

        result.Status.Should().Be(OverallStatus.Intact);
    }

    /// <summary>Destination whose CreateDirectory blows up with an unexpected exception.</summary>
    private sealed class ThrowingDestination : IDestinationFileSystem
    {
        private readonly IDestinationFileSystem _inner;
        public ThrowingDestination(IDestinationFileSystem inner) => _inner = inner;
        public DestinationInfo? GetInfo(string path) => _inner.GetInfo(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public bool FileExists(string path) => _inner.FileExists(path);
        public void CreateDirectory(string path) => throw new InvalidOperationException("unexpected failure");
        public Stream Create(string path) => _inner.Create(path);
        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public void FlushToDisk(Stream stream) => _inner.FlushToDisk(stream);
        public void Move(string source, string destination) => _inner.Move(source, destination);
        public void Delete(string path) => _inner.Delete(path);
        public long GetFileSize(string path) => _inner.GetFileSize(path);
        public void SetTimes(string path, DateTime modifiedUtc) => _inner.SetTimes(path, modifiedUtc);
        public bool ProbeWritable(string directory, out string? error) => _inner.ProbeWritable(directory, out error);
        public string? ReadAllText(string path) => _inner.ReadAllText(path);
        public void WriteAllTextAtomic(string path, string content) => _inner.WriteAllTextAtomic(path, content);
    }
}
