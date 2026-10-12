using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaRescue.Core;

/// <summary>Folder/file naming rules (RF-04, E-20, E-21, E-31). Owned by the Core agent; signatures are fixed.</summary>
public static class NameSanitizer
{
    public const int MaxFolderNameLength = 80;
    /// <summary>Per path segment limit used for files/sub-folders (NTFS allows 255; we keep headroom for " (n)" and ".partial").</summary>
    public const int MaxSegmentLength = 200;

    private static readonly Regex ReservedDeviceName = new(
        @"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(COM|LPT)[0-9¹²³])$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> GenericLabels = new(StringComparer.Ordinal)
    {
        "NEW", "NEWVOLUME", "NOVOVOLUME", "CDROM", "CDR", "CDRW", "DVD", "DVDVIDEO", "DVDROM", "DVDR", "DVDRW", "DVDRAM",
        "MYDISC", "MYDVD", "MYCD", "DISC", "DISCO", "UNTITLED", "UNTITLEDDISC", "NONAME", "NONAMEDISC", "VIDEO", "VOLUME",
        "UDFVOLUME", "SEMTITULO", "SEMTÍTULO", "SEMNOME", "DATA", "DATADISC", "UNKNOWN",
    };

    /// <summary>
    /// Suggested folder name: the volume label, or "Disco yyyy-MM-dd" when the label is empty/generic.
    /// The date comes from the disc's recording date (UTC date, no timezone shifting) unless it is absurd
    /// (E-31: before 1990 or in the future) or missing, in which case <paramref name="today"/> is used.
    /// </summary>
    public static string SuggestFolderName(DiscInfo disc, DateTime today)
    {
        var label = (disc.VolumeLabel ?? "").Trim();
        if (label.Length == 0 || IsGenericLabel(label))
            return "Disco " + SaneDate(disc.RecordedAtUtc, today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return SanitizeFolderName(label);
    }

    /// <summary>True for "NEW", "CDROM", "DVD_VIDEO", "MY DISC", digits-only labels and the like.</summary>
    public static bool IsGenericLabel(string label)
    {
        var sb = new StringBuilder();
        foreach (var c in label)
            if (!char.IsWhiteSpace(c) && c != '_' && c != '-' && c != '.' && c != '+')
                sb.Append(char.ToUpperInvariant(c));
        var n = sb.ToString();
        if (n.Length == 0) return true;
        if (n.All(char.IsDigit)) return true;
        return GenericLabels.Contains(n);
    }

    /// <summary>Returns the date part of <paramref name="recordedUtc"/> if plausible, else today's date.</summary>
    public static DateTime SaneDate(DateTime? recordedUtc, DateTime today)
    {
        if (recordedUtc is null) return today.Date;
        var d = recordedUtc.Value.Date;
        if (d.Year < 1990 || d > today.Date.AddDays(1)) return today.Date;
        return d;
    }

    /// <summary>Makes a valid single folder name: no invalid chars, no reserved names, no trailing dot/space, at most 80 chars.</summary>
    public static string SanitizeFolderName(string name)
    {
        var s = SanitizeCore(name ?? "", MaxFolderNameLength, keepExtension: false);
        return s.Length == 0 ? "Disco" : s;
    }

    /// <summary>
    /// Sanitizes one path segment (file or sub-folder name). Deterministic; does not guarantee uniqueness by itself,
    /// use <see cref="PathMap"/> / <see cref="BuildPathMap"/> for collision-free results.
    /// </summary>
    public static string SanitizeSegment(string name, bool isFile = true)
    {
        var s = SanitizeCore(name ?? "", MaxSegmentLength, keepExtension: isFile);
        return s.Length == 0 ? "_" : s;
    }

    /// <summary>Returns name, "name (2)", "name (3)"... the first for which exists() is false.</summary>
    public static string MakeUnique(string name, Func<string, bool> exists)
    {
        if (!exists(name)) return name;
        for (int n = 2; n < 100_000; n++)
        {
            var candidate = WithSuffix(name, $" ({n})", MaxFolderNameLength, keepExtension: false);
            if (!exists(candidate)) return candidate;
        }
        return WithSuffix(name, " (" + Guid.NewGuid().ToString("N")[..8] + ")", MaxFolderNameLength, keepExtension: false);
    }

    /// <summary>
    /// Deterministic, collision-free map from original relative paths on the disc to sanitized relative paths
    /// (E-20). The result does not depend on the input order. Case-only differences get distinct names.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildPathMap(IEnumerable<string> relativePaths) => PathMap.Build(relativePaths);

    internal static string WithSuffix(string name, string suffix, int maxLength, bool keepExtension)
    {
        string ext = "";
        string stem = name;
        if (keepExtension)
        {
            var e = Path.GetExtension(name);
            if (e.Length > 0 && e.Length <= 16 && e.Length < name.Length) { ext = e; stem = name[..^e.Length]; }
        }
        int allowed = Math.Max(1, maxLength - suffix.Length - ext.Length);
        if (stem.Length > allowed) stem = TrimEnd(CutAt(stem, allowed));
        return stem + suffix + ext;
    }

    private static string SanitizeCore(string name, int maxLength, bool keepExtension)
    {
        var sb = new StringBuilder(name.Length);
        bool lastSpace = false;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]))
            {
                sb.Append(c).Append(name[i + 1]);
                i++;
                lastSpace = false;
                continue;
            }
            if (char.IsSurrogate(c)) { sb.Append('_'); lastSpace = false; continue; }
            if (c < 0x20 || c == 0x7F || "<>:\"/\\|?*".IndexOf(c) >= 0) { sb.Append('_'); lastSpace = false; continue; }
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
                continue;
            }
            sb.Append(c);
            lastSpace = false;
        }

        var s = TrimEnd(sb.ToString().TrimStart());
        if (s.Length == 0) return "";

        // Reserved device names are reserved even with an extension ("CON.txt").
        int dot = s.IndexOf('.');
        var stem = dot >= 0 ? s[..dot] : s;
        if (ReservedDeviceName.IsMatch(stem.TrimEnd()))
            s = stem.TrimEnd() + "_" + (dot >= 0 ? s[dot..] : "");

        if (s.Length > maxLength)
        {
            string ext = "";
            if (keepExtension)
            {
                var e = Path.GetExtension(s);
                if (e.Length > 0 && e.Length <= 16 && e.Length < s.Length) ext = e;
            }
            var stemPart = ext.Length > 0 ? s[..^ext.Length] : s;
            int allowed = Math.Max(1, maxLength - ext.Length);
            s = TrimEnd(CutAt(stemPart, allowed)) + ext;
            if (s.Length == 0) s = "_";
        }
        return TrimEnd(s);
    }

    private static string TrimEnd(string s) => s.TrimEnd(' ', '.');

    private static string CutAt(string s, int length)
    {
        if (s.Length <= length) return s;
        if (length > 0 && char.IsHighSurrogate(s[length - 1])) length--;
        return s[..length];
    }
}

