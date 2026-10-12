using FluentAssertions;
using MediaRescue.Core;

namespace MediaRescue.Core.Tests;

/// <summary>RF-11 planning: which extracted files become which MP4.</summary>
public class Mp4PlannerTests
{
    private static FileResult F(string path, FileStatus status = FileStatus.Intact, long size = 1_000_000, long lost = 0, string? written = null) => new()
    {
        RelativePath = path, WrittenPath = written ?? path, Status = status, BytesTotal = size, BytesLost = lost,
    };

    private static IReadOnlyList<Mp4Job> Plan(params FileResult[] files) => Mp4ConversionPlanner.Plan(files);

    // ------------------------------------------------------------------ DVD-Video

    [Fact]
    public void DvdTitleSet_PartsAreJoinedInOrder_AndMenuVobsAreLeftOut()
    {
        var jobs = Plan(
            F(@"VIDEO_TS\VIDEO_TS.VOB"), F(@"VIDEO_TS\VIDEO_TS.IFO"),
            F(@"VIDEO_TS\VTS_01_0.VOB"), F(@"VIDEO_TS\VTS_01_0.IFO"),
            F(@"VIDEO_TS\VTS_01_2.VOB", size: 300), F(@"VIDEO_TS\VTS_01_1.VOB", size: 700));

        var job = jobs.Should().ContainSingle().Subject;
        job.Kind.Should().Be(Mp4SourceKind.DvdTitle);
        job.Inputs.Should().Equal(@"VIDEO_TS\VTS_01_1.VOB", @"VIDEO_TS\VTS_01_2.VOB");
        job.InputBytes.Should().Be(1000);
        job.SourceDamaged.Should().BeFalse();
        job.Output.Should().Be(@"MP4\Título 01 (VTS_01).mp4");
    }

    [Fact]
    public void DvdParts_AreOrderedNumerically_NotAlphabetically()
    {
        var parts = Enumerable.Range(1, 11).Reverse().Select(i => F($@"VIDEO_TS\VTS_01_{i}.VOB")).ToArray();
        var job = Plan(parts).Single();
        job.Inputs.Should().HaveCount(11);
        job.Inputs.First().Should().EndWith("VTS_01_1.VOB");
        job.Inputs[1].Should().EndWith("VTS_01_2.VOB");
        job.Inputs.Last().Should().EndWith("VTS_01_11.VOB");
    }

    [Fact]
    public void EachTitleSet_IsItsOwnJob_InTitleOrder()
    {
        var jobs = Plan(F(@"VIDEO_TS\VTS_03_1.VOB"), F(@"VIDEO_TS\VTS_01_1.VOB"), F(@"VIDEO_TS\VTS_02_1.VOB"), F(@"VIDEO_TS\VTS_02_2.VOB"));
        jobs.Select(j => j.Output).Should().Equal(@"MP4\Título 01 (VTS_01).mp4", @"MP4\Título 02 (VTS_02).mp4", @"MP4\Título 03 (VTS_03).mp4");
        jobs[1].Inputs.Should().HaveCount(2);
    }

    [Fact]
    public void MenuOnlyDisc_ProducesNothing()
    {
        Plan(F(@"VIDEO_TS\VIDEO_TS.VOB"), F(@"VIDEO_TS\VTS_01_0.VOB"), F(@"VIDEO_TS\VTS_01_0.IFO")).Should().BeEmpty();
    }

    [Fact]
    public void PartialPart_MarksTheTitleAsDamaged_ButStillConverts()
    {
        var job = Plan(F(@"VIDEO_TS\VTS_01_1.VOB"), F(@"VIDEO_TS\VTS_01_2.VOB", FileStatus.Partial, lost: 4096)).Single();
        job.SourceDamaged.Should().BeTrue();
        job.Inputs.Should().HaveCount(2);
    }

