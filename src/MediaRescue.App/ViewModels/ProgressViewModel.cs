using CommunityToolkit.Mvvm.ComponentModel;
using MediaRescue.App.Infrastructure;
using MediaRescue.Core;

namespace MediaRescue.App.ViewModels;

/// <summary>Taskbar/visual state without WPF types.</summary>
public enum ProgressVisual { None, Indeterminate, Normal, Paused, Error }

/// <summary>Maps <see cref="ExtractionProgress"/> to the texts and values shown in the progress area (RF-06).</summary>
public sealed partial class ProgressViewModel : ObservableObject
{
    /// <summary>ETA is shown as "calculando…" while there is not enough data.</summary>
    public static readonly TimeSpan EtaWarmup = TimeSpan.FromSeconds(10);

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private double _overallFraction;
    [ObservableProperty] private double _fileFraction;
    [ObservableProperty] private string _overallText = "0%";
    [ObservableProperty] private string _fileText = "";
    [ObservableProperty] private string _currentFileName = "";
    [ObservableProperty] private string _filesText = "";
    [ObservableProperty] private string _bytesText = "";
    [ObservableProperty] private string _speedText = "—";
    [ObservableProperty] private string _etaText = "calculando…";
    [ObservableProperty] private string _elapsedText = "00:00";
    [ObservableProperty] private string _intactText = "Íntegros 0";
    [ObservableProperty] private string _errorsText = "Com erro 0";
    [ObservableProperty] private string _lostText = "";
    [ObservableProperty] private string _phaseText = "";
    [ObservableProperty] private StatusTone _phaseTone = StatusTone.Neutral;
    [ObservableProperty] private ProgressVisual _visual = ProgressVisual.None;
    [ObservableProperty] private double _taskbarValue;
    [ObservableProperty] private bool _canInsist;
    [ObservableProperty] private bool _isUserPaused;
    [ObservableProperty] private ExtractionPhase _phase = ExtractionPhase.Preparing;

    public string PauseButtonText => IsUserPaused ? "Continuar" : "Pausar";

    partial void OnIsUserPausedChanged(bool value) => OnPropertyChanged(nameof(PauseButtonText));

    public void Begin()
    {
        IsActive = true;
        IsUserPaused = false;
        Update(new ExtractionProgress { Phase = ExtractionPhase.Preparing }, false);
        Visual = ProgressVisual.Indeterminate;
    }

    public void End()
    {
        IsActive = false;
        IsUserPaused = false;
        Visual = ProgressVisual.None;
        TaskbarValue = 0;
        CanInsist = false;
    }

    public void Update(ExtractionProgress p, bool userPaused)
    {
        Phase = p.Phase;
        var total = p.TotalBytesTotal;
        OverallFraction = total > 0 ? Math.Clamp((double)p.TotalBytesDone / total, 0, 1) : 0;
        FileFraction = p.FileBytesTotal > 0 ? Math.Clamp((double)p.FileBytesDone / p.FileBytesTotal, 0, 1) : 0;
        OverallText = Fmt.Percent(OverallFraction);
        FileText = Fmt.Percent(FileFraction);
        CurrentFileName = p.CurrentFile ?? "";
        FilesText = p.FileCount > 0 ? $"{Math.Clamp(p.FileIndex, 0, p.FileCount)}/{p.FileCount} arquivos" : "";
        BytesText = total > 0 ? $"{Fmt.Bytes(p.TotalBytesDone)} de {Fmt.Bytes(total)}" : "";
        SpeedText = Fmt.Speed(p.BytesPerSecond);
        EtaText = EtaFor(p);
        ElapsedText = Fmt.Clock(p.Elapsed);
        IntactText = $"Íntegros {p.FilesIntact}";
        ErrorsText = $"Com erro {p.FilesWithErrors}";
        LostText = p.BytesLost > 0 ? $"Irrecuperável: {Fmt.Bytes(p.BytesLost)}" : "";
        (PhaseText, PhaseTone) = DescribePhase(p.Phase, userPaused);
        CanInsist = p.Phase == ExtractionPhase.SecondPass || p.FilesWithErrors > 0;
        TaskbarValue = OverallFraction;
        Visual = VisualFor(p, userPaused);
    }

    /// <summary>"calculando…" while unknown, "indeterminado" during error recovery or waiting, otherwise "~mm:ss".</summary>
    public static string EtaFor(ExtractionProgress p)
    {
        if (p.Eta is { } eta && eta >= TimeSpan.Zero)
            return "~" + Fmt.Clock(eta);
        return p.Phase switch
        {
            ExtractionPhase.SecondPass or ExtractionPhase.Paused or ExtractionPhase.WaitingForDrive
                or ExtractionPhase.WaitingForDestination or ExtractionPhase.DestinationFull => "indeterminado",
            _ => "calculando…",
        };
    }

    private static ProgressVisual VisualFor(ExtractionProgress p, bool userPaused)
    {
        if (userPaused || p.Phase == ExtractionPhase.Paused) return ProgressVisual.Paused;
        return p.Phase switch
        {
            ExtractionPhase.WaitingForDrive or ExtractionPhase.WaitingForDestination or ExtractionPhase.DestinationFull => ProgressVisual.Error,
            ExtractionPhase.Preparing => ProgressVisual.Indeterminate,
            _ => p.BytesLost > 0 ? ProgressVisual.Error : ProgressVisual.Normal,
        };
    }

    public static (string Text, StatusTone Tone) DescribePhase(ExtractionPhase phase, bool userPaused)
    {
        if (userPaused && phase is ExtractionPhase.Copying or ExtractionPhase.SecondPass or ExtractionPhase.Verifying)
            return ("Em pausa. Clique em “Continuar” para seguir.", StatusTone.Neutral);
        return phase switch
        {
            ExtractionPhase.Preparing => ("Preparando a cópia…", StatusTone.Accent),
            ExtractionPhase.Copying => ("Copiando arquivos…", StatusTone.Accent),
            ExtractionPhase.SecondPass => ("Tentando recuperar trechos com erro de leitura. Isso pode demorar; o programa não travou.", StatusTone.Warn),
            ExtractionPhase.Verifying => ("Verificando se a cópia ficou idêntica ao original…", StatusTone.Accent),
            ExtractionPhase.Paused => ("Em pausa.", StatusTone.Neutral),
            ExtractionPhase.WaitingForDrive => ("Leitor desconectado ou disco removido. Reconecte o leitor ou reinsira o mesmo disco; a cópia continua sozinha.", StatusTone.Danger),
            ExtractionPhase.WaitingForDestination => ("O destino ficou indisponível. Reconecte o pendrive ou HD; a cópia continua sozinha.", StatusTone.Danger),
            ExtractionPhase.DestinationFull => ("O destino ficou sem espaço. Libere espaço nele (ou troque o pendrive) e a cópia continua.", StatusTone.Danger),
            ExtractionPhase.Completed => ("Concluído.", StatusTone.Ok),
            ExtractionPhase.Cancelled => ("Cancelado.", StatusTone.Neutral),
            _ => ("", StatusTone.Neutral),
        };
    }
}
