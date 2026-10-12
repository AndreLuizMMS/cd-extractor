using FluentAssertions;
using MediaRescue.Core;
using MediaRescue.Core.Tests.Fakes;

namespace MediaRescue.Core.Tests;

/// <summary>RF-03 validation: E-14, E-17, E-18 and the warnings.</summary>
public class DestinationValidatorTests
{
    private const long Gib = 1024L * 1024 * 1024;
    private const long Mib = 1024L * 1024;

    private static DiscEntry File(string path, long size) => new() { RelativePath = path, Size = size };

    private static (FakeDestination Fs, DestinationValidator V) Setup(string root = @"E:\Backup", long free = 100 * Gib, string fsName = "exFAT")
    {
        var fs = new FakeDestination(root) { CapacityBytes = free, FileSystemName = fsName };
        return (fs, new DestinationValidator(fs));
    }

    private static IReadOnlyList<ValidationIssue> Validate(DestinationValidator v, string root, string folder, IReadOnlyList<DiscEntry> entries, string drive = "D:") =>
        v.Validate(root, folder, entries, drive);

    [Fact]
    public void AllGood_ReturnsASingleOkLine_WithVolumeAndFreeSpace()
    {
        var (fs, v) = Setup();
        fs.VolumeLabel = "KINGSTON";

        var issues = Validate(v, fs.Root, "Formatura 2009", new[] { File("a.avi", 1 * Gib) });

        issues.Should().ContainSingle().Which.Severity.Should().Be(ValidationSeverity.Ok);
        issues[0].Message.Should().Contain("KINGSTON").And.Contain("exFAT").And.Contain("livres");
    }

    [Fact]
    public void RequiredBytes_IsSelectedPlusFivePercentPlus50MB()
    {
        DestinationValidator.RequiredBytes(0).Should().Be(50 * Mib);
        DestinationValidator.RequiredBytes(1000).Should().Be(1050 + 50 * Mib);
        DestinationValidator.RequiredBytes(10 * Gib).Should().Be((long)Math.Ceiling(10 * Gib * 1.05) + 50 * Mib);
    }

    [Fact]
    public void BlankDestination_IsBlocked()
    {
        var (_, v) = Setup();
        Validate(v, "", "x", Array.Empty<DiscEntry>()).Should().ContainSingle(i => i.Severity == ValidationSeverity.Blocking);
        Validate(v, "   ", "x", Array.Empty<DiscEntry>()).Should().ContainSingle(i => i.Severity == ValidationSeverity.Blocking);
    }

    [Fact]
    public void DestinationThatDoesNotExist_IsBlocked()
    {
        var (_, v) = Setup();
        var issues = Validate(v, @"F:\Nao\Existe", "x", new[] { File("a", 1) });
        issues.Should().ContainSingle().Which.Severity.Should().Be(ValidationSeverity.Blocking);
        issues[0].Message.Should().Contain("não existe");
    }

    [Fact]
    public void BlankFolderName_IsBlocked()
    {
        var (fs, v) = Setup();
        Validate(v, fs.Root, " ", new[] { File("a", 1) }).Should().Contain(i => i.Severity == ValidationSeverity.Blocking && i.Message.Contains("nome"));
    }

    [Fact]
    public void RemovedVolume_IsReportedAsMissing_WithoutThrowing()
    {
        var (fs, v) = Setup();
        fs.Removed = true;
        Validate(v, fs.Root, "x", new[] { File("a", 1) }).Should().ContainSingle(i => i.Severity == ValidationSeverity.Blocking);
    }

