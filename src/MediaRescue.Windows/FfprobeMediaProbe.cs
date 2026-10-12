using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MediaRescue.Core;

namespace MediaRescue.Windows;

/// <summary>
/// <see cref="IMediaProbe"/> backed by ffprobe.exe / ffmpeg.exe found in a tools directory (optional). Never throws:
/// absent tools, a hung process (15 s timeout, killed) or invalid output simply yield null (E-28).
/// </summary>
public sealed class FfprobeMediaProbe : IMediaProbe
{
    private readonly string _ffprobe;
    private readonly string _ffmpeg;
    private readonly TimeSpan _timeout;

    public FfprobeMediaProbe(string toolsDirectory) : this(toolsDirectory, TimeSpan.FromSeconds(15)) { }

    internal FfprobeMediaProbe(string toolsDirectory, TimeSpan timeout)
    {
        _timeout = timeout;
        string dir = toolsDirectory ?? "";
        _ffprobe = Path.Combine(dir, "ffprobe.exe");
        _ffmpeg = Path.Combine(dir, "ffmpeg.exe");
    }

    public bool IsAvailable => SafeExists(_ffprobe);

    private static bool SafeExists(string p) { try { return File.Exists(p); } catch { return false; } }

    public async Task<VideoProbe?> ProbeAsync(string fullPath, CancellationToken ct)
    {
        try
        {
            if (!IsAvailable || string.IsNullOrWhiteSpace(fullPath)) return null;
            var args = new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-i", fullPath };
            var result = await RunAsync(_ffprobe, args, captureStdout: true, ct).ConfigureAwait(false);
            if (result == null) return null;
            return Parse(result);
        }
        catch
        {
            return null;
        }
    }

    public async Task<byte[]?> ThumbnailAsync(string fullPath, TimeSpan at, CancellationToken ct)
    {
        try
        {
            if (!SafeExists(_ffmpeg) || string.IsNullOrWhiteSpace(fullPath)) return null;
            var args = new[]
            {
                "-v", "error", "-nostdin", "-ss", at.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", fullPath, "-frames:v", "1", "-vf", "scale=320:-2", "-f", "image2pipe", "-c:v", "png", "pipe:1",
            };
            var data = await RunAsync(_ffmpeg, args, captureStdout: true, ct).ConfigureAwait(false);
            return data is { Length: > 8 } ? data : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Runs the tool, returns stdout bytes on exit code 0, otherwise null. Kills the process tree on timeout/cancel.</summary>
    private async Task<byte[]?> RunAsync(string exe, string[] args, bool captureStdout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = captureStdout,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) return null;
        }
        catch
        {
            return null;
        }

        try { process.StandardInput.Close(); } catch { }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);

        var stdoutTask = Task.Run(async () =>
        {
            using var ms = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(ms, 81920, CancellationToken.None).ConfigureAwait(false);
            return ms.ToArray();
        });
        var stderrTask = Task.Run(async () =>
        {
            var buf = new byte[4096];
            while (await process.StandardError.BaseStream.ReadAsync(buf, CancellationToken.None).ConfigureAwait(false) > 0) { }
        });

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            return null;
        }

        // Output streams close when the process exits; do not wait forever for grandchildren holding the pipe.
        var drain = Task.WhenAll(stdoutTask, stderrTask);
        if (await Task.WhenAny(drain, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false) != drain)
        {
            Kill(process);
            return null;
        }
        return process.ExitCode == 0 ? await stdoutTask.ConfigureAwait(false) : null;
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.WaitForExit(2000); } catch { }
    }

    internal static VideoProbe? Parse(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            JsonElement? video = null, audio = null;
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in streams.EnumerateArray())
                {
                    var type = Str(s, "codec_type");
                    if (type == "video" && video == null && !IsAttachedPicture(s)) video = s;
                    else if (type == "audio" && audio == null) audio = s;
                }
            }
            if (video == null && audio == null) return null;

            double durationSeconds = 0;
            long bitrate = 0;
            if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Object)
            {
                durationSeconds = Num(format, "duration");
                bitrate = (long)Num(format, "bit_rate");
            }
            if (durationSeconds <= 0 && video is { } vd) durationSeconds = Num(vd, "duration");
            if (bitrate <= 0 && video is { } vb) bitrate = (long)Num(vb, "bit_rate");
            if (double.IsNaN(durationSeconds) || double.IsInfinity(durationSeconds) || durationSeconds < 0 || durationSeconds > 1e7) durationSeconds = 0;

            int width = 0, height = 0;
            double fps = 0;
            string vcodec = "", acodec = "";
            bool interlaced = false;
            if (video is { } v)
            {
                width = (int)Num(v, "width");
                height = (int)Num(v, "height");
                vcodec = Str(v, "codec_name");
                fps = Rate(Str(v, "avg_frame_rate"));
                if (fps <= 0) fps = Rate(Str(v, "r_frame_rate"));
                var order = Str(v, "field_order");
                interlaced = order is "tt" or "bb" or "tb" or "bt";
            }
            if (audio is { } a) acodec = Str(a, "codec_name");

            return new VideoProbe(TimeSpan.FromSeconds(durationSeconds), width, height, fps, vcodec, acodec, bitrate, interlaced);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var d) && d.ValueKind == JsonValueKind.Object && Num(d, "attached_pic") > 0;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static double Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var p)) return 0;
        if (p.ValueKind == JsonValueKind.Number) return p.TryGetDouble(out var d) ? d : 0;
        if (p.ValueKind == JsonValueKind.String && double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return s;
        return 0;
    }

    private static double Rate(string r)
    {
        if (string.IsNullOrEmpty(r)) return 0;
        var parts = r.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
            return n / d;
        return double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var single) ? single : 0;
    }
}
