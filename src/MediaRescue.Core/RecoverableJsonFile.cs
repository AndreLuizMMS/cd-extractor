using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaRescue.Core;

/// <summary>Shared JSON options for every file MediaRescue writes (all carry a schemaVersion).</summary>
internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

/// <summary>
/// Loads a JSON file that is saved atomically with a ".bak" copy (see <see cref="IDestinationFileSystem.WriteAllTextAtomic"/>).
/// A corrupted file is kept as ".corrupt", the ".bak" is tried, and a pt-BR warning is produced (E-30).
/// </summary>
internal static class RecoverableJsonFile
{
    /// <returns>The parsed value, or null when there is nothing usable (first run or unrecoverable corruption).</returns>
    public static T? Load<T>(IDestinationFileSystem fs, string path, string what, Func<string, T> parse, out string? warning) where T : class
    {
        warning = null;
        var raw = SafeRead(fs, path);
        if (raw == null)
        {
            // Missing main file: normal on first run; if only the backup survived, use it.
            var bakOnly = SafeRead(fs, path + ".bak");
            if (bakOnly == null) return null;
            var fromBak = TryParse(bakOnly, parse);
            if (fromBak != null)
            {
                warning = $"{what} não foi encontrado; os dados foram restaurados da cópia de segurança.";
                TryWrite(fs, path, bakOnly);
                return fromBak;
            }
            warning = $"{what} não foi encontrado e a cópia de segurança está ilegível; começando do zero.";
            return null;
        }

        var value = TryParse(raw, parse);
        if (value != null) return value;

        // Corrupted: keep the evidence, then try the backup.
        TryWrite(fs, path + ".corrupt", raw);
        var bak = SafeRead(fs, path + ".bak");
        var recovered = bak == null ? null : TryParse(bak, parse);
        if (recovered != null)
        {
            warning = $"{what} estava corrompido; os dados foram restaurados da cópia de segurança.";
            TryWrite(fs, path, bak!);
            return recovered;
        }
        warning = $"{what} estava corrompido e não havia cópia de segurança utilizável; começando do zero (o arquivo original foi guardado como \"{Path.GetFileName(path)}.corrupt\").";
        return null;
    }

    private static T? TryParse<T>(string text, Func<string, T> parse) where T : class
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return parse(text); }
        catch (Exception) { return null; }
    }

    private static string? SafeRead(IDestinationFileSystem fs, string path)
    {
        try { return fs.ReadAllText(path); }
        catch (Exception) { return null; }
    }

    private static void TryWrite(IDestinationFileSystem fs, string path, string text)
    {
        try { fs.WriteAllTextAtomic(path, text); }
        catch (Exception) { /* best effort */ }
    }
}
