using System.Diagnostics;
using System.IO.Pipes;
using MediaRescue.Core;
using MediaRescue.Integration.Tests.Support;
using MediaRescue.Windows;

namespace MediaRescue.Integration.Tests;

public sealed class FfprobeMediaProbeTests
{
    private static string Media(string name) => Path.Combine(TestEnv.TestMediaDir, name);

    [Fact]
    public async Task E28_WithoutTools_IsUnavailable_AndReturnsNullWithoutThrowing()
    {
        var empty = TestEnv.NewTempDir("mr-notools");
        try
        {
            var probe = new FfprobeMediaProbe(empty);
            probe.IsAvailable.Should().BeFalse();
            (await probe.ProbeAsync(@"C:\qualquer.avi", CancellationToken.None)).Should().BeNull();
            (await probe.ThumbnailAsync(@"C:\qualquer.avi", TimeSpan.FromSeconds(1), CancellationToken.None)).Should().BeNull();
            new FfprobeMediaProbe(Path.Combine(empty, "nao-existe")).IsAvailable.Should().BeFalse();
            new FfprobeMediaProbe("").IsAvailable.Should().BeFalse();
        }
        finally { Directory.Delete(empty, true); }
    }

    [ToolsFact]
    public async Task WithTools_IsAvailable_AndProbesDvdVob()
    {
        var probe = new FfprobeMediaProbe(TestEnv.ToolsDir);
        probe.IsAvailable.Should().BeTrue();
        var p = await probe.ProbeAsync(Media("dvd.vob"), CancellationToken.None);
        p.Should().NotBeNull();
        p!.VideoCodec.Should().Be("mpeg2video");
        p.Width.Should().Be(720);
        p.Height.Should().Be(480);
        p.Fps.Should().BeApproximately(29.97, 0.05);
        p.Duration.TotalSeconds.Should().BeApproximately(5, 1);
        p.AudioCodec.Should().Be("ac3");
        p.Bitrate.Should().BeGreaterThan(0);
    }

    [ToolsTheory]
    [InlineData("vcd.mpg", "mpeg1video", 352, 240)]
    [InlineData("sample.avi", "mpeg4", 320, 240)]
    [InlineData("sample.wmv", "wmv2", 320, 240)]
    [InlineData("sample.mp4", "mpeg4", 320, 240)]
    public async Task WithTools_ProbesOtherFormats(string file, string codec, int w, int h)
    {
        var p = await new FfprobeMediaProbe(TestEnv.ToolsDir).ProbeAsync(Media(file), CancellationToken.None);
        p.Should().NotBeNull(file);
        p!.VideoCodec.Should().Be(codec);
        p.Width.Should().Be(w);
        p.Height.Should().Be(h);
        p.Duration.TotalSeconds.Should().BeInRange(2.5, 6);
        p.AudioCodec.Should().NotBeNullOrEmpty();
    }

    [ToolsFact]
    public async Task WithTools_ThumbnailIsAPng()
    {
        var png = await new FfprobeMediaProbe(TestEnv.ToolsDir).ThumbnailAsync(Media("sample.mp4"), TimeSpan.FromSeconds(1), CancellationToken.None);
        png.Should().NotBeNull();
        png!.Take(8).Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
    }

    [ToolsFact]
    public async Task E28_GarbageAndMissingFiles_YieldNull()
    {
        var dir = TestEnv.NewTempDir("mr-garbage");
        try
        {
            var junk = Path.Combine(dir, "lixo.avi");
            File.WriteAllBytes(junk, IsoSpec.RandomBytes(50_000, 1));
            var probe = new FfprobeMediaProbe(TestEnv.ToolsDir);
            (await probe.ProbeAsync(junk, CancellationToken.None)).Should().BeNull();
            (await probe.ProbeAsync(Path.Combine(dir, "nao-existe.avi"), CancellationToken.None)).Should().BeNull();
            var text = Path.Combine(dir, "texto.mpg");
            File.WriteAllText(text, "isto não é um vídeo");
            (await probe.ProbeAsync(text, CancellationToken.None)).Should().BeNull();
        }
        finally { Directory.Delete(dir, true); }
    }

