using MediaRescue.App.Infrastructure;
using MediaRescue.App.ViewModels;
using MediaRescue.Core;

namespace MediaRescue.App.Demo;

/// <summary>Jumps straight to a demo state (--demo-state=...) so screenshots can be taken.</summary>
public static class DemoStateApplier
{
    public static async Task ApplyAsync(MainViewModel vm, StartupOptions options)
    {
        switch (options.DemoState)
        {
            case "history":
                vm.Navigate(AppPage.History);
                await vm.History.LoadAsync();
                break;

            case "batch":
                // Not in smoke mode: batch mode starts by itself and asks for the disc name (a modal dialog).
                if (!options.Smoke) vm.Extract.Batch.IsEnabled = true;
                break;

            case "settings":
                vm.Navigate(AppPage.Settings);
                break;

            case "diagnostics":
                vm.Navigate(AppPage.Diagnostics);
                vm.Diagnostics.FullScan = true;
                if (!options.Smoke) _ = Safe(vm, () => vm.Diagnostics.RunAsync());
                break;

            case "copying":
            case "partial":
                if (!options.Smoke) _ = Safe(vm, () => StartCopyAsync(vm));
                break;

            case "converting":
            case "converted":
                // Copy with the "convert to MP4" box ticked; the demo converter then shows its progress card / result text.
                if (!options.Smoke)
                {
                    vm.Extract.ConvertThisExtraction = vm.Extract.Mp4Available;
                    _ = Safe(vm, () => StartCopyAsync(vm));
                }
                break;
        }
    }

    private static async Task StartCopyAsync(MainViewModel vm)
    {
        // Wait for the disc analysis and destination validation to finish, then press "Extrair" like a user would.
        for (int i = 0; i < 100 && !vm.Extract.CanExtract; i++)
        {
            await Task.Delay(100);
            if (vm.Extract.Screen == ScreenState.Ready && !vm.Extract.IsValidating) await vm.Extract.ValidateNowAsync();
        }
        if (vm.Extract.CanExtract) await vm.Extract.ExtractCommand.ExecuteAsync(null);
    }

    private static async Task Safe(MainViewModel vm, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { vm.Log.Error("Erro na demonstração: " + ex.Message, ex); }
    }
}


