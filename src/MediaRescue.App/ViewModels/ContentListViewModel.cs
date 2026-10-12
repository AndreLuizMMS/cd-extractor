using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaRescue.App.Infrastructure;
using MediaRescue.Core;

namespace MediaRescue.App.ViewModels;

public enum ContentRowKind { Directory, File, TitleGroup }

/// <summary>One row of the disc content list: a folder, a file or a synthetic DVD title group (VTS_nn).</summary>
public sealed partial class ContentItemViewModel : ObservableObject
{
    private readonly ContentListViewModel _owner;
    private bool? _isChecked = true;
    private bool _isExpanded;

    internal ContentItemViewModel(ContentListViewModel owner, ContentRowKind kind, string name, string relativePath, DiscEntry? entry)
    {
        _owner = owner;
        RowKind = kind;
        Name = name;
        RelativePath = relativePath;
        Entry = entry;
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public ContentRowKind RowKind { get; }
    public DiscEntry? Entry { get; }
    public string Name { get; }
    /// <summary>Path relative to the disc root (for groups: parent path + group id).</summary>
    public string RelativePath { get; }
    public int Depth { get; internal set; }
    public ContentItemViewModel? Parent { get; internal set; }
    public List<ContentItemViewModel> Children { get; } = new();
    public bool IsContainer => RowKind != ContentRowKind.File;
    public bool HasChildren => Children.Count > 0;
    public double Indent => Depth * 18;
    public long Size { get; internal set; }
    public int FileCount { get; internal set; }
    public VideoProbe? Probe { get; private set; }
    public RelayCommand ToggleExpandCommand { get; }

    public bool IsMenuVob => RowKind == ContentRowKind.File
        && Name.EndsWith("_0.VOB", StringComparison.OrdinalIgnoreCase);

    public bool IsProbeCandidate => RowKind == ContentRowKind.File && Entry is { Kind: EntryKind.Video } && !IsMenuVob;

    /// <summary>True, false, or null (some descendants selected).</summary>
    public bool? IsChecked
    {
        get => _isChecked;
        set => _owner.SetChecked(this, value ?? false);
    }

    internal void SetCheckedRaw(bool? value)
    {
        if (_isChecked == value) return;
        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            _owner.OnExpandedChanged();
        }
    }

    internal void SetExpandedRaw(bool value)
    {
        if (_isExpanded == value) return;
        _isExpanded = value;
        OnPropertyChanged(nameof(IsExpanded));
    }

    public string TypeText { get; private set; } = "";
    public string DateText { get; private set; } = "—";
    public string SizeText { get; private set; } = "—";

    [ObservableProperty] private string _durationText = "";
    [ObservableProperty] private string _resolutionText = "";
    [ObservableProperty] private string _toolTipText = "";
    /// <summary>Blocking destination issue for this file (e.g. too big for FAT32); null when fine.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasIssue))] private string? _issue;
    public bool HasIssue => !string.IsNullOrEmpty(Issue);

    internal void InitTexts()
    {
        switch (RowKind)
        {
            case ContentRowKind.Directory:
                TypeText = "Pasta";
                SizeText = FileCount > 0 ? Fmt.Bytes(Size) : "—";
                break;
            case ContentRowKind.TitleGroup:
                TypeText = "Título DVD";
                SizeText = Fmt.Bytes(Size);
                break;
            default:
                TypeText = Entry?.Kind switch
                {
                    EntryKind.Video => "Vídeo",
                    EntryKind.Image => "Foto",
                    EntryKind.Audio => "Áudio",
                    _ => "Arquivo",
                };
                SizeText = Fmt.Bytes(Size);
                break;
        }
        var date = Entry?.ModifiedUtc;
        DateText = date is { } d && d.Year is >= 1981 and <= 2100 ? Fmt.Date(d) : "—";
        ToolTipText = DateText == "—" ? RelativePath : RelativePath + " · " + DateText;
    }

    public void ApplyProbe(VideoProbe p)
    {
        Probe = p;
        DurationText = Fmt.VideoDuration(p.Duration);
        ResolutionText = p.Width > 0 && p.Height > 0 ? $"{p.Width}x{p.Height}" : "—";
        var parts = new List<string> { RelativePath };
        if (!string.IsNullOrWhiteSpace(p.VideoCodec)) parts.Add("vídeo " + p.VideoCodec);
        if (!string.IsNullOrWhiteSpace(p.AudioCodec)) parts.Add("áudio " + p.AudioCodec);
        if (p.Fps > 0) parts.Add(p.Fps.ToString("0.##", Fmt.Pt) + " quadros/s");
        if (p.Bitrate > 0) parts.Add(Fmt.Bytes(p.Bitrate / 8) + "/s");
        parts.Add(p.Interlaced ? "entrelaçado" : "progressivo");
        ToolTipText = string.Join(" · ", parts);
        Parent?.RecomputeGroup();
    }

