using System.Windows;
using System.Windows.Threading;
using MediaRescue.App.Infrastructure;
using MediaRescue.App.Services;
using MediaRescue.App.ViewModels;

namespace MediaRescue.App;

/// <summary>
/// Startup: command line, logging, crash handler, single instance, composition, main window.
/// Flags: --demo, --demo-state=nodrive|nodisc|ready|copying|partial|converting|converted|diagnostics|history|settings|batch, --demo-files=N,
/// --demo-duplicate, --smoke (open the window, wait until loaded, exit 0; non-zero on any exception).
/// </summary>
public partial class App : Application
{
    private StartupOptions _options = StartupOptions.Parse(Array.Empty<string>());
    private MainViewModel? _vm;
    private AppServices? _services;
    private SingleInstanceGuard? _single;
    private int _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _options = StartupOptions.Parse(e.Args);
        AppLogging.Init();
        Serilog.Log.Information("Extrator de Mídias iniciando (demo={Demo}, estado={State}, smoke={Smoke})", _options.Demo, _options.DemoState, _options.Smoke);
        CrashHandler.Install(this, () => _vm, failFast: _options.Smoke);

        try
        {
            if (!_options.Demo && !_options.Smoke)
            {
                _single = SingleInstanceGuard.TryAcquire();
                if (_single == null)
                {
                    Quit(0); // E-34: the first instance was asked to come to the front
                    return;
                }
                _single.ActivationRequested += () => Dispatcher.InvokeAsync(BringToFront);
            }

            var theme = new WpfThemeService();
            theme.Apply("System");
            var ui = new WpfUiDispatcher(Dispatcher);
            _services = await Task.Run(() => CompositionRoot.Create(_options));
            _vm = new MainViewModel(_services, ui, new WpfDialogService(ui), new WindowsShellService(), theme, _options);

            var window = new MainWindow(_vm, _options);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();

            if (_options.Smoke)
            {
                var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
                watchdog.Tick += (_, _) => FailFast(new TimeoutException("O teste de fumaça não terminou em 60 segundos."));
                watchdog.Start();
            }
        }
        catch (Exception ex)
        {
            FailFast(ex);
        }
    }

    /// <summary>Called by the main window once initialisation (and any demo state) finished.</summary>
    internal void OnMainWindowReady()
    {
        Serilog.Log.Information("Janela principal pronta.");
        if (_options.Smoke) Quit(0);
    }

    internal void FailFast(Exception ex)
    {
        try { Serilog.Log.Fatal(ex, "Falha ao iniciar"); } catch (Exception) { /* logging is best effort */ }
        if (!_options.Smoke)
        {
            try
            {
                MessageBox.Show(
                    "O programa não conseguiu iniciar. Feche-o e abra novamente. Se o problema continuar, abra a pasta de registros em " +
                    AppLogging.LogDirectory + " e envie o arquivo mais recente para quem está ajudando você.\n\nDetalhes: " + ex.Message,
                    "Extrator de Mídias", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception) { /* nothing else to try */ }
        }
        Quit(1);
    }

    private void Quit(int code)
    {
        if (Interlocked.Exchange(ref _exiting, 1) == 1) return;
        Environment.ExitCode = code;
        Shutdown(code);
    }

    private void BringToFront()
    {
        var w = MainWindow;
        if (w == null) return;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = true; // brings it above other windows even when Windows blocks focus stealing
        w.Topmost = false;
        w.Focus();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _vm?.Dispose(); } catch (Exception) { /* exiting */ }
        try { _services?.Dispose(); } catch (Exception) { /* exiting */ }
        try { _single?.Dispose(); } catch (Exception) { /* exiting */ }
        AppLogging.Shutdown();
        base.OnExit(e);
    }
}