    [ToolsFact]
    public async Task E28_HungProcess_IsKilledAtTheTimeout_AndNullIsReturned()
    {
        // ffprobe opens the named pipe and blocks forever waiting for data: a stuck probe.
        var name = "mr-probe-hang-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var before = Process.GetProcessesByName("ffprobe").Select(p => p.Id).ToHashSet();

        var probe = new FfprobeMediaProbe(TestEnv.ToolsDir, TimeSpan.FromSeconds(2));
        var sw = Stopwatch.StartNew();
        var result = await probe.ProbeAsync($@"\\.\pipe\{name}", CancellationToken.None);
        sw.Stop();

        result.Should().BeNull();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        await Task.Delay(500);
        Process.GetProcessesByName("ffprobe").Where(p => !before.Contains(p.Id)).Should().BeEmpty("a timed-out ffprobe must be killed");
    }

    [ToolsFact]
    public async Task Cancellation_KillsTheProcess_AndReturnsNull()
    {
        var name = "mr-probe-cancel-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var before = Process.GetProcessesByName("ffprobe").Select(p => p.Id).ToHashSet();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        var sw = Stopwatch.StartNew();
        (await new FfprobeMediaProbe(TestEnv.ToolsDir).ProbeAsync($@"\\.\pipe\{name}", cts.Token)).Should().BeNull();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8));
        await Task.Delay(500);
        Process.GetProcessesByName("ffprobe").Where(p => !before.Contains(p.Id)).Should().BeEmpty();
    }

    [Fact]
    public void Parser_RejectsInvalidJson_AndHandlesMinimalStreams()
    {
        FfprobeMediaProbe.Parse(System.Text.Encoding.UTF8.GetBytes("not json")).Should().BeNull();
        FfprobeMediaProbe.Parse(System.Text.Encoding.UTF8.GetBytes("{}")).Should().BeNull();
        FfprobeMediaProbe.Parse(System.Text.Encoding.UTF8.GetBytes("[1,2]")).Should().BeNull();
        var json = """
        {"streams":[{"codec_type":"video","codec_name":"mpeg2video","width":720,"height":576,"avg_frame_rate":"25/1","field_order":"tt"},
                    {"codec_type":"audio","codec_name":"mp2"}],
         "format":{"duration":"61.5","bit_rate":"6000000"}}
        """;
        var p = FfprobeMediaProbe.Parse(System.Text.Encoding.UTF8.GetBytes(json));
        p.Should().NotBeNull();
        p!.Interlaced.Should().BeTrue();
        p.Fps.Should().Be(25);
        p.Duration.Should().Be(TimeSpan.FromSeconds(61.5));
        p.Bitrate.Should().Be(6_000_000);
        p.AudioCodec.Should().Be("mp2");
        FfprobeMediaProbe.Parse(System.Text.Encoding.UTF8.GetBytes("""{"streams":[{"codec_type":"video","width":1,"height":1,"avg_frame_rate":"0/0"}],"format":{"duration":"NaN"}}"""))!
            .Duration.Should().Be(TimeSpan.Zero);
    }
}

/// <summary>[Theory] counterpart of <see cref="ToolsFactAttribute"/>.</summary>
public sealed class ToolsTheoryAttribute : TheoryAttribute
{
    public ToolsTheoryAttribute()
    {
        if (!TestEnv.HasTools || !Directory.Exists(TestEnv.TestMediaDir))
            Skip = "SKIPPED: tools\\ffmpeg.exe/ffprobe.exe or .testmedia\\ missing (run build\\fetch-tools.ps1 and build\\make-test-media.ps1).";
    }
}
