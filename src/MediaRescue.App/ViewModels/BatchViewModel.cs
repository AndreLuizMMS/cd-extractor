using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaRescue.App.Infrastructure;
using MediaRescue.App.Services;
using MediaRescue.Core;

namespace MediaRescue.App.ViewModels;

public enum BatchState { Off, WaitingForDisc, Analyzing, AwaitingName, Extracting, Blocked }

public enum BatchBlocker { None, DiscRefused, Duplicate, Destination, NameCancelled, StartFailed }

public enum BatchItemStatus { Extracting, Intact, WithWarnings, Failed, Skipped, Cancelled }

public sealed partial class BatchItemViewModel : ObservableObject
{
    public BatchItemViewModel(string name, BatchItemStatus status, string detail = "")
    {
        Name = name;
        _status = status;
        _detail = detail;
    }

    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Icon), nameof(Tone), nameof(StatusText), nameof(StatusLine))]
    private BatchItemStatus _status;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(StatusLine))] private string _detail;
    public string StatusLine => string.IsNullOrEmpty(Detail) ? StatusText : $"{StatusText} · {Detail}";

    public string Icon => Status switch
    {
        BatchItemStatus.Intact => "✔",
        BatchItemStatus.WithWarnings => "⚠",
        BatchItemStatus.Failed => "✖",
        BatchItemStatus.Skipped => "↷",
        BatchItemStatus.Cancelled => "—",
        _ => "…",
    };

    public StatusTone Tone => Status switch
    {
        BatchItemStatus.Intact => StatusTone.Ok,
        BatchItemStatus.WithWarnings => StatusTone.Warn,
        BatchItemStatus.Failed => StatusTone.Danger,
        BatchItemStatus.Extracting => StatusTone.Accent,
        _ => StatusTone.Neutral,
    };

    public string StatusText => Status switch
    {
        BatchItemStatus.Extracting => "Extraindo…",
        BatchItemStatus.Intact => "Concluído",
        BatchItemStatus.WithWarnings => "Concluído com avisos",
        BatchItemStatus.Failed => "Falhou",
        BatchItemStatus.Skipped => "Pulado",
        BatchItemStatus.Cancelled => "Cancelado",
        _ => "",
    };
}

public sealed class BatchActionViewModel
{
    public BatchActionViewModel(string text, Func<Task> run, bool isPrimary = false)
    {
        Text = text;
        IsPrimary = isPrimary;
        Command = new AsyncRelayCommand(run);
    }

    public string Text { get; }
    public bool IsPrimary { get; }
    public AsyncRelayCommand Command { get; }
}

/// <summary>
/// RF-09 batch mode: waits for a disc, analyses, asks only for the name, extracts, ejects and asks for the next one.
/// It never starts by itself when something blocks (protected/unreadable disc, already extracted, destination problem):
/// it pauses and asks. Driven by <see cref="ExtractViewModel"/> through <see cref="Evaluate"/>.
/// </summary>
public sealed partial class BatchViewModel : ObservableObject
{
    private readonly ExtractViewModel _ext;
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly LogService _log;
    private readonly HashSet<string> _allowDuplicates = new(StringComparer.OrdinalIgnoreCase);
    private bool _enabled;
    private int _gate; // 1 while a disc is being started or copied (atomic: tests run continuations on pool threads)
    private bool _exitPending;
    private string? _lastFingerprint;
    private string? _blockedFingerprint;
    private Task _step = Task.CompletedTask;

    public BatchViewModel(ExtractViewModel ext, SettingsService settings, IDialogService dialogs, IUiDispatcher ui, LogService log)
    {
        _ext = ext;
        _settings = settings;
        _dialogs = dialogs;
        _ui = ui;
        _log = log;
        ExitCommand = new RelayCommand(() => IsEnabled = false);
    }

    public ObservableCollection<BatchItemViewModel> Session { get; } = new();
    public ObservableCollection<BatchActionViewModel> Actions { get; } = new();
    public RelayCommand ExitCommand { get; }

    [ObservableProperty] private BatchState _state = BatchState.Off;
    [ObservableProperty] private BatchBlocker _blocker = BatchBlocker.None;
    [ObservableProperty] private string _instruction = "";
    [ObservableProperty] private string _blockMessage = "";

