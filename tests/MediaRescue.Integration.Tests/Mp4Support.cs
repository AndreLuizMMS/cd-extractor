using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MediaRescue.Core;
using MediaRescue.Integration.Tests.Support;
using MediaRescue.Windows;

namespace MediaRescue.Integration.Tests;

/// <summary>Helpers for the RF-11 conversion tests: a fake "disc folder" built from the synthetic clips, ffprobe/ffmpeg shortcuts.</summary>
internal sealed class Mp4Workspace : IDisposable
{
    public string Folder { get; } = TestEnv.NewTempDir("mr-mp4");
    public List<FileResult> Files { get; } = new();

    public string Path_(string relative) => Path.Combine(Folder, relative);
    public string OutputDir => Path.Combine(Folder, "MP4");

    public FileResult Add(string relative, byte[] data, FileStatus status = FileStatus.Intact, long lost = 0, string? discPath = null)
    {
        var full = Path_(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, data);
        var f = new FileResult { RelativePath = discPath ?? relative, WrittenPath = relative, Status = status, BytesTotal = data.Length, BytesLost = lost };
        Files.Add(f);
        return f;
    }

    public FileResult AddClip(string clip, string relative, FileStatus status = FileStatus.Intact, long lost = 0, string? discPath = null) =>
        Add(relative, File.ReadAllBytes(Path.Combine(TestEnv.TestMediaDir, clip)), status, lost, discPath);

    /// <summary>A fake VIDEO_TS: a menu VOB (garbage that must never be converted) and the title VOBs, split at pack boundaries.</summary>
    public void AddDvdTitle(string vobFile, int titleSet = 1, int parts = 2, bool withMenus = true)
    {
        var data = File.ReadAllBytes(vobFile);
        var packs = data.Length / 2048;
        int perPart = Math.Max(1, packs / parts);
        for (int i = 0; i < parts; i++)
        {
            int startPack = i * perPart;
            int endPack = i == parts - 1 ? packs : (i + 1) * perPart;
            var chunk = data.AsSpan(startPack * 2048, (endPack - startPack) * 2048).ToArray();
            Add($@"VIDEO_TS\VTS_{titleSet:00}_{i + 1}.VOB", chunk);
        }
        if (withMenus)
        {
            Add($@"VIDEO_TS\VTS_{titleSet:00}_0.VOB", IsoSpec.RandomBytes(40_000, 77));
            Add(@"VIDEO_TS\VIDEO_TS.VOB", IsoSpec.RandomBytes(40_000, 78));
            Add($@"VIDEO_TS\VTS_{titleSet:00}_0.IFO", IsoSpec.RandomBytes(4096, 79));
        }
    }

    public Dictionary<string, string> HashSources() =>
        Files.ToDictionary(f => f.WrittenPath, f => Hash(Path_(f.WrittenPath)), StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> OutputFiles() =>
        Directory.Exists(OutputDir) ? Directory.EnumerateFiles(OutputDir, "*", SearchOption.AllDirectories) : Enumerable.Empty<string>();

    public void Dispose()
    {
        try { Directory.Delete(Folder, true); } catch { }
    }

    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

internal sealed record StreamInfo(string CodecType, string CodecName, int Width, int Height, string? SampleAspect, string? DisplayAspect, string? FieldOrder, string? PixFmt, double Duration);

internal sealed record MediaInfo(IReadOnlyList<StreamInfo> Streams, double Duration)
{
    public StreamInfo? Video => Streams.FirstOrDefault(s => s.CodecType == "video");
    public StreamInfo? Audio => Streams.FirstOrDefault(s => s.CodecType == "audio");

    /// <summary>Display aspect ratio of the first video stream as a number (from DAR, or width/height x SAR).</summary>
    public double DisplayAspect
    {
        get
        {
            var v = Video!;
            if (TryRatio(v.DisplayAspect, out var dar) && dar > 0) return dar;
            var sar = TryRatio(v.SampleAspect, out var s) && s > 0 ? s : 1.0;
            return (double)v.Width / v.Height * sar;
        }
    }

    private static bool TryRatio(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text) || text == "N/A") return false;
        var p = text.Split(':', '/');
        if (p.Length != 2 || !double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ||
            !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) || b == 0) return false;
        value = a / b;
        return true;
    }
}