    [Fact]
    public void FailedPart_IsLeftOut_AndTheTitleIsMarkedDamaged()
    {
        var job = Plan(F(@"VIDEO_TS\VTS_01_1.VOB"), F(@"VIDEO_TS\VTS_01_2.VOB", FileStatus.Failed)).Single();
        job.Inputs.Should().Equal(@"VIDEO_TS\VTS_01_1.VOB");
        job.SourceDamaged.Should().BeTrue("a part of the title is missing");
    }

    [Fact]
    public void TitleWhoseEveryPartFailed_IsNotPlanned()
    {
        Plan(F(@"VIDEO_TS\VTS_01_1.VOB", FileStatus.Failed), F(@"VIDEO_TS\VTS_01_2.VOB", FileStatus.Skipped)).Should().BeEmpty();
    }

    [Fact]
    public void RecoveredParts_AreNotDamaged()
    {
        Plan(F(@"VIDEO_TS\VTS_01_1.VOB", FileStatus.Recovered)).Single().SourceDamaged.Should().BeFalse();
    }

    [Fact]
    public void TitleGroups_AreUsingTheWrittenPath_WhenTheCopyWasRenamed()
    {
        var job = Plan(F(@"VIDEO_TS\VTS_01_1.VOB", written: @"VIDEO_TS\VTS_01_1_.VOB")).Single();
        job.Inputs.Should().Equal(@"VIDEO_TS\VTS_01_1_.VOB");
    }

    // ------------------------------------------------------------------ one file, one MP4

    [Fact]
    public void Vcd_EachTrackIsOneMp4()
    {
        var jobs = Plan(F(@"MPEGAV\AVSEQ01.DAT"), F(@"MPEGAV\AVSEQ02.DAT"), F(@"VCD\INFO.VCD"), F(@"EXT\PSD_X.VCD"));
        jobs.Select(j => j.Output).Should().Equal(@"MP4\AVSEQ01.mp4", @"MP4\AVSEQ02.mp4");
        jobs.Should().OnlyContain(j => j.Kind == Mp4SourceKind.File && j.Inputs.Count == 1);
    }

    [Fact]
    public void DatOutsideMpegav_IsNotVideo()
    {
        Plan(F(@"Dados\arquivo.dat")).Should().BeEmpty();
    }

    [Fact]
    public void Svcd_TracksAreOneMp4Each()
    {
        Plan(F(@"MPEG2\AVSEQ01.MPG"), F(@"MPEG2\AVSEQ02.MPG")).Select(j => j.Output)
            .Should().Equal(@"MP4\AVSEQ01.mp4", @"MP4\AVSEQ02.mp4");
    }

    [Fact]
    public void DvdVr_MoviesAreConverted_StillsAudioAndInfoAreNot()
    {
        var jobs = Plan(F(@"DVD_RTAV\VR_MOVIE.VRO"), F(@"DVD_RTAV\VR_STILL.VRO"), F(@"DVD_RTAV\VR_AUDIO.VRO"), F(@"DVD_RTAV\VR_MANGR.IFO"));
        jobs.Should().ContainSingle().Which.Output.Should().Be(@"MP4\VR_MOVIE.mp4");
    }

    [Theory]
    [InlineData("avi")] [InlineData("wmv")] [InlineData("mpg")] [InlineData("mpeg")] [InlineData("mov")] [InlineData("3gp")]
    [InlineData("asf")] [InlineData("mts")] [InlineData("m2ts")] [InlineData("dv")] [InlineData("mkv")] [InlineData("flv")]
    public void PlainVideoFiles_AreConverted(string ext)
    {
        var job = Plan(F($@"Festa\filme.{ext}")).Should().ContainSingle().Subject;
        job.Inputs.Should().Equal($@"Festa\filme.{ext}");
        job.Output.Should().Be(@"MP4\Festa\filme.mp4", "the folder structure of the disc is kept below MP4\\");
    }

    [Theory]
    [InlineData("mp4")] [InlineData("MP4")] [InlineData("m4v")]
    public void FilesThatAreAlreadyMp4_AreNotConverted(string ext)
    {
        Plan(F($"clip.{ext}")).Should().BeEmpty();
    }

