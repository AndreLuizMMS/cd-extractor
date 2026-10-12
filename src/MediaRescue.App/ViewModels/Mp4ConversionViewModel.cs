using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaRescue.App.Infrastructure;
using MediaRescue.Core;

namespace MediaRescue.App.ViewModels;

/// <summary>Progress card and result text of the optional MP4 conversion phase (RF-11), shown after the copy.</summary>
public sealed partial class Mp4ConversionViewModel : ObservableObject
{
    public Mp4ConversionViewModel()
    {
        CancelCommand = new RelayCommand(() => CancelRequested?.Invoke(), () => IsActive && !IsCancelling);
    }

    /// <summary>Set by the owner: stops the conversion.</summary>
    public Action? CancelRequested { get; set; }

    public RelayCommand CancelCommand { get; }

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CancelCommand))] private bool _isActive;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CancelCommand))] private bool _isCancelling;
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private string _percentText = "0%";
    [ObservableProperty] private string _jobText = "";
    [ObservableProperty] private string _currentName = "";
    [ObservableProperty] private string _elapsedText = "00:00";
    [ObservableProperty] private string _etaText = "calculando…";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private StatusTone _summaryTone = StatusTone.Neutral;

    public string CancelButtonText => IsCancelling ? "Cancelando…" : "Cancelar conversão";

    partial void OnIsCancellingChanged(bool value) => OnPropertyChanged(nameof(CancelButtonText));

    public void Begin()
    {
        IsActive = true;
        IsCancelling = false;
        Fraction = 0;
        PercentText = "0%";
        JobText = "";
        CurrentName = "";
        ElapsedText = "00:00";
        EtaText = "calculando…";
        StatusText = "Preparando a conversão para MP4…";
        ClearSummary();
    }

    public void Update(Mp4ConversionProgress p)
    {
        Fraction = Math.Clamp(p.OverallFraction, 0, 1);
        PercentText = Fmt.Percent(Fraction);
        JobText = p.JobCount > 0 ? $"Vídeo {Math.Clamp(p.JobIndex, 1, p.JobCount)} de {p.JobCount}" : "";
        if (!string.IsNullOrEmpty(p.CurrentName)) CurrentName = p.CurrentName;
        ElapsedText = Fmt.Clock(p.Elapsed);
        EtaText = p.Eta is { } eta ? "~" + Fmt.Clock(eta) : "calculando…";
        if (!string.IsNullOrWhiteSpace(p.Message)) StatusText = p.Message!;
    }

    public void End()
    {
        IsActive = false;
        IsCancelling = false;
    }

    public void SetSummary(string text, StatusTone tone)
    {
        SummaryText = text;
        SummaryTone = tone;
    }

    public void ClearSummary()
    {
        SummaryText = "";
        SummaryTone = StatusTone.Neutral;
    }
}

/// <summary>Plain-language (pt-BR) description of what the conversion did.</summary>
public static class Mp4ConversionSummary
{
    public static (string Text, StatusTone Tone) Describe(Mp4ConversionResult r)
    {
        if (r.Error != null)
            return ("Conversão para MP4 não realizada: " + r.Error + " Os arquivos originais continuam intactos.", StatusTone.Warn);

        int total = r.Jobs.Count;
        int ok = r.ConvertedCount;
        if (r.WasCancelled)
        {
            var kept = ok > 0 ? $" {Fmt.Plural(ok, "vídeo já convertido foi mantido", "vídeos já convertidos foram mantidos")} na pasta MP4." : "";
            return ("Conversão para MP4 cancelada." + kept + " Os arquivos originais continuam intactos.", StatusTone.Neutral);
        }
        if (total == 0)
            return ("Conversão para MP4: não havia vídeos para converter (arquivos que já são MP4 não são convertidos).", StatusTone.Neutral);

        var encoders = r.Jobs.Where(j => j.Status == Mp4JobStatus.Converted && !string.IsNullOrEmpty(j.Encoder))
            .Select(j => Mp4Encoders.Describe(j.Encoder)).Distinct().ToList();
        var lines = new List<string>
        {
            $"Conversão para MP4: {ok} de {total} {(total == 1 ? "vídeo convertido" : "vídeos convertidos")}"
            + (encoders.Count > 0 ? $" com {string.Join(" e ", encoders)}." : ".")
            + " Os originais foram mantidos; os MP4 estão na subpasta “MP4” da pasta do disco.",
        };
        foreach (var j in r.Jobs)
        {
            if (j.Status == Mp4JobStatus.Failed)
                lines.Add($"• {j.Name} — não foi convertido. {j.Message}".TrimEnd());
            else if (j.Status == Mp4JobStatus.Converted && j.FromDamagedSource)
                lines.Add($"• {j.Name} — convertido a partir de uma cópia incompleta; pode ter saltos.");
            else if (j.Status == Mp4JobStatus.Converted && !string.IsNullOrWhiteSpace(j.Message))
                lines.Add($"• {j.Name} — {j.Message}");
        }
        var tone = ok == 0 ? StatusTone.Danger : r.FailedCount > 0 || r.Jobs.Any(j => j.FromDamagedSource) ? StatusTone.Warn : StatusTone.Ok;
        return (string.Join(Environment.NewLine, lines), tone);
    }
}
