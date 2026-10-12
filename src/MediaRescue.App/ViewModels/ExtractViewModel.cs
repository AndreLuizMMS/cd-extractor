using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaRescue.App.Infrastructure;
using MediaRescue.App.Services;
using MediaRescue.Core;

namespace MediaRescue.App.ViewModels;

public sealed record DuplicateInfo(CatalogEntry Entry, bool FolderExists);

/// <summary>
/// The main "Extrair" screen: reader selection and state (RF-01), disc details (RF-02), content selection,
/// destination (RF-03/04), extraction and progress (RF-05/06). Split in partial files: drive/disc (this file),
/// destination, extraction.
/// </summary>
public sealed partial class ExtractViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _s;
    private readonly SettingsService _settings;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly LogService _log;
    private readonly ProbeScheduler _probe;
    private readonly ReconnectTracker _reconnects = new();
    private readonly HashSet<string> _everSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _refreshLock = new();

    private HashSet<string> _present = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource? _monitorCts;
    private Task? _refreshLoop;
    private bool _refreshPending;
    private bool _forcePending;
    private bool _applyingSnapshot;
    private bool _prevReady;
    private bool _analysisFailed;
    private bool _disposed;
    private int _pollTicks;

    public ExtractViewModel(AppServices services, SettingsService settings, IUiDispatcher ui, IDialogService dialogs,
        IShellService shell, LogService log)
    {
        _s = services;
        _settings = settings;
        _ui = ui;
        _dialogs = dialogs;
        _shell = shell;
        _log = log;

        Content = new ContentListViewModel();
        Progress = new ProgressViewModel();
        _probe = new ProbeScheduler(services.Probe, ui);
        Batch = new BatchViewModel(this, settings, dialogs, ui, log);

        ExtractCommand = new AsyncRelayCommand(ExtractFromUiAsync, () => CanExtract);
        EjectCommand = new AsyncRelayCommand(() => EjectAsync(), () => SelectedDrive != null && !IsExtracting);
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(true), () => !IsExtracting);
        BrowseCommand = new RelayCommand(Browse, () => !IsExtracting);
        PauseCommand = new RelayCommand(TogglePause, () => IsExtracting);
        SkipCommand = new RelayCommand(SkipFile, () => IsExtracting);
        InsistCommand = new RelayCommand(Insist, () => IsExtracting);
        CancelCommand = new AsyncRelayCommand(CancelAsync, () => IsExtracting);
        UncheckBlockedCommand = new RelayCommand(() => Content.UncheckBlocked());
        OpenResultFolderCommand = new RelayCommand(() => { if (Result != null) _shell.OpenFolder(Result.FolderPath); });

        Drives.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasMultipleDrives));
        Content.SelectionChanged += (_, _) => OnSelectionChanged();
        _probe.ProgressChanged += OnProbeProgress;
        _s.Drives.Changed += OnDriveServiceChanged;

        DiscRows = PlaceholderRows;
        InitializeMp4();
        UpdatePresentation();
    }

    // ----- child view models -----
    public ContentListViewModel Content { get; }
    public ProgressViewModel Progress { get; }
    public BatchViewModel Batch { get; }
    public LogService Log => _log;

    // ----- commands -----
    public AsyncRelayCommand ExtractCommand { get; }
    public AsyncRelayCommand EjectCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand BrowseCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand SkipCommand { get; }
    public RelayCommand InsistCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public RelayCommand UncheckBlockedCommand { get; }
    public RelayCommand OpenResultFolderCommand { get; }

    // ----- readers -----
    public ObservableCollection<DriveItemViewModel> Drives { get; } = new();
    public bool HasMultipleDrives => Drives.Count > 1;
    /// <summary>How often the safety-net poll runs (the drive service also raises Changed).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);

    [ObservableProperty] private DriveItemViewModel? _selectedDrive;
    [ObservableProperty] private IReadOnlyList<DetailRow> _driveRows = Array.Empty<DetailRow>();
    [ObservableProperty] private string? _powerTip;

    // ----- screen state (RF-01) -----
    [ObservableProperty] private ScreenState _screen = ScreenState.NoDrive;
    [ObservableProperty] private string _statusTitle = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private StatusTone _statusTone;
    [ObservableProperty] private bool _showEmptyState = true;
    [ObservableProperty] private bool _isAnalyzing;

    // ----- disc -----
    [ObservableProperty] private DiscInfo? _disc;
    [ObservableProperty] private IReadOnlyList<DetailRow> _discRows = Array.Empty<DetailRow>();
    [ObservableProperty] private string _discNotice = "";
    [ObservableProperty] private DuplicateInfo? _duplicate;
    [ObservableProperty] private string _probeNotice = "";

    /// <summary>The pending duplicate/resume prompt, if one is open (lets tests await it).</summary>
    public Task? PendingPrompt { get; private set; }

    /// <summary>Raised after the catalog changed (a disc was extracted).</summary>
    public event EventHandler? CatalogChanged;

    private static readonly IReadOnlyList<DetailRow> PlaceholderRows = new[]
    {
        new DetailRow("Tipo", "—"), new DetailRow("Formato", "—"), new DetailRow("Rótulo", "—"), new DetailRow("Gravado em", "—"),
        new DetailRow("Usado", "—"), new DetailRow("Sessões", "—"), new DetailRow("Estado", "—"),
    };

    // =============================================================================================
    // Lifecycle / refresh
    // =============================================================================================

    public async Task InitializeAsync()
    {
        var last = _settings.Current.LastDestination;
        if (!string.IsNullOrWhiteSpace(last))
        {
            var exists = await Task.Run(() => SafeDirExists(last));
            if (exists) DestinationRoot = last;
        }
        await RefreshQuickDrivesAsync();
        await RefreshAsync(true);
        StartMonitoring();
    }

    public void StartMonitoring()
    {
        if (_monitorCts != null || _disposed) return;
        var cts = _monitorCts = new CancellationTokenSource();
        _ = MonitorAsync(cts.Token);
    }

    private async Task MonitorAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _s.Clock.Delay(PollInterval, ct);
                await RefreshAsync();
                if (++_pollTicks % 5 == 0 && !IsExtracting) await RefreshQuickDrivesAsync();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.Error("Falha ao verificar o leitor: " + ex.Message, ex); }
        }
    }

    private void OnDriveServiceChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        // May be raised on any thread (WM_DEVICECHANGE, polling thread...).
        _ui.Post(() => _ = RunSafe(async () =>
        {
            await RefreshAsync();
            if (!IsExtracting) await RefreshQuickDrivesAsync();
        }));
    }

    /// <summary>Re-reads readers and media state. Concurrent calls are coalesced; the returned task covers the pass.</summary>
    public Task RefreshAsync(bool forceAnalyze = false)
    {
        lock (_refreshLock)
        {
            _refreshPending = true;
            _forcePending |= forceAnalyze;
            _refreshLoop ??= RefreshLoopAsync();
            return _refreshLoop;
        }
    }

    private async Task RefreshLoopAsync()
    {
        await Task.Yield(); // lets RefreshAsync publish _refreshLoop first
        while (true)
        {
            bool force;
            lock (_refreshLock)
            {
                if (!_refreshPending) { _refreshLoop = null; return; }
                _refreshPending = false;
                force = _forcePending;
                _forcePending = false;
            }
            try { await RefreshOnceAsync(force); }
            catch (Exception ex) { _log.Error("Erro ao atualizar o leitor: " + ex.Message, ex); }
        }
    }

    private sealed record DriveSnap(OpticalDrive Drive, DriveState State);

    private List<DriveSnap> TakeSnapshot()
    {
        var list = new List<DriveSnap>();
        IReadOnlyList<OpticalDrive> drives;
        try { drives = _s.Drives.GetDrives(); }
        catch (Exception) { return list; }
        foreach (var d in drives)
        {
            DriveState state;
            try { state = _s.Drives.GetState(d.Letter); }
            catch (Exception) { state = DriveState.NoMedia; }
            list.Add(new DriveSnap(d, state));
        }
        return list;
    }

    private async Task RefreshOnceAsync(bool force)
    {
        var snapshot = await Task.Run(TakeSnapshot);
        ApplySnapshot(snapshot);

        if (IsExtracting) { UpdatePresentation(); return; }

        var drive = SelectedDrive;
        if (drive == null || drive.State != DriveState.Ready)
        {
            _prevReady = false;
            ClearDisc();
            UpdatePresentation();
            Batch.Evaluate();
            return;
        }

        bool needAnalysis = force || !_prevReady || (Disc == null && !_analysisFailed && !IsAnalyzing);
        _prevReady = true;
        if (!needAnalysis) { UpdatePresentation(); return; }
        await AnalyzeAsync(drive);
    }

    private void ApplySnapshot(List<DriveSnap> snapshot)
    {
        _applyingSnapshot = true;
        try
        {
            var now = _s.Clock.UtcNow;
            var previousLetter = SelectedDrive?.Letter;
            var letters = new HashSet<string>(snapshot.Select(x => x.Drive.Letter), StringComparer.OrdinalIgnoreCase);

            foreach (var l in letters)
                if (!_present.Contains(l) && _everSeen.Contains(l)) _reconnects.Record(now); // E-05
            foreach (var l in letters) _everSeen.Add(l);
            _present = letters;

            for (int i = Drives.Count - 1; i >= 0; i--)
                if (!letters.Contains(Drives[i].Letter)) Drives.RemoveAt(i);
            foreach (var snap in snapshot)
            {
                var existing = Drives.FirstOrDefault(d => string.Equals(d.Letter, snap.Drive.Letter, StringComparison.OrdinalIgnoreCase));
                if (existing == null) Drives.Add(new DriveItemViewModel(snap.Drive, snap.State));
                else existing.State = snap.State;
            }

            var selected = Drives.FirstOrDefault(d => string.Equals(d.Letter, previousLetter, StringComparison.OrdinalIgnoreCase))
                           ?? Drives.FirstOrDefault();
            if (!ReferenceEquals(selected, SelectedDrive))
            {
                SelectedDrive = selected;
                if (!IsExtracting && !string.Equals(previousLetter, selected?.Letter, StringComparison.OrdinalIgnoreCase))
                {
                    _prevReady = false;
                    ClearDisc();
                }
            }

            PowerTip = _reconnects.IsTripped(now)
                ? "O leitor está desligando e ligando sozinho. Isso costuma ser falta de energia na USB: conecte-o direto no computador " +
                  "(sem hub) e, se ele tiver um cabo com duas pontas USB, ligue as duas."
                : null;
            UpdateDriveRows();
        }
        finally { _applyingSnapshot = false; }
    }

    partial void OnSelectedDriveChanged(DriveItemViewModel? value)
    {
        NotifyCommands();
        if (_applyingSnapshot || IsExtracting) return;
        // The user picked another reader.
        _prevReady = false;
        ClearDisc();
        UpdateDriveRows();
        UpdatePresentation();
        _ = RunSafe(() => RefreshAsync(true));
    }

    private void UpdateDriveRows()
    {
        var d = SelectedDrive?.Drive;
        var rows = new List<DetailRow>();
        if (d != null)
        {
            rows.Add(new DetailRow("Firmware", Dash(d.Firmware)));
            rows.Add(new DetailRow("Conexão", Dash(d.BusType)));
            if (d.IsVirtual) rows.Add(new DetailRow("Tipo", "Unidade virtual (imagem de disco montada)"));
        }
        if (!rows.SequenceEqual(DriveRows)) DriveRows = rows;
    }

    private static string Dash(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s.Trim();

    // =============================================================================================
    // Disc analysis
    // =============================================================================================

    private sealed record AnalysisData(DiscInfo? Disc, CatalogEntry? Entry, bool FolderExists);

    private void ClearDisc()
    {
        try { _analysisCts?.Cancel(); } catch (ObjectDisposedException) { }
        _analysisCts = null;
        _probe.Cancel();
        IsAnalyzing = false;
        _analysisFailed = false;
        Disc = null;
        Duplicate = null;
        DuplicateAcknowledged = false;
        ProbeNotice = "";
        DiscNotice = "";
        DiscRows = PlaceholderRows;
        if (Content.All.Count > 0) Content.Clear();
        _folderNameEdited = false;
        SetFolderNameSilently("");
    }

    private async Task AnalyzeAsync(DriveItemViewModel drive)
    {
        try { _analysisCts?.Cancel(); } catch (ObjectDisposedException) { }
        var cts = _analysisCts = new CancellationTokenSource();
        var ct = cts.Token;
        var letter = drive.Letter;

        IsAnalyzing = true;
        _analysisFailed = false;
        Disc = null;
        UpdatePresentation();
        _log.Info($"Lendo o disco em {letter}…");

        AnalysisData? data = null;
        Exception? error = null;
        try
        {
            data = await Task.Run(async () =>
            {
                var d = await _s.Drives.AnalyzeAsync(letter, ct).ConfigureAwait(false);
                if (d == null) return new AnalysisData(null, null, false);
                var fingerprint = d.Fingerprint; // hashing happens off the UI thread
                var entry = _s.Catalog.FindByFingerprint(fingerprint);
                var exists = entry != null && SafeDirExists(entry.DestinationPath);
                return new AnalysisData(d, entry, exists);
            }, ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { error = ex; }

        if (ct.IsCancellationRequested || !ReferenceEquals(_analysisCts, cts)) return;

        IsAnalyzing = false;
        if (error != null || data?.Disc == null)
        {
            _analysisFailed = true;
            _log.Warn($"Não foi possível ler o disco em {letter}." + (error != null ? " Detalhes: " + error.Message : ""));
            UpdatePresentation();
            Batch.Evaluate();
            return;
        }

        var disc = data.Disc;
        Disc = disc;
        Result = null; // a new disc replaces the previous result card
        DiscRows = BuildDiscRows(disc);
        DiscNotice = BuildDiscNotice(disc);
        var screen = ComputeScreen();
        if (screen == ScreenState.Ready)
        {
            Content.Load(disc.Entries);
            _folderNameEdited = false;
            var suggestion = NameSanitizer.SanitizeFolderName(NameSanitizer.SuggestFolderName(disc, _s.Clock.UtcNow.ToLocalTime().Date));
            SetFolderNameSilently(suggestion);
            Duplicate = data.Entry != null ? new DuplicateInfo(data.Entry, data.FolderExists) : null;
            DuplicateAcknowledged = false;
            StartProbing(letter);
        }
        else
        {
            Content.Clear();
            Duplicate = null;
        }
        UpdatePresentation();
        _log.Info($"Disco detectado em {letter} — {Dash(disc.VolumeLabel)} ({FormatName(disc.LogicalFormat)}, {Fmt.Bytes(disc.UsedBytes)}).");

        if (screen == ScreenState.Ready) await ValidateNowAsync();
        else Batch.Evaluate();

        if (screen == ScreenState.Ready && Duplicate != null && !Batch.IsEnabled)
            PendingPrompt = RunSafe(async () =>
            {
                await Task.Yield(); // never show a modal dialog from inside the refresh loop
                await ShowDuplicatePromptAsync();
            });
    }

    private void StartProbing(string letter)
    {
        if (!_probe.IsAvailable)
        {
            ProbeNotice = "Duração e resolução dos vídeos indisponíveis (ffprobe não encontrado). A cópia funciona normalmente.";
            return;
        }
        ProbeNotice = "";
        _probe.SetPaused(IsExtracting);
        _probe.Start(letter, Content.ProbeCandidates());
    }

    private void OnProbeProgress(int done, int total)
    {
        if (IsExtracting) return; // the status line says probing resumes after the copy
        Content.ProbeStatus = total == 0 || done >= total ? "" : $"Lendo informações dos vídeos… {done} de {total}";
    }

    private ScreenState ComputeScreen()
    {
        var drive = SelectedDrive;
        if (drive == null) return ScreenState.NoDrive;
        switch (drive.State)
        {
            case DriveState.NoMedia: return ScreenState.NoMedia;
            case DriveState.TrayOpen: return ScreenState.TrayOpen;
            case DriveState.Loading: return ScreenState.Loading;
            case DriveState.Blank: return ScreenState.Blank;
            case DriveState.Unfinalized: return ScreenState.Unfinalized;
            case DriveState.Unreadable: return ScreenState.Unreadable;
            case DriveState.Protected: return ScreenState.Protected;
            case DriveState.AudioCd: return ScreenState.AudioCd;
        }
        // DriveState.Ready: the analysis decides.
        if (_analysisFailed) return ScreenState.Unreadable;
        var disc = Disc;
        if (IsAnalyzing || disc == null) return ScreenState.Loading;
        if (disc.IsCopyProtected) return ScreenState.Protected;
        if (disc.LogicalFormat == LogicalFormat.AudioCd) return ScreenState.AudioCd;
        if (disc.LogicalFormat == LogicalFormat.Empty || disc.Entries.Count == 0) return ScreenState.Blank;
        return ScreenState.Ready;
    }

    private void UpdatePresentation()
    {
        var screen = ComputeScreen();
        Screen = screen;
        var info = StatusPresenter.For(screen, SelectedDrive?.Letter);
        StatusTitle = info.Title;
        StatusMessage = info.Message;
        StatusTone = info.Tone;
        ShowEmptyState = screen != ScreenState.Ready && !IsExtracting;
        UpdateGate();
    }

    private void OnSelectionChanged()
    {
        ScheduleValidation();
        UpdateGate();
    }

    // ----- detail rows -----

    public static string FormatName(LogicalFormat f) => f switch
    {
        LogicalFormat.Empty => "Vazio",
        LogicalFormat.DataVideo => "Disco de dados com vídeos",
        LogicalFormat.Mixed => "Misto (vídeos, fotos e documentos)",
        LogicalFormat.Vcd => "VCD",
        LogicalFormat.Svcd => "SVCD",
        LogicalFormat.DvdVideo => "DVD-Video",
        LogicalFormat.DvdVr => "DVD-VR (gravador de mesa)",
        LogicalFormat.Avchd => "AVCHD (filmadora)",
        LogicalFormat.AudioCd => "CD de áudio",
        _ => "Não identificado",
    };

    private static IReadOnlyList<DetailRow> BuildDiscRows(DiscInfo d)
    {
        var used = d.CapacityBytes is { } cap && cap > 0 ? $"{Fmt.Bytes(d.UsedBytes)} / {Fmt.Bytes(cap)}" : Fmt.Bytes(d.UsedBytes);
        var recorded = d.RecordedAtUtc is { } r && r.Year is >= 1981 and <= 2100 ? Fmt.Date(r) : "—";
        return new[]
        {
            new DetailRow("Tipo", Dash(d.PhysicalType)),
            new DetailRow("Formato", FormatName(d.LogicalFormat)),
            new DetailRow("Rótulo", Dash(d.VolumeLabel)),
            new DetailRow("Gravado em", recorded),
            new DetailRow("Usado", used),
            new DetailRow("Sessões", d.Sessions?.ToString() ?? "—"),
            new DetailRow("Faixas", d.Tracks?.ToString() ?? "—"),
            new DetailRow("Estado", Dash(d.MediaState)),
            new DetailRow("Sistema de arquivos", Dash(d.FileSystem)),
            new DetailRow("Nº de série", Dash(d.VolumeSerial)),
        };
    }

    private static string BuildDiscNotice(DiscInfo d)
    {
        var notes = new List<string>();
        if (d.Sessions is > 1)
            notes.Add($"Este disco tem {d.Sessions} sessões. O Windows mostra só a última; sessões antigas podem conter arquivos que não aparecem aqui.");
        if (d.HasAudioTracks && d.LogicalFormat != LogicalFormat.AudioCd)
            notes.Add("Este disco tem faixas de áudio junto com os arquivos (modo misto): só a parte de dados será copiada. As faixas de áudio não são suportadas nesta versão.");
        if (d.PhysicalType == null && d.MediaState == null)
            notes.Add("Alguns detalhes físicos do disco não estão disponíveis (o leitor não permitiu). A cópia funciona normalmente.");
        return string.Join(Environment.NewLine, notes);
    }

    // =============================================================================================
    // Eject, prompts
    // =============================================================================================

    public async Task<bool> EjectAsync(bool silent = false)
    {
        var drive = SelectedDrive;
        if (drive == null) return false;
        var letter = drive.Letter;
        bool ok = await Task.Run(() =>
        {
            try { return _s.Drives.Eject(letter); }
            catch (Exception) { return false; }
        });
        if (ok) _log.Info($"Disco ejetado de {letter}.");
        else
        {
            _log.Warn($"Não foi possível ejetar o disco de {letter}.");
            if (!silent)
                await _dialogs.InfoAsync("Não foi possível ejetar",
                    "O disco está em uso por outro programa ou o leitor não respondeu. Feche programas que possam estar usando o disco " +
                    "(como o Explorador de Arquivos) e tente de novo, ou use o botão do próprio leitor.", DialogIcon.Warning);
        }
        await RefreshAsync();
        return ok;
    }

    /// <summary>Set by the duplicate prompt ("Extrair de novo"); lets batch mode continue.</summary>
    public bool DuplicateAcknowledged { get; private set; }

    public async Task ShowDuplicatePromptAsync()
    {
        var dup = Duplicate;
        if (dup == null) return;
        var when = Fmt.Date(dup.Entry.ExtractedAtUtc);
        var path = dup.Entry.DestinationPath;
        while (true)
        {
            var message = dup.FolderExists
                ? $"Este disco já foi extraído em {when} para:{Environment.NewLine}{path}"
                : $"Este disco já foi extraído em {when}, mas a pasta registrada não existe mais:{Environment.NewLine}{path}" +
                  $"{Environment.NewLine}Você pode extrair de novo ou ejetar o disco.";
            var buttons = new List<DialogButton>();
            if (dup.FolderExists) buttons.Add(new DialogButton("open", "Abrir pasta", IsDefault: true));
            buttons.Add(new DialogButton("again", "Extrair de novo", IsDefault: !dup.FolderExists));
            buttons.Add(new DialogButton("eject", "Ejetar disco")); // not IsCancel: Esc / closing the window must never eject
            var r = await _dialogs.ShowAsync(new DialogRequest("Este disco já foi extraído", message, DialogIcon.Info, buttons));
            switch (r.Key)
            {
                case "open":
                    if (!_shell.OpenFolder(path))
                        await _dialogs.InfoAsync("Não foi possível abrir a pasta", $"O Windows não conseguiu abrir:{Environment.NewLine}{path}", DialogIcon.Warning);
                    continue;
                case "eject":
                    await EjectAsync();
                    return;
                case "again":
                    DuplicateAcknowledged = true;
                    return;
                default:
                    return; // dialog dismissed: leave the disc as it is
            }
        }
    }

    private enum ResumeChoice { Continue, Restart, Cancel }

    private async Task<ResumeChoice> ShowResumePromptAsync(string folderName)
    {
        var r = await _dialogs.ShowAsync(new DialogRequest(
            "Continuar de onde parou?",
            $"A extração deste disco para a pasta “{folderName}” foi interrompida antes de terminar. " +
            "Os arquivos que já foram copiados e conferidos não serão copiados de novo.",
            DialogIcon.Question,
            new[]
            {
                new DialogButton("continue", "Continuar de onde parou", IsDefault: true),
                new DialogButton("restart", "Começar do zero (nova pasta)"),
                new DialogButton("cancel", "Cancelar", IsCancel: true),
            }));
        return r.Key switch { "continue" => ResumeChoice.Continue, "restart" => ResumeChoice.Restart, _ => ResumeChoice.Cancel };
    }

    // =============================================================================================
    // Helpers
    // =============================================================================================

    private bool SafeDirExists(string path)
    {
        try { return _s.Fs.DirectoryExists(path); }
        catch (Exception) { return false; }
    }

    private async Task RunSafe(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Error("Erro inesperado: " + ex.Message, ex); }
    }

    private void NotifyCommands()
    {
        ExtractCommand.NotifyCanExecuteChanged();
        EjectCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        BrowseCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        SkipCommand.NotifyCanExecuteChanged();
        InsistCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _s.Drives.Changed -= OnDriveServiceChanged;
        try { _monitorCts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _analysisCts?.Cancel(); } catch (ObjectDisposedException) { }
        try { _mp4Cts?.Cancel(); } catch (ObjectDisposedException) { }
        _probe.Dispose();
    }
}



