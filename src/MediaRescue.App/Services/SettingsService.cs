using MediaRescue.Core;

namespace MediaRescue.App.Services;

/// <summary>
/// Holds the live <see cref="AppSettings"/> shared by every ViewModel and persists changes through
/// <see cref="ISettingsStore"/> off the UI thread (saves are serialised).
/// </summary>
public sealed class SettingsService
{
    private readonly ISettingsStore _store;
    private readonly object _lock = new();
    private Task _saveChain = Task.CompletedTask;

    public SettingsService(ISettingsStore store) { _store = store; }

    public AppSettings Current { get; private set; } = new();
    public event EventHandler? Changed;
    public Exception? LastSaveError { get; private set; }

    public async Task LoadAsync()
    {
        AppSettings loaded;
        try { loaded = await Task.Run(() => _store.Load()) ?? new AppSettings(); }
        catch { loaded = new AppSettings(); }
        Current = loaded;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Action<AppSettings> mutate)
    {
        mutate(Current);
        Changed?.Invoke(this, EventArgs.Empty);
        QueueSave();
    }

    public Task FlushAsync()
    {
        lock (_lock) return _saveChain;
    }

    private void QueueSave()
    {
        var snapshot = Clone(Current);
        lock (_lock)
        {
            _saveChain = _saveChain.ContinueWith(_ =>
            {
                try { _store.Save(snapshot); LastSaveError = null; }
                catch (Exception ex) { LastSaveError = ex; }
            }, TaskScheduler.Default);
        }
    }

    public static AppSettings Clone(AppSettings s) => new()
    {
        SchemaVersion = s.SchemaVersion,
        LastDestination = s.LastDestination,
        EjectWhenDone = s.EjectWhenDone,
        ConvertToMp4 = s.ConvertToMp4,
        BatchAskName = s.BatchAskName,
        Theme = s.Theme,
        Extraction = CloneOptions(s.Extraction),
    };

    public static ExtractionOptions CloneOptions(ExtractionOptions o) => new()
    {
        RetriesPerSector = o.RetriesPerSector,
        BlockTimeout = o.BlockTimeout,
        SecondPassBudget = o.SecondPassBudget,
        ReduceSpeedOnError = o.ReduceSpeedOnError,
        VerifyAfterCopy = o.VerifyAfterCopy,
    };
}