    [Fact]
    public void NonVideoFiles_AreIgnored()
    {
        Plan(F("foto.jpg"), F("musica.mp3"), F("LEIAME.txt"), F("autorun.inf")).Should().BeEmpty();
    }

    [Fact]
    public void LooseVobOutsideAnyTitleSet_IsOneMp4()
    {
        Plan(F(@"Filmes\casamento.vob")).Should().ContainSingle().Which.Output.Should().Be(@"MP4\Filmes\casamento.mp4");
    }

    // ------------------------------------------------------------------ statuses

    [Theory]
    [InlineData(FileStatus.Intact, true, false)]
    [InlineData(FileStatus.Recovered, true, false)]
    [InlineData(FileStatus.Partial, true, true)]
    [InlineData(FileStatus.Failed, false, false)]
    [InlineData(FileStatus.Skipped, false, false)]
    [InlineData(FileStatus.Pending, false, false)]
    [InlineData(FileStatus.DestinationFailure, false, false)]
    public void OnlyFilesThatWereCopied_AreConverted_AndPartialOnesAreFlaggedDamaged(FileStatus status, bool planned, bool damaged)
    {
        var jobs = Plan(F("a.avi", status, lost: status == FileStatus.Partial ? 2048 : 0));
        jobs.Any().Should().Be(planned);
        if (planned) jobs.Single().SourceDamaged.Should().Be(damaged);
    }

    [Fact]
    public void EmptyFiles_AreNotConverted()
    {
        Plan(F("vazio.avi", size: 0)).Should().BeEmpty();
    }

    // ------------------------------------------------------------------ naming

    [Fact]
    public void SameNameInDifferentFolders_KeepsTheFolders()
    {
        var jobs = Plan(F(@"2008\clip.avi"), F(@"2009\clip.avi"));
        jobs.Select(j => j.Output).Should().Equal(@"MP4\2008\clip.mp4", @"MP4\2009\clip.mp4");
    }

    [Fact]
    public void SameBaseNameInTheSameFolder_GetsASuffix_WithoutCaseCollisions()
    {
        var jobs = Plan(F("clip.avi"), F("clip.wmv"), F("CLIP.mpg"));
        jobs.Select(j => j.Output).Should().Equal(@"MP4\clip.mp4", @"MP4\clip (2).mp4", @"MP4\CLIP (3).mp4");
        jobs.Select(j => j.Output.ToLowerInvariant()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void UnicodeNames_AreKept()
    {
        Plan(F(@"Férias 2009 – Praia\Ação e emoção ☺.avi")).Single().Output.Should().Be(@"MP4\Férias 2009 – Praia\Ação e emoção ☺.mp4");
    }

    [Fact]
    public void OutputIsAlwaysBelowTheMp4Folder_AndNeverEqualsAnInput()
    {
        var jobs = Plan(F(@"VIDEO_TS\VTS_01_1.VOB"), F(@"a\b\c.avi"), F("x.mpg"));
        jobs.Should().OnlyContain(j => j.Output.StartsWith(@"MP4\") && j.Output.EndsWith(".mp4"));
        jobs.SelectMany(j => j.Inputs).Should().NotContain(i => i.StartsWith(@"MP4\"));
    }

    [Fact]
    public void MixedDisc_PlansTitlesFirst_ThenFiles()
    {
        var jobs = Plan(F("fotos\\a.jpg"), F("b.avi"), F(@"VIDEO_TS\VTS_01_1.VOB"));
        jobs.Select(j => j.Kind).Should().Equal(Mp4SourceKind.DvdTitle, Mp4SourceKind.File);
    }

    [Fact]
    public void NoFiles_NoJobs()
    {
        Plan().Should().BeEmpty();
    }

    [Fact]
    public void SpaceEstimate_IsConservative()
    {
        var jobs = Plan(F("a.avi", size: 1_000_000_000));
        Mp4ConversionPlanner.EstimateOutputBytes(jobs).Should().BeGreaterThan(900_000_000);
    }
}
