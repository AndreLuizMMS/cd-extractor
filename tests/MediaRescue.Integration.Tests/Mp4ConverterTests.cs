using MediaRescue.Core;
using MediaRescue.Integration.Tests.Support;
using MediaRescue.Windows;
using Xunit.Abstractions;

namespace MediaRescue.Integration.Tests;

/// <summary>RF-11 with the real ffmpeg/ffprobe from tools\ and the synthetic clips of .testmedia\ (skipped when they are absent).</summary>
public class Mp4ConverterTests
{
    private readonly ITestOutputHelper _out;
    public Mp4ConverterTests(ITestOutputHelper output) { _out = output; }

    private static (FfmpegMp4Converter Converter, FfprobeMediaProbe Probe) NewConverter(Func<string, long?>? freeSpace = null, Func<IMediaProbe, IMediaProbe>? wrap = null, TimeSpan? stall = null)
    {
        var probe = new FfprobeMediaProbe(TestEnv.ToolsDir);
        IMediaProbe used = wrap != null ? wrap(probe) : probe;
        return (new FfmpegMp4Converter(TestEnv.ToolsDir, used, freeSpace, stall), probe);
    }

    private static string Clip(string name) => Path.Combine(TestEnv.TestMediaDir, name);

    private static async Task<Mp4ConversionResult> ConvertAsync(FfmpegMp4Converter c, Mp4Workspace w, RecordingProgress? progress = null, CancellationToken ct = default) =>
        await c.ConvertAsync(w.Folder, w.Files, progress ?? new RecordingProgress(), ct);

    private static void AssertValidMp4(Mp4Workspace w, Mp4JobResult job, double expectedSeconds)
    {
        job.Status.Should().Be(Mp4JobStatus.Converted, job.Message);
        job.OutputPath.Should().NotBeNull();
        var path = w.Path_(job.OutputPath!);
        File.Exists(path).Should().BeTrue();
        File.Exists(path + ".partial").Should().BeFalse("the .partial name must be gone after the rename");

        var info = Mp4Tools.Probe(path);
        info.Video.Should().NotBeNull("the MP4 must have a video stream");
        info.Video!.CodecName.Should().BeOneOf("h264", "mpeg4");
        info.Video.PixFmt.Should().Be("yuv420p");
        info.Audio.Should().NotBeNull("the MP4 must keep the audio");
        info.Audio!.CodecName.Should().Be("aac");
        info.Duration.Should().BeApproximately(expectedSeconds, Math.Max(2, expectedSeconds * 0.02));
        job.OutputDuration.TotalSeconds.Should().BeApproximately(info.Duration, 0.5);

        var boxes = Mp4Tools.TopLevelBoxes(path);
        boxes.Should().Contain("moov").And.Contain("mdat");
        boxes.IndexOf("moov").Should().BeLessThan(boxes.IndexOf("mdat"), "+faststart puts the index before the media data");
        job.OutputBytes.Should().Be(new FileInfo(path).Length);
    }

    // ------------------------------------------------------------------ encoder selection

    [ToolsFact]
    public async Task Encoder_IsDetectedAtRunTime_AndEveryReportedOneReallyWorks()
    {
        var (c, _) = NewConverter();
        c.IsAvailable.Should().BeTrue();

        var working = await c.GetWorkingEncodersAsync(CancellationToken.None);
        _out.WriteLine("working encoders: " + string.Join(", ", working));
        working.Should().NotBeEmpty("at least the mpeg4 fallback exists in any ffmpeg build");
        working.Should().OnlyContain(e => Mp4Encoders.Preference.Contains(e));
        working.Select(e => Mp4Encoders.Preference.ToList().IndexOf(e)).Should().BeInAscendingOrder("candidates are tried in the specified preference order");

        // The list is cached: asking again must not run the tests again.
        var again = await c.GetWorkingEncodersAsync(CancellationToken.None);
        ReferenceEquals(again, working).Should().BeTrue();
    }

    // ------------------------------------------------------------------ DVD-Video

