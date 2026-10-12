using System.Collections.Concurrent;
using MediaRescue.App.Infrastructure;
using MediaRescue.App.Services;
using MediaRescue.App.ViewModels;
using MediaRescue.Core;
using MediaRescue.Integration.Tests.Support;
using MediaRescue.Windows;

namespace MediaRescue.Integration.Tests.EndToEnd;

/// <summary>
/// A single "UI thread" with its own SynchronizationContext, like the WPF dispatcher: <c>Post</c> always queues, continuations of
/// awaits made on it come back to it. Lets the ViewModel tests prove that nothing slow runs on the UI thread.
/// </summary>
internal sealed class UiThread : IUiDispatcher, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public int ThreadId { get; private set; }
    public ConcurrentQueue<Exception> Unhandled { get; } = new();

    private sealed class Context : SynchronizationContext
    {
        private readonly UiThread _owner;
        public Context(UiThread owner) => _owner = owner;
        public override void Post(SendOrPostCallback d, object? state) => _owner._queue.Add(() => d(state));
        public override void Send(SendOrPostCallback d, object? state)
        {
            if (_owner.CheckAccess()) { d(state); return; }
            using var done = new ManualResetEventSlim();
            _owner._queue.Add(() => { try { d(state); } finally { done.Set(); } });
            done.Wait();
        }
        public override SynchronizationContext CreateCopy() => this;
    }

    public UiThread()
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            ThreadId = Environment.CurrentManagedThreadId;
            SynchronizationContext.SetSynchronizationContext(new Context(this));
            ready.Set();
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try { action(); } catch (Exception ex) { Unhandled.Enqueue(ex); }
            }
        }) { IsBackground = true, Name = "MediaRescue.TestUi" };
        _thread.Start();
        ready.Wait();
    }

    public bool CheckAccess() => Environment.CurrentManagedThreadId == ThreadId;
    public void Post(Action action) => _queue.Add(action);

    public Task InvokeAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() => { try { action(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } });
        return tcs.Task;
    }

    /// <summary>Runs an async body on the UI thread (all its continuations stay there) and returns when it finishes.</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> body, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(async () =>
        {
            try { tcs.SetResult(await body()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task.WaitAsync(timeout ?? TimeSpan.FromMinutes(8));
    }

    public Task RunAsync(Func<Task> body, TimeSpan? timeout = null) =>
        RunAsync<int>(async () => { await body(); return 0; }, timeout);

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>Answers dialogs from a script (by title); records everything that was shown.</summary>
internal sealed class ScriptedDialogs : IDialogService
{
    private readonly object _gate = new();
    private readonly List<DialogRequest> _shown = new();

    /// <summary>Returns the button key to press; null picks the default button.</summary>
    public Func<DialogRequest, string?> Responder { get; set; } = _ => null;

    public IReadOnlyList<DialogRequest> Shown { get { lock (_gate) return _shown.ToList(); } }
    public bool WasShown(string title) => Shown.Any(d => d.Title == title);

    public Task<DialogResponse> ShowAsync(DialogRequest request)
    {
        lock (_gate) _shown.Add(request);
        var key = Responder(request) ?? request.Buttons.FirstOrDefault(b => b.IsDefault)?.Key ?? request.Buttons[0].Key;
        return Task.FromResult(new DialogResponse(key));
    }

    public Task<NamePromptResult?> PromptNameAsync(NamePromptRequest request) =>
        Task.FromResult<NamePromptResult?>(new NamePromptResult(request.SuggestedName, false));
}

internal sealed class RecordingShell : IShellService
{
    public ConcurrentQueue<SoundKind> Sounds { get; } = new();
    public ConcurrentQueue<string> Opened { get; } = new();
    public bool OpenFolder(string path) { Opened.Enqueue(path); return true; }
    public string? PickFolder(string? initialPath) => null;
    public string? PickSaveFile(string suggestedName, string filter) => null;
    public Task WriteAllTextAsync(string path, string text) => Task.CompletedTask;
    public void CopyToClipboard(string text) { }
    public void PlaySound(SoundKind kind) => Sounds.Enqueue(kind);
    public void OpenLog() { }
}

internal sealed class NoopTheme : IThemeService
{
    public void Apply(string theme) { }
}

/// <summary>Wraps the real keep-awake factory (a real PowerGuard) and counts how many guards are alive.</summary>
internal sealed class KeepAwakeTracker
{
    private readonly Func<IDisposable> _inner;
    private int _begun, _released;
    private int _inactive;

    public KeepAwakeTracker(Func<IDisposable> inner) => _inner = inner;

    public int Begun => Volatile.Read(ref _begun);
    public int Released => Volatile.Read(ref _released);
    public int Held => Begun - Released;
    /// <summary>Guards the OS refused (should be none: the request must really be active while copying).</summary>
    public int InactiveGuards => Volatile.Read(ref _inactive);
    public int MaxHeld { get; private set; }

    public Func<IDisposable> Factory => () =>
    {
        var guard = _inner();
        if (guard is PowerGuard { IsActive: false }) Interlocked.Increment(ref _inactive);
        Interlocked.Increment(ref _begun);
        MaxHeld = Math.Max(MaxHeld, Held);
        return new Releaser(guard, this);
    };

    private sealed class Releaser : IDisposable
    {
        private readonly IDisposable _guard;
        private readonly KeepAwakeTracker _owner;
        private int _done;
        public Releaser(IDisposable guard, KeepAwakeTracker owner) { _guard = guard; _owner = owner; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            _guard.Dispose();
            Interlocked.Increment(ref _owner._released);
        }
    }
}

internal sealed class ThrowingEngine : IExtractionEngine
{
    public bool CanResume(DiscInfo disc, string destinationRoot, string folderName) => false;
    public Task<ExtractionResult> RunAsync(ExtractionRequest request, IProgress<ExtractionProgress> progress, ExtractionControl control, CancellationToken ct) =>
        throw new InvalidOperationException("falha simulada do motor");
}

/// <summary>
/// The App's real object graph built by <see cref="CompositionRoot"/> (real drive service, file system, catalog, settings store,
/// validator, diagnostics, probe, clock, PowerGuard) on a temp data directory, plus observers: the engine is the same class with
/// the same wiring but reads through a counting wrapper and a recording drive service so lock calls and file opens can be asserted.
/// </summary>
internal sealed class VmRig : IDisposable
{
    private readonly List<MountedIso> _mounts = new();
    private readonly AppServices _real;

    public string Work { get; } = TestEnv.NewTempDir("mr-vm");
    public string DataDir { get; }
    public string DestRoot { get; }
    public string IsoDir { get; }

    public UiThread Ui { get; } = new();
    public RecordingDriveService Drives { get; }
    public CountingDiscReader Reader { get; } = new(new DiscReader());
    public ScriptedDialogs Dialogs { get; } = new();
    public RecordingShell Shell { get; } = new();
    public KeepAwakeTracker Awake { get; }
    public AppServices Services { get; }
    public MainViewModel Main { get; private set; } = null!;

    public VmRig(Func<IExtractionEngine, IExtractionEngine>? engineWrap = null)
    {
        DataDir = Path.Combine(Work, "appdata");
        DestRoot = Path.Combine(Work, "destino");
        IsoDir = Path.Combine(Work, "iso");
        Directory.CreateDirectory(DestRoot);
        Directory.CreateDirectory(IsoDir);

        _real = CompositionRoot.Create(StartupOptions.Parse(Array.Empty<string>()), DataDir);
        Drives = new RecordingDriveService(_real.Drives);
        Awake = new KeepAwakeTracker(_real.KeepAwake);
        IExtractionEngine engine = new ExtractionEngine(Reader, _real.Fs, Drives, _real.Clock);
        if (engineWrap != null) engine = engineWrap(engine);

        Services = new AppServices
        {
            Drives = Drives,
            Engine = engine,
            Catalog = _real.Catalog,
            SettingsStore = _real.SettingsStore,
            Validator = _real.Validator,
            Fs = _real.Fs,
            Probe = _real.Probe,
            Diagnostics = _real.Diagnostics,
            Clock = _real.Clock,
            DestinationDrives = _real.DestinationDrives,
            KeepAwake = Awake.Factory,
            Mp4 = _real.Mp4,
        };

        Ui.RunAsync(() =>
        {
            Main = new MainViewModel(Services, Ui, Dialogs, Shell, new NoopTheme(), StartupOptions.Parse(Array.Empty<string>()));
            Main.Extract.AutoValidate = false;
            Main.Extract.ValidationDelay = TimeSpan.Zero;
            return Task.CompletedTask;
        }).GetAwaiter().GetResult();
    }

    public string BuildIso(IsoSpec spec, string name = "disc.iso") => spec.Build(IsoDir, Guid.NewGuid().ToString("N") + "-" + name);

    public MountedIso MountIso(string isoPath)
    {
        var m = MountedIso.Mount(isoPath);
        lock (_mounts) _mounts.Add(m);
        return m;
    }

    /// <summary>Waits (on the UI thread) for a condition; the awaits resume on the UI thread.</summary>
    public static async Task<bool> UiWaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    /// <summary>Mounts the ISO, waits until the running app notices the new drive by itself, selects it and waits for "Disco pronto". Call on the UI thread.</summary>
    public async Task<(MountedIso Mount, string Letter)> MountAndSelectAsync(string isoPath)
    {
        var ext = Main.Extract;
        var m = await Task.Run(() => MountIso(isoPath));
        (await UiWaitUntil(() => ext.Drives.Any(d => d.Letter == m.Letter), TimeSpan.FromSeconds(40)))
            .Should().BeTrue("RF-01: a drive that appears is noticed without restarting the app");
        ext.SelectedDrive = ext.Drives.First(d => d.Letter == m.Letter);
        await ext.RefreshAsync(true);
        (await UiWaitUntil(() => ext.Screen == ScreenState.Ready && ext.Disc != null && !ext.IsAnalyzing, TimeSpan.FromSeconds(90)))
            .Should().BeTrue($"the mounted disc must reach 'Disco pronto' (screen is {ext.Screen})");
        return (m, m.Letter);
    }

    /// <summary>Picks the destination like the user would and waits for the validation; call on the UI thread.</summary>
    public async Task ChooseDestinationAsync(string? path = null)
    {
        var ext = Main.Extract;
        ext.DestinationRoot = path ?? DestRoot;
        await ext.ValidateNowAsync();
    }

    public void Dispose()
    {
        try { Ui.RunAsync(() => { Main.Dispose(); return Task.CompletedTask; }, TimeSpan.FromSeconds(20)).GetAwaiter().GetResult(); } catch { }
        try { _real.Dispose(); } catch { }
        List<MountedIso> mounts;
        lock (_mounts) mounts = _mounts.ToList();
        foreach (var m in mounts) { try { m.Dispose(); } catch { } }
        Ui.Dispose();
        for (int i = 0; i < 20; i++)
        {
            try { Directory.Delete(@"\\?\" + Work, true); break; }
            catch { Thread.Sleep(250); }
        }
    }
}