    internal void RecomputeGroup()
    {
        if (RowKind != ContentRowKind.TitleGroup) return;
        var probed = Children.Where(c => c.Probe != null).ToList();
        if (probed.Count == 0) return;
        var total = TimeSpan.FromTicks(probed.Sum(c => c.Probe!.Duration.Ticks));
        DurationText = Fmt.VideoDuration(total) + (probed.Count < Children.Count ? "…" : "");
        var first = probed[0].Probe!;
        ResolutionText = first.Width > 0 && first.Height > 0 ? $"{first.Width}x{first.Height}" : "—";
    }
}

/// <summary>
/// Flat, virtualisable list of the disc content with tri-state checkboxes, collapsible folders and DVD title groups.
/// </summary>
public sealed partial class ContentListViewModel : ObservableObject
{
    private readonly List<ContentItemViewModel> _all = new();
    private readonly Dictionary<string, ContentItemViewModel> _files = new(StringComparer.OrdinalIgnoreCase);

    public ContentListViewModel()
    {
        SelectAllCommand = new RelayCommand(() => SelectAll(true));
        ClearAllCommand = new RelayCommand(() => SelectAll(false));
    }

    public BulkObservableCollection<ContentItemViewModel> VisibleItems { get; } = new();
    public IReadOnlyList<ContentItemViewModel> All => _all;
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearAllCommand { get; }

    /// <summary>Raised after the checked set changed.</summary>
    public event EventHandler? SelectionChanged;

    [ObservableProperty] private int _totalFiles;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private int _videoCount;
    [ObservableProperty] private int _selectedFiles;
    [ObservableProperty] private long _selectedBytes;
    [ObservableProperty] private int _selectedEntryCount;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _selectionText = "";
    [ObservableProperty] private string _probeStatus = "";

    public void Clear()
    {
        _all.Clear();
        _files.Clear();
        VisibleItems.ReplaceAll(Array.Empty<ContentItemViewModel>());
        TotalFiles = 0; TotalBytes = 0; VideoCount = 0;
        SummaryText = ""; ProbeStatus = "";
        Recount(raise: false);
    }

    public void Load(IReadOnlyList<DiscEntry> entries)
    {
        _all.Clear();
        _files.Clear();
        var roots = new List<ContentItemViewModel>();
        var dirs = new Dictionary<string, ContentItemViewModel>(StringComparer.OrdinalIgnoreCase);
        var groups = new Dictionary<string, ContentItemViewModel>(StringComparer.OrdinalIgnoreCase);
        var dirEntries = new Dictionary<string, DiscEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries.Where(e => e.IsDirectory))
            dirEntries.TryAdd(e.RelativePath.Trim('\\'), e);

        List<ContentItemViewModel> ChildrenOf(ContentItemViewModel? p) => p == null ? roots : p.Children;

        ContentItemViewModel GetDir(string path)
        {
            if (dirs.TryGetValue(path, out var existing)) return existing;
            var idx = path.LastIndexOf('\\');
            var parent = idx < 0 ? null : GetDir(path[..idx]);
            var name = idx < 0 ? path : path[(idx + 1)..];
            dirEntries.TryGetValue(path, out var entry);
            var d = new ContentItemViewModel(this, ContentRowKind.Directory, name, path, entry) { Parent = parent };
            dirs[path] = d;
            ChildrenOf(parent).Add(d);
            return d;
        }

        foreach (var e in entries)
        {
            var path = e.RelativePath.Trim('\\');
            if (path.Length == 0) continue;
            if (e.IsDirectory) { GetDir(path); continue; }

            var idx = path.LastIndexOf('\\');
            var parent = idx < 0 ? null : GetDir(path[..idx]);
            var container = parent;
            if (!string.IsNullOrEmpty(e.TitleGroup))
            {
                var key = (parent?.RelativePath ?? "") + "\\" + e.TitleGroup;
                if (!groups.TryGetValue(key, out var g))
                {
                    g = new ContentItemViewModel(this, ContentRowKind.TitleGroup, GroupName(e.TitleGroup!), key, null) { Parent = parent };
                    groups[key] = g;
                    ChildrenOf(parent).Add(g);
                }
                container = g;
            }
            var name = idx < 0 ? path : path[(idx + 1)..];
            var file = new ContentItemViewModel(this, ContentRowKind.File, name, path, e) { Parent = container };
            ChildrenOf(container).Add(file);
            _files[path] = file;
        }

        Flatten(roots, 0);
        foreach (var r in roots) Aggregate(r);
        foreach (var i in _all) i.InitTexts();

        bool small = _all.Count <= 400;
        foreach (var i in _all.Where(i => i.IsContainer))
            i.SetExpandedRaw(small || i.Children.Count <= 50 && i.Depth == 0);

        TotalFiles = _all.Where(i => i.RowKind == ContentRowKind.File).Count();
        TotalBytes = _all.Where(i => i.RowKind == ContentRowKind.File).Sum(i => i.Size);
        VideoCount = _all.Count(i => i.RowKind == ContentRowKind.TitleGroup
            || (i.RowKind == ContentRowKind.File && i.Parent?.RowKind != ContentRowKind.TitleGroup
                && i.Entry is { Kind: EntryKind.Video } && !i.IsMenuVob));
        SummaryText = $"{Fmt.Plural(TotalFiles, "arquivo", "arquivos")} · {Fmt.Bytes(TotalBytes)} · {Fmt.Plural(VideoCount, "vídeo", "vídeos")}";
        ProbeStatus = "";

