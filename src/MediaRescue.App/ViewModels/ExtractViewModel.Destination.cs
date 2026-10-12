using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using MediaRescue.App.Infrastructure;
using MediaRescue.Core;

namespace MediaRescue.App.ViewModels;

// Destination picking, validation (RF-03) and the Extrair gate.
public sealed partial class ExtractViewModel
{
    private CancellationTokenSource? _validationDebounce;
    private int _validationVersion;
    private bool _folderNameEdited;
    private bool _settingFolderName;
    private IReadOnlyList<ValidationIssue> _rawIssues = Array.Empty<ValidationIssue>();

    /// <summary>Delay before validating after typing. Tests set it to zero.</summary>
    public TimeSpan ValidationDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    public ObservableCollection<QuickDriveViewModel> QuickDrives { get; } = new();
    public ObservableCollection<IssueViewModel> Issues { get; } = new();

    [ObservableProperty] private string _destinationRoot = "";
    [ObservableProperty] private string _folderName = "";
    [ObservableProperty] private string _effectiveFolderName = "";
    [ObservableProperty] private bool _willResume;
    [ObservableProperty] private string _nameNote = "";
    [ObservableProperty] private string _destinationSummary = "Escolha a pasta onde os arquivos serão salvos.";
    [ObservableProperty] private string _destinationIcon = "";
    [ObservableProperty] private StatusTone _destinationTone = StatusTone.Neutral;
    [ObservableProperty] private bool _isValidating;

    [ObservableProperty] private bool _canExtract;
    [ObservableProperty] private string _extractDisabledReason = "";
    [ObservableProperty] private GateResult _gate = new(false, GateBlocker.NoDrive, "");

    public bool HasBlockedFiles => Content.HasBlockedFiles;

    /// <summary>True once the user typed a folder name (a suggestion no longer replaces it).</summary>
    public bool FolderNameEdited => _folderNameEdited;

    partial void OnDestinationRootChanged(string value) => ScheduleValidation();

    partial void OnFolderNameChanged(string value)
    {
        if (!_settingFolderName) _folderNameEdited = true;
        ScheduleValidation();
    }

    /// <summary>Sanitized folder name, or empty when the user left the box empty (the sanitizer would answer "Disco").</summary>
    private static string CleanName(string? raw) => string.IsNullOrWhiteSpace(raw) ? "" : NameSanitizer.SanitizeFolderName(raw);

    private void SetFolderNameSilently(string name)
    {
        _settingFolderName = true;
        try { FolderName = name; }
        finally { _settingFolderName = false; }
    }

    private void Browse()
    {
        var picked = _shell.PickFolder(string.IsNullOrWhiteSpace(DestinationRoot) ? null : DestinationRoot);
        if (!string.IsNullOrWhiteSpace(picked)) DestinationRoot = picked;
    }

    private void SelectRoot(string root) => DestinationRoot = root;

    // ----- quick list of destination drives -----

    public async Task RefreshQuickDrivesAsync()
    {
        var found = await Task.Run(() =>
        {
            var list = new List<(string Root, DestinationInfo Info)>();
            IReadOnlyList<string> roots;
            try { roots = _s.DestinationDrives.ListRoots(); }
            catch (Exception) { return list; }
            foreach (var root in roots)
            {
                try
                {
                    var info = _s.Fs.GetInfo(root);
                    if (info != null && !info.IsOptical) list.Add((root, info));
                }
                catch (Exception) { /* a drive that cannot be queried is simply not offered */ }
            }
            return list;
        });

        var items = found.Select(f => new QuickDriveViewModel(f.Root, DescribeQuick(f.Root, f.Info), SelectRoot, DescribeDestination(f.Info))).ToList();
        if (items.Count == QuickDrives.Count && items.Zip(QuickDrives).All(p => p.First.Root == p.Second.Root && p.First.Text == p.Second.Text))
            return;
        QuickDrives.Clear();
        foreach (var i in items) QuickDrives.Add(i);
    }

    private static string DescribeQuick(string root, DestinationInfo info)
    {
        var label = string.IsNullOrWhiteSpace(info.VolumeLabel) ? "" : " " + info.VolumeLabel;
        var kind = info.IsNetworkOrCloud ? " · nuvem/rede" : "";
        return $"{root.TrimEnd('\\')}{label} · {Fmt.Bytes(info.FreeBytes)}{kind}";
    }

    public static string DescribeDestination(DestinationInfo info)
    {
        var kind = info.IsNetworkOrCloud ? "Unidade de nuvem ou rede" : info.IsRemovable ? "Pendrive ou HD externo" : "Disco do computador";
        var label = string.IsNullOrWhiteSpace(info.VolumeLabel) ? "" : " " + info.VolumeLabel;
        return $"{kind}{label} ({info.FileSystem}) — {Fmt.Bytes(info.FreeBytes)} livres";
    }

    // ----- validation -----

    /// <summary>
    /// When false, typing/selection changes do not trigger background validation by themselves (callers use
    /// <see cref="ValidateNowAsync"/>). Tests turn it off so results are deterministic.
    /// </summary>
    public bool AutoValidate { get; set; } = true;

