using System.Text.Json;
using FluentAssertions;
using MediaRescue.Core;
using MediaRescue.Core.Tests.Fakes;

namespace MediaRescue.Core.Tests;

/// <summary>RF-08 per-disc manifest, LEIA-ME - ERROS.txt, and E-20/E-21 naming through the engine.</summary>
public class EngineReportTests
{
    private const int Sector = 2048;

    private static JsonDocument Manifest(Rig rig) => JsonDocument.Parse(rig.Dest.ReadText(rig.DestPath("mediarescue-manifest.json")));

    [Fact]
    public async Task Manifest_HasSchemaVersion_Disc_Drive_AndOneEntryPerFile()
    {
        var rig = new Rig();
        rig.AddFile(@"Fotos\a.jpg", 1000);
        rig.AddFile("b.avi", 70_000);

        var result = await rig.RunWithTimeout();

        using var doc = Manifest(rig);
        var root = doc.RootElement;
        root.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        root.GetProperty("appVersion").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("disc").GetProperty("fingerprint").GetString().Should().Be(rig.Disc.Fingerprint);
        root.GetProperty("disc").GetProperty("volumeLabel").GetString().Should().Be("FORMATURA");
        root.GetProperty("drive").GetProperty("model").GetString().Should().Contain("TS-L633M");
        root.GetProperty("job").GetProperty("overallStatus").GetString().Should().Be("Intact");
        var files = root.GetProperty("files").EnumerateArray().ToList();
        files.Should().HaveCount(2);
        var b = files.Single(f => f.GetProperty("originalPath").GetString() == "b.avi");
        b.GetProperty("sha256").GetString().Should().Be(TestData.Sha(rig.Contents["b.avi"]));
        b.GetProperty("status").GetString().Should().Be("Intact");
        b.GetProperty("statusLabel").GetString().Should().Be("Íntegro");
        b.GetProperty("bytesTotal").GetInt64().Should().Be(70_000);
        b.GetProperty("sha256Scope").GetString().Should().Be("original");
        root.GetProperty("totals").GetProperty("files").GetInt32().Should().Be(2);
        result.Files.Should().HaveCount(2);
    }

    [Fact]
    public async Task Manifest_ListsBadRanges_AndMarksThePartialHashAsRecoveredContent()
    {
        var rig = new Rig();
        rig.AddFile("v.mpg", 600_000).BadRange(100 * Sector, 3 * Sector);

        await rig.RunWithTimeout();

        using var doc = Manifest(rig);
        var f = doc.RootElement.GetProperty("files")[0];
        f.GetProperty("status").GetString().Should().Be("Partial");
        f.GetProperty("sha256Scope").GetString().Should().Be("recovered-content");
        f.GetProperty("bytesLost").GetInt64().Should().Be(3 * Sector);
        var ranges = f.GetProperty("badRanges").EnumerateArray().ToList();
        ranges.Should().ContainSingle();
        ranges[0].GetProperty("offset").GetInt64().Should().Be(100 * Sector);
        ranges[0].GetProperty("length").GetInt64().Should().Be(3 * Sector);
        f.GetProperty("lostPercent").GetDouble().Should().BeApproximately(100.0 * 3 * Sector / 600_000, 0.001);
        doc.RootElement.GetProperty("job").GetProperty("overallStatus").GetString().Should().Be("Partial");
    }

    [Fact]
    public async Task E20_Manifest_MapsOriginalNamesToTheWrittenOnes()
    {
        var rig = new Rig();
        rig.AddFile(@"Festa: 2009?\video*.avi", 1000);
        rig.AddFile("CON.txt", 100);
        rig.AddFile("Aula.", 100);

        await rig.RunWithTimeout();

        using var doc = Manifest(rig);
        var map = doc.RootElement.GetProperty("pathMap");
        map.GetProperty(@"Festa: 2009?\video*.avi").GetString().Should().Be(@"Festa_ 2009_\video_.avi");
        map.GetProperty("CON.txt").GetString().Should().Be("CON_.txt");
        map.GetProperty("Aula.").GetString().Should().Be("Aula");
        rig.Written(@"Festa_ 2009_\video_.avi").Should().Equal(rig.Contents[@"Festa: 2009?\video*.avi"]);
    }

