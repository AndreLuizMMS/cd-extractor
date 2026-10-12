using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaRescue.Core;

/// <summary>
/// RF-11: which ffmpeg video encoder to use and how to translate "good quality H.264" into its options.
/// The bundled ffmpeg is an LGPL build, so libx264 is normally absent; the list is detected at run time.
/// </summary>
public static class Mp4Encoders
{
    /// <summary>Preference order: x264 (best), Windows Media Foundation, OpenH264, and MPEG-4 part 2 as the last resort.</summary>
    public static readonly IReadOnlyList<string> Preference = new[] { "libx264", "h264_mf", "libopenh264", "mpeg4" };

    private static readonly Regex EncoderLine = new(@"^\s*V\S{5}\s+(\S+)", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>Names of the video encoders listed by <c>ffmpeg -hide_banner -encoders</c>.</summary>
    public static IReadOnlySet<string> ParseVideoEncoders(string encodersOutput)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in EncoderLine.Matches(encodersOutput ?? ""))
        {
            var name = m.Groups[1].Value;
            if (name != "=") set.Add(name); // " V..... = Video" is the legend, not an encoder
        }
        return set;
    }

    /// <summary>The encoders from <see cref="Preference"/> that the build lists, in preference order.</summary>
    public static IReadOnlyList<string> Candidates(string encodersOutput)
    {
        var available = ParseVideoEncoders(encodersOutput);
        return Preference.Where(available.Contains).ToList();
    }

    /// <summary>Target bitrate (bits/s) for encoders without a constant-quality mode: 0.25 bit per pixel per frame, 600 kb/s..12 Mb/s.</summary>
    public static int TargetBitrate(int width, int height, double fps)
    {
        if (width <= 0 || height <= 0) { width = 720; height = 480; }
        if (fps <= 1 || fps > 120 || double.IsNaN(fps)) fps = 25;
        var bits = width * (double)height * fps * 0.25;
        var rounded = (int)(Math.Round(bits / 50_000) * 50_000);
        return Math.Clamp(rounded, 600_000, 12_000_000);
    }

    /// <summary>Video codec options for the encoder (without the pixel format, which is common to all).</summary>
    public static IReadOnlyList<string> VideoArgs(string encoder, int width, int height, double fps)
    {
        var kbps = TargetBitrate(width, height, fps) / 1000;
        switch (encoder.ToLowerInvariant())
        {
            case "libx264":
                return new[] { "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-profile:v", "high" };
            case "mpeg4":
                return new[] { "-c:v", "mpeg4", "-q:v", "4" };
            default: // h264_mf, libopenh264 and any other H.264 encoder: no CRF, so aim at a bitrate
                return new[] { "-c:v", encoder, "-b:v", kbps.ToString(CultureInfo.InvariantCulture) + "k" };
        }
    }

    /// <summary>pt-BR description for the summary.</summary>
    public static string Describe(string? encoder) => (encoder ?? "").ToLowerInvariant() switch
    {
        "libx264" => "H.264 (x264)",
        "h264_mf" => "H.264 (Windows Media Foundation)",
        "libopenh264" => "H.264 (OpenH264)",
        "mpeg4" => "MPEG-4 Parte 2 (compatibilidade básica)",
        "" => "—",
        _ => encoder!,
    };
}

/// <summary>Builds the ffmpeg argument lists (pure, so they can be unit tested and logged).</summary>
public static class Mp4CommandLine
{
    /// <summary>
    /// Arguments for one conversion. <paramref name="inputPaths"/> are absolute; several of them are joined with the concat protocol.
    /// <paramref name="source"/> is the ffprobe result of the input (may be null when it could not be probed).
    /// </summary>
    public static IReadOnlyList<string> Build(IReadOnlyList<string> inputPaths, string outputPath, string encoder, VideoProbe? source, bool damagedSource)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-nostats", "-progress", "pipe:1",
        };
        if (damagedSource)
        {
            args.AddRange(new[] { "-err_detect", "ignore_err" });
            args.AddRange(new[] { "-fflags", "+genpts+discardcorrupt" });
        }
        else
        {
            args.AddRange(new[] { "-fflags", "+genpts" });
        }
        args.AddRange(new[] { "-i", InputSpec(inputPaths) });
        args.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn" });

        var filters = new List<string>();
        if (source is { Interlaced: true }) filters.Add("yadif=mode=send_frame:parity=auto:deint=all");
        if (source != null && (source.Width % 2 != 0 || source.Height % 2 != 0)) filters.Add("scale=trunc(iw/2)*2:trunc(ih/2)*2");
        if (filters.Count > 0) args.AddRange(new[] { "-vf", string.Join(",", filters) });

        args.AddRange(Mp4Encoders.VideoArgs(encoder, source?.Width ?? 0, source?.Height ?? 0, source?.Fps ?? 0));
        args.AddRange(new[] { "-pix_fmt", "yuv420p" });
        args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k", "-ac", "2" });
        args.AddRange(new[] { "-movflags", "+faststart", "-f", "mp4", outputPath });
        return args;
    }

    /// <summary>Tiny synthetic encode used to prove that an encoder really works on this machine.</summary>
    public static IReadOnlyList<string> BuildEncoderTest(string encoder, string outputPath)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc=duration=0.6:size=320x240:rate=25",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=0.6",
            "-map", "0:v", "-map", "1:a",
        };
        args.AddRange(Mp4Encoders.VideoArgs(encoder, 320, 240, 25));
        args.AddRange(new[] { "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", "-f", "mp4", outputPath });
        return args;
    }

    public static string InputSpec(IReadOnlyList<string> paths) => paths.Count == 1 ? paths[0] : "concat:" + string.Join("|", paths);

    /// <summary>Command line as text (for logs and reports). Not used to start the process.</summary>
    public static string ToText(IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        foreach (var a in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(a.Length > 0 && a.IndexOfAny(new[] { ' ', '"', '|' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"");
        }
        return sb.ToString();
    }
}

/// <summary>Parsing of <c>-progress pipe:1</c> output and the output validation rule.</summary>
public static class Mp4Checks
{
    /// <summary>Reads the encoded position from a progress line ("out_time_us=1234567" or "out_time=00:00:01.234567").</summary>
    public static bool TryParseOutTime(string? line, out TimeSpan position)
    {
        position = TimeSpan.Zero;
        if (string.IsNullOrEmpty(line)) return false;
        var eq = line.IndexOf('=');
        if (eq <= 0) return false;
        var key = line[..eq].Trim();
        var value = line[(eq + 1)..].Trim();
        if (key is "out_time_us" or "out_time_ms") // ffmpeg's out_time_ms is in microseconds too
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) || us < 0) return false;
            position = TimeSpan.FromTicks(us * 10);
            return true;
        }
        if (key == "out_time" && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero)
        {
            position = t;
            return true;
        }
        return false;
    }

    /// <summary>RF-11: output duration within ±2 s or ±2% of the source.</summary>
    public static bool DurationAcceptable(TimeSpan source, TimeSpan output)
    {
        var tolerance = Math.Max(2.0, source.TotalSeconds * 0.02);
        return Math.Abs((source - output).TotalSeconds) <= tolerance;
    }
}