    [ToolsFact]
    public async Task DvdTitleSet_TwoVobsAndMenu_BecomeOneMp4_WithoutTheMenu_AndOriginalsUntouched()
    {
        using var w = new Mp4Workspace();
        w.AddDvdTitle(Clip("dvd.vob"), parts: 2);
        var before = w.HashSources();
        var (c, _) = NewConverter();
        var commands = new List<string>();
        c.OnCommand = commands.Add;
        var progress = new RecordingProgress();

        var result = await ConvertAsync(c, w, progress);

        result.Error.Should().BeNull();
        result.WasCancelled.Should().BeFalse();
        result.Encoder.Should().BeOneOf(Mp4Encoders.Preference);
        var job = result.Jobs.Should().ContainSingle("one MP4 per title set; the menu VOB is excluded").Subject;
        job.Name.Should().Be("Título 01 (VTS_01)");
        job.Sources.Should().Equal(@"VIDEO_TS\VTS_01_1.VOB", @"VIDEO_TS\VTS_01_2.VOB");
        job.Encoder.Should().Be(result.Encoder);
        job.FromDamagedSource.Should().BeFalse();
        job.OutputPath.Should().Be(@"MP4\Título 01 (VTS_01).mp4");
        AssertValidMp4(w, job, 5.0);
        w.OutputFiles().Should().ContainSingle();

        commands.Should().ContainSingle();
        _out.WriteLine("DVD command: " + commands[0]);
        commands[0].Should().Contain("concat:").And.Contain("VTS_01_1.VOB|").And.Contain("VTS_01_2.VOB");
        commands[0].Should().NotContain("VTS_01_0").And.NotContain("VIDEO_TS.VOB");

        w.HashSources().Should().BeEquivalentTo(before, "originals are never modified");

        // Progress reaches the end and never goes backwards.
        var items = progress.Items;
        items.Should().NotBeEmpty();
        items.Select(p => p.OverallFraction).Should().BeInAscendingOrder();
        items.Last().OverallFraction.Should().BeApproximately(1.0, 0.0001);
        items.Should().OnlyContain(p => p.JobCount == 1 && p.OverallFraction >= 0 && p.OverallFraction <= 1);
        items.Any(p => p.JobIndex == 1 && p.CurrentName == "Título 01 (VTS_01)").Should().BeTrue();
        items.Where(p => p.JobFraction != null).Should().OnlyContain(p => p.JobFraction >= 0 && p.JobFraction <= 1);
    }

    [ToolsFact]
    public async Task TwoTitleSets_AreTwoMp4s()
    {
        using var w = new Mp4Workspace();
        w.AddDvdTitle(Clip("dvd.vob"), titleSet: 1, parts: 1, withMenus: false);
        w.AddDvdTitle(Clip("dvd.vob"), titleSet: 2, parts: 2, withMenus: true);
        var (c, _) = NewConverter();

        var result = await ConvertAsync(c, w);

        result.Jobs.Select(j => j.Status).Should().OnlyContain(s => s == Mp4JobStatus.Converted);
        result.Jobs.Select(j => j.OutputPath).Should().Equal(@"MP4\Título 01 (VTS_01).mp4", @"MP4\Título 02 (VTS_02).mp4");
        foreach (var j in result.Jobs) AssertValidMp4(w, j, 5.0);
    }

    [ToolsFact]
    public async Task WidescreenAndFullscreenDvd_KeepTheirDisplayAspectRatio()
    {
        foreach (var (aspect, expected) in new[] { ("16:9", 16.0 / 9.0), ("4:3", 4.0 / 3.0) })
        {
            using var w = new Mp4Workspace();
            Directory.CreateDirectory(w.Path_("VIDEO_TS"));
            var vob = w.Path_(@"VIDEO_TS\VTS_01_1.VOB");
            Mp4Tools.Make(vob, "-f", "lavfi", "-i", "testsrc=duration=3:size=720x480:rate=30000/1001", "-f", "lavfi", "-i", "sine=duration=3:sample_rate=48000",
                "-target", "ntsc-dvd", "-aspect", aspect);
            var source = Mp4Tools.Probe(vob);
            source.DisplayAspect.Should().BeApproximately(expected, 0.01, "precondition");
            source.Video!.SampleAspect.Should().NotBe("1:1", "DVD uses non-square pixels");
            w.Files.Add(new FileResult { RelativePath = @"VIDEO_TS\VTS_01_1.VOB", WrittenPath = @"VIDEO_TS\VTS_01_1.VOB", Status = FileStatus.Intact, BytesTotal = new FileInfo(vob).Length });
            var (c, _) = NewConverter();

            var result = await ConvertAsync(c, w);

            var job = result.Jobs.Single();
            AssertValidMp4(w, job, 3.0);
            var output = Mp4Tools.Probe(w.Path_(job.OutputPath!));
            _out.WriteLine($"{aspect}: source SAR {source.Video.SampleAspect} DAR {source.Video.DisplayAspect} -> output {output.Video!.Width}x{output.Video.Height} SAR {output.Video.SampleAspect} DAR {output.Video.DisplayAspect} ({job.Encoder})");
            output.DisplayAspect.Should().BeApproximately(expected, 0.02, $"{aspect} must stay {aspect} in the MP4 (encoder {job.Encoder})");
        }
    }