        RebuildVisible();
        Recount(raise: false);
    }

    private static string GroupName(string group)
    {
        // "VTS_01" -> "Título 1 (VTS_01)"
        var digits = new string(group.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? $"Título {n} ({group})" : group;
    }

    private static int Rank(ContentItemViewModel i) => i.RowKind switch { ContentRowKind.Directory => 0, ContentRowKind.TitleGroup => 1, _ => 2 };

    private void Flatten(List<ContentItemViewModel> list, int depth)
    {
        list.Sort((a, b) =>
        {
            var r = Rank(a).CompareTo(Rank(b));
            return r != 0 ? r : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });
        foreach (var item in list)
        {
            item.Depth = depth;
            _all.Add(item);
            Flatten(item.Children, depth + 1);
        }
    }

    private static void Aggregate(ContentItemViewModel item)
    {
        if (item.RowKind == ContentRowKind.File)
        {
            item.Size = item.Entry?.Size ?? 0;
            item.FileCount = 1;
            return;
        }
        long size = 0; int count = 0;
        foreach (var c in item.Children) { Aggregate(c); size += c.Size; count += c.FileCount; }
        item.Size = size;
        item.FileCount = count;
    }

    // ----- visibility (collapse/expand) -----

    internal void OnExpandedChanged()
    {
        RebuildVisible();
    }

    private void RebuildVisible()
    {
        var visible = new List<ContentItemViewModel>(_all.Count);
        int collapsedDepth = int.MaxValue;
        foreach (var item in _all)
        {
            if (item.Depth > collapsedDepth) continue;
            collapsedDepth = int.MaxValue;
            visible.Add(item);
            if (item.IsContainer && !item.IsExpanded) collapsedDepth = item.Depth;
        }
        VisibleItems.ReplaceAll(visible);
    }

    // ----- selection -----

    internal void SetChecked(ContentItemViewModel item, bool value)
    {
        SetDown(item, value);
        for (var p = item.Parent; p != null; p = p.Parent) p.SetCheckedRaw(StateOf(p));
        Recount(raise: true);
    }

    private static void SetDown(ContentItemViewModel item, bool value)
    {
        var stack = new Stack<ContentItemViewModel>();
        stack.Push(item);
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            i.SetCheckedRaw(value);
            foreach (var c in i.Children) stack.Push(c);
        }
    }

    private static bool? StateOf(ContentItemViewModel parent)
    {
        bool any = false, all = true;
        foreach (var c in parent.Children)
        {
            var s = c.IsChecked;
            if (s != false) any = true;
            if (s != true) all = false;
        }
        return all ? true : (any ? null : false);
    }

    public void SelectAll(bool value)
    {
        foreach (var i in _all.Where(i => i.Parent == null)) SetDown(i, value);
        Recount(raise: true);
    }

    private void Recount(bool raise)
    {
        int files = 0, entries = 0;
        long bytes = 0;
        foreach (var i in _all)
        {
            if (i.IsChecked != true || i.Entry == null) continue;
            entries++;
            if (i.RowKind == ContentRowKind.File) { files++; bytes += i.Size; }
        }
        SelectedFiles = files;
        SelectedBytes = bytes;
        SelectedEntryCount = entries;
        SelectionText = TotalFiles == 0
            ? ""
            : $"Selecionados: {files} de {TotalFiles} · {Fmt.Bytes(bytes)}";
        if (raise) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Entries to hand to the engine: every checked file and every fully checked real folder.</summary>
    public IReadOnlyList<DiscEntry> GetSelectedEntries()
    {
        var list = new List<DiscEntry>();
        foreach (var i in _all)
            if (i.Entry != null && i.IsChecked == true) list.Add(i.Entry);
        return list;
    }

    public IReadOnlyList<ContentItemViewModel> ProbeCandidates() => _all.Where(i => i.IsProbeCandidate).ToList();

    /// <summary>Marks files that the destination cannot hold (blocking issues with a RelativePath).</summary>
    public void ApplyIssues(IReadOnlyList<ValidationIssue> issues)
    {
        foreach (var f in _files.Values) f.Issue = null;
        foreach (var issue in issues)
        {
            if (issue.Severity != ValidationSeverity.Blocking || string.IsNullOrEmpty(issue.RelativePath)) continue;
            if (_files.TryGetValue(issue.RelativePath.Trim('\\'), out var item)) item.Issue = issue.Message;
        }
    }

    /// <summary>Unchecks files flagged by a blocking issue.</summary>
    public void UncheckBlocked()
    {
        foreach (var f in _files.Values.Where(f => f.Issue != null && f.IsChecked == true).ToList())
            SetChecked(f, false);
    }

    public bool HasBlockedFiles => _files.Values.Any(f => f.Issue != null);

    public ContentItemViewModel? FindFile(string relativePath) =>
        _files.TryGetValue(relativePath.Trim('\\'), out var f) ? f : null;
}


