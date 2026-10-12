using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaRescue.App.Infrastructure;
using MediaRescue.App.Services;
using MediaRescue.Core;

namespace MediaRescue.App.ViewModels;

public sealed class HistoryItemViewModel
{
    public HistoryItemViewModel(CatalogEntry entry)
    {
        Entry = entry;
        Name = entry.Name;
        DateText = Fmt.DateTimeText(entry.ExtractedAtUtc);
        SizeText = Fmt.Bytes(entry.TotalBytes);
        LostText = entry.BytesLost > 0 ? Fmt.Bytes(entry.BytesLost) : "—";
        (StatusText, Tone) = entry.Status switch
        {
            OverallStatus.Intact => ("Íntegro", StatusTone.Ok),
            OverallStatus.Recovered => ("Recuperado", StatusTone.Ok),
            OverallStatus.Partial => ("Com perdas", StatusTone.Warn),
            OverallStatus.Failed => ("Falhou", StatusTone.Danger),
            _ => ("Cancelado", StatusTone.Neutral),
        };
        Path = entry.DestinationPath;
        SearchKey = $"{Name} {DateText} {SizeText} {StatusText} {Path}".ToLowerInvariant();
    }

    public CatalogEntry Entry { get; }
    public string Name { get; }
    public string DateText { get; }
    public string SizeText { get; }
    public string LostText { get; }
    public string StatusText { get; }
    public StatusTone Tone { get; }
    public string Path { get; }
    public string SearchKey { get; }
}

/// <summary>"Histórico" screen (RF-08): searchable list of extracted discs, open folder, export CSV.</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly LogService _log;
    private List<HistoryItemViewModel> _all = new();

    public HistoryViewModel(AppServices services, IDialogService dialogs, IShellService shell, LogService log)
    {
        _s = services;
        _dialogs = dialogs;
        _shell = shell;
        _log = log;
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        OpenFolderCommand = new AsyncRelayCommand<HistoryItemViewModel?>(OpenFolderAsync);
        ExportCommand = new AsyncRelayCommand(ExportCsvAsync, () => _all.Count > 0);
    }

    public BulkObservableCollection<HistoryItemViewModel> Items { get; } = new();
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand<HistoryItemViewModel?> OpenFolderCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private HistoryItemViewModel? _selected;
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string? _warning;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public async Task LoadAsync()
    {
        try
        {
            var data = await Task.Run(() =>
            {
                var entries = _s.Catalog.GetAll().OrderByDescending(e => e.ExtractedAtUtc).Select(e => new HistoryItemViewModel(e)).ToList();
                return (entries, _s.Catalog.LoadWarning);
            });
            _all = data.entries;
            Warning = data.LoadWarning;
        }
        catch (Exception ex)
        {
            _log.Error("Não foi possível ler o histórico: " + ex.Message, ex);
            _all = new List<HistoryItemViewModel>();
            Warning = "Não foi possível ler o histórico. Ele pode estar corrompido; novos discos continuarão sendo registrados.";
        }
        ApplyFilter();
        ExportCommand.NotifyCanExecuteChanged();
    }

    private void ApplyFilter()
    {
        var q = (SearchText ?? "").Trim().ToLowerInvariant();
        var filtered = q.Length == 0 ? _all : _all.Where(i => q.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => i.SearchKey.Contains(w))).ToList();
        Items.ReplaceAll(filtered);
        EmptyText = _all.Count == 0
            ? "Nenhum disco foi extraído ainda. Quando você extrair um disco, ele aparece aqui."
            : filtered.Count == 0 ? $"Nenhum resultado para “{SearchText}”." : "";
        SummaryText = _all.Count == 0 ? "" : q.Length == 0
            ? Fmt.Plural(_all.Count, "disco extraído", "discos extraídos")
            : $"{filtered.Count} de {_all.Count} discos";
    }

    private async Task OpenFolderAsync(HistoryItemViewModel? item)
    {
        item ??= Selected;
        if (item == null) return;
        var path = item.Path;
        var exists = await Task.Run(() => { try { return _s.Fs.DirectoryExists(path); } catch (Exception) { return false; } });
        if (!exists)
        {
            await _dialogs.InfoAsync("A pasta não existe mais",
                $"Esta pasta foi movida, renomeada ou apagada, ou o pendrive não está conectado:{Environment.NewLine}{path}", DialogIcon.Warning);
            return;
        }
        if (!_shell.OpenFolder(path))
            await _dialogs.InfoAsync("Não foi possível abrir a pasta", $"O Windows não conseguiu abrir:{Environment.NewLine}{path}", DialogIcon.Warning);
    }

    public async Task ExportCsvAsync()
    {
        string csv;
        try { csv = await Task.Run(() => _s.Catalog.ExportCsv()); }
        catch (Exception ex)
        {
            _log.Error("Falha ao gerar o CSV: " + ex.Message, ex);
            await _dialogs.InfoAsync("Não foi possível exportar", "O histórico não pôde ser convertido em CSV.", DialogIcon.Error, ex.Message);
            return;
        }
        var target = _shell.PickSaveFile("historico-extrator-de-midias.csv", "Planilha CSV (*.csv)|*.csv");
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            await _shell.WriteAllTextAsync(target, csv);
            _log.Info("Histórico exportado para " + target);
            await _dialogs.InfoAsync("Histórico exportado", $"O arquivo foi salvo em:{Environment.NewLine}{target}", DialogIcon.Success);
        }
        catch (Exception ex)
        {
            _log.Error("Falha ao salvar o CSV: " + ex.Message, ex);
            await _dialogs.InfoAsync("Não foi possível salvar o arquivo",
                "Escolha outra pasta (por exemplo, Documentos) e tente de novo.", DialogIcon.Error, ex.Message);
        }
    }
}