    [Theory]
    [InlineData(@"D:\Videos", "D:")]
    [InlineData(@"d:\", "D:")]
    [InlineData(@"D:\", "d")]
    [InlineData(@"D:\Sub", "D:\\")]
    public void DestinationEqualToTheOpticalDrive_IsBlocked(string root, string drive)
    {
        var (fs, v) = Setup();
        fs.AddDirectory(root);

        var issues = Validate(v, root, "x", new[] { File("a", 1) }, drive);

        issues.Should().ContainSingle().Which.Severity.Should().Be(ValidationSeverity.Blocking);
        issues[0].Message.Should().Contain("leitor");
    }

    [Fact]
    public void DestinationReportedAsOptical_IsBlocked()
    {
        var (fs, v) = Setup();
        fs.IsOptical = true;
        Validate(v, fs.Root, "x", new[] { File("a", 1) }, "Z:").Should().ContainSingle(i => i.Severity == ValidationSeverity.Blocking && i.Message.Contains("leitor"));
    }

    [Fact]
    public void E18_ReadOnlyDestination_IsBlocked_ByTheRealWriteTest()
    {
        var (fs, v) = Setup();
        fs.Writable = false;
        fs.WriteProbeError = "Acesso negado (código 5)";

        var issues = Validate(v, fs.Root, "x", new[] { File("a", 1) });

        issues.Should().ContainSingle().Which.Severity.Should().Be(ValidationSeverity.Blocking);
        issues[0].Message.Should().Contain("gravar").And.Contain("Acesso negado");
    }

    [Fact]
    public void E14_NotEnoughSpace_IsBlocked_SayingHowMuchIsMissing()
    {
        var (fs, v) = Setup(free: 5 * Gib);

        var issues = Validate(v, fs.Root, "x", new[] { File("a.vob", 4 * Gib), File("b.vob", 2 * Gib) });

        var block = issues.Should().ContainSingle(i => i.Severity == ValidationSeverity.Blocking).Subject;
        long required = DestinationValidator.RequiredBytes(6 * Gib);
        block.Message.Should().Contain("faltam").And.Contain(Fmt.Bytes(required - 5 * Gib));
    }

    [Fact]
    public void E14_SpaceBoundary_ExactlyEnoughPasses_OneByteLessBlocks()
    {
        long required = DestinationValidator.RequiredBytes(1 * Gib);
        var (fs, v) = Setup(free: required);
        Validate(v, fs.Root, "x", new[] { File("a", 1 * Gib) }).Should().NotContain(i => i.Severity == ValidationSeverity.Blocking);

        var (fs2, v2) = Setup(free: required - 1);
        Validate(v2, fs2.Root, "x", new[] { File("a", 1 * Gib) }).Should().Contain(i => i.Severity == ValidationSeverity.Blocking);
    }

    [Fact]
    public void E14_OnlySelectedEntriesCount_DirectoriesAreFree()
    {
        var (fs, v) = Setup(free: 200 * Mib);
        var entries = new List<DiscEntry> { File("a", 100 * Mib), new() { RelativePath = "pasta", IsDirectory = true, Size = 999 * Gib } };
        Validate(v, fs.Root, "x", entries).Should().NotContain(i => i.Severity == ValidationSeverity.Blocking);
    }

    [Fact]
    public void E17_Fat32_BlocksOnlyFilesOf4GBOrMore_WithExplanation()
    {
        var (fs, v) = Setup(fsName: "FAT32");
        var entries = new[]
        {
            File(@"VIDEO_TS\VTS_01_1.VOB", 1 * Gib),
            File("gigante.mpg", 4 * Gib),
            File("justo.avi", 4 * Gib - 1),
            File("enorme.mpg", 6 * Gib),
        };

        var issues = Validate(v, fs.Root, "x", entries);

        var blocks = issues.Where(i => i.Severity == ValidationSeverity.Blocking).ToList();
        blocks.Select(b => b.RelativePath).Should().BeEquivalentTo("gigante.mpg", "enorme.mpg");
        blocks.Should().OnlyContain(b => b.Message.Contains("exFAT") && b.Message.Contains("FAT32"));
        issues.Should().NotContain(i => i.RelativePath == "justo.avi");
    }

    [Theory]
    [InlineData("exFAT")]
    [InlineData("NTFS")]
    [InlineData("ReFS")]
    public void E17_OtherFileSystems_AllowBigFiles(string fsName)
    {
        var (fs, v) = Setup(fsName: fsName);
        Validate(v, fs.Root, "x", new[] { File("gigante.mpg", 8 * Gib) }).Should().NotContain(i => i.Severity == ValidationSeverity.Blocking);
    }

    [Fact]
    public void Fat16_HasA2GBLimit()
    {
        var (fs, v) = Setup(fsName: "FAT");
        Validate(v, fs.Root, "x", new[] { File("a", 2 * Gib) }).Should().Contain(i => i.Severity == ValidationSeverity.Blocking && i.RelativePath == "a");
    }

    [Fact]
    public void CloudOrNetwork_OnlyWarns()
    {
        var (fs, v) = Setup();
        fs.IsNetworkOrCloud = true;

        var issues = Validate(v, fs.Root, "x", new[] { File("a", 1 * Mib) });

        issues.Should().ContainSingle().Which.Severity.Should().Be(ValidationSeverity.Warning);
        issues[0].Message.Should().Contain("cache").And.Contain("C:");
    }

    [Fact]
    public void UncPath_Warns_EvenWhenTheVolumeDoesNotSayItIsNetwork()
    {
        var (fs, v) = Setup(@"\\servidor\pasta");

        var issues = Validate(v, fs.Root, "x", new[] { File("a", 1 * Mib) });

        issues.Should().Contain(i => i.Severity == ValidationSeverity.Warning && i.Message.Contains("rede"));
        issues.Should().NotContain(i => i.Severity == ValidationSeverity.Blocking);
    }

    [Fact]
    public void LowFreeSpaceOnC_AfterTheCopy_Warns()
    {
        var (fs, v) = Setup(@"C:\Videos", free: 15 * Gib);

        var issues = Validate(v, fs.Root, "x", new[] { File("a", 8 * Gib) });

        issues.Should().ContainSingle().Which.Severity.Should().Be(ValidationSeverity.Warning);
        issues[0].Message.Should().Contain("C:").And.Contain("10 GB");
    }

    [Fact]
    public void PlentyOfSpaceOnC_DoesNotWarn()
    {
        var (fs, v) = Setup(@"C:\Videos", free: 200 * Gib);
        Validate(v, fs.Root, "x", new[] { File("a", 8 * Gib) }).Should().ContainSingle().Which.Severity.Should().Be(ValidationSeverity.Ok);
    }

    [Fact]
    public void LowFreeSpaceOnAnotherDrive_DoesNotWarn()
    {
        var (fs, v) = Setup(@"E:\Videos", free: 15 * Gib);
        Validate(v, fs.Root, "x", new[] { File("a", 8 * Gib) }).Should().ContainSingle().Which.Severity.Should().Be(ValidationSeverity.Ok);
    }

    [Fact]
    public void ExistingFolderWithTheSameName_Warns_AndSuggestsTheNextFreeName()
    {
        var (fs, v) = Setup();
        fs.AddDirectory(@"E:\Backup\Festa");
        fs.AddDirectory(@"E:\Backup\Festa (2)");

        var issues = Validate(v, fs.Root, "Festa", new[] { File("a", 1) });

        issues.Should().ContainSingle().Which.Severity.Should().Be(ValidationSeverity.Warning);
        issues[0].Message.Should().Contain("Festa (3)");
    }

    [Fact]
    public void VeryLongFinalPaths_Warn_ShortOnesDoNot()
    {
        var (fs, v) = Setup();
        var longPath = new string('a', 120) + @"\" + new string('b', 100) + @"\video.avi";

        Validate(v, fs.Root, "Formatura", new[] { File(longPath, 1) })
            .Should().Contain(i => i.Severity == ValidationSeverity.Warning && i.Message.Contains("240"));
        Validate(v, fs.Root, "Formatura", new[] { File(@"Fotos\a.jpg", 1) })
            .Should().NotContain(i => i.Message.Contains("240"));
    }

    [Fact]
    public void LongPathWarning_UsesTheSanitizedPaths()
    {
        var (fs, v) = Setup();
        // 100 characters that grow when sanitized would not matter, but a hostile name must not break validation
        var issues = Validate(v, fs.Root, "x", new[] { File(@"..\..\a?.avi", 1), File("CON", 1) });
        issues.Should().NotContain(i => i.Severity == ValidationSeverity.Blocking);
    }

    [Fact]
    public void BlockingAndWarnings_CanBeReportedTogether()
    {
        var (fs, v) = Setup(@"C:\Videos", free: 4 * Gib);
        fs.IsNetworkOrCloud = true;

        var issues = Validate(v, fs.Root, "x", new[] { File("a", 8 * Gib) });

        issues.Select(i => i.Severity).Should().Contain(new[] { ValidationSeverity.Blocking, ValidationSeverity.Warning });
        issues.Should().NotContain(i => i.Severity == ValidationSeverity.Ok);
    }

    [Fact]
    public void NoEntries_IsFine()
    {
        var (fs, v) = Setup();
        Validate(v, fs.Root, "x", Array.Empty<DiscEntry>()).Should().ContainSingle().Which.Severity.Should().Be(ValidationSeverity.Ok);
    }

    [Theory]
    [InlineData("D:", "D:")]
    [InlineData("d:\\", "D:")]
    [InlineData("D", "D:")]
    [InlineData(@"c:\Users\x", "C:")]
    [InlineData(@"\\server\share", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData(@"relative\path", null)]
    public void DriveLetterOf_Normalises(string? input, string? expected)
    {
        DestinationValidator.DriveLetterOf(input).Should().Be(expected);
    }
}
