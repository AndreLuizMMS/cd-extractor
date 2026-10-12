using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaRescue.Core;

public enum Mp4SourceKind { DvdTitle, File }

/// <summary>
/// One MP4 to produce. Paths are relative to the disc folder; the converter combines them with the folder.
/// </summary>
/// <param name="Name">Display name (pt-BR friendly).</param>
/// <param name="Inputs">Copies to read, in playback order (several for a DVD title set).</param>
/// <param name="Output">Relative output path, always below <c>MP4\</c>.</param>
/// <param name="SourceDamaged">A source is Partial, or a part of the title is missing/failed.</param>
/// <param name="InputBytes">Sum of the sizes of the inputs (used to weigh progress and to estimate space).</param>
public sealed record Mp4Job(string Name, Mp4SourceKind Kind, IReadOnlyList<string> Inputs, string Output, bool SourceDamaged, long InputBytes);

/// <summary>
/// RF-11, pure planning: which extracted files become which MP4. DVD-Video title sets (VTS_nn_1..n.VOB) are joined in one job
/// and the menu VOBs are left out; VCD/SVCD/DVD-VR tracks and ordinary video files are one job each; files that are already
/// MP4 are not touched. Partial files are included (flagged as damaged); Failed/Skipped/Pending ones are not.
/// </summary>
public static class Mp4ConversionPlanner
{
    public const string OutputFolder = "MP4";

    /// <summary>Folders that only exist to hold a disc structure: their name is not repeated below MP4\.</summary>
    private static readonly HashSet<string> StructureFolders = new(StringComparer.OrdinalIgnoreCase)
    { "VIDEO_TS", "DVD_RTAV", "MPEGAV", "MPEG2", "AUDIO_TS" };

    /// <summary>DVD-VR files that are not movies (stills, audio, menu).</summary>
    private static readonly HashSet<string> NonMovieNames = new(StringComparer.OrdinalIgnoreCase)
    { "VR_STILL.VRO", "VR_AUDIO.VRO", "VR_MENU.VRO" };

    private static readonly Regex MenuVob = new(@"^(VIDEO_TS\.VOB|VTS_\d{2}_0\.VOB)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VtsPart = new(@"^VTS_\d{2}_(\d+)\.VOB$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<Mp4Job> Plan(IReadOnlyList<FileResult> files)
    {
        var jobs = new List<Mp4Job>();
        var usedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var titles = new SortedDictionary<string, List<FileResult>>(StringComparer.OrdinalIgnoreCase);
        var plain = new List<FileResult>();

        foreach (var f in files)
        {
            if (string.IsNullOrWhiteSpace(f.RelativePath) || string.IsNullOrWhiteSpace(f.WrittenPath)) continue;
            if (DiscClassifier.KindOf(f.RelativePath) != EntryKind.Video) continue;

            var name = Path.GetFileName(Normalize(f.RelativePath));
            var group = DiscClassifier.TitleGroupOf(f.RelativePath);
            if (group != null)
            {
                var key = DirectoryOf(f.WrittenPath).ToUpperInvariant() + "|" + group;
                if (!titles.TryGetValue(key, out var list)) titles[key] = list = new List<FileResult>();
                list.Add(f);
                continue;
            }
            // An unusable menu VOB is not a hole in a title; it is simply not part of one.
            if (MenuVob.IsMatch(name) || NonMovieNames.Contains(name)) continue;

            var ext = Path.GetExtension(name);
            if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsUsable(f)) continue;
            plain.Add(f);
        }

        foreach (var (key, parts) in titles)
        {
            var group = key[(key.LastIndexOf('|') + 1)..];
            var ordered = parts.OrderBy(PartNumber).ThenBy(p => p.WrittenPath, StringComparer.OrdinalIgnoreCase).ToList();
            var usable = ordered.Where(IsUsable).ToList();
            if (usable.Count == 0) continue;

            bool damaged = usable.Count != ordered.Count || usable.Any(IsDamaged);
            var number = group.Length >= 6 && int.TryParse(group.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
            var title = $"Título {number:00} ({group})";
            var output = UniqueOutput(OutputFolder, title, usedOutputs);
            jobs.Add(new Mp4Job(title, Mp4SourceKind.DvdTitle, usable.Select(u => u.WrittenPath).ToList(), output, damaged, usable.Sum(u => u.BytesTotal)));
        }

        foreach (var f in plain)
        {
            var dir = OutputSubfolder(f.WrittenPath);
            var baseName = Path.GetFileNameWithoutExtension(Normalize(f.WrittenPath));
            var folder = dir.Length == 0 ? OutputFolder : OutputFolder + "\\" + dir;
            var output = UniqueOutput(folder, baseName, usedOutputs);
            jobs.Add(new Mp4Job(Normalize(f.WrittenPath), Mp4SourceKind.File, new[] { f.WrittenPath }, output, IsDamaged(f), f.BytesTotal));
        }

        return jobs;
    }

    /// <summary>Conservative estimate of the space the outputs may need.</summary>
    public static long EstimateOutputBytes(IReadOnlyList<Mp4Job> jobs) =>
        (long)(jobs.Sum(j => j.InputBytes) * 0.9) + 64L * 1024 * 1024;

    private static bool IsUsable(FileResult f) =>
        f.Status is FileStatus.Intact or FileStatus.Recovered or FileStatus.Partial && f.BytesTotal > 0;

    private static bool IsDamaged(FileResult f) => f.Status == FileStatus.Partial || f.BytesLost > 0;

    private static int PartNumber(FileResult f)
    {
        var m = VtsPart.Match(Path.GetFileName(Normalize(f.RelativePath)));
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : int.MaxValue;
    }

    private static string Normalize(string path) => path.Replace('/', '\\').TrimStart('\\');

    private static string DirectoryOf(string path)
    {
        var p = Normalize(path);
        var i = p.LastIndexOf('\\');
        return i < 0 ? "" : p[..i];
    }

    /// <summary>The source's folder below MP4\, minus structure folders (VIDEO_TS, MPEGAV...).</summary>
    private static string OutputSubfolder(string writtenPath)
    {
        var segments = DirectoryOf(writtenPath).Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => !StructureFolders.Contains(s));
        return string.Join("\\", segments);
    }

    private static string UniqueOutput(string folder, string baseName, HashSet<string> used)
    {
        var candidate = folder + "\\" + baseName + ".mp4";
        for (int i = 2; !used.Add(candidate); i++)
            candidate = folder + "\\" + baseName + $" ({i}).mp4";
        return candidate;
    }
}
