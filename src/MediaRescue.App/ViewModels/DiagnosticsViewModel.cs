using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaRescue.App.Infrastructure;
using MediaRescue.App.Services;
using MediaRescue.Core;

namespace MediaRescue.App.ViewModels;

public sealed class CheckItemViewModel
{
    public CheckItemViewModel(DiagnosticCheck check)
    {
        Check = check;
        (Icon, Tone, OutcomeText) = check.Outcome switch
        {
            CheckOutcome.Pass => ("✔", StatusTone.Ok, "Passou"),
            CheckOutcome.Warning => ("⚠", StatusTone.Warn, "Atenção"),
            CheckOutcome.Fail => ("✖", StatusTone.Danger, "Falhou"),
            CheckOutcome.NotSupported => ("—", StatusTone.Neutral, "Não suportado por este leitor"),
            _ => ("↷", StatusTone.Neutral, "Não executado"),
        };
    }

    public DiagnosticCheck Check { get; }
    public string Id => Check.Id;
    public string Title => Check.Title;
    public string Detail => Check.Detail;
    public string? Tip => Check.Tip;
    public bool HasTip => !string.IsNullOrWhiteSpace(Check.Tip) && Check.Outcome is CheckOutcome.Fail or CheckOutcome.Warning;
    public string Icon { get; }
    public StatusTone Tone { get; }
    public string OutcomeText { get; }
}

public static class DiagnosticReport
{
    public static readonly string[] GeneralTips =
    {
        "Use uma porta USB direto no computador, não em um hub.",
        "Se o leitor não gira ou desliga sozinho, use o cabo em Y (duas portas USB) ou uma porta com mais energia.",
        "Limpe o disco com um pano macio, do centro para a borda.",
        "Teste com outro disco para separar defeito do leitor de defeito do disco.",
    };

    public static string Build(string driveName, bool fullScan, IEnumerable<DiagnosticCheck> checks, DateTime localNow)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Relatório do leitor — Extrator de Mídias");
        sb.AppendLine("Data: " + localNow.ToString("dd/MM/yyyy HH:mm", Fmt.Pt));
        sb.AppendLine("Leitor: " + driveName);
        sb.AppendLine("Teste de superfície completo: " + (fullScan ? "sim" : "não"));
        sb.AppendLine();
        foreach (var c in checks)
        {
            var icon = c.Outcome switch { CheckOutcome.Pass => "[OK]", CheckOutcome.Warning => "[ATENÇÃO]", CheckOutcome.Fail => "[FALHOU]", CheckOutcome.NotSupported => "[NÃO SUPORTADO]", _ => "[NÃO EXECUTADO]" };
            sb.AppendLine($"{icon} {c.Title}");
            if (!string.IsNullOrWhiteSpace(c.Detail)) sb.AppendLine("    " + c.Detail);
            if (!string.IsNullOrWhiteSpace(c.Tip) && c.Outcome is CheckOutcome.Fail or CheckOutcome.Warning) sb.AppendLine("    Dica: " + c.Tip);
        }
        return sb.ToString();
    }
}