    public bool IsBlocked => State == BatchState.Blocked;
    partial void OnStateChanged(BatchState value) => OnPropertyChanged(nameof(IsBlocked));

    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled) return;
            if (value) Enable();
            else RequestDisable();
        }
    }

    /// <summary>Completes when no batch step is running (for tests and orderly shutdown).</summary>
    public async Task WhenIdleAsync()
    {
        Task t;
        do { t = _step; await t; } while (!ReferenceEquals(t, _step));
    }

    private void Enable()
    {
        _enabled = true;
        _exitPending = false;
        _lastFingerprint = null;
        _blockedFingerprint = null;
        _allowDuplicates.Clear();
        Session.Clear();
        Actions.Clear();
        Blocker = BatchBlocker.None;
        BlockMessage = "";
        State = BatchState.WaitingForDisc;
        OnPropertyChanged(nameof(IsEnabled));
        _log.Info("Modo lote ligado.");
        UpdateWaitingInstruction();
        Evaluate();
    }

    private void RequestDisable()
    {
        if (State == BatchState.Extracting || _ext.IsExtracting)
        {
            _exitPending = true;
            _ui.Post(() => OnPropertyChanged(nameof(IsEnabled))); // the switch stays on until the copy ends
            _ = _dialogs.InfoAsync("Modo lote",
                "Há uma cópia em andamento. O modo lote será encerrado assim que ela terminar.", DialogIcon.Info);
            return;
        }
        _step = DisableAsync();
    }

    private async Task DisableAsync()
    {
        _enabled = false;
        State = BatchState.Off;
        Blocker = BatchBlocker.None;
        Actions.Clear();
        BlockMessage = "";
        Instruction = "";
        OnPropertyChanged(nameof(IsEnabled));
        _log.Info("Modo lote desligado.");
        await ShowSessionSummaryAsync();
    }

    private async Task ShowSessionSummaryAsync()
    {
        if (Session.Count == 0) return;
        int ok = Session.Count(i => i.Status == BatchItemStatus.Intact);
        int warn = Session.Count(i => i.Status == BatchItemStatus.WithWarnings);
        int bad = Session.Count(i => i.Status == BatchItemStatus.Failed);
        int skipped = Session.Count(i => i.Status is BatchItemStatus.Skipped or BatchItemStatus.Cancelled);
        var lines = new List<string> { $"Discos nesta sessão: {Session.Count}", $"Concluídos: {ok}" };
        if (warn > 0) lines.Add($"Concluídos com avisos: {warn}");
        if (bad > 0) lines.Add($"Com falha: {bad}");
        if (skipped > 0) lines.Add($"Pulados ou cancelados: {skipped}");
        lines.Add("");
        foreach (var i in Session.Reverse()) lines.Add($"{i.Icon} {i.Name} — {i.StatusText}{(string.IsNullOrEmpty(i.Detail) ? "" : " (" + i.Detail + ")")}");
        await _dialogs.InfoAsync("Resumo da sessão", string.Join(Environment.NewLine, lines),
            bad > 0 || warn > 0 ? DialogIcon.Warning : DialogIcon.Success);
    }

    // ----- state machine -----

    /// <summary>Called by the extract screen whenever the reader state, the disc analysis or the validation changed.</summary>
    public void Evaluate()
    {
        if (!_enabled || IsBusy) return;

        var screen = _ext.Screen;
        switch (screen)
        {
            case ScreenState.NoDrive:
            case ScreenState.NoMedia:
            case ScreenState.TrayOpen:
                _lastFingerprint = null;
                _blockedFingerprint = null;
                ClearBlock();
                State = BatchState.WaitingForDisc;
                UpdateWaitingInstruction();
                return;
            case ScreenState.Loading:
                if (State != BatchState.Analyzing) { ClearBlock(); State = BatchState.Analyzing; }
                Instruction = "Lendo o disco…";
                return;
        }

        // A disc is in the drive and the analysis finished (usable or refused).
        var fp = _ext.Disc?.Fingerprint;
        if (State == BatchState.Blocked && fp == _blockedFingerprint)
        {
            RefreshBlock();
            return;
        }
        if (fp != null && fp == _lastFingerprint)
        {
            ClearBlock();
            State = BatchState.WaitingForDisc;
            Instruction = "Retire o disco e insira o próximo.";
            return;
        }
        if (!TryEnter()) return;
        _step = BeginDiscAsync();
    }

    private bool IsBusy => Volatile.Read(ref _gate) != 0;
    private bool TryEnter() => Interlocked.CompareExchange(ref _gate, 1, 0) == 0;

    private void UpdateWaitingInstruction()
    {
        Instruction = _ext.Screen == ScreenState.NoDrive
            ? "Conecte o leitor para começar."
            : Session.Count == 0 ? "Insira o primeiro disco." : "Insira o próximo disco.";
    }

    /// <summary>Starts (or blocks) the disc in the drive. The caller must already hold the gate; it is released here.</summary>
    private async Task BeginDiscAsync()
    {
        bool evaluateAgain = false;
        try
        {
            // Leave the refresh/validation call stack first: prompts and the copy must start from a clean dispatcher turn.
            await Task.Yield();
            var fp = _ext.Disc?.Fingerprint;

            if (_ext.Screen != ScreenState.Ready)
            {
                var info = StatusPresenter.For(_ext.Screen, _ext.SelectedDrive?.Letter);
                Block(BatchBlocker.DiscRefused, fp, $"{info.Title}. {info.Message}");
                return;
            }

            var gate = _ext.Gate;
            if (gate.Blocker == GateBlocker.Busy) return;
            if (gate.Blocker == GateBlocker.Validating)
            {
                ClearBlock();
                State = BatchState.Analyzing;
                Instruction = "Verificando o destino…";
                return;
            }
            if (_ext.Duplicate != null && fp != null && !_allowDuplicates.Contains(fp))
            {
                var d = _ext.Duplicate;
                Block(BatchBlocker.Duplicate, fp,
                    $"Este disco já foi extraído em {Fmt.Date(d.Entry.ExtractedAtUtc)} para {d.Entry.DestinationPath}"
                    + (d.FolderExists ? "." : ", mas essa pasta não existe mais."));
                return;
            }
            if (!gate.CanExtract)
            {
                Block(BatchBlocker.Destination, fp, gate.Reason);
                return;
            }

            ClearBlock();
            var name = _ext.FolderName;
            if (_settings.Current.BatchAskName)
            {
                State = BatchState.AwaitingName;
                Instruction = "Digite o nome deste disco e pressione Enter.";
                var answer = await _dialogs.PromptNameAsync(new NamePromptRequest(
                    "Nome do disco",
                    "Como você quer chamar este disco? Pressione Enter para confirmar o nome sugerido.",
                    name, OfferDontAsk: true));
                if (answer == null)
                {
                    Block(BatchBlocker.NameCancelled, fp, "Você cancelou a pergunta do nome. O modo lote está em pausa neste disco.");
                    return;
                }
                name = answer.Name;
                if (answer.DontAskAgain) _settings.Update(s => s.BatchAskName = false);
            }

            State = BatchState.Extracting;
            var item = new BatchItemViewModel(name, BatchItemStatus.Extracting);
            Session.Insert(0, item);
            Instruction = $"Copiando “{name}”…";

            var outcome = await _ext.RunExtractionAsync(batch: true, nameOverride: name);
            if (!outcome.Started)
            {
                Session.Remove(item);
                Block(BatchBlocker.StartFailed, fp, outcome.NotStartedReason ?? "Não foi possível começar a cópia.");
                return;
            }

            var result = outcome.Result;
            item.Status = result?.Status switch
            {
                OverallStatus.Intact or OverallStatus.Recovered => BatchItemStatus.Intact,
                OverallStatus.Partial => BatchItemStatus.WithWarnings,
                OverallStatus.Cancelled => BatchItemStatus.Cancelled,
                _ => BatchItemStatus.Failed,
            };
            item.Detail = result == null ? "erro inesperado" : DescribeResult(result);
            _lastFingerprint = fp;
            ClearBlock();
            State = BatchState.WaitingForDisc;
            Instruction = outcome.Ejected ? "Insira o próximo disco." : "Retire o disco e insira o próximo.";
            evaluateAgain = true;
        }
        finally
        {
            Volatile.Write(ref _gate, 0);
        }

        if (_exitPending)
        {
            _exitPending = false;
            await DisableAsync();
            return;
        }
        if (evaluateAgain) Evaluate();
    }
    private static string DescribeResult(ExtractionResult r)
    {
        var bad = r.Files.Count(f => f.Status is FileStatus.Partial or FileStatus.Failed or FileStatus.DestinationFailure);
        return bad > 0
            ? Fmt.Plural(bad, "arquivo com problema", "arquivos com problema")
            : Fmt.Plural(r.Files.Count, "arquivo", "arquivos") + " · " + Fmt.Bytes(r.Files.Sum(f => f.BytesTotal));
    }

    // ----- blocking -----

    private void ClearBlock()
    {
        if (Blocker == BatchBlocker.None && Actions.Count == 0 && BlockMessage.Length == 0) return;
        Blocker = BatchBlocker.None;
        BlockMessage = "";
        Actions.Clear();
    }

    private void Block(BatchBlocker kind, string? fingerprint, string message)
    {
        Blocker = kind;
        _blockedFingerprint = fingerprint;
        BlockMessage = message;
        State = BatchState.Blocked;
        Instruction = "Modo lote em pausa: preciso da sua decisão.";
        BuildActions(ready: false);
        _log.Warn("Modo lote em pausa: " + message);
    }

    private void RefreshBlock()
    {
        if (Blocker == BatchBlocker.Destination)
        {
            var gate = _ext.Gate;
            if (gate.CanExtract)
            {
                BlockMessage = "O destino está pronto. Clique em “Continuar” para seguir com este disco.";
                BuildActions(ready: true);
            }
            else if (gate.Blocker != GateBlocker.Validating)
            {
                BlockMessage = gate.Reason;
                BuildActions(ready: false);
            }
        }
    }

    private void BuildActions(bool ready)
    {
        Actions.Clear();
        switch (Blocker)
        {
            case BatchBlocker.Duplicate:
                Actions.Add(new BatchActionViewModel("Extrair de novo", ExtractAgainAsync, isPrimary: true));
                Actions.Add(new BatchActionViewModel("Pular este disco (ejetar)", SkipAsync));
                break;
            case BatchBlocker.Destination:
                Actions.Add(new BatchActionViewModel(ready ? "Continuar" : "Tentar de novo", RetryAsync, isPrimary: true));
                Actions.Add(new BatchActionViewModel("Pular este disco (ejetar)", SkipAsync));
                break;
            case BatchBlocker.NameCancelled:
            case BatchBlocker.StartFailed:
                Actions.Add(new BatchActionViewModel("Tentar de novo", RetryAsync, isPrimary: true));
                Actions.Add(new BatchActionViewModel("Pular este disco (ejetar)", SkipAsync));
                break;
            default:
                Actions.Add(new BatchActionViewModel("Pular este disco (ejetar)", SkipAsync, isPrimary: true));
                break;
        }
    }

    private Task ExtractAgainAsync()
    {
        if (_blockedFingerprint != null) _allowDuplicates.Add(_blockedFingerprint);
        ClearBlock();
        State = BatchState.Analyzing;
        if (!TryEnter()) return Task.CompletedTask;
        _step = BeginDiscAsync();
        return _step;
    }

    private Task RetryAsync()
    {
        ClearBlock();
        State = BatchState.Analyzing;
        if (!TryEnter()) return Task.CompletedTask;
        _step = BeginDiscAsync();
        return _step;
    }

    private Task SkipAsync()
    {
        _step = SkipCoreAsync();
        return _step;
    }

    private async Task SkipCoreAsync()
    {
        var label = _ext.Disc != null && !string.IsNullOrWhiteSpace(_ext.Disc.VolumeLabel) ? _ext.Disc.VolumeLabel : "Disco sem nome";
        var reason = BlockMessage;
        Session.Insert(0, new BatchItemViewModel(label, BatchItemStatus.Skipped, ShortReason(Blocker)));
        _log.Info($"Disco “{label}” pulado no modo lote: {reason}");
        _lastFingerprint = _blockedFingerprint;
        ClearBlock();
        State = BatchState.WaitingForDisc;
        Instruction = "Retire o disco e insira o próximo.";
        await _ext.EjectAsync(silent: true);
        if (_ext.Screen is ScreenState.NoDrive or ScreenState.NoMedia or ScreenState.TrayOpen) UpdateWaitingInstruction();
    }

    private static string ShortReason(BatchBlocker b) => b switch
    {
        BatchBlocker.Duplicate => "já extraído",
        BatchBlocker.Destination => "problema no destino",
        BatchBlocker.DiscRefused => "disco não suportado ou ilegível",
        _ => "",
    };
}





