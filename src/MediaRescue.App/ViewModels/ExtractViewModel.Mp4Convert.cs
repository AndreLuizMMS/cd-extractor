using CommunityToolkit.Mvvm.ComponentModel;
using MediaRescue.App.Infrastructure;
using MediaRescue.Core;

namespace MediaRescue.App.ViewModels;

// RF-11: optional conversion to MP4 after the copy. It reads only the copy in the destination, so the disc can already be
// ejected; a failure or cancellation here never changes the result of the extraction.
public sealed partial class ExtractViewModel
{
    private CancellationTokenSource? _mp4Cts;
    private bool _lastMp4Setting;
    private bool _closingDuringMp4;

    /// <summary>Progress card and summary of the conversion phase.</summary>
    public Mp4ConversionViewModel Mp4 { get; } = new();

    /// <summary>Per-extraction choice (the checkbox beside "Extrair disco"). Starts as the "Converter para MP4" setting.</summary>
    [ObservableProperty] private bool _convertThisExtraction;

    /// <summary>True when the converter (ffmpeg) is present; the checkbox is only offered then.</summary>
    public bool Mp4Available => _s.Mp4.IsAvailable;

    /// <summary>The checkbox can be changed (not while a conversion is running).</summary>
    public bool Mp4OptionEnabled => Mp4Available && !Mp4.IsActive;

    private void InitializeMp4()
    {
        Mp4.CancelRequested = CancelMp4;
        Mp4.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Mp4ConversionViewModel.IsActive)) return;
            OnPropertyChanged(nameof(Mp4OptionEnabled));
            UpdateGate();
        };
        _lastMp4Setting = _settings.Current.ConvertToMp4;
        ConvertThisExtraction = _lastMp4Setting && Mp4Available;
        _settings.Changed += (_, _) => OnSettingsChangedForMp4();
    }

    private void OnSettingsChangedForMp4()
    {
        var now = _settings.Current.ConvertToMp4;
        if (now == _lastMp4Setting) return;
        _lastMp4Setting = now;
        if (!Mp4.IsActive) ConvertThisExtraction = now && Mp4Available;
    }

    private void CancelMp4()
    {
        var cts = _mp4Cts;
        if (cts == null || Mp4.IsCancelling) return;
        Mp4.IsCancelling = true;
        Mp4.StatusText = "Cancelando a conversão…";
        _log.Info("Cancelando a conversão para MP4…");
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Runs the conversion when the user asked for it. Never throws; the extraction result is not touched.</summary>
    private async Task RunMp4PhaseAsync(ExtractionResult result)
    {
        if (!ConvertThisExtraction || !Mp4Available) return;
        if (result.Status is OverallStatus.Failed or OverallStatus.Cancelled || result.Files.Count == 0) return;

        var cts = _mp4Cts = new CancellationTokenSource();
        Mp4.Begin();
        IDisposable? awake = null;
        try { awake = _s.KeepAwake(); }
        catch (Exception ex) { _log.Warn("Não foi possível impedir a suspensão do computador: " + ex.Message); }
        _log.Info("Convertendo os vídeos para MP4…");

        Mp4ConversionResult conversion;
        try
        {
            var progress = new SyncProgress<Mp4ConversionProgress>(_ui, OnMp4Progress);
            var folder = result.DiscFolder;
            var files = result.Files;
            var ct = cts.Token;
            conversion = await Task.Run(() => _s.Mp4.ConvertAsync(folder, files, progress, ct));
        }
        catch (OperationCanceledException) { conversion = new Mp4ConversionResult { WasCancelled = true }; }
        catch (Exception ex)
        {
            _log.Error("A conversão para MP4 terminou com um erro inesperado.", ex);
            conversion = new Mp4ConversionResult { Error = "Ocorreu um erro inesperado durante a conversão." };
        }
        finally
        {
            try { awake?.Dispose(); } catch (Exception) { /* nothing to release */ }
            _mp4Cts = null;
            cts.Dispose();
            Progress.Visual = ProgressVisual.None;
            Progress.TaskbarValue = 0;
            Mp4.End();
        }

        var (text, tone) = Mp4ConversionSummary.Describe(conversion);
        Mp4.SetSummary(text, tone);
        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (tone is StatusTone.Warn or StatusTone.Danger) _log.Warn(line); else _log.Info(line);
        }
        if (!_closingDuringMp4 && !conversion.WasCancelled)
            _shell.PlaySound(tone switch { StatusTone.Ok => SoundKind.Success, StatusTone.Neutral => SoundKind.Success, StatusTone.Warn => SoundKind.Warning, _ => SoundKind.Error });
    }

    private void OnMp4Progress(Mp4ConversionProgress p)
    {
        if (!Mp4.IsActive) return;
        Mp4.Update(p);
        Progress.Visual = ProgressVisual.Normal;
        Progress.TaskbarValue = Mp4.Fraction;
        if (!string.IsNullOrWhiteSpace(p.Message)) _log.Info(p.Message!);
    }

    partial void OnResultChanged(ResultSummary? value)
    {
        if (value == null) Mp4.ClearSummary(); // a new extraction (or disc) starts: forget the previous conversion text
    }

    /// <summary>Window closing during a conversion: stops ffmpeg (it would otherwise outlive the app). True when it was running.</summary>
    private async Task<bool> StopMp4ForCloseAsync()
    {
        if (!Mp4.IsActive) return false;
        _closingDuringMp4 = true;
        CancelMp4();
        var running = _runningExtraction;
        if (running != null) await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(8)));
        return true;
    }
}
