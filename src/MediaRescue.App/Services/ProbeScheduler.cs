using MediaRescue.App.Infrastructure;
using MediaRescue.App.ViewModels;
using MediaRescue.Core;

namespace MediaRescue.App.Services;

/// <summary>
/// Probes videos with ffprobe one at a time, in the background (RF-02). Pausable (paused while a copy runs, the
/// in-flight probe is cancelled and redone later) and cancellable. Never touches the UI thread except to post results.
/// </summary>
public sealed class ProbeScheduler : IDisposable
{
    private readonly IMediaProbe _probe;
    private readonly IUiDispatcher _ui;
    private readonly object _lock = new();
    private CancellationTokenSource? _session;
    private CancellationTokenSource? _itemCts;
    private volatile bool _paused;
    private Task _loop = Task.CompletedTask;

    public ProbeScheduler(IMediaProbe probe, IUiDispatcher ui) { _probe = probe; _ui = ui; }

    public bool IsAvailable => _probe.IsAvailable;
    public bool IsPaused => _paused;
    /// <summary>The currently running loop (completed when idle). Exposed for tests.</summary>
    public Task Completion => _loop;
    /// <summary>(done, total) — raised on the UI thread.</summary>
    public event Action<int, int>? ProgressChanged;

    public void Start(string driveLetter, IReadOnlyList<ContentItemViewModel> candidates)
    {
        Cancel();
        if (!_probe.IsAvailable || candidates.Count == 0) { ProgressChanged?.Invoke(0, 0); return; }
        var cts = new CancellationTokenSource();
        lock (_lock) _session = cts;
        var items = candidates.ToList();
        var letter = driveLetter.TrimEnd('\\');
        _loop = Task.Run(() => RunAsync(letter, items, cts.Token));
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (!paused) return;
        lock (_lock) _itemCts?.Cancel(); // the item will be retried after resuming
    }

    public void Cancel()
    {
        CancellationTokenSource? s;
        lock (_lock) { s = _session; _session = null; }
        try { s?.Cancel(); } catch { /* already disposed */ }
    }

    private async Task RunAsync(string letter, List<ContentItemViewModel> items, CancellationToken ct)
    {
        int done = 0;
        _ui.Post(() => ProgressChanged?.Invoke(0, items.Count));
        foreach (var item in items)
        {
            VideoProbe? result = null;
            bool finished = false;
            while (!finished && !ct.IsCancellationRequested)
            {
                while (_paused && !ct.IsCancellationRequested)
                {
                    try { await Task.Delay(150, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                }
                if (ct.IsCancellationRequested) return;

                using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                lock (_lock) { _itemCts = itemCts; if (_paused) itemCts.Cancel(); }
                try
                {
                    var path = letter + "\\" + item.RelativePath;
                    result = await _probe.ProbeAsync(path, itemCts.Token).ConfigureAwait(false);
                    finished = true;
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested) return;
                    // paused: loop around and wait
                }
                catch
                {
                    finished = true; // E-28: no metadata for this file, nothing else is affected
                }
                finally
                {
                    lock (_lock) _itemCts = null;
                }
            }
            if (ct.IsCancellationRequested) return;
            done++;
            var probe = result;
            var d = done;
            _ui.Post(() =>
            {
                if (ct.IsCancellationRequested) return;
                if (probe != null) item.ApplyProbe(probe);
                ProgressChanged?.Invoke(d, items.Count);
            });
        }
    }

    public void Dispose() => Cancel();
}

