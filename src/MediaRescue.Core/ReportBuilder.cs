using System.Globalization;
using System.Text;

namespace MediaRescue.Core;

/// <summary>Builds the per-disc manifest (RF-08) and the plain-language "LEIA-ME - ERROS.txt" (pt-BR).</summary>
internal static class ReportBuilder
{
    public static string StatusLabel(FileStatus s) => s switch
    {
        FileStatus.Intact => "Íntegro",
        FileStatus.Recovered => "Recuperado",
        FileStatus.Partial => "Parcial",
        FileStatus.Failed => "Falhou",
        FileStatus.Skipped => "Ignorado",
        FileStatus.DestinationFailure => "Falha no destino",
        _ => "Pendente",
    };

    public static bool HasProblems(IEnumerable<FileResult> results) =>
        results.Any(r => r.Status is FileStatus.Partial or FileStatus.Failed or FileStatus.DestinationFailure or FileStatus.Skipped);

    public static OverallStatus Overall(IReadOnlyList<FileResult> results, bool cancelled)
    {
        if (cancelled) return OverallStatus.Cancelled;
        if (results.Count == 0) return OverallStatus.Intact;
        bool anyData = results.Any(r => r.Status is FileStatus.Intact or FileStatus.Recovered or FileStatus.Partial);
        if (!anyData) return OverallStatus.Failed;
        if (HasProblems(results)) return OverallStatus.Partial;
        return results.Any(r => r.Status == FileStatus.Recovered) ? OverallStatus.Recovered : OverallStatus.Intact;
    }

    public static ManifestDto BuildManifest(ExtractionRequest req, string jobId, DateTime startedUtc, DateTime finishedUtc,
        TimeSpan duration, OverallStatus overall, IReadOnlyList<FileResult> results, IReadOnlyDictionary<string, string> pathMap,
        IReadOnlyDictionary<string, DateTime> modified, string? note)
    {
        var d = req.Disc;
        var m = new ManifestDto
        {
            AppVersion = JobFiles.AppVersion,
            GeneratedAtUtc = finishedUtc,
            Job = new ManifestJob
            {
                JobId = jobId, StartedAtUtc = startedUtc, FinishedAtUtc = finishedUtc, DurationSeconds = duration.TotalSeconds,
                OverallStatus = overall, Note = note,
            },
            Disc = new ManifestDisc
            {
                Fingerprint = d.Fingerprint, VolumeLabel = d.VolumeLabel, VolumeSerial = d.VolumeSerial, PhysicalType = d.PhysicalType,
                MediaState = d.MediaState, FileSystem = d.FileSystem, LogicalFormat = d.LogicalFormat, RecordedAtUtc = d.RecordedAtUtc,
                Sessions = d.Sessions, Tracks = d.Tracks, UsedBytes = d.UsedBytes,
            },
            Drive = new ManifestDrive
            {
                Letter = req.Drive.Letter, Vendor = req.Drive.Vendor, Model = req.Drive.Model, Firmware = req.Drive.Firmware, BusType = req.Drive.BusType,
            },
            Totals = new ManifestTotals
            {
                Files = results.Count,
                Bytes = results.Sum(r => r.BytesTotal),
                BytesLost = results.Sum(r => r.BytesLost),
                Intact = results.Count(r => r.Status == FileStatus.Intact),
                Recovered = results.Count(r => r.Status == FileStatus.Recovered),
                Partial = results.Count(r => r.Status == FileStatus.Partial),
                Failed = results.Count(r => r.Status == FileStatus.Failed),
                Skipped = results.Count(r => r.Status == FileStatus.Skipped),
                DestinationFailure = results.Count(r => r.Status == FileStatus.DestinationFailure),
            },
        };
        foreach (var kv in pathMap.OrderBy(k => k.Key, StringComparer.Ordinal)) m.PathMap[kv.Key] = kv.Value;
        foreach (var r in results)
        {
            m.Files.Add(new ManifestFile
            {
                OriginalPath = r.RelativePath,
                WrittenPath = r.WrittenPath,
                Status = r.Status,
                StatusLabel = StatusLabel(r.Status),
                BytesTotal = r.BytesTotal,
                BytesLost = r.BytesLost,
                LostPercent = r.BytesTotal > 0 ? Math.Round(100.0 * r.BytesLost / r.BytesTotal, 4) : 0,
                BadRanges = r.BadRanges.Select(b => new RangeDto { Offset = b.Offset, Length = b.Length }).ToList(),
                Sha256 = r.Sha256,
                Sha256Scope = r.Sha256 == null ? null : (r.Status == FileStatus.Partial ? "recovered-content" : "original"),
                Error = r.Error,
                DurationSeconds = r.Duration.TotalSeconds,
                ModifiedUtc = modified.TryGetValue(r.RelativePath, out var mt) && mt.Ticks != 0 ? mt : null,
            });
        }
        return m;
    }