    [ToolsFact]
    public async Task InterlacedDvd_IsDeinterlaced_ProgressiveSourcesAreNot()
    {
        using var w = new Mp4Workspace();
        Directory.CreateDirectory(w.Path_("VIDEO_TS"));
        var inter = w.Path_(@"VIDEO_TS\VTS_01_1.VOB");
        Mp4Tools.Make(inter, "-f", "lavfi", "-i", "testsrc2=duration=3:size=720x480:rate=30000/1001", "-f", "lavfi", "-i", "sine=duration=3:sample_rate=48000",
            "-flags", "+ildct+ilme", "-top", "1", "-target", "ntsc-dvd", "-aspect", "4:3");
        Mp4Tools.Probe(inter).Video!.FieldOrder.Should().Be("tt", "precondition: the clip is flagged interlaced");
        w.Files.Add(new FileResult { RelativePath = @"VIDEO_TS\VTS_01_1.VOB", WrittenPath = @"VIDEO_TS\VTS_01_1.VOB", Status = FileStatus.Intact, BytesTotal = new FileInfo(inter).Length });
        w.AddClip("vcd.mpg", @"MPEGAV\AVSEQ01.DAT");   // progressive
        var (c, _) = NewConverter();
        var commands = new List<string>();
        c.OnCommand = commands.Add;

        var result = await ConvertAsync(c, w);

        result.Jobs.Should().HaveCount(2).And.OnlyContain(j => j.Status == Mp4JobStatus.Converted);
        foreach (var cmd in commands) _out.WriteLine(cmd);
        commands.Single(x => x.Contains("VTS_01_1.VOB")).Should().Contain("yadif");
        commands.Single(x => x.Contains("AVSEQ01.DAT")).Should().NotContain("yadif");
        var outFile = w.Path_(result.Jobs.First(j => j.Name.StartsWith("Título")).OutputPath!);
        Mp4Tools.Probe(outFile).Video!.FieldOrder.Should().BeOneOf("progressive", "unknown", null);
    }

    // ------------------------------------------------------------------ VCD, AVI, WMV

    [ToolsFact]
    public async Task VcdTrack_DatFile_BecomesOneMp4_WithTheSameAspect()
    {
        using var w = new Mp4Workspace();
        w.AddClip("vcd.mpg", @"MPEGAV\AVSEQ01.DAT");
        var before = w.HashSources();
        var source = Mp4Tools.Probe(Clip("vcd.mpg"));
        var (c, _) = NewConverter();
        var commands = new List<string>();
        c.OnCommand = commands.Add;

        var result = await ConvertAsync(c, w);

        var job = result.Jobs.Should().ContainSingle().Subject;
        job.OutputPath.Should().Be(@"MP4\AVSEQ01.mp4");
        AssertValidMp4(w, job, source.Duration);
        var output = Mp4Tools.Probe(w.Path_(job.OutputPath!));
        output.DisplayAspect.Should().BeApproximately(source.DisplayAspect, 0.02);
        output.Audio!.CodecName.Should().Be("aac");
        _out.WriteLine("VCD command: " + commands.Single());
        w.HashSources().Should().BeEquivalentTo(before);
    }