    private void ScheduleValidation()
    {
        if (!AutoValidate) { UpdateGate(); return; }
        _validationDebounce?.Cancel();
        if (IsExtracting) { UpdateGate(); return; }
        IsValidating = !string.IsNullOrWhiteSpace(DestinationRoot);
        UpdateGate();
        var cts = _validationDebounce = new CancellationTokenSource();
        if (ValidationDelay <= TimeSpan.Zero)
        {
            _ = RunSafe(ValidateNowAsync);
            return;
        }
        _ = RunSafe(async () =>
        {
            try { await Task.Delay(ValidationDelay, cts.Token); }
            catch (OperationCanceledException) { return; }
            await ValidateNowAsync();
        });
    }

    private sealed record ValidationData(DestinationInfo? Info, IReadOnlyList<ValidationIssue> Issues, string EffectiveName, bool WillResume);

    /// <summary>Validates the destination now (off the UI thread). Only the latest call applies its result.</summary>
    public async Task ValidateNowAsync()
    {
        var version = Interlocked.Increment(ref _validationVersion);
        if (IsExtracting) { IsValidating = false; UpdateGate(); return; }

        var root = (DestinationRoot ?? "").Trim();
        var folder = CleanName(FolderName);
        var entries = Content.GetSelectedEntries();
        var letter = SelectedDrive?.Letter ?? "";
        var disc = Disc;

        if (root.Length == 0)
        {
            ApplyValidation(new ValidationData(null, Array.Empty<ValidationIssue>(), folder, false), root);
            return;
        }

        IsValidating = true;
        UpdateGate();
        ValidationData data;
        try { data = await Task.Run(() => RunValidation(root, folder, entries, letter, disc)); }
        catch (Exception ex)
        {
            data = new ValidationData(null, new[] { new ValidationIssue(ValidationSeverity.Blocking, "Não foi possível verificar este destino. Escolha outra pasta.") }, folder, false);
            _log.Warn("Falha ao validar o destino: " + ex.Message);
        }
        if (version != Volatile.Read(ref _validationVersion)) return; // a newer validation is in flight
        ApplyValidation(data, root);
    }

    private ValidationData RunValidation(string root, string folder, IReadOnlyList<DiscEntry> entries, string letter, DiscInfo? disc)
    {
        try
        {
            var info = _s.Fs.GetInfo(root);
            var issues = _s.Validator.Validate(root, folder, entries, letter);
            var effective = folder;
            var resume = false;
            if (folder.Length > 0 && info != null)
            {
                resume = disc != null && _s.Engine.CanResume(disc, root, folder);
                if (!resume) effective = NameSanitizer.MakeUnique(folder, n => SafeDirExists(Path.Combine(root, n)));
            }
            return new ValidationData(info, issues, effective, resume);
        }
        catch (Exception)
        {
            return new ValidationData(null,
                new[] { new ValidationIssue(ValidationSeverity.Blocking, "Não foi possível verificar este destino. Escolha outra pasta.") },
                folder, false);
        }
    }

    private void ApplyValidation(ValidationData data, string root)
    {
        _rawIssues = data.Issues;
        Issues.Clear();
        foreach (var i in data.Issues.Where(i => i.Severity != ValidationSeverity.Ok))
            Issues.Add(new IssueViewModel(i));
        Content.ApplyIssues(data.Issues);
        OnPropertyChanged(nameof(HasBlockedFiles));

        var blocking = data.Issues.Any(i => i.Severity == ValidationSeverity.Blocking);
        var warning = data.Issues.Any(i => i.Severity == ValidationSeverity.Warning);
        if (root.Length == 0)
        {
            DestinationSummary = "Escolha a pasta onde os arquivos serão salvos.";
            DestinationIcon = "";
            DestinationTone = StatusTone.Neutral;
        }
        else if (data.Info == null)
        {
            DestinationSummary = "Não foi possível acessar esse local. Escolha outra pasta ou unidade.";
            DestinationIcon = "✖";
            DestinationTone = StatusTone.Danger;
        }
        else
        {
            DestinationSummary = DescribeDestination(data.Info);
            (DestinationIcon, DestinationTone) = blocking ? ("✖", StatusTone.Danger) : warning ? ("⚠", StatusTone.Warn) : ("✔", StatusTone.Ok);
        }

        EffectiveFolderName = data.EffectiveName;
        WillResume = data.WillResume;
        var typed = CleanName(FolderName);
        NameNote = data.WillResume
            ? "Há uma cópia interrompida deste disco nesta pasta. Ao extrair, você poderá continuar de onde parou."
            : typed.Length > 0 && !string.Equals(typed, data.EffectiveName, StringComparison.Ordinal)
                ? $"Já existe uma pasta com esse nome. Será usada: “{data.EffectiveName}”."
                : "";

        IsValidating = false;
        UpdateGate();
        Batch.Evaluate();
    }

    // ----- gate -----

    private void UpdateGate()
    {
        var gate = ExtractGate.Evaluate(new GateInput(
            IsExtracting,
            SelectedDrive != null,
            Screen,
            SelectedDrive?.Letter,
            Content.SelectedEntryCount,
            !string.IsNullOrWhiteSpace(DestinationRoot),
            !string.IsNullOrWhiteSpace(FolderName),
            IsValidating,
            _rawIssues));
        Gate = gate;
        CanExtract = gate.CanExtract && !Mp4.IsActive;
        ExtractDisabledReason = Mp4.IsActive ? "Aguarde a conversão para MP4 terminar ou cancele-a." : gate.CanExtract ? "" : gate.Reason;
        NotifyCommands();
    }
}




