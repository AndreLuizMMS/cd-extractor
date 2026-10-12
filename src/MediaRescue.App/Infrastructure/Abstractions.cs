using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using MediaRescue.App.ViewModels;

namespace MediaRescue.App.Infrastructure;

// ---------------------------------------------------------------------------------------------
// UI-facing abstractions. ViewModels only see these interfaces, so they can be tested without a window.
// ---------------------------------------------------------------------------------------------

public interface IUiDispatcher
{
    bool CheckAccess();
    void Post(Action action);
    Task InvokeAsync(Action action);
}

/// <summary>Runs everything inline. Used by tests and as a fallback.</summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;
    public void Post(Action action) => action();
    public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
}

public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;
    public WpfUiDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;
    public bool CheckAccess() => _dispatcher.CheckAccess();
    public void Post(Action action) => _dispatcher.InvokeAsync(action);
    public Task InvokeAsync(Action action) => _dispatcher.InvokeAsync(action).Task;
}

/// <summary>IProgress that marshals to the UI through the dispatcher abstraction (inline in tests).</summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly IUiDispatcher _ui;
    private readonly Action<T> _handler;
    public SyncProgress(IUiDispatcher ui, Action<T> handler) { _ui = ui; _handler = handler; }
    public void Report(T value) => _ui.Post(() => _handler(value));
}

public interface IThemeService
{
    /// <param name="theme">"System", "Light" or "Dark".</param>
    void Apply(string theme);
}

public enum SoundKind { Success, Warning, Error }

public interface IShellService
{
    /// <summary>Opens a folder in the file explorer. Returns false when it could not be opened.</summary>
    bool OpenFolder(string path);
    /// <summary>Shows the folder picker. Null when cancelled.</summary>
    string? PickFolder(string? initialPath);
    /// <summary>Shows the save dialog. Null when cancelled.</summary>
    string? PickSaveFile(string suggestedName, string filter);
    Task WriteAllTextAsync(string path, string text);
    void CopyToClipboard(string text);
    void PlaySound(SoundKind kind);
    /// <summary>Opens the current log file (or the log folder).</summary>
    void OpenLog();
}

/// <summary>Lists candidate destination roots (fixed, removable and network drives; never optical).</summary>
public interface IDestinationDriveLister
{
    IReadOnlyList<string> ListRoots();
}

// ---------------------------------------------------------------------------------------------
// Dialogs
// ---------------------------------------------------------------------------------------------

public enum DialogIcon { Info, Success, Warning, Error, Question }

public sealed record DialogButton(string Key, string Text, bool IsDefault = false, bool IsCancel = false, bool IsDanger = false);

public sealed record DialogRequest(string Title, string Message, DialogIcon Icon, IReadOnlyList<DialogButton> Buttons)
{
    /// <summary>Technical details (Win32 codes, stack traces) shown under a "Detalhes" expander.</summary>
    public string? Details { get; init; }
    public string? CheckBoxText { get; init; }
}

public sealed record DialogResponse(string Key, bool IsChecked = false);

public sealed record NamePromptRequest(string Title, string Message, string SuggestedName, bool OfferDontAsk);
public sealed record NamePromptResult(string Name, bool DontAskAgain);

public interface IDialogService
{
    Task<DialogResponse> ShowAsync(DialogRequest request);
    /// <summary>Asks for the disc folder name. Null when the user cancels.</summary>
    Task<NamePromptResult?> PromptNameAsync(NamePromptRequest request);
}

public static class DialogExtensions
{
    public static async Task<bool> ConfirmAsync(this IDialogService dialogs, string title, string message, string yesText,
        string noText = "Cancelar", DialogIcon icon = DialogIcon.Question, bool danger = false, string? details = null)
    {
        var result = await dialogs.ShowAsync(new DialogRequest(title, message, icon, new[]
        {
            new DialogButton("yes", yesText, IsDefault: !danger, IsDanger: danger),
            new DialogButton("no", noText, IsDefault: danger, IsCancel: true),
        }) { Details = details });
        return result.Key == "yes";
    }

    public static Task<DialogResponse> InfoAsync(this IDialogService dialogs, string title, string message,
        DialogIcon icon = DialogIcon.Info, string? details = null) =>
        dialogs.ShowAsync(new DialogRequest(title, message, icon, new[] { new DialogButton("ok", "OK", IsDefault: true, IsCancel: true) })
        { Details = details });
}

// ---------------------------------------------------------------------------------------------
// Small helpers
// ---------------------------------------------------------------------------------------------

/// <summary>ObservableCollection with a single-notification bulk replace (keeps big lists fast).</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>Attached property that lets styles colour elements by <see cref="StatusTone"/>.</summary>
public static class Ui
{
    public static readonly DependencyProperty ToneProperty = DependencyProperty.RegisterAttached(
        "Tone", typeof(StatusTone), typeof(Ui), new PropertyMetadata(StatusTone.Neutral));

    public static StatusTone GetTone(DependencyObject o) => (StatusTone)o.GetValue(ToneProperty);
    public static void SetTone(DependencyObject o, StatusTone value) => o.SetValue(ToneProperty, value);
}