    [ToolsFact]
    public async Task AviWmvAndUnicodeNames_AreConverted_AndMp4IsLeftAlone()
    {
        using var w = new Mp4Workspace();
        w.AddClip("sample.avi", "sample.avi");
        w.AddClip("sample.wmv", @"Férias 2009 – Praia\Ação e emoção ☺.wmv");
        w.AddClip("sample.mp4", "ja-e-mp4.mp4");
        var before = w.HashSources();
        var (c, _) = NewConverter();
        var commands = new List<string>();
        c.OnCommand = commands.Add;

        var result = await ConvertAsync(c, w);

        result.Error.Should().BeNull();
        result.Jobs.Should().HaveCount(2, "the .mp4 is not converted");
        result.Jobs.Should().OnlyContain(j => j.Status == Mp4JobStatus.Converted, string.Join("; ", result.Jobs.Select(j => j.Message)));
        result.Jobs.Select(j => j.OutputPath).Should().BeEquivalentTo(new[] { @"MP4\Férias 2009 – Praia\Ação e emoção ☺.mp4", @"MP4\sample.mp4" });
        AssertValidMp4(w, result.Jobs.Single(j => j.Name == "sample.avi"), Mp4Tools.Probe(Clip("sample.avi")).Duration);
        AssertValidMp4(w, result.Jobs.Single(j => j.Name.EndsWith(".wmv")), Mp4Tools.Probe(Clip("sample.wmv")).Duration);
        foreach (var cmd in commands) _out.WriteLine(cmd);
        w.HashSources().Should().BeEquivalentTo(before);
    }

    // ------------------------------------------------------------------ cancellation, failures

    [ToolsFact]
    public async Task Cancellation_StopsFfmpeg_AndLeavesNoOutputAtAll()
    {
        using var w = new Mp4Workspace();
        var longClip = w.Path_("longo.avi");
        Mp4Tools.Make(longClip, "-stream_loop", "150", "-i", Clip("sample.avi"), "-c", "copy");
        w.Files.Add(new FileResult { RelativePath = "longo.avi", WrittenPath = "longo.avi", Status = FileStatus.Intact, BytesTotal = new FileInfo(longClip).Length });
        var before = w.HashSources();
        var (c, _) = NewConverter();
        using var cts = new CancellationTokenSource();
        var progress = new RecordingProgress();
        progress.Reported += p => { if (p.JobFraction is > 0.02) cts.Cancel(); };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await ConvertAsync(c, w, progress, cts.Token);
        _out.WriteLine($"cancelled after {sw.ElapsedMilliseconds} ms");

        cts.IsCancellationRequested.Should().BeTrue("the test must have reached the conversion before cancelling");
        result.WasCancelled.Should().BeTrue();
        result.Jobs.Should().ContainSingle().Which.Status.Should().Be(Mp4JobStatus.Cancelled);
        result.ConvertedCount.Should().Be(0);
        w.OutputFiles().Should().BeEmpty("neither the .mp4 nor the .partial may be left behind");
        Directory.Exists(w.OutputDir).Should().BeFalse("an empty MP4 folder created for this run is removed again");
        w.HashSources().Should().BeEquivalentTo(before);
        // ffmpeg keeps its input open without delete-sharing: if it were still running, deleting the source would fail.
        var deleting = () => File.Delete(longClip);
        deleting.Should().NotThrow("the ffmpeg process was killed and released the file");
    }

    [ToolsFact]
    public async Task CancellationBeforeStart_ReturnsCancelled_WithoutThrowing()
    {
        using var w = new Mp4Workspace();
        w.AddClip("sample.avi", "a.avi");
        var (c, _) = NewConverter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await ConvertAsync(c, w, ct: cts.Token);

        result.WasCancelled.Should().BeTrue();
        w.OutputFiles().Should().BeEmpty();
    }

