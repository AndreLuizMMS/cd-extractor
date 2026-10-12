using System.Windows;
using System.Windows.Threading;
using MediaRescue.App.ViewModels;
using MediaRescue.App.Views;
using Serilog;

namespace MediaRescue.App.Infrastructure;

/// <summary>
/// Global unhandled-exception handling: log, release the eject lock and power state, and show a friendly dialog
/// (technical details only under "Detalhes").
/// </summary>
public static class CrashHandler
{
    private static int _showing;

    public static void Install(Application app, Func<MainViewModel?> viewModel, bool failFast)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            Handle(e.Exception, viewModel(), failFast, fatal: false);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString());
            Handle(ex, viewModel(), failFast, fatal: true);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Exceção de tarefa não observada");
            e.SetObserved();
        };
    }

    private static void Handle(Exception ex, MainViewModel? vm, bool failFast, bool fatal)
    {
        try { Log.Error(ex, fatal ? "Exceção fatal não tratada" : "Exceção não tratada"); } catch (Exception) { /* logging is best effort */ }
        try { vm?.Extract.EmergencyCleanup(); } catch (Exception) { /* best effort */ }
        if (failFast || fatal) AppLogging.Shutdown(); // the process is about to end: flush the log
        if (failFast)
        {
            Environment.ExitCode = 1;
            Environment.Exit(1);
        }
        if (Interlocked.Exchange(ref _showing, 1) == 1) return; // do not stack dialogs

        try
        {
            void Show()
            {
                var request = new DialogRequest(
                    "Algo inesperado aconteceu",
                    "O programa encontrou um problema. Os arquivos que já foram copiados estão seguros e os detalhes técnicos foram salvos no registro. " +
                    (fatal ? "O programa precisa ser fechado; abra-o novamente para continuar de onde parou." : "Você pode continuar usando o programa; se o problema se repetir, feche e abra de novo."),
                    DialogIcon.Error,
                    new[]
                    {
                        new DialogButton("log", "Abrir log"),
                        new DialogButton("ok", "OK", IsDefault: true, IsCancel: true),
                    })
                { Details = ex.ToString() };
                var w = new MessageDialogWindow(request);
                var owner = Application.Current?.MainWindow;
                if (owner is { IsVisible: true }) w.Owner = owner;
                else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                w.ShowDialog();
                if (w.Response.Key == "log") new WindowsShellService().OpenLog();
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            if (dispatcher.CheckAccess()) Show();
            else dispatcher.Invoke(Show);
        }
        catch (Exception secondary)
        {
            // Last resort: nothing else to try, but leave a trace.
            try { Log.Error(secondary, "Falha ao mostrar o diálogo de erro"); } catch (Exception) { /* nothing else to try */ }
        }
        finally
        {
            Interlocked.Exchange(ref _showing, 0);
        }
    }
}

