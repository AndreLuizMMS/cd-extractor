using CommunityToolkit.Mvvm.ComponentModel;
using MediaRescue.App.Infrastructure;
using MediaRescue.App.Services;
using MediaRescue.Core;

namespace MediaRescue.App.ViewModels;

public sealed record ThemeOption(string Value, string Label);

/// <summary>"Configurações" screen (RF-12). Every change is saved immediately through <see cref="SettingsService"/>.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IUiDispatcher _ui;
    private readonly IMp4Converter? _mp4;
    private bool _loading;
    private bool _verifyAfterCopy = true;

    public SettingsViewModel(SettingsService settings, IDialogService dialogs, IUiDispatcher ui, IMp4Converter? mp4 = null)
    {
        _mp4 = mp4;
        _settings = settings;
        _dialogs = dialogs;
        _ui = ui;
        _settings.Changed += (_, _) => Reload();
        Reload();
    }

    public IReadOnlyList<ThemeOption> ThemeOptions { get; } = new[]
    {
        new ThemeOption("System", "Seguir o Windows"),
        new ThemeOption("Light", "Claro"),
        new ThemeOption("Dark", "Escuro"),
    };

    /// <summary>The most recent confirmation dialog (lets tests await it).</summary>
    public Task PendingConfirmation { get; private set; } = Task.CompletedTask;

    [ObservableProperty] private bool _ejectWhenDone;
    [ObservableProperty] private int _retriesPerSector;
    [ObservableProperty] private int _blockTimeoutSeconds;
    [ObservableProperty] private int _secondPassBudgetMinutes;
    [ObservableProperty] private bool _reduceSpeedOnError;
    [ObservableProperty] private bool _batchAskName;
    [ObservableProperty] private string _theme = "System";
    [ObservableProperty] private bool _convertToMp4;

    /// <summary>False when the converter (ffmpeg) is missing: the option is shown disabled, with an explanation.</summary>
    public bool Mp4Available => _mp4?.IsAvailable == true;

    public string Mp4Note => Mp4Available
        ? "Depois da cópia, o programa cria versões em MP4 (H.264) dos vídeos na subpasta “MP4” da pasta do disco, sempre a partir da cópia no destino, nunca do disco. " +
          "Os arquivos originais são mantidos. A conversão pode demorar e ocupa espaço extra no destino. Você também pode ligar ou desligar isso a cada extração, ao lado do botão Extrair."
        : "Indisponível: o conversor (ffmpeg) não foi encontrado nesta instalação. Copie ffmpeg.exe e ffprobe.exe para a pasta “tools”, ao lado do programa, para usar esta opção.";

    /// <summary>Post-copy verification. Turning it off asks for confirmation first.</summary>
    public bool VerifyAfterCopy
    {
        get => _verifyAfterCopy;
        set
        {
            if (_loading) { SetProperty(ref _verifyAfterCopy, value); return; }
            if (value == _verifyAfterCopy) return;
            if (value)
            {
                _verifyAfterCopy = true;
                OnPropertyChanged();
                _settings.Update(s => s.Extraction.VerifyAfterCopy = true);
                return;
            }
            // Keep it on until the user confirms; refresh the switch afterwards.
            _ui.Post(() => OnPropertyChanged(nameof(VerifyAfterCopy)));
            PendingConfirmation = ConfirmDisableVerificationAsync();
        }
    }

    private async Task ConfirmDisableVerificationAsync()
    {
        var ok = await _dialogs.ConfirmAsync(
            "Desligar a verificação da cópia?",
            "Com a verificação ligada, o programa relê cada arquivo copiado e confere se ele ficou idêntico ao original. " +
            "Sem ela a cópia é mais rápida, mas um pendrive defeituoso pode gravar vídeos corrompidos sem avisar.",
            yesText: "Desligar mesmo assim", noText: "Manter ligada", icon: DialogIcon.Warning, danger: true);
        if (!ok) { OnPropertyChanged(nameof(VerifyAfterCopy)); return; }
        _verifyAfterCopy = false;
        OnPropertyChanged(nameof(VerifyAfterCopy));
        _settings.Update(s => s.Extraction.VerifyAfterCopy = false);
    }

    public void Reload()
    {
        var s = _settings.Current;
        _loading = true;
        try
        {
            EjectWhenDone = s.EjectWhenDone;
            RetriesPerSector = s.Extraction.RetriesPerSector;
            BlockTimeoutSeconds = (int)Math.Round(s.Extraction.BlockTimeout.TotalSeconds);
            SecondPassBudgetMinutes = (int)Math.Round(s.Extraction.SecondPassBudget.TotalMinutes);
            ReduceSpeedOnError = s.Extraction.ReduceSpeedOnError;
            VerifyAfterCopy = s.Extraction.VerifyAfterCopy;
            BatchAskName = s.BatchAskName;
            Theme = string.IsNullOrWhiteSpace(s.Theme) ? "System" : s.Theme;
            ConvertToMp4 = s.ConvertToMp4;
        }
        finally { _loading = false; }
    }

    partial void OnEjectWhenDoneChanged(bool value) { if (!_loading) _settings.Update(s => s.EjectWhenDone = value); }
    partial void OnReduceSpeedOnErrorChanged(bool value) { if (!_loading) _settings.Update(s => s.Extraction.ReduceSpeedOnError = value); }
    partial void OnBatchAskNameChanged(bool value) { if (!_loading) _settings.Update(s => s.BatchAskName = value); }
    partial void OnThemeChanged(string value) { if (!_loading) _settings.Update(s => s.Theme = value); }
    partial void OnConvertToMp4Changed(bool value) { if (!_loading) _settings.Update(s => s.ConvertToMp4 = value); }

    partial void OnRetriesPerSectorChanged(int value)
    {
        var c = Math.Clamp(value, 1, 10);
        if (c != value) { RetriesPerSector = c; return; }
        if (!_loading) _settings.Update(s => s.Extraction.RetriesPerSector = c);
    }

    partial void OnBlockTimeoutSecondsChanged(int value)
    {
        var c = Math.Clamp(value, 5, 300);
        if (c != value) { BlockTimeoutSeconds = c; return; }
        if (!_loading) _settings.Update(s => s.Extraction.BlockTimeout = TimeSpan.FromSeconds(c));
    }

    partial void OnSecondPassBudgetMinutesChanged(int value)
    {
        var c = Math.Clamp(value, 1, 600);
        if (c != value) { SecondPassBudgetMinutes = c; return; }
        if (!_loading) _settings.Update(s => s.Extraction.SecondPassBudget = TimeSpan.FromMinutes(c));
    }
}
