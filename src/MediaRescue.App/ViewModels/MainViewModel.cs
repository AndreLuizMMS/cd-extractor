using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MediaRescue.App.Infrastructure;
using MediaRescue.App.Services;

namespace MediaRescue.App.ViewModels;

public enum AppPage { Extract, History, Diagnostics, Settings }

/// <summary>Window-level ViewModel: navigation, theme, and the four screens.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly IThemeService _theme;
    private readonly IShellService _shell;
    private string? _appliedTheme;

    public MainViewModel(AppServices services, IUiDispatcher ui, IDialogService dialogs, IShellService shell,
        IThemeService theme, StartupOptions options)
    {
        _services = services;
        _theme = theme;
        _shell = shell;
        Options = options;

        Settings = new SettingsService(services.SettingsStore);
        Log = new LogService(ui, services.Clock);
        Extract = new ExtractViewModel(services, Settings, ui, dialogs, shell, Log);
        History = new HistoryViewModel(services, dialogs, shell, Log);
        Diagnostics = new DiagnosticsViewModel(services, Extract, ui, shell, Log);
        SettingsPage = new SettingsViewModel(Settings, dialogs, ui, services.Mp4);

        Extract.CatalogChanged += (_, _) => _ = History.LoadAsync();
        Settings.Changed += (_, _) => ApplyTheme();
        OpenLogCommand = new RelayCommand(_shell.OpenLog);
        GoExtractCommand = new RelayCommand(() => Navigate(AppPage.Extract));
        GoHistoryCommand = new RelayCommand(() => Navigate(AppPage.History));
        GoDiagnosticsCommand = new RelayCommand(() => Navigate(AppPage.Diagnostics));
        GoSettingsCommand = new RelayCommand(() => Navigate(AppPage.Settings));
    }

    public StartupOptions Options { get; }
    public SettingsService Settings { get; }
    public LogService Log { get; }
    public ExtractViewModel Extract { get; }
    public HistoryViewModel History { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public SettingsViewModel SettingsPage { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand GoExtractCommand { get; }
    public RelayCommand GoHistoryCommand { get; }
    public RelayCommand GoDiagnosticsCommand { get; }
    public RelayCommand GoSettingsCommand { get; }

    public string Title => "Extrator de Mídias";

    [ObservableProperty] private AppPage _page = AppPage.Extract;

    partial void OnPageChanged(AppPage value)
    {
        OnPropertyChanged(nameof(IsExtractPage));
        OnPropertyChanged(nameof(IsHistoryPage));
        OnPropertyChanged(nameof(IsDiagnosticsPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        if (value == AppPage.History) _ = History.LoadAsync();
    }

    public bool IsExtractPage { get => Page == AppPage.Extract; set { if (value) Page = AppPage.Extract; } }
    public bool IsHistoryPage { get => Page == AppPage.History; set { if (value) Page = AppPage.History; } }
    public bool IsDiagnosticsPage { get => Page == AppPage.Diagnostics; set { if (value) Page = AppPage.Diagnostics; } }
    public bool IsSettingsPage { get => Page == AppPage.Settings; set { if (value) Page = AppPage.Settings; } }

    public void Navigate(AppPage page) => Page = page;

    /// <summary>Loads settings, applies the theme, then starts reader detection.</summary>
    public async Task InitializeAsync()
    {
        await Settings.LoadAsync();
        ApplyTheme();
        Log.Info("Extrator de Mídias iniciado.");
        await Extract.InitializeAsync();
    }

    private void ApplyTheme()
    {
        var t = Settings.Current.Theme;
        if (string.IsNullOrWhiteSpace(t)) t = "System";
        if (t == _appliedTheme) return;
        _appliedTheme = t;
        _theme.Apply(t);
    }

    /// <summary>Asks for confirmation when a copy is running; true when the window may close.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!await Extract.ConfirmCloseAsync()) return false;
        try { await Settings.FlushAsync(); } catch (Exception) { /* closing anyway */ }
        return true;
    }

    public void Dispose()
    {
        Extract.Dispose();
    }
}
