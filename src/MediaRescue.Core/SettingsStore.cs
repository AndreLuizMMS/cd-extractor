using System.Text.Json;
using System.Text.Json.Nodes;

namespace MediaRescue.Core;

/// <summary>
/// settings.json (RF-12). Never throws on load: a missing/invalid file yields defaults (E-30), a corrupted file is
/// restored from ".bak" when possible, and out-of-range values fall back to their defaults.
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    public const int CurrentSchemaVersion = 1;

    private readonly IDestinationFileSystem _fs;
    private readonly string _path;

    public SettingsStore(IDestinationFileSystem fs, string settingsPath)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _path = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));
    }

    /// <summary>Non-null after a <see cref="Load"/> that had to recover from backup or fall back to defaults.</summary>
    public string? LoadWarning { get; private set; }

    public AppSettings Load()
    {
        var loaded = RecoverableJsonFile.Load(_fs, _path, "O arquivo de configurações", Parse, out var warning);
        LoadWarning = warning;
        return loaded ?? new AppSettings { SchemaVersion = CurrentSchemaVersion };
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var ex = settings.Extraction ?? new ExtractionOptions();
        var node = new JsonObject
        {
            ["schemaVersion"] = CurrentSchemaVersion,
            ["lastDestination"] = settings.LastDestination,
            ["ejectWhenDone"] = settings.EjectWhenDone,
            ["convertToMp4"] = settings.ConvertToMp4,
            ["batchAskName"] = settings.BatchAskName,
            ["theme"] = settings.Theme,
            ["extraction"] = new JsonObject
            {
                ["retriesPerSector"] = ex.RetriesPerSector,
                ["blockTimeoutSeconds"] = ex.BlockTimeout.TotalSeconds,
                ["secondPassBudgetMinutes"] = ex.SecondPassBudget.TotalMinutes,
                ["reduceSpeedOnError"] = ex.ReduceSpeedOnError,
                ["verifyAfterCopy"] = ex.VerifyAfterCopy,
            },
        };
        _fs.WriteAllTextAtomic(_path, node.ToJsonString(JsonDefaults.Options));
    }

    private static readonly string[] Themes = { "System", "Light", "Dark" };

    private static AppSettings Parse(string text)
    {
        var root = JsonNode.Parse(text, null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (root is not JsonObject obj) throw new JsonException("settings root is not an object");

        int version = Get(obj, "schemaVersion", 0);
        if (version < 1) Migrate0To1(obj);

        var defaults = new AppSettings();
        var s = new AppSettings
        {
            SchemaVersion = CurrentSchemaVersion,
            LastDestination = GetString(obj, "lastDestination"),
            EjectWhenDone = Get(obj, "ejectWhenDone", defaults.EjectWhenDone),
            ConvertToMp4 = Get(obj, "convertToMp4", defaults.ConvertToMp4),
            BatchAskName = Get(obj, "batchAskName", defaults.BatchAskName),
        };
        var theme = GetString(obj, "theme");
        s.Theme = theme != null && Themes.Contains(theme, StringComparer.OrdinalIgnoreCase)
            ? Themes.First(t => string.Equals(t, theme, StringComparison.OrdinalIgnoreCase))
            : defaults.Theme;

        var d = defaults.Extraction;
        var ex = obj["extraction"] as JsonObject ?? new JsonObject();
        int retries = Get(ex, "retriesPerSector", d.RetriesPerSector);
        double timeout = Get(ex, "blockTimeoutSeconds", d.BlockTimeout.TotalSeconds);
        double budget = Get(ex, "secondPassBudgetMinutes", d.SecondPassBudget.TotalMinutes);
        s.Extraction = new ExtractionOptions
        {
            RetriesPerSector = retries is >= 1 and <= 50 ? retries : d.RetriesPerSector,
            BlockTimeout = timeout is >= 1 and <= 600 ? TimeSpan.FromSeconds(timeout) : d.BlockTimeout,
            SecondPassBudget = budget is >= 0 and <= 24 * 60 ? TimeSpan.FromMinutes(budget) : d.SecondPassBudget,
            ReduceSpeedOnError = Get(ex, "reduceSpeedOnError", d.ReduceSpeedOnError),
            VerifyAfterCopy = Get(ex, "verifyAfterCopy", d.VerifyAfterCopy),
        };
        return s;
    }

    /// <summary>Schema 0 (pre-release builds) used "lastFolder" and "eject".</summary>
    private static void Migrate0To1(JsonObject obj)
    {
        if (obj["lastDestination"] == null && obj["lastFolder"] is JsonNode folder) obj["lastDestination"] = folder.DeepClone();
        if (obj["ejectWhenDone"] == null && obj["eject"] is JsonNode eject) obj["ejectWhenDone"] = eject.DeepClone();
    }

    private static string? GetString(JsonObject o, string key)
    {
        try { return o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null; }
        catch (Exception) { return null; }
    }

    private static T Get<T>(JsonObject o, string key, T fallback)
    {
        try { return o[key] is JsonValue v && v.TryGetValue<T>(out var r) ? r : fallback; }
        catch (Exception) { return fallback; }
    }
}
