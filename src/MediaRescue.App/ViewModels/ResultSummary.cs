using MediaRescue.App.Infrastructure;
using MediaRescue.Core;

namespace MediaRescue.App.ViewModels;

public sealed record ProblemFile(string Name, string Description, StatusTone Tone);

/// <summary>Plain-language summary of a finished extraction (shown in the progress card and in the end dialog).</summary>
public sealed class ResultSummary
{
    public required OverallStatus Status { get; init; }
    public required string Headline { get; init; }
    public required string Detail { get; init; }
    public required StatusTone Tone { get; init; }
    public required string FolderPath { get; init; }
    public int Intact { get; init; }
    public int Recovered { get; init; }
    public int Partial { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public int DestinationFailures { get; init; }
    public long BytesLost { get; init; }
    public long BytesTotal { get; init; }
    public int FileCount { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<ProblemFile> Problems { get; init; } = Array.Empty<ProblemFile>();
    public bool HasProblems => Problems.Count > 0;

    public string CountsText
    {
        get
        {
            var parts = new List<string> { $"Íntegros {Intact}" };
            if (Recovered > 0) parts.Add($"Recuperados {Recovered}");
            if (Partial > 0) parts.Add($"Incompletos {Partial}");
            if (Failed > 0) parts.Add($"Falharam {Failed}");
            if (DestinationFailures > 0) parts.Add($"Erro no destino {DestinationFailures}");
            if (Skipped > 0) parts.Add($"Ignorados {Skipped}");
            return string.Join(" · ", parts);
        }
    }

    public string FullText
    {
        get
        {
            var lines = new List<string> { Headline };
            if (!string.IsNullOrWhiteSpace(Detail)) lines.Add(Detail);
            lines.Add(CountsText + (BytesLost > 0 ? $" · Irrecuperável: {Fmt.Bytes(BytesLost)}" : "") + $" · Tempo: {Fmt.Clock(Duration)}");
            foreach (var p in Problems.Take(8)) lines.Add("• " + p.Name + " — " + p.Description);
            if (Problems.Count > 8) lines.Add($"• … e mais {Problems.Count - 8}.");
            return string.Join(Environment.NewLine, lines);
        }
    }

    public static ResultSummary From(ExtractionResult result)
    {
        int Count(FileStatus s) => result.Files.Count(f => f.Status == s);
        var lost = result.Files.Sum(f => f.BytesLost);
        var total = result.Files.Sum(f => f.BytesTotal);
        var problems = new List<ProblemFile>();
        foreach (var f in result.Files)
        {
            switch (f.Status)
            {
                case FileStatus.Partial:
                    var pct = f.BytesTotal > 0 ? (double)f.BytesLost / f.BytesTotal * 100 : 0;
                    problems.Add(new ProblemFile(f.WrittenPath,
                        $"incompleto: faltam {Fmt.Bytes(f.BytesLost)} ({pct.ToString("0.##", Fmt.Pt)}%). O vídeo deve abrir, com um pequeno “salto” nos trechos perdidos.",
                        StatusTone.Warn));
                    break;
                case FileStatus.Failed:
                    problems.Add(new ProblemFile(f.WrittenPath, "não foi possível ler este arquivo do disco." + ErrorSuffix(f), StatusTone.Danger));
                    break;
                case FileStatus.DestinationFailure:
                    problems.Add(new ProblemFile(f.WrittenPath,
                        "falha ao gravar no destino. O pendrive ou HD pode estar com defeito: tente outro destino." + ErrorSuffix(f), StatusTone.Danger));
                    break;
            }
        }

        string headline, detail;
        StatusTone tone;
        var n = result.Files.Count;
        switch (result.Status)
        {
            case OverallStatus.Intact:
                headline = n == 1 ? "Tudo certo! O arquivo foi copiado e verificado." : $"Tudo certo! {n} arquivos copiados e verificados.";
                detail = "";
                tone = StatusTone.Ok;
                break;
            case OverallStatus.Recovered:
                headline = "Concluído: o disco tinha trechos difíceis de ler, mas tudo foi recuperado.";
                detail = "Os arquivos estão completos.";
                tone = StatusTone.Ok;
                break;
            case OverallStatus.Partial:
                headline = "Concluído com perdas: " + Fmt.Plural(Math.Max(1, Count(FileStatus.Partial) + Count(FileStatus.Failed) + Count(FileStatus.DestinationFailure)), "arquivo ficou incompleto", "arquivos ficaram incompletos")
                           + (lost > 0 ? $" ({Fmt.Bytes(lost)} não puderam ser lidos)." : ".");
                detail = "A lista do que faltou está em “LEIA-ME - ERROS.txt”, dentro da pasta do disco.";
                tone = StatusTone.Warn;
                break;
            case OverallStatus.Failed:
                headline = "Não foi possível copiar os arquivos deste disco.";
                detail = "Limpe o disco com um pano macio e tente de novo, ou use “Testar leitor” para saber se o problema é o leitor ou o disco.";
                tone = StatusTone.Danger;
                break;
            default:
                headline = "Cópia cancelada.";
                detail = "";
                tone = StatusTone.Neutral;
                break;
        }

        return new ResultSummary
        {
            Status = result.Status,
            Headline = headline,
            Detail = detail,
            Tone = tone,
            FolderPath = result.DiscFolder,
            Intact = Count(FileStatus.Intact),
            Recovered = Count(FileStatus.Recovered),
            Partial = Count(FileStatus.Partial),
            Failed = Count(FileStatus.Failed),
            Skipped = Count(FileStatus.Skipped),
            DestinationFailures = Count(FileStatus.DestinationFailure),
            BytesLost = lost,
            BytesTotal = total,
            FileCount = n,
            Duration = result.Duration,
            Problems = problems,
        };
    }

    private static string ErrorSuffix(FileResult f) => string.IsNullOrWhiteSpace(f.Error) ? "" : $" (Detalhes: {f.Error})";
}

