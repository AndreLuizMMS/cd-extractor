using System.Text.RegularExpressions;

namespace MediaRescue.Core;

/// <summary>One DVD-Video title set: the VOBs of VTS_nn_1..n summed up (the _0 menu VOB is excluded).</summary>
public sealed record DvdTitle(string Group, IReadOnlyList<DiscEntry> Vobs, long TotalBytes);

/// <summary>Classifies disc contents (spec section 2.3). Owned by the Core agent; signatures are fixed.</summary>
public static class DiscClassifier
{
    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase)
    { ".avi", ".mpg", ".mpeg", ".wmv", ".mp4", ".mov", ".3gp", ".asf", ".mts", ".m2ts", ".dv", ".mkv", ".flv", ".vob", ".vro", ".m2v", ".m4v" };
    private static readonly HashSet<string> Image = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };
    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".wma", ".cda", ".ogg", ".flac", ".aac", ".m4a" };

    /// <summary>Housekeeping files that do not make a disc "mixed".</summary>
    private static readonly HashSet<string> Ignorable = new(StringComparer.OrdinalIgnoreCase)
    { "autorun.inf", "thumbs.db", "desktop.ini", ".ds_store", "folder.jpg", "albumartsmall.jpg" };

    /// <summary>Navigation/index files that belong to a video structure and do not make a disc "mixed".</summary>
    private static readonly HashSet<string> SupportExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".ifo", ".bup", ".bdm", ".mpl", ".clpi", ".cpi", ".vcd", ".svd" };

    private static readonly Regex VtsVob = new(@"^VTS_(\d{2})_(\d+)\.VOB$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VcdTrack = new(@"^AVSEQ\d+\.DAT$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SvcdTrack = new(@"^AVSEQ\d+\.MPG$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static EntryKind KindOf(string relativePath)
    {
        var ext = Path.GetExtension(relativePath);
        // .DAT is only video inside a VCD's MPEGAV folder; elsewhere it is an arbitrary data file.
        if (string.Equals(ext, ".dat", StringComparison.OrdinalIgnoreCase))
            return Normalize(relativePath).StartsWith(@"MPEGAV\", StringComparison.OrdinalIgnoreCase) ? EntryKind.Video : EntryKind.Other;
        if (Video.Contains(ext)) return EntryKind.Video;
        if (Image.Contains(ext)) return EntryKind.Image;
        if (Audio.Contains(ext)) return EntryKind.Audio;
        return EntryKind.Other;
    }

    /// <summary>"VTS_01" for VIDEO_TS\VTS_01_1.VOB (not for the _0 menu VOB); null otherwise.</summary>
    public static string? TitleGroupOf(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath)) return null;
        var name = Path.GetFileName(Normalize(relativePath));
        var m = VtsVob.Match(name);
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[2].Value, out var part) || part == 0) return null; // _0 is the menu
        return "VTS_" + m.Groups[1].Value;
    }

    /// <summary>Groups the title VOBs by title set, ordered by title set and by part. Menu VOBs are ignored.</summary>
    public static IReadOnlyList<DvdTitle> GroupDvdTitles(IReadOnlyList<DiscEntry> entries) =>
        entries.Where(e => !e.IsDirectory)
            .Select(e => (Entry: e, Group: e.TitleGroup ?? TitleGroupOf(e.RelativePath)))
            .Where(x => x.Group != null)
            .GroupBy(x => x.Group!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var vobs = g.Select(x => x.Entry).OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
                return new DvdTitle(g.Key.ToUpperInvariant(), vobs, vobs.Sum(v => v.Size));
            })
            .ToList();

    public static LogicalFormat Classify(IReadOnlyList<DiscEntry> entries)
    {
        var files = entries.Where(e => !e.IsDirectory && e.Error == null).ToList();
        if (files.Count == 0)
            return entries.Any(e => e.Error != null) ? LogicalFormat.Unknown : LogicalFormat.Empty;

        // CD-DA shows up as TrackNN.cda only. In mixed-mode CDs the data part decides.
        var data = files.Where(f => !IsCda(f.RelativePath)).ToList();
        if (data.Count == 0) return LogicalFormat.AudioCd;

        var paths = data.Select(f => Normalize(f.RelativePath)).ToList();

        if (paths.Any(p => Under(p, "VIDEO_TS") && (HasExt(p, ".ifo") || HasExt(p, ".vob") || HasExt(p, ".bup"))))
            return LogicalFormat.DvdVideo;
        if (paths.Any(p => Under(p, "DVD_RTAV") && (HasExt(p, ".vro") || HasExt(p, ".ifo"))))
            return LogicalFormat.DvdVr;
        if (paths.Any(p => (Under(p, @"BDMV\STREAM") || Under(p, @"AVCHD\BDMV\STREAM")) && (HasExt(p, ".mts") || HasExt(p, ".m2ts"))))
            return LogicalFormat.Avchd;
        if (paths.Any(p => Under(p, "MPEG2") && SvcdTrack.IsMatch(Path.GetFileName(p))))
            return LogicalFormat.Svcd;
        if (paths.Any(p => Under(p, "MPEGAV") && VcdTrack.IsMatch(Path.GetFileName(p))))
            return LogicalFormat.Vcd;

        var kinds = data
            .Where(f => !Ignorable.Contains(Path.GetFileName(f.RelativePath)) && !SupportExtensions.Contains(Path.GetExtension(f.RelativePath)))
            .Select(f => KindOf(f.RelativePath))
            .ToList();
        bool hasVideo = kinds.Contains(EntryKind.Video);
        if (!hasVideo) return LogicalFormat.Unknown;
        return kinds.Any(k => k != EntryKind.Video) ? LogicalFormat.Mixed : LogicalFormat.DataVideo;
    }

    private static string Normalize(string path) => path.Replace('/', '\\').TrimStart('\\');

    private static bool IsCda(string path) => string.Equals(Path.GetExtension(path), ".cda", StringComparison.OrdinalIgnoreCase);

    private static bool HasExt(string path, string ext) => string.Equals(Path.GetExtension(path), ext, StringComparison.OrdinalIgnoreCase);

    private static bool Under(string normalizedPath, string folder) =>
        normalizedPath.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
}
