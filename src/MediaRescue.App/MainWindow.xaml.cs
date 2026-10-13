using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using MediaRescue.App.Demo;
using MediaRescue.App.Infrastructure;
using MediaRescue.App.ViewModels;

namespace MediaRescue.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly StartupOptions _options;
    private bool _closeConfirmed;

    public MainWindow(MainViewModel vm, StartupOptions options)
    {
        _vm = vm;
        _options = options;
        DataContext = vm;
        InitializeComponent();
        ApplyDemoSize();

        SourceInitialized += (_, _) => WpfThemeService.ApplyTitleBar(this);
        Loaded += OnLoaded;
        Closing += OnClosing;
        vm.Log.Lines.CollectionChanged += OnLogChanged;
    }

    /// <summary>Demo only (MEDIARESCUE_DEMO_SIZE=1100x700): lets screenshots be taken at the minimum window size.</summary>
    private void ApplyDemoSize()
    {
        if (!_options.Demo) return;
        var s = Environment.GetEnvironmentVariable("MEDIARESCUE_DEMO_SIZE")?.Split('x');
        if (s is { Length: 2 } && double.TryParse(s[0], out var w) && double.TryParse(s[1], out var h)) { Width = w; Height = h; }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.InitializeAsync();
            if (_options.DemoState != null) await DemoStateApplier.ApplyAsync(_vm, _options);
            InjectDemoFault();
            ((App)Application.Current).OnMainWindowReady();
        }
        catch (Exception ex)
        {
            ((App)Application.Current).FailFast(ex);
        }
    }

    /// <summary>
    /// Demo only (MEDIARESCUE_DEMO_FAULT=load|ui): proves the failure paths. "load" throws while starting up,
    /// "ui" throws inside a dispatcher callback (global exception handler). With --smoke both end with a non-zero exit code.
    /// </summary>
    private void InjectDemoFault()
    {
        if (!_options.Demo) return;
        switch (Environment.GetEnvironmentVariable("MEDIARESCUE_DEMO_FAULT"))
        {
            case "load": throw new InvalidOperationException("Falha simulada durante a inicialização.");
            case "ui": Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("Falha simulada na interface."))); break;
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || LogList.Items.Count == 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
        });
    }

    // Closing is synchronous, the confirmation is not: cancel first, then close again once confirmed.
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed || _options.Smoke) return;
        e.Cancel = true;
        try
        {
            if (await _vm.ConfirmCloseAsync())
            {
                _closeConfirmed = true;
                CloseDeferred();
            }
        }
        catch (Exception)
        {
            _closeConfirmed = true;
            CloseDeferred();
        }
    }

    // Close() throws when called from inside the Closing event, which happens whenever the confirmation
    // completes synchronously (nothing in progress). Posting it lets the cancelled close finish first.
    private void CloseDeferred() => Dispatcher.BeginInvoke(new Action(Close));
}