    [Fact]
    public async Task Manifest_IsAlsoWrittenWhenTheJobIsCancelled()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 100_000);
        rig.AddFile("b.avi", 100_000);
        using var cts = new CancellationTokenSource();
        rig.Reader.File("b.avi").OnRead = (_, _) => cts.Cancel();

        await rig.RunWithTimeout(cts.Token);

        using var doc = Manifest(rig);
        doc.RootElement.GetProperty("job").GetProperty("overallStatus").GetString().Should().Be("Cancelled");
        doc.RootElement.GetProperty("files").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task ReadMe_IsNotCreated_WhenNothingWasLost()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 100_000);

        await rig.RunWithTimeout();

        rig.Dest.FileExists(rig.DestPath("LEIA-ME - ERROS.txt")).Should().BeFalse();
        rig.Dest.FileExists(rig.DestPath("mediarescue-manifest.json")).Should().BeTrue();
    }

    [Fact]
    public async Task ReadMe_IsNotCreated_ForRecoveredFiles()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 400_000).FailTimes(65_536, 65_536, 3);

        var result = await rig.RunWithTimeout();

        result.Status.Should().Be(OverallStatus.Recovered);
        rig.Dest.FileExists(rig.DestPath("LEIA-ME - ERROS.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task ReadMe_ListsLostRangesInPortuguese_WhenSomethingWasLost()
    {
        var rig = new Rig();
        rig.AddFile(@"Videos\festa.mpg", 600_000).BadRange(100 * Sector, 2 * Sector);
        rig.AddFile("morto.avi", 50_000).BadRange(0, 50_000);
        rig.AddFile("bom.avi", 10_000);

        await rig.RunWithTimeout();

        var text = rig.Dest.ReadText(rig.DestPath("LEIA-ME - ERROS.txt"));
        text.Should().Contain("LEIA-ME - ERROS");
        text.Should().Contain(@"Videos\festa.mpg");
        text.Should().Contain("Perdido: 4 KB");
        text.Should().Contain("posição 204800, 4096 bytes");
        text.Should().Contain("morto.avi").And.Contain("NÃO RECUPERADOS");
        text.Should().NotContain("bom.avi");
        text.Should().Contain("O QUE FAZER").And.Contain("Limpe o disco");
        text.Should().Contain("Situação geral: cópia parcial");
    }

    [Fact]
    public async Task ReadMe_ForPartialFiles_ShowsTheLostPercentage()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 102_400).BadRange(0, 10_240); // 10 % (sector-aligned: 10240 = 5 sectors)

        await rig.RunWithTimeout();

        rig.Dest.ReadText(rig.DestPath("LEIA-ME - ERROS.txt")).Should().Contain("10%");
    }

    [Fact]
    public async Task StaleReadMe_FromAnInterruptedRun_IsRemovedWhenTheResumedJobEndsClean()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 100_000);
        rig.AddFile("b.avi", 100_000);
        using var cts = new CancellationTokenSource();
        rig.Reader.File("b.avi").OnRead = (_, _) => cts.Cancel();
        await rig.RunWithTimeout(cts.Token);
        rig.Reader.File("b.avi").OnRead = null;
        rig.Dest.SeedFile(rig.DestPath("LEIA-ME - ERROS.txt"), new byte[] { 65 });

        await rig.RunWithTimeout();

        rig.Dest.FileExists(rig.DestPath("LEIA-ME - ERROS.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task ReportFilesThatCannotBeWritten_DoNotFailTheCopy_ButAreAnnounced()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 100_000);
        rig.Dest.AtomicWriteFault = p => p.EndsWith("manifest.json") ? new IOException("sem espaço") : null;

        var result = await rig.RunWithTimeout();

        result.Status.Should().Be(OverallStatus.Intact);
        rig.Written("a.avi").Should().Equal(rig.Contents["a.avi"]);
        rig.Progress.Messages.Should().Contain(m => m.Contains("mediarescue-manifest.json"));
        rig.Dest.FileExists(rig.DestPath(".mediarescue-job.json")).Should().BeTrue("the state stays when the report could not be written");
    }

    // ------------------------------------------------------------------ E-20 / E-21 through the engine

    [Fact]
    public async Task E20_HostileNames_AreSanitized_AndNeverEscapeTheDiscFolder()
    {
        var rig = new Rig();
        var names = new[]
        {
            @"..\..\Windows\evil.txt", @"C:\autoexec.bat", @"/etc/passwd", @"a\.\b\..\c.txt",
            "NUL", "com1.log", "Trailing dot.", "Trailing space ", "FILME \"final\" <v2>|x.avi",
            "açaí ☂ 日本語 😀.mp4", new string('x', 300) + ".avi",
        };
        for (int i = 0; i < names.Length; i++) rig.AddFile(names[i], 100 + i, seed: i + 1);

        var result = await rig.RunWithTimeout();

        result.Files.Should().HaveCount(names.Length).And.OnlyContain(f => f.Status == FileStatus.Intact);
        var written = result.Files.Select(f => f.WrittenPath).ToList();
        written.Select(w => w.ToUpperInvariant()).Should().OnlyHaveUniqueItems();
        foreach (var f in result.Files)
        {
            f.WrittenPath.Split('\\').Should().NotContain(new[] { "..", "." , ""});
            f.WrittenPath.Should().NotContainAny("<", ">", ":", "\"", "|", "?", "*");
            rig.Written(f.WrittenPath).Should().Equal(rig.Contents[f.RelativePath]);
        }
        // everything on the volume is inside the disc folder
        rig.Dest.AllFiles.Should().OnlyContain(p => p.StartsWith(rig.DiscFolder + "\\", StringComparison.OrdinalIgnoreCase));
        result.Files.Max(f => f.WrittenPath.Split('\\').Max(s => s.Length)).Should().BeLessThanOrEqualTo(NameSanitizer.MaxSegmentLength);
    }

    [Fact]
    public async Task E20_NamesThatDifferOnlyByCase_GetDistinctDestinationNames()
    {
        var rig = new Rig();
        rig.AddFile("Video.AVI", 1000, seed: 1);
        rig.AddFile("video.avi", 1100, seed: 2);
        rig.AddFile("VIDEO.avi", 1200, seed: 3);
        rig.AddFile(@"Fotos\a.jpg", 100, seed: 4);
        rig.AddFile(@"fotos\a.jpg", 110, seed: 5);

        var result = await rig.RunWithTimeout();

        result.Files.Should().OnlyContain(f => f.Status == FileStatus.Intact);
        result.Files.Select(f => f.WrittenPath).Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(5);
        foreach (var f in result.Files) rig.Written(f.WrittenPath).Should().Equal(rig.Contents[f.RelativePath]);
    }

    [Fact]
    public async Task E20_DiscFileCalledLikeOneOfOurOwnFiles_DoesNotCollideWithThem()
    {
        var rig = new Rig();
        rig.AddFile("mediarescue-manifest.json", 50, seed: 1);
        rig.AddFile("LEIA-ME - ERROS.txt", 60, seed: 2);
        rig.AddFile("x.avi.partial", 70, seed: 3);
        rig.AddFile("x.avi", 80, seed: 4);

        var result = await rig.RunWithTimeout();

        result.Files.Should().OnlyContain(f => f.Status == FileStatus.Intact);
        foreach (var f in result.Files) rig.Written(f.WrittenPath).Should().Equal(rig.Contents[f.RelativePath]);
        result.Files.Select(f => f.WrittenPath).Should().NotContain("mediarescue-manifest.json").And.NotContain("LEIA-ME - ERROS.txt");
        using var doc = Manifest(rig);
        doc.RootElement.GetProperty("job").GetProperty("overallStatus").GetString().Should().Be("Intact");
    }

    [Fact]
    public async Task E20_TheSameInputProducesTheSameOutputNames_RegardlessOfOrder()
    {
        string[] names = { "Aa.txt", "aa.txt", "AA.TXT", "b?.txt", "b*.txt", "CON", "con" };
        var rigA = new Rig();
        foreach (var n in names) rigA.AddFile(n, 10, seed: 7);
        var rigB = new Rig();
        foreach (var n in names.Reverse()) rigB.AddFile(n, 10, seed: 7);

        var a = await rigA.RunWithTimeout();
        var b = await rigB.RunWithTimeout();

        a.Files.ToDictionary(f => f.RelativePath, f => f.WrittenPath)
            .Should().BeEquivalentTo(b.Files.ToDictionary(f => f.RelativePath, f => f.WrittenPath));
    }

    [Fact]
    public async Task E21_TwoDiscsWithTheSameLabel_AreCopiedIntoSeparateFolders()
    {
        var first = new Rig { Label = "FESTA", Serial = "AAAA-0001" };
        first.AddFile("a.avi", 5000, seed: 1);
        var second = new Rig(first.Dest) { Label = "FESTA", Serial = "BBBB-0002" };
        second.AddFile("a.avi", 6000, seed: 2);

        string NextFolder(Rig r) => NameSanitizer.MakeUnique(NameSanitizer.SuggestFolderName(r.Disc, DateTime.Today),
            n => r.Dest.DirectoryExists(Path.Combine(Rig.DestRoot, n)));
        var name1 = NextFolder(first);
        await first.Engine.RunAsync(new ExtractionRequest(first.Drive, first.Disc, Rig.DestRoot, name1, first.Entries, first.Options), first.Progress, first.Control, default);
        var name2 = NextFolder(second);
        await second.Engine.RunAsync(new ExtractionRequest(second.Drive, second.Disc, Rig.DestRoot, name2, second.Entries, second.Options), second.Progress, second.Control, default);

        name1.Should().Be("FESTA");
        name2.Should().Be("FESTA (2)");
        first.Disc.Fingerprint.Should().NotBe(second.Disc.Fingerprint);
        first.Dest.ReadAllBytes(Path.Combine(Rig.DestRoot, "FESTA", "a.avi")).Should().Equal(first.Contents["a.avi"]);
        first.Dest.ReadAllBytes(Path.Combine(Rig.DestRoot, "FESTA (2)", "a.avi")).Should().Equal(second.Contents["a.avi"]);
    }
}