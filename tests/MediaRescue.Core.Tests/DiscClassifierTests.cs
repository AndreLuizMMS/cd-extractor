using FluentAssertions;
using MediaRescue.Core;

namespace MediaRescue.Core.Tests;

/// <summary>Section 2.3 table: one synthetic tree per row, plus DVD title grouping.</summary>
public class DiscClassifierTests
{
    private static List<DiscEntry> Tree(params string[] paths) =>
        paths.Select(p => new DiscEntry { RelativePath = p, Size = 1000, ModifiedUtc = new DateTime(2009, 1, 1, 0, 0, 0, DateTimeKind.Utc) }).ToList();

    private static LogicalFormat Classify(params string[] paths) => DiscClassifier.Classify(Tree(paths));

    // ------------------------------------------------------------------ table 2.3

    [Theory]
    [InlineData("avi")] [InlineData("mpg")] [InlineData("mpeg")] [InlineData("wmv")] [InlineData("mp4")] [InlineData("mov")]
    [InlineData("3gp")] [InlineData("asf")] [InlineData("mts")] [InlineData("m2ts")] [InlineData("dv")] [InlineData("mkv")] [InlineData("flv")]
    public void DataDisc_WithVideoFiles_IsDataVideo(string ext)
    {
        Classify($@"Festa\filme.{ext}", $@"Festa\outro.{ext.ToUpperInvariant()}").Should().Be(LogicalFormat.DataVideo);
        DiscClassifier.KindOf($"x.{ext}").Should().Be(EntryKind.Video);
    }

    [Fact]
    public void DataDisc_WithVideosPhotosAndDocuments_IsMixed()
    {
        Classify(@"Videos\a.avi", @"Fotos\b.jpg", @"Docs\c.doc").Should().Be(LogicalFormat.Mixed);
    }

    [Fact]
    public void DataDisc_WithOnlyHousekeepingFilesBesidesVideos_IsStillDataVideo()
    {
        Classify("autorun.inf", "Thumbs.db", "filme.avi").Should().Be(LogicalFormat.DataVideo);
    }

    [Fact]
    public void Vcd_IsRecognisedByMpegavAndAvseqDat()
    {
        Classify(@"MPEGAV\AVSEQ01.DAT", @"MPEGAV\AVSEQ02.DAT", @"VCD\INFO.VCD", @"VCD\ENTRIES.VCD").Should().Be(LogicalFormat.Vcd);
    }

    [Fact]
    public void Svcd_IsRecognisedByMpeg2AndAvseqMpg()
    {
        Classify(@"MPEG2\AVSEQ01.MPG", @"SVCD\INFO.SVD").Should().Be(LogicalFormat.Svcd);
    }

    [Fact]
    public void DvdVideo_IsRecognisedByVideoTs()
    {
        Classify(@"VIDEO_TS\VIDEO_TS.IFO", @"VIDEO_TS\VIDEO_TS.BUP", @"VIDEO_TS\VTS_01_0.IFO", @"VIDEO_TS\VTS_01_1.VOB").Should().Be(LogicalFormat.DvdVideo);
    }

    [Fact]
    public void DvdVideo_WinsOverExtraPhotosInTheSameDisc()
    {
        Classify(@"VIDEO_TS\VTS_01_1.VOB", @"Fotos\a.jpg").Should().Be(LogicalFormat.DvdVideo);
    }

    [Fact]
    public void DvdVideo_IsCaseInsensitive_AndAcceptsForwardSlashes()
    {
        Classify("video_ts/vts_01_1.vob").Should().Be(LogicalFormat.DvdVideo);
    }

    [Fact]
    public void DvdVr_IsRecognisedByDvdRtavAndVrMovie()
    {
        Classify(@"DVD_RTAV\VR_MANGR.IFO", @"DVD_RTAV\VR_MANGR.BUP", @"DVD_RTAV\VR_MOVIE.VRO").Should().Be(LogicalFormat.DvdVr);
    }

    [Theory]
    [InlineData("MTS")]
    [InlineData("M2TS")]
    public void Avchd_IsRecognisedByBdmvStream(string ext)
    {
        Classify(@"BDMV\INDEX.BDM", @"BDMV\PLAYLIST\00000.MPL", $@"BDMV\STREAM\00000.{ext}").Should().Be(LogicalFormat.Avchd);
    }

    [Fact]
    public void AudioCd_IsRecognisedByOnlyCdaTracks()
    {
        Classify("Track01.cda", "Track02.cda").Should().Be(LogicalFormat.AudioCd);
    }

    [Fact]
    public void E08_MixedModeCd_IsClassifiedByItsDataPart()
    {
        Classify("Track02.cda", "Track03.cda", @"Videos\festa.avi").Should().Be(LogicalFormat.DataVideo);
    }

    [Fact]
    public void E06_DiscWithoutFiles_IsEmpty()
    {
        DiscClassifier.Classify(Array.Empty<DiscEntry>()).Should().Be(LogicalFormat.Empty);
        DiscClassifier.Classify(new[] { new DiscEntry { RelativePath = "Pasta", IsDirectory = true } }).Should().Be(LogicalFormat.Empty);
    }

    [Fact]
    public void UnreadableRoot_WithNoFiles_IsUnknown_NotEmpty()
    {
        DiscClassifier.Classify(new[] { new DiscEntry { RelativePath = "Pasta", IsDirectory = true, Error = "erro de E/S" } }).Should().Be(LogicalFormat.Unknown);
    }

