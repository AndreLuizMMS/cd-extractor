using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaRescue.App.Infrastructure;
using MediaRescue.Core;

namespace MediaRescue.App.ViewModels;

public enum StatusTone { Neutral, Ok, Warn, Danger, Accent }

/// <summary>What the main screen shows for the selected reader (RF-01), combining drive state and analysis result.</summary>
public enum ScreenState { NoDrive, NoMedia, TrayOpen, Loading, Ready, Blank, Unfinalized, Unreadable, Protected, AudioCd }

public sealed record StatusInfo(string Title, string Message, StatusTone Tone, string DisabledReason);

/// <summary>Plain-language (pt-BR) messages for every RF-01 state: what happened + what to do.</summary>
public static class StatusPresenter
{
    public static StatusInfo For(ScreenState state, string? letter)
    {
        var drive = string.IsNullOrWhiteSpace(letter) ? "no leitor" : $"no leitor {letter.TrimEnd('\\')}";
        return state switch
        {
            ScreenState.NoDrive => new StatusInfo(
                "Nenhum leitor encontrado",
                "Conecte um leitor de CD/DVD ao computador. O programa percebe sozinho quando ele for conectado. " +
                "Se o leitor já está conectado, tente uma porta USB direto no computador (sem hub).",
                StatusTone.Warn,
                "Conecte um leitor de CD/DVD para começar."),
            ScreenState.NoMedia => new StatusInfo(
                "Sem disco",
                $"Insira um disco {drive}.",
                StatusTone.Neutral,
                $"Insira um disco {drive}."),
            ScreenState.TrayOpen => new StatusInfo(
                "Bandeja aberta",
                "Coloque o disco na bandeja e feche-a (empurre com cuidado ou use o botão do leitor).",
                StatusTone.Neutral,
                "Feche a bandeja com um disco dentro."),
            ScreenState.Loading => new StatusInfo(
                "Lendo disco…",
                "O leitor está reconhecendo o disco. Em discos antigos ou riscados isso pode levar até 1 minuto. Aguarde.",
                StatusTone.Accent,
                "Aguarde: o disco ainda está sendo lido."),
            ScreenState.Ready => new StatusInfo("Disco pronto", "", StatusTone.Ok, ""),
            ScreenState.Blank => new StatusInfo(
                "Disco vazio",
                "Este disco não tem nenhum arquivo gravado. Insira outro disco.",
                StatusTone.Neutral,
                "O disco está vazio. Insira outro disco."),
            ScreenState.Unfinalized => new StatusInfo(
                "Disco não finalizado",
                "Este disco foi gravado, mas não foi finalizado, por isso o computador não consegue ler os arquivos. " +
                "Coloque-o de volta no aparelho onde foi gravado (filmadora ou gravador de DVD), finalize o disco e tente novamente.",
                StatusTone.Warn,
                "Disco não finalizado: finalize-o no aparelho onde foi gravado."),
            ScreenState.Unreadable => new StatusInfo(
                "Disco ilegível",
                "Não foi possível ler este disco. Limpe-o com um pano macio, do centro para a borda, e insira novamente. " +
                "Se continuar assim, use “Testar leitor” para descobrir se o problema é o leitor ou o disco.",
                StatusTone.Danger,
                "Não foi possível ler o disco. Limpe-o e tente novamente."),
            ScreenState.Protected => new StatusInfo(
                "Disco protegido contra cópia",
                "Este parece ser um disco comercial protegido contra cópia, e este programa não consegue copiá-lo. " +
                "Discos gravados em casa (filmadora, gravador de DVD, computador) funcionam normalmente.",
                StatusTone.Danger,
                "Disco protegido contra cópia: não é suportado."),
            ScreenState.AudioCd => new StatusInfo(
                "CD de áudio",
                "CDs de áudio não são suportados nesta versão. Este programa extrai vídeos e arquivos de discos de dados.",
                StatusTone.Warn,
                "CD de áudio não é suportado nesta versão."),
            _ => new StatusInfo("", "", StatusTone.Neutral, ""),
        };
    }