    [ToolsFact]
    public async Task CorruptInput_FailsOnlyThatJob_TheOthersStillConvert()
    {
        using var w = new Mp4Workspace();
        w.Add("a_corrompido.avi", IsoSpec.RandomBytes(120_000, 5));
        w.AddClip("sample.avi", "b_bom.avi");
        w.Add("c_vazio_de_video.wmv", System.Text.Encoding.ASCII.GetBytes("isto nao e um video"));
        var before = w.HashSources();
        var (c, _) = NewConverter();

        var result = await ConvertAsync(c, w);

        result.Error.Should().BeNull("a per-file failure does not fail the whole conversion");
        result.Jobs.Select(j => j.Status).Should().Equal(Mp4JobStatus.Failed, Mp4JobStatus.Converted, Mp4JobStatus.Failed);
        var bad = result.Jobs[0];
        bad.OutputPath.Should().BeNull();
        bad.Message.Should().NotBeNullOrWhiteSpace();
        _out.WriteLine("failure message: " + bad.Message);
        AssertValidMp4(w, result.Jobs[1], Mp4Tools.Probe(Clip("sample.avi")).Duration);
        w.OutputFiles().Should().ContainSingle().Which.Should().EndWith("b_bom.mp4");
        w.HashSources().Should().BeEquivalentTo(before);
    }

    [ToolsFact]
    public async Task PartialSource_IsConverted_AndMarkedAsDerivedFromDamagedData()
    {
        using var w = new Mp4Workspace();
        var data = File.ReadAllBytes(Clip("vcd.mpg"));
        Array.Clear(data, 300_000, 16_384);   // a zero-filled hole, like the engine writes for unreadable ranges
        w.Add(@"MPEG2\AVSEQ01.MPG", data, FileStatus.Partial, lost: 16_384);
        var (c, _) = NewConverter();
        var commands = new List<string>();
        c.OnCommand = commands.Add;

        var result = await ConvertAsync(c, w);

        var job = result.Jobs.Should().ContainSingle().Subject;
        job.Status.Should().Be(Mp4JobStatus.Converted, job.Message);
        job.FromDamagedSource.Should().BeTrue();
        job.Message.Should().Contain("incompleta");
        Mp4Tools.Probe(w.Path_(job.OutputPath!)).Video.Should().NotBeNull();
        commands.Single().Should().Contain("-err_detect ignore_err").And.Contain("discardcorrupt");
        _out.WriteLine("partial command: " + commands.Single());
    }

    [ToolsFact]
    public async Task FailedAndSkippedFiles_AreNotTouched()
    {
        using var w = new Mp4Workspace();
        w.AddClip("sample.avi", "falhou.avi", FileStatus.Failed);
        w.AddClip("sample.avi", "pulado.avi", FileStatus.Skipped);
        w.AddClip("sample.avi", "ok.avi");
        var (c, _) = NewConverter();

        var result = await ConvertAsync(c, w);

        result.Jobs.Should().ContainSingle().Which.Name.Should().Be("ok.avi");
    }

    [ToolsFact]
    public async Task NothingToConvert_ReturnsAnEmptyResult_AndCreatesNoFolder()
    {
        using var w = new Mp4Workspace();
        w.AddClip("sample.mp4", "ja.mp4");
        w.Add("foto.jpg", new byte[100]);
        var (c, _) = NewConverter();

        var result = await ConvertAsync(c, w);

        result.Jobs.Should().BeEmpty();
        result.Error.Should().BeNull();
        Directory.Exists(w.OutputDir).Should().BeFalse();
    }

    [ToolsFact]
    public async Task ExistingFilesInMp4Folder_AreNeverOverwritten()
    {
        using var w = new Mp4Workspace();
        w.AddClip("sample.avi", "sample.avi");
        Directory.CreateDirectory(w.OutputDir);
        File.WriteAllText(Path.Combine(w.OutputDir, "sample.mp4"), "do not touch");
        var (c, _) = NewConverter();

        var result = await ConvertAsync(c, w);

        File.ReadAllText(Path.Combine(w.OutputDir, "sample.mp4")).Should().Be("do not touch");
        result.Jobs.Single().OutputPath.Should().Be(@"MP4\sample (2).mp4");
        AssertValidMp4(w, result.Jobs.Single(), Mp4Tools.Probe(Clip("sample.avi")).Duration);
    }

    [ToolsFact]
    public async Task NotEnoughFreeSpace_RefusesBeforeStarting()
    {
        using var w = new Mp4Workspace();
        w.AddClip("sample.avi", "sample.avi");
        var (c, _) = NewConverter(freeSpace: _ => 1000);
        var commands = new List<string>();
        c.OnCommand = commands.Add;

        var result = await ConvertAsync(c, w);

        result.Error.Should().NotBeNull().And.Contain("espaço");
        result.Jobs.Should().BeEmpty();
        commands.Should().BeEmpty("nothing is started when the space check fails");
        Directory.Exists(w.OutputDir).Should().BeFalse();
    }