/// <summary>
/// Maps original disc paths to sanitized, unique (case-insensitively) destination paths, segment by segment.
/// Can be seeded with a previous mapping (job resume) so that names never change between runs.
/// </summary>
public sealed class PathMap
{
    /// <summary>Names the engine itself writes in the disc folder; no disc file may take them.</summary>
    internal static readonly string[] ReservedRootNames =
    {
        ".mediarescue-job.json", ".mediarescue-job.json.bak",
        "mediarescue-manifest.json", "mediarescue-manifest.json.bak",
        "LEIA-ME - ERROS.txt", "LEIA-ME - ERROS.txt.bak",
    };

    private readonly Dictionary<string, string> _segments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _used = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _full = new(StringComparer.Ordinal);
    private readonly Func<string, bool>? _fileExists;

    /// <param name="fileExists">Optional: tells whether a (sanitized, relative) file path is already taken in the destination.</param>
    public PathMap(Func<string, bool>? fileExists = null)
    {
        _fileExists = fileExists;
        var root = Used("");
        foreach (var n in ReservedRootNames) root.Add(n.ToUpperInvariant());
    }

    public static IReadOnlyDictionary<string, string> Build(IEnumerable<string> relativePaths)
    {
        var map = new PathMap();
        foreach (var p in relativePaths.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
            map.Map(p);
        return map._full;
    }

    internal static string[] Split(string path) =>
        path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Registers an already decided mapping (from a previous run).</summary>
    public void Seed(string original, string written)
    {
        var o = Split(original);
        var w = Split(written);
        if (o.Length == 0 || o.Length != w.Length) return;
        string parent = "";
        for (int i = 0; i < o.Length; i++)
        {
            _segments[Key(parent, o[i])] = w[i];
            Used(parent).Add(w[i].ToUpperInvariant());
            parent = parent.Length == 0 ? w[i] : parent + "\\" + w[i];
        }
        _full[original] = string.Join('\\', w);
    }

    /// <summary>Returns the sanitized relative path for <paramref name="original"/> (memoized).</summary>
    public string Map(string original, bool isDirectory = false)
    {
        if (_full.TryGetValue(original, out var cached)) return cached;
        var segs = Split(original);
        if (segs.Length == 0) return _full[original] = "_";

        var parts = new List<string>(segs.Length);
        string parent = "";
        for (int i = 0; i < segs.Length; i++)
        {
            bool last = i == segs.Length - 1;
            bool file = last && !isDirectory;
            var key = Key(parent, segs[i]);
            if (!_segments.TryGetValue(key, out var written))
            {
                var baseName = NameSanitizer.SanitizeSegment(segs[i], file);
                if (file && baseName.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) baseName += "_";
                var used = Used(parent);
                var candidate = baseName;
                for (int n = 2; Taken(used, parent, candidate, file); n++)
                    candidate = NameSanitizer.WithSuffix(baseName, $" ({n})", NameSanitizer.MaxSegmentLength, file);
                used.Add(candidate.ToUpperInvariant());
                _segments[key] = written = candidate;
            }
            parts.Add(written);
            parent = parent.Length == 0 ? written : parent + "\\" + written;
        }
        return _full[original] = string.Join('\\', parts);
    }

    private bool Taken(HashSet<string> used, string parent, string candidate, bool isFile)
    {
        if (used.Contains(candidate.ToUpperInvariant())) return true;
        if (isFile && _fileExists != null)
        {
            var rel = parent.Length == 0 ? candidate : parent + "\\" + candidate;
            return _fileExists(rel);
        }
        return false;
    }

    private HashSet<string> Used(string parent)
    {
        var k = parent.ToUpperInvariant();
        if (!_used.TryGetValue(k, out var set)) _used[k] = set = new HashSet<string>(StringComparer.Ordinal);
        return set;
    }

    private static string Key(string parentWritten, string originalSegment) => parentWritten.ToUpperInvariant() + "\0" + originalSegment;
}
