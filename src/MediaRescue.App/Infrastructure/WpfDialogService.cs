using System.Windows;
using MediaRescue.App.Views;

namespace MediaRescue.App.Infrastructure;

/// <summary>Shows the modal dialogs on the UI thread (the message loop keeps running, so progress keeps updating).</summary>
public sealed class WpfDialogService : IDialogService
{
    private readonly IUiDispatcher _ui;
    public WpfDialogService(IUiDispatcher ui) => _ui = ui;

    private static Window? Owner
    {
        get
        {
            var app = Application.Current;
            if (app == null) return null;
            var active = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);
            var owner = active ?? app.MainWindow;
            return owner is { IsVisible: true } ? owner : null;
        }
    }

    public Task<DialogResponse> ShowAsync(DialogRequest request)
    {
        if (!_ui.CheckAccess())
        {
            var tcs = new TaskCompletionSource<DialogResponse>();
            _ui.Post(() =>
            {
                try { tcs.SetResult(ShowCore(request)); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }
        return Task.FromResult(ShowCore(request));
    }

    public Task<NamePromptResult?> PromptNameAsync(NamePromptRequest request)
    {
        if (!_ui.CheckAccess())
        {
            var tcs = new TaskCompletionSource<NamePromptResult?>();
            _ui.Post(() =>
            {
                try { tcs.SetResult(PromptCore(request)); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }
        return Task.FromResult(PromptCore(request));
    }

    private static DialogResponse ShowCore(DialogRequest request)
    {
        var w = new MessageDialogWindow(request);
        var owner = Owner;
        if (owner != null && !ReferenceEquals(owner, w)) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowDialog();
        return w.Response;
    }

    private static NamePromptResult? PromptCore(NamePromptRequest request)
    {
        var w = new NamePromptWindow(request);
        var owner = Owner;
        if (owner != null) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return w.ShowDialog() == true ? w.Result : null;
    }
}
