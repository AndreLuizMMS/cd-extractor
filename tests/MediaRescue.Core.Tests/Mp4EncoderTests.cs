using FluentAssertions;
using MediaRescue.Core;

namespace MediaRescue.Core.Tests;

/// <summary>RF-11: encoder selection, ffmpeg argument lines, progress parsing and the output validation rule.</summary>
public class Mp4EncoderTests
{
    // Excerpt of "ffmpeg -hide_banner -encoders" of the bundled LGPL build (plus a libx264 line for the GPL case).
    private const string LgplListing = @"Encoders:
 V..... = Video
 A..... = Audio
 S..... = Subtitle
 .F.... = Frame-level multithreading
 ------
 V....D av1_mf               AV1 via MediaFoundation (codec av1)
 V....D libopenh264          OpenH264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)
 V....D h264_amf             AMD AMF H.264 Encoder (codec h264)
 V....D h264_mf              H264 via MediaFoundation (codec h264)
 V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
 V.S..D mpeg4                MPEG-4 part 2
 V....D msmpeg4v2            MPEG-4 part 2 Microsoft variant version 2
 A....D aac                  AAC (Advanced Audio Coding)
 A....D aac_mf               AAC via MediaFoundation (codec aac)
";

    private static string WithX264(string listing) => listing + " V....D libx264             libx264 H.264 / AVC / MPEG-4 AVC / MPEG-4 part 10 (codec h264)\n";

    [Fact]
    public void ParseVideoEncoders_ReadsOnlyVideoEncoders_NotTheLegend()
    {
        var set = Mp4Encoders.ParseVideoEncoders(LgplListing);
        set.Should().Contain(new[] { "libopenh264", "h264_mf", "mpeg4", "av1_mf" });
        set.Should().NotContain(new[] { "aac", "aac_mf", "=", "libx264" });
    }

    [Fact]
    public void Candidates_OfTheLgplBuild_AreMediaFoundationOpenH264AndMpeg4_InThatOrder()
    {
        Mp4Encoders.Candidates(LgplListing).Should().Equal("h264_mf", "libopenh264", "mpeg4");
    }

    [Fact]
    public void Candidates_PreferX264WhenTheBuildHasIt()
    {
        Mp4Encoders.Candidates(WithX264(LgplListing)).Should().Equal("libx264", "h264_mf", "libopenh264", "mpeg4");
    }

    [Fact]
    public void Candidates_OfAnEmptyOrGarbageListing_AreEmpty()
    {
        Mp4Encoders.Candidates("").Should().BeEmpty();
        Mp4Encoders.Candidates("not ffmpeg output at all").Should().BeEmpty();
    }

    [Fact]
    public void PreferenceOrder_IsTheSpecifiedOne()
    {
        Mp4Encoders.Preference.Should().Equal("libx264", "h264_mf", "libopenh264", "mpeg4");
    }

    // ------------------------------------------------------------------ quality mapping

    [Fact]
    public void X264_UsesCrf20()
    {
        var a = Mp4Encoders.VideoArgs("libx264", 720, 480, 29.97);
        a.Should().ContainInOrder("-c:v", "libx264").And.ContainInOrder("-crf", "20");
    }

    [Theory]
    [InlineData("h264_mf")]
    [InlineData("libopenh264")]
    public void EncodersWithoutCrf_AimAtABitrate(string encoder)
    {
        var a = Mp4Encoders.VideoArgs(encoder, 720, 480, 29.97);
        a.Should().ContainInOrder("-c:v", encoder);
        a.Should().Contain("-b:v");
        a.Should().NotContain("-crf");
    }

    [Fact]
    public void Mpeg4Fallback_UsesAFixedQuantiser()
    {
        Mp4Encoders.VideoArgs("mpeg4", 320, 240, 30).Should().ContainInOrder("-c:v", "mpeg4", "-q:v", "4");
    }

    [Theory]
    [InlineData(720, 480, 29.97, 2_600_000, 2_700_000)]
    [InlineData(352, 240, 29.97, 600_000, 700_000)]     // small VCD: clamped to the 600 kb/s floor
    [InlineData(1920, 1080, 29.97, 12_000_000, 12_000_000)] // HD: clamped to the ceiling
    [InlineData(0, 0, 0, 2_100_000, 2_300_000)]           // unknown source: treated like a 25 fps DVD
    public void TargetBitrate_ScalesWithPixelsAndIsClamped(int w, int h, double fps, int min, int max)
    {
        Mp4Encoders.TargetBitrate(w, h, fps).Should().BeInRange(min, max);
    }

    [Fact]
    public void Describe_HasPortugueseNames()
    {
        Mp4Encoders.Describe("h264_mf").Should().Contain("Media Foundation");
        Mp4Encoders.Describe("libx264").Should().Contain("x264");
        Mp4Encoders.Describe("libopenh264").Should().Contain("OpenH264");
        Mp4Encoders.Describe("mpeg4").Should().Contain("MPEG-4");
        Mp4Encoders.Describe("estranho").Should().Be("estranho");
        Mp4Encoders.Describe(null).Should().Be("—");
    }

    // ------------------------------------------------------------------ ffmpeg argument lines

    private static readonly VideoProbe Dvd = new(TimeSpan.FromSeconds(60), 720, 480, 29.97, "mpeg2video", "ac3", 6_000_000, Interlaced: true);
    private static readonly VideoProbe Vcd = new(TimeSpan.FromSeconds(60), 352, 240, 29.97, "mpeg1video", "mp2", 1_150_000, Interlaced: false);

    private static string Joined(IEnumerable<string> a) => " " + string.Join(" ", a) + " ";

    [Fact]
    public void DvdTitle_ConcatenatesTheVobsWithTheConcatProtocol()
    {
        var args = Mp4CommandLine.Build(new[] { @"C:\d\VTS_01_1.VOB", @"C:\d\VTS_01_2.VOB" }, @"C:\d\MP4\t.mp4.partial", "h264_mf", Dvd, damagedSource: false);
        args.Should().ContainInOrder("-i", @"concat:C:\d\VTS_01_1.VOB|C:\d\VTS_01_2.VOB");
    }

    [Fact]
    public void SingleInput_IsPassedAsIs()
    {
        var args = Mp4CommandLine.Build(new[] { @"C:\d\a.avi" }, @"C:\d\o.mp4.partial", "mpeg4", Vcd, false);
        args.Should().ContainInOrder("-i", @"C:\d\a.avi");
        Joined(args).Should().NotContain("concat:");
    }

    [Fact]
    public void Interlaced_AddsYadif_Progressive_DoesNot()
    {
        var interlaced = Joined(Mp4CommandLine.Build(new[] { "a.vob" }, "o.partial", "h264_mf", Dvd, false));
        interlaced.Should().Contain(" -vf yadif");
        var progressive = Joined(Mp4CommandLine.Build(new[] { "a.dat" }, "o.partial", "h264_mf", Vcd, false));
        progressive.Should().NotContain("yadif").And.NotContain("-vf");
    }

    [Fact]
    public void UnknownSource_HasNoDeinterlaceFilter()
    {
        Joined(Mp4CommandLine.Build(new[] { "a.avi" }, "o.partial", "mpeg4", null, false)).Should().NotContain("yadif");
    }

    [Fact]
    public void OddDimensions_AreRoundedDownToEven_KeepingTheFilterChainOrder()
    {
        var odd = new VideoProbe(TimeSpan.FromSeconds(5), 321, 241, 25, "mpeg4", "aac", 0, Interlaced: true);
        var args = Mp4CommandLine.Build(new[] { "a.avi" }, "o.partial", "h264_mf", odd, false);
        args.Should().ContainInOrder("-vf", "yadif=mode=send_frame:parity=auto:deint=all,scale=trunc(iw/2)*2:trunc(ih/2)*2");
    }

    [Fact]
    public void CommonOutputOptions_AreAlwaysPresent()
    {
        var args = Mp4CommandLine.Build(new[] { "a.avi" }, @"C:\d\MP4\o.mp4.partial", "libx264", Vcd, false);
        args.Should().ContainInOrder("-pix_fmt", "yuv420p");
        args.Should().ContainInOrder("-c:a", "aac", "-b:a", "160k");
        args.Should().ContainInOrder("-movflags", "+faststart");
        args.Should().ContainInOrder("-f", "mp4", @"C:\d\MP4\o.mp4.partial");
        args.Should().ContainInOrder("-progress", "pipe:1");
        args.Should().ContainInOrder("-map", "0:v:0", "-map", "0:a:0?");
        args.Last().Should().Be(@"C:\d\MP4\o.mp4.partial", "the output is the last argument");
        args.Should().Contain("-nostdin");
    }

    [Fact]
    public void DamagedSource_ToleratesErrors_IntactSourceDoesNot()
    {
        var damaged = Mp4CommandLine.Build(new[] { "a.vob" }, "o.partial", "h264_mf", Dvd, damagedSource: true);
        damaged.Should().ContainInOrder("-err_detect", "ignore_err");
        damaged.Should().ContainInOrder("-fflags", "+genpts+discardcorrupt");
        // input options must come before -i
        damaged.ToList().IndexOf("-err_detect").Should().BeLessThan(damaged.ToList().IndexOf("-i"));
        damaged.ToList().IndexOf("-fflags").Should().BeLessThan(damaged.ToList().IndexOf("-i"));

        var intact = Mp4CommandLine.Build(new[] { "a.vob" }, "o.partial", "h264_mf", Dvd, damagedSource: false);
        intact.Should().NotContain("-err_detect");
        intact.Should().NotContain(a => a.Contains("discardcorrupt"));
    }

    [Fact]
    public void EncoderTestCommand_EncodesAShortSyntheticClipWithTheSameOptions()
    {
        var args = Mp4CommandLine.BuildEncoderTest("h264_mf", @"C:\t\x.mp4");
        args.Should().ContainInOrder("-c:v", "h264_mf");
        Joined(args).Should().Contain("testsrc").And.Contain("sine");
        args.Last().Should().Be(@"C:\t\x.mp4");
    }

    [Fact]
    public void ToText_QuotesArgumentsWithSpacesAndPipes()
    {
        Mp4CommandLine.ToText(new[] { "-i", @"C:\a b\c.avi", "concat:a|b" }).Should().Be("-i \"C:\\a b\\c.avi\" \"concat:a|b\"");
    }

    // ------------------------------------------------------------------ progress and validation

    [Theory]
    [InlineData("out_time_us=1500000", 1.5)]
    [InlineData("out_time_ms=2500000", 2.5)]
    [InlineData("out_time=00:00:03.250000", 3.25)]
    [InlineData("out_time_us=0", 0)]
    public void TryParseOutTime_ReadsFfmpegProgressLines(string line, double seconds)
    {
        Mp4Checks.TryParseOutTime(line, out var t).Should().BeTrue();
        t.TotalSeconds.Should().BeApproximately(seconds, 0.0001);
    }

    [Theory]
    [InlineData("progress=continue")]
    [InlineData("out_time_us=N/A")]
    [InlineData("out_time_us=-9223372036854775807")]
    [InlineData("out_time=N/A")]
    [InlineData("frame=12")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseOutTime_IgnoresEverythingElse(string? line)
    {
        Mp4Checks.TryParseOutTime(line, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(60, 60, true)]
    [InlineData(60, 61.9, true)]    // within 2 s
    [InlineData(60, 62.5, false)]   // 2 s < diff and 2 % of 60 s is only 1.2 s
    [InlineData(3600, 3660, true)]  // 60 s off but exactly under 2 % of an hour (72 s)
    [InlineData(3600, 3680, false)]
    [InlineData(5, 0.5, false)]
    [InlineData(5, 3.5, true)]      // diff 1.5 s <= 2 s
    [InlineData(5, 2.9, false)]
    public void DurationAcceptable_IsTwoSecondsOrTwoPercent(double source, double output, bool expected)
    {
        Mp4Checks.DurationAcceptable(TimeSpan.FromSeconds(source), TimeSpan.FromSeconds(output)).Should().Be(expected);
    }
}