    public static string Short(DriveState state) => state switch
    {
        DriveState.NoMedia => "Sem disco",
        DriveState.TrayOpen => "Bandeja aberta",
        DriveState.Loading => "Lendo disco…",
        DriveState.Ready => "Disco pronto",
        DriveState.Blank => "Disco vazio",
        DriveState.Unfinalized => "Disco não finalizado",
        DriveState.Unreadable => "Disco ilegível",
        DriveState.Protected => "Protegido",
        DriveState.AudioCd => "CD de áudio",
        _ => "—",
    };
}

public enum GateBlocker
{
    None, Busy, NoDrive, DiscNotReady, NothingSelected, NoDestination, NoName, Validating, DestinationIssue,
}

public sealed record GateResult(bool CanExtract, GateBlocker Blocker, string Reason)
{
    public static readonly GateResult Ok = new(true, GateBlocker.None, "");
}

public sealed record GateInput(
    bool IsExtracting,
    bool HasDrive,
    ScreenState Screen,
    string? DriveLetter,
    int SelectedEntryCount,
    bool HasDestination,
    bool HasFolderName,
    bool IsValidating,
    IReadOnlyList<ValidationIssue> Issues);

/// <summary>Decides whether "Extrair disco" is enabled and, when not, the reason shown beside the button.</summary>
public static class ExtractGate
{
    public static GateResult Evaluate(GateInput g)
    {
        if (g.IsExtracting) return new(false, GateBlocker.Busy, "Uma cópia está em andamento.");
        if (!g.HasDrive) return new(false, GateBlocker.NoDrive, StatusPresenter.For(ScreenState.NoDrive, null).DisabledReason);
        if (g.Screen != ScreenState.Ready)
            return new(false, GateBlocker.DiscNotReady, StatusPresenter.For(g.Screen, g.DriveLetter).DisabledReason);
        if (g.SelectedEntryCount <= 0) return new(false, GateBlocker.NothingSelected, "Marque pelo menos um item do disco para extrair.");
        if (!g.HasDestination) return new(false, GateBlocker.NoDestination, "Escolha a pasta de destino.");
        if (!g.HasFolderName) return new(false, GateBlocker.NoName, "Digite um nome para a pasta do disco.");
        if (g.IsValidating) return new(false, GateBlocker.Validating, "Verificando o destino…");
        var blocking = g.Issues.FirstOrDefault(i => i.Severity == ValidationSeverity.Blocking);
        if (blocking != null) return new(false, GateBlocker.DestinationIssue, blocking.Message);
        return GateResult.Ok;
    }
}

public sealed record DetailRow(string Label, string Value);

public sealed class IssueViewModel
{
    public IssueViewModel(ValidationIssue issue)
    {
        Issue = issue;
        Message = issue.Message;
        (Icon, Tone) = issue.Severity switch
        {
            ValidationSeverity.Blocking => ("✖", StatusTone.Danger),
            ValidationSeverity.Warning => ("⚠", StatusTone.Warn),
            _ => ("✔", StatusTone.Ok),
        };
    }

    public ValidationIssue Issue { get; }
    public string Message { get; }
    public string Icon { get; }
    public StatusTone Tone { get; }
    public bool IsBlocking => Issue.Severity == ValidationSeverity.Blocking;
}

public sealed partial class DriveItemViewModel : ObservableObject
{
    public DriveItemViewModel(OpticalDrive drive, DriveState state) { Drive = drive; _state = state; }

    public OpticalDrive Drive { get; }
    public string Letter => Drive.Letter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private DriveState _state;

    public string ModelName
    {
        get
        {
            var name = $"{Drive.Vendor} {Drive.Model}".Trim();
            return name.Length == 0 ? "Leitor de CD/DVD" : name;
        }
    }

    public string StateText => StatusPresenter.Short(State);
    public string DisplayName => $"{Letter}  {ModelName}{(Drive.IsVirtual ? " (virtual)" : "")} — {StateText}";
}

public sealed class QuickDriveViewModel
{
    public QuickDriveViewModel(string root, string text, Action<string> select, string? tooltip = null)
    {
        Root = root;
        Text = text;
        ToolTip = tooltip ?? text;
        SelectCommand = new RelayCommand(() => select(root));
    }

    public string Root { get; }
    public string Text { get; }
    public string ToolTip { get; }
    public RelayCommand SelectCommand { get; }
}