    public static string BuildReadme(ExtractionRequest req, IReadOnlyList<FileResult> results, OverallStatus overall, DateTime finishedUtc, string? note)
    {
        var sb = new StringBuilder();
        var nl = "\r\n";
        sb.Append("LEIA-ME - ERROS").Append(nl);
        sb.Append("===============").Append(nl).Append(nl);
        sb.Append("Este arquivo foi criado pelo Extrator de Mídias (MediaRescue) em ").Append(Fmt.Date(finishedUtc)).Append('.').Append(nl);
        sb.Append("Ele explica o que NÃO foi copiado perfeitamente deste disco. Os demais arquivos estão íntegros.").Append(nl).Append(nl);

        var label = string.IsNullOrWhiteSpace(req.Disc.VolumeLabel) ? "(sem rótulo)" : req.Disc.VolumeLabel;
        sb.Append("Disco: ").Append(label).Append(nl);
        sb.Append("Leitor: ").Append(req.Drive.Letter).Append(' ').Append(req.Drive.Vendor).Append(' ').Append(req.Drive.Model).Append(nl);
        sb.Append("Situação geral: ").Append(OverallLabel(overall)).Append(nl);
        if (!string.IsNullOrWhiteSpace(note)) sb.Append("Observação: ").Append(note).Append(nl);
        sb.Append(nl);

        int good = results.Count(r => r.Status is FileStatus.Intact or FileStatus.Recovered);
        var partial = results.Where(r => r.Status == FileStatus.Partial).ToList();
        var failed = results.Where(r => r.Status == FileStatus.Failed).ToList();
        var destFail = results.Where(r => r.Status == FileStatus.DestinationFailure).ToList();
        var skipped = results.Where(r => r.Status == FileStatus.Skipped).ToList();
        sb.Append("Resumo: ").Append(good).Append(" arquivo(s) copiado(s) sem perdas, ")
          .Append(partial.Count).Append(" com trechos perdidos, ")
          .Append(failed.Count).Append(" não recuperado(s), ")
          .Append(destFail.Count).Append(" com falha no destino, ")
          .Append(skipped.Count).Append(" ignorado(s).").Append(nl).Append(nl);

        if (partial.Count > 0)
        {
            sb.Append("ARQUIVOS COM TRECHOS PERDIDOS").Append(nl);
            sb.Append("O arquivo foi salvo com o tamanho original, mas os trechos abaixo ficaram preenchidos com zeros.").Append(nl);
            sb.Append("Ao reproduzir o vídeo você pode notar um \"salto\" ou uma imagem congelada nesses pontos.").Append(nl).Append(nl);
            foreach (var r in partial)
            {
                sb.Append(" - ").Append(r.WrittenPath);
                if (!string.Equals(r.WrittenPath, r.RelativePath, StringComparison.Ordinal)) sb.Append("  (no disco: ").Append(r.RelativePath).Append(')');
                sb.Append(nl);
                double frac = r.BytesTotal > 0 ? (double)r.BytesLost / r.BytesTotal : 0;
                sb.Append("   Perdido: ").Append(Fmt.Bytes(r.BytesLost)).Append(" de ").Append(Fmt.Bytes(r.BytesTotal))
                  .Append(" (").Append(Fmt.Percent(frac)).Append(')').Append(nl);
                int shown = 0;
                foreach (var b in r.BadRanges)
                {
                    if (++shown > 50) { sb.Append("   ... e mais ").Append(r.BadRanges.Count - 50).Append(" trecho(s). Veja a lista completa em mediarescue-manifest.json.").Append(nl); break; }
                    sb.Append("   Trecho ").Append(shown).Append(": de ").Append(Fmt.Bytes(b.Offset)).Append(" até ").Append(Fmt.Bytes(b.Offset + b.Length))
                      .Append(" (posição ").Append(b.Offset.ToString(CultureInfo.InvariantCulture)).Append(", ")
                      .Append(b.Length.ToString(CultureInfo.InvariantCulture)).Append(" bytes)").Append(nl);
                }
                sb.Append(nl);
            }
        }

        if (failed.Count > 0)
        {
            sb.Append("ARQUIVOS NÃO RECUPERADOS (nenhum dado pôde ser lido do disco)").Append(nl).Append(nl);
            foreach (var r in failed)
            {
                sb.Append(" - ").Append(r.RelativePath).Append(" (").Append(Fmt.Bytes(r.BytesTotal)).Append(')').Append(nl);
                if (!string.IsNullOrWhiteSpace(r.Error)) sb.Append("   Motivo: ").Append(r.Error).Append(nl);
            }
            sb.Append(nl);
        }

        if (destFail.Count > 0)
        {
            sb.Append("ARQUIVOS NÃO GRAVADOS NO DESTINO").Append(nl);
            sb.Append("O arquivo foi lido do disco, mas a gravação ou a conferência no destino falhou mesmo após uma nova tentativa.").Append(nl);
            sb.Append("O pendrive ou HD de destino pode estar com defeito: tente outro destino e copie estes arquivos novamente.").Append(nl).Append(nl);
            foreach (var r in destFail)
            {
                sb.Append(" - ").Append(r.RelativePath).Append(" (").Append(Fmt.Bytes(r.BytesTotal)).Append(')').Append(nl);
                if (!string.IsNullOrWhiteSpace(r.Error)) sb.Append("   Motivo: ").Append(r.Error).Append(nl);
            }
            sb.Append(nl);
        }

        if (skipped.Count > 0)
        {
            sb.Append("ARQUIVOS IGNORADOS (você pulou)").Append(nl).Append(nl);
            foreach (var r in skipped) sb.Append(" - ").Append(r.RelativePath).Append(nl);
            sb.Append(nl);
        }

        sb.Append("O QUE FAZER").Append(nl);
        sb.Append(" - Limpe o disco com um pano macio, do centro para a borda, e tente copiar de novo (a opção \"Insistir mais\" dá mais tempo à recuperação).").Append(nl);
        sb.Append(" - Se possível, teste em outro leitor de CD/DVD; leitores diferentes leem discos riscados de forma diferente.").Append(nl);
        sb.Append(" - Guarde este disco: ele pode ter dados que outro leitor consiga recuperar.").Append(nl);
        return sb.ToString();
    }

    public static string OverallLabel(OverallStatus s) => s switch
    {
        OverallStatus.Intact => "tudo íntegro",
        OverallStatus.Recovered => "tudo recuperado (houve erros de leitura, mas nada foi perdido)",
        OverallStatus.Partial => "cópia parcial (alguma coisa foi perdida ou não copiada)",
        OverallStatus.Failed => "a cópia falhou",
        OverallStatus.Cancelled => "cancelada",
        _ => s.ToString(),
    };
}