    [Fact]
    public void DiscWithoutAnyVideo_IsUnknown()
    {
        Classify(@"Fotos\a.jpg", "notas.txt").Should().Be(LogicalFormat.Unknown);
    }

    [Fact]
    public void ADvdStructureNestedInASubfolder_IsJustAVideoCollection()
    {
        Classify(@"Backup\VIDEO_TS\VTS_01_1.VOB", @"Backup\VIDEO_TS\VIDEO_TS.IFO").Should().Be(LogicalFormat.DataVideo);
    }

    [Fact]
    public void FilesWithErrors_AreIgnoredWhenClassifying()
    {
        var entries = Tree(@"VIDEO_TS\VTS_01_1.VOB");
        entries.Add(new DiscEntry { RelativePath = "foto.jpg", Error = "ilegível" });
        DiscClassifier.Classify(entries).Should().Be(LogicalFormat.DvdVideo);
    }

    // ------------------------------------------------------------------ kinds

    [Theory]
    [InlineData("a.JPG", EntryKind.Image)]
    [InlineData("a.png", EntryKind.Image)]
    [InlineData("a.mp3", EntryKind.Audio)]
    [InlineData("Track01.cda", EntryKind.Audio)]
    [InlineData(@"VIDEO_TS\VTS_01_1.VOB", EntryKind.Video)]
    [InlineData(@"DVD_RTAV\VR_MOVIE.VRO", EntryKind.Video)]
    [InlineData(@"VIDEO_TS\VIDEO_TS.IFO", EntryKind.Other)]
    [InlineData(@"VIDEO_TS\VIDEO_TS.BUP", EntryKind.Other)]
    [InlineData("notas.txt", EntryKind.Other)]
    [InlineData("semextensao", EntryKind.Other)]
    [InlineData(@"MPEGAV\AVSEQ01.DAT", EntryKind.Video)]
    [InlineData(@"Docs\dados.dat", EntryKind.Other)]
    public void KindOf_ClassifiesByExtension(string path, EntryKind expected)
    {
        DiscClassifier.KindOf(path).Should().Be(expected);
    }

    // ------------------------------------------------------------------ DVD title grouping

    [Theory]
    [InlineData(@"VIDEO_TS\VTS_01_1.VOB", "VTS_01")]
    [InlineData(@"VIDEO_TS\VTS_01_4.vob", "VTS_01")]
    [InlineData(@"VIDEO_TS\VTS_02_3.VOB", "VTS_02")]
    [InlineData("VTS_12_10.VOB", "VTS_12")]
    [InlineData("video_ts/vts_03_1.vob", "VTS_03")]
    [InlineData(@"VIDEO_TS\VTS_01_0.VOB", null)]      // menu
    [InlineData(@"VIDEO_TS\VIDEO_TS.VOB", null)]       // first-play menu
    [InlineData(@"VIDEO_TS\VTS_01_0.IFO", null)]
    [InlineData(@"VIDEO_TS\VTS_01_1.IFO", null)]
    [InlineData(@"VIDEO_TS\VTS_01_1.BUP", null)]
    [InlineData(@"VIDEO_TS\VTS_1_1.VOB", null)]
    [InlineData(@"Pasta\filme.vob", null)]
    [InlineData("", null)]
    public void TitleGroupOf_GroupsTheTitleVobsAndIgnoresTheMenu(string path, string? expected)
    {
        DiscClassifier.TitleGroupOf(path).Should().Be(expected);
    }

    [Fact]
    public void GroupDvdTitles_SumsTheVobsOfEachTitle_AndDropsMenuVobs()
    {
        var entries = new List<DiscEntry>
        {
            new() { RelativePath = @"VIDEO_TS\VIDEO_TS.IFO", Size = 10 },
            new() { RelativePath = @"VIDEO_TS\VIDEO_TS.VOB", Size = 111 },
            new() { RelativePath = @"VIDEO_TS\VTS_01_0.VOB", Size = 222 },
            new() { RelativePath = @"VIDEO_TS\VTS_01_1.VOB", Size = 1_000_000_000 },
            new() { RelativePath = @"VIDEO_TS\VTS_01_2.VOB", Size = 500_000_000 },
            new() { RelativePath = @"VIDEO_TS\VTS_02_1.VOB", Size = 300 },
            new() { RelativePath = @"VIDEO_TS", IsDirectory = true },
        };

        var groups = DiscClassifier.GroupDvdTitles(entries);

        groups.Select(g => g.Group).Should().Equal("VTS_01", "VTS_02");
        groups[0].TotalBytes.Should().Be(1_500_000_000);
        groups[0].Vobs.Select(v => v.RelativePath).Should().Equal(@"VIDEO_TS\VTS_01_1.VOB", @"VIDEO_TS\VTS_01_2.VOB");
        groups[1].TotalBytes.Should().Be(300);
    }

    [Fact]
    public void GroupDvdTitles_UsesTheTitleGroupAlreadySetOnTheEntry()
    {
        var entries = new List<DiscEntry> { new() { RelativePath = @"X\whatever.bin", Size = 5, TitleGroup = "VTS_07" } };
        DiscClassifier.GroupDvdTitles(entries).Should().ContainSingle().Which.Group.Should().Be("VTS_07");
    }

    [Fact]
    public void GroupDvdTitles_OnADiscWithoutDvd_IsEmpty()
    {
        DiscClassifier.GroupDvdTitles(Tree("a.avi")).Should().BeEmpty();
    }
}