    [ToolsFact]
    public async Task SpaceCheck_UsesTheRealVolume_AndPassesOnThisMachine()
    {
        using var w = new Mp4Workspace();
        w.AddClip("sample.avi", "sample.avi");
        var (c, _) = NewConverter();   // default free-space provider

        var result = await ConvertAsync(c, w);

        result.Error.Should().BeNull();
        result.ConvertedCount.Should().Be(1);
    }

    [ToolsFact]
    public async Task OutputWhoseDurationDoesNotMatchTheSource_IsDiscarded_ForIntactSources()
    {
        using var w = new Mp4Workspace();
        w.AddClip("sample.avi", "sample.avi");
        // The converter is told the source lasts 60 s while the MP4 will last ~4 s.
        var (c, _) = NewConverter(wrap: p => new TweakedProbe(p)
        {
            Tweak = (path, v) => v != null && !path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ? v with { Duration = TimeSpan.FromSeconds(60) } : v,
        });

        var result = await ConvertAsync(c, w);

        var job = result.Jobs.Should().ContainSingle().Subject;
        job.Status.Should().Be(Mp4JobStatus.Failed);
        job.Message.Should().Contain("duração");
        w.OutputFiles().Should().BeEmpty("the invalid output must not be kept, not even as .partial");
    }

    [ToolsFact]
    public async Task OutputWhoseDurationDiffers_IsKept_ForDamagedSources_WithAWarning()
    {
        using var w = new Mp4Workspace();
        w.AddClip("sample.avi", "sample.avi", FileStatus.Partial, lost: 100);
        var (c, _) = NewConverter(wrap: p => new TweakedProbe(p)
        {
            Tweak = (path, v) => v != null && !path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ? v with { Duration = TimeSpan.FromSeconds(60) } : v,
        });

        var result = await ConvertAsync(c, w);

        var job = result.Jobs.Should().ContainSingle().Subject;
        job.Status.Should().Be(Mp4JobStatus.Converted);
        job.FromDamagedSource.Should().BeTrue();
        job.Message.Should().Contain("duração").And.Contain("incompleta");
    }

    // ------------------------------------------------------------------ no tools

    [Fact]
    public async Task WithoutTools_ConverterIsUnavailable_AndReportsItInsteadOfThrowing()
    {
        var empty = TestEnv.NewTempDir("mr-notools");
        try
        {
            var probe = new FfprobeMediaProbe(empty);
            var c = new FfmpegMp4Converter(empty, probe);
            c.IsAvailable.Should().BeFalse();

            var folder = TestEnv.NewTempDir("mr-disc");
            try
            {
                File.WriteAllBytes(Path.Combine(folder, "a.avi"), new byte[100]);
                var files = new[] { new FileResult { RelativePath = "a.avi", WrittenPath = "a.avi", Status = FileStatus.Intact, BytesTotal = 100 } };
                var result = await c.ConvertAsync(folder, files, new RecordingProgress(), CancellationToken.None);
                result.Error.Should().Contain("ffmpeg");
                result.Jobs.Should().BeEmpty();
                Directory.Exists(Path.Combine(folder, "MP4")).Should().BeFalse();
            }
            finally { Directory.Delete(folder, true); }
        }
        finally { Directory.Delete(empty, true); }
    }

    [ToolsFact]
    public async Task MissingSourceCopy_FailsThatJobOnly()
    {
        using var w = new Mp4Workspace();
        w.AddClip("sample.avi", "existe.avi");
        w.Files.Add(new FileResult { RelativePath = "sumiu.avi", WrittenPath = "sumiu.avi", Status = FileStatus.Intact, BytesTotal = 1000 });
        var (c, _) = NewConverter();

        var result = await ConvertAsync(c, w);

        result.Jobs.Select(j => (j.Name, j.Status)).Should().BeEquivalentTo(new[] { ("existe.avi", Mp4JobStatus.Converted), ("sumiu.avi", Mp4JobStatus.Failed) });
    }
}