internal static class Mp4Tools
{
    public static string Ffmpeg => Path.Combine(TestEnv.ToolsDir, "ffmpeg.exe");
    public static string Ffprobe => Path.Combine(TestEnv.ToolsDir, "ffprobe.exe");

    public static (int Exit, string Stdout, string Stderr) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } throw new TimeoutException(exe); }
        return (p.ExitCode, o.Result, e.Result);
    }

    public static MediaInfo Probe(string path)
    {
        var (exit, stdout, stderr) = Run(Ffprobe, "-v", "error", "-print_format", "json", "-show_format", "-show_streams", path);
        exit.Should().Be(0, $"ffprobe must be able to read {path}: {stderr}");
        using var doc = JsonDocument.Parse(stdout);
        string? S(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        double D(JsonElement e, string n) => S(e, n) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().Select(s => new StreamInfo(
            S(s, "codec_type") ?? "", S(s, "codec_name") ?? "",
            s.TryGetProperty("width", out var w) ? w.GetInt32() : 0, s.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
            S(s, "sample_aspect_ratio"), S(s, "display_aspect_ratio"), S(s, "field_order"), S(s, "pix_fmt"), D(s, "duration"))).ToList();
        return new MediaInfo(streams, D(doc.RootElement.GetProperty("format"), "duration"));
    }

    /// <summary>Runs ffmpeg to create a synthetic clip; fails the test when it does not work.</summary>
    public static void Make(string output, params string[] args)
    {
        var all = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        all.AddRange(args);
        all.Add(output);
        var (exit, _, stderr) = Run(Ffmpeg, all.ToArray());
        exit.Should().Be(0, "ffmpeg must create the synthetic clip: " + stderr);
        File.Exists(output).Should().BeTrue();
    }

    /// <summary>Names of the top-level MP4 boxes in file order (ftyp, moov, mdat, ...).</summary>
    public static List<string> TopLevelBoxes(string path)
    {
        var boxes = new List<string>();
        using var fs = File.OpenRead(path);
        var header = new byte[16];
        while (fs.Position + 8 <= fs.Length)
        {
            if (fs.Read(header, 0, 8) < 8) break;
            long size = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            boxes.Add(System.Text.Encoding.ASCII.GetString(header, 4, 4));
            if (size == 1) { fs.Read(header, 8, 8); size = 0; for (int i = 8; i < 16; i++) size = (size << 8) | header[i]; fs.Position += size - 16; }
            else if (size == 0) break;
            else fs.Position += size - 8;
        }
        return boxes;
    }
}

internal sealed class RecordingProgress : IProgress<Mp4ConversionProgress>
{
    public List<Mp4ConversionProgress> Items { get; } = new();
    public event Action<Mp4ConversionProgress>? Reported;
    public void Report(Mp4ConversionProgress value)
    {
        lock (Items) Items.Add(value);
        Reported?.Invoke(value);
    }
}

/// <summary>Wraps the real probe; lets a test change what the converter believes about the source.</summary>
internal sealed class TweakedProbe : IMediaProbe
{
    private readonly IMediaProbe _inner;
    public Func<string, VideoProbe?, VideoProbe?> Tweak { get; set; } = (_, p) => p;
    public TweakedProbe(IMediaProbe inner) => _inner = inner;
    public bool IsAvailable => _inner.IsAvailable;
    public async Task<VideoProbe?> ProbeAsync(string fullPath, CancellationToken ct) => Tweak(fullPath, await _inner.ProbeAsync(fullPath, ct));
    public Task<byte[]?> ThumbnailAsync(string fullPath, TimeSpan at, CancellationToken ct) => _inner.ThumbnailAsync(fullPath, at, ct);
}