/// <summary>"Testar leitor" screen (RF-07).</summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly ExtractViewModel _ext;
    private readonly IUiDispatcher _ui;
    private readonly IShellService _shell;
    private readonly LogService _log;
    private CancellationTokenSource? _cts;

    public DiagnosticsViewModel(AppServices services, ExtractViewModel ext, IUiDispatcher ui, IShellService shell, LogService log)
    {
        _s = services;
        _ext = ext;
        _ui = ui;
        _shell = shell;
        _log = log;
        RunCommand = new AsyncRelayCommand(RunAsync, () => CanRun);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsRunning);
        CopyReportCommand = new RelayCommand(CopyReport, () => Checks.Count > 0);
        Checks.CollectionChanged += (_, _) => CopyReportCommand.NotifyCanExecuteChanged();
        _ext.PropertyChanged += OnExtChanged;
        Refresh();
    }

    public ObservableCollection<CheckItemViewModel> Checks { get; } = new();
    public IReadOnlyList<string> GeneralTips => DiagnosticReport.GeneralTips;
    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand CopyReportCommand { get; }

    [ObservableProperty] private bool _fullScan;
    /// <summary>Opt-in: the tray test ejects the disc that is in the drive, so it is never on unless the user asks (RF-07 item 3).</summary>
    [ObservableProperty] private bool _includeTrayTest;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasRun;
    [ObservableProperty] private bool _showGeneralTips;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private StatusTone _summaryTone;
    [ObservableProperty] private bool _canRun;
    [ObservableProperty] private string _runDisabledReason = "";
    [ObservableProperty] private string _driveText = "";

    partial void OnIsRunningChanged(bool value) => Refresh();

    private void OnExtChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ExtractViewModel.IsExtracting) or nameof(ExtractViewModel.SelectedDrive))
            _ui.Post(Refresh);
    }

    private void Refresh()
    {
        var drive = _ext.SelectedDrive;
        DriveText = drive == null ? "Nenhum leitor" : $"{drive.Letter}  {drive.ModelName}";
        if (IsRunning) { CanRun = false; RunDisabledReason = "Testando…"; }
        else if (_ext.IsExtracting) { CanRun = false; RunDisabledReason = "Aguarde o fim da cópia para testar o leitor."; }
        else if (drive == null) { CanRun = false; RunDisabledReason = "Conecte um leitor de CD/DVD para testar."; }
        else { CanRun = true; RunDisabledReason = ""; }
        RunCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    public async Task RunAsync()
    {
        Refresh();
        if (!CanRun || _ext.SelectedDrive == null) return;
        var letter = _ext.SelectedDrive.Letter;
        var driveName = DriveText;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsRunning = true;
        HasRun = true;
        ShowGeneralTips = false;
        Checks.Clear();
        SummaryText = "Testando o leitor… isso pode levar alguns instantes.";
        SummaryTone = StatusTone.Accent;
        _log.Info($"Testando o leitor {letter}…");

        IReadOnlyList<DiagnosticCheck>? all = null;
        bool cancelled = false;
        var progress = new SyncProgress<DiagnosticCheck>(_ui, Upsert);
        var full = FullScan;
        _s.Diagnostics.IncludeTrayTest = IncludeTrayTest; // always set explicitly: a previous run must not leak the choice
        try { all = await Task.Run(() => _s.Diagnostics.RunAsync(letter, full, progress, ct)); }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex)
        {
            _log.Error("O teste do leitor falhou: " + ex.Message, ex);
            Upsert(new DiagnosticCheck("error", "O teste não pôde ser concluído", CheckOutcome.Fail, ex.Message,
                "Feche outros programas que usem o leitor e tente de novo."));
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsRunning = false;
        }
        if (all != null) foreach (var c in all) Upsert(c);

        Summarize(cancelled);
        _log.Info("Teste do leitor: " + SummaryText);
        await _ext.RefreshAsync();
    }

    private void Upsert(DiagnosticCheck check)
    {
        var vm = new CheckItemViewModel(check);
        var index = -1;
        for (int i = 0; i < Checks.Count; i++) if (Checks[i].Id == check.Id) { index = i; break; }
        if (index >= 0) Checks[index] = vm; else Checks.Add(vm);
    }

    private void Summarize(bool cancelled)
    {
        int fail = Checks.Count(c => c.Check.Outcome == CheckOutcome.Fail);
        int warn = Checks.Count(c => c.Check.Outcome == CheckOutcome.Warning);
        if (cancelled) { SummaryText = "Teste cancelado."; SummaryTone = StatusTone.Neutral; }
        else if (fail > 0) { SummaryText = $"O teste encontrou {Fmt.Plural(fail, "problema", "problemas")}. Veja as dicas abaixo."; SummaryTone = StatusTone.Danger; }
        else if (warn > 0) { SummaryText = $"O teste terminou com {Fmt.Plural(warn, "aviso", "avisos")}."; SummaryTone = StatusTone.Warn; }
        else if (Checks.Count == 0) { SummaryText = "Nenhuma verificação foi executada."; SummaryTone = StatusTone.Neutral; }
        else { SummaryText = "Tudo certo: o leitor respondeu bem a todos os testes."; SummaryTone = StatusTone.Ok; }
        ShowGeneralTips = fail > 0 || warn > 0;
    }

    public string BuildReport() =>
        DiagnosticReport.Build(DriveText, FullScan, Checks.Select(c => c.Check), DateTime.Now);

    private void CopyReport()
    {
        _shell.CopyToClipboard(BuildReport());
        _log.Info("Relatório do leitor copiado para a área de transferência.");
    }
}
