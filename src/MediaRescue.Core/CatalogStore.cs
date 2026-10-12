using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MediaRescue.Core;

/// <summary>
/// Global catalog of extracted discs (RF-08). JSON with schemaVersion, atomic writes with .bak,
/// recovery from a corrupted file without ever throwing from the constructor or <see cref="GetAll"/>.
/// Every extraction is a record; <see cref="FindByFingerprint"/> returns the most recent one.
/// </summary>
public sealed class CatalogStore : ICatalogStore
{
    public const int CurrentSchemaVersion = 1;

    private readonly IDestinationFileSystem _fs;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<CatalogEntry> _entries = new();

    public CatalogStore(IDestinationFileSystem fs, string catalogPath)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _path = catalogPath ?? throw new ArgumentNullException(nameof(catalogPath));
        var loaded = RecoverableJsonFile.Load(_fs, _path, "O catálogo de discos", Parse, out var warning);
        if (loaded != null) _entries.AddRange(loaded);
        LoadWarning = warning;
    }

    public string? LoadWarning { get; }

    /// <summary>Message of the last failed attempt to persist the catalog (null when the last save worked).</summary>
    public string? LastSaveError { get; private set; }

    public IReadOnlyList<CatalogEntry> GetAll()
    {
        lock (_gate) return _entries.ToList();
    }

    public CatalogEntry? FindByFingerprint(string fingerprint)
    {
        lock (_gate)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
                if (string.Equals(_entries[i].Fingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                    return _entries[i];
            return null;
        }
    }

    public void Add(CatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries.Add(entry);
            Save();
        }
    }

    private void Save()
    {
        try
        {
            _fs.WriteAllTextAtomic(_path, Serialize(_entries));
            LastSaveError = null;
        }
        catch (Exception ex)
        {
            // The in-memory catalog stays usable; the UI may surface LastSaveError.
            LastSaveError = "Não foi possível gravar o catálogo: " + ex.Message;
        }
    }

    public string ExportCsv()
    {
        var sb = new StringBuilder();
        sb.Append("Nome;Data;Arquivos;Tamanho (bytes);Bytes perdidos;Situação;Pasta de destino;Impressão digital\r\n");
        foreach (var e in GetAll().OrderByDescending(e => e.ExtractedAtUtc))
        {
            sb.Append(Cell(e.Name)).Append(';')
              .Append(e.ExtractedAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)).Append(';')
              .Append(e.FileCount.ToString(CultureInfo.InvariantCulture)).Append(';')
              .Append(e.TotalBytes.ToString(CultureInfo.InvariantCulture)).Append(';')
              .Append(e.BytesLost.ToString(CultureInfo.InvariantCulture)).Append(';')
              .Append(StatusLabel(e.Status)).Append(';')
              .Append(Cell(e.DestinationPath)).Append(';')
              .Append(Cell(e.Fingerprint)).Append("\r\n");
        }
        return sb.ToString();
    }

    internal static string StatusLabel(OverallStatus s) => s switch
    {
        OverallStatus.Intact => "Íntegro",
        OverallStatus.Recovered => "Recuperado",
        OverallStatus.Partial => "Parcial",
        OverallStatus.Failed => "Falhou",
        OverallStatus.Cancelled => "Cancelado",
        _ => s.ToString(),
    };

    /// <summary>CSV cell with quoting, and a guard against spreadsheet formula injection from disc labels.</summary>
    private static string Cell(string? value)
    {
        value ??= "";
        if (value.Length > 0 && "=+-@\t\r".IndexOf(value[0]) >= 0) value = "'" + value;
        if (value.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0) value = "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    // ---- persistence ----

    private sealed class FileDto
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public List<EntryDto> Entries { get; set; } = new();
    }

    private sealed class EntryDto
    {
        public string? Fingerprint { get; set; }
        public string? Name { get; set; }
        public DateTime ExtractedAtUtc { get; set; }
        public string? DestinationPath { get; set; }
        public long TotalBytes { get; set; }
        public long BytesLost { get; set; }
        public int FileCount { get; set; }
        public OverallStatus Status { get; set; }
    }

    private static string Serialize(IEnumerable<CatalogEntry> entries) =>
        JsonSerializer.Serialize(new FileDto
        {
            SchemaVersion = CurrentSchemaVersion,
            Entries = entries.Select(e => new EntryDto
            {
                Fingerprint = e.Fingerprint, Name = e.Name, ExtractedAtUtc = e.ExtractedAtUtc, DestinationPath = e.DestinationPath,
                TotalBytes = e.TotalBytes, BytesLost = e.BytesLost, FileCount = e.FileCount, Status = e.Status,
            }).ToList(),
        }, JsonDefaults.Options);

    /// <summary>Parses any known schema version; throws on corruption.</summary>
    private static List<CatalogEntry> Parse(string text)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        JsonElement array;
        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                array = root; // schema 0: a bare array of entries
                break;
            case JsonValueKind.Object:
                if (!root.TryGetProperty("entries", out array) || array.ValueKind != JsonValueKind.Array)
                    throw new JsonException("catalog without entries");
                break;
            default:
                throw new JsonException("unexpected catalog root");
        }

        var result = new List<CatalogEntry>();
        foreach (var el in array.EnumerateArray())
        {
            var dto = el.Deserialize<EntryDto>(JsonDefaults.Options) ?? throw new JsonException("null entry");
            if (string.IsNullOrEmpty(dto.Fingerprint) || dto.Name == null || dto.DestinationPath == null)
                throw new JsonException("incomplete entry");
            result.Add(new CatalogEntry
            {
                Fingerprint = dto.Fingerprint, Name = dto.Name, ExtractedAtUtc = dto.ExtractedAtUtc, DestinationPath = dto.DestinationPath,
                TotalBytes = dto.TotalBytes, BytesLost = dto.BytesLost, FileCount = dto.FileCount, Status = dto.Status,
            });
        }
        return result;
    }
}
