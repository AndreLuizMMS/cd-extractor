using CommunityToolkit.Mvvm.ComponentModel;
using MediaRescue.App.Infrastructure;
using MediaRescue.App.Services;
using MediaRescue.Core;

namespace MediaRescue.App.ViewModels;

/// <summary>What happened when a batch/UI extraction was requested.</summary>
public sealed record ExtractionOutcome(bool Started, ExtractionResult? Result, string? NotStartedReason, bool Ejected = false)
{
    public OverallStatus? Status => Result?.Status;
    public static ExtractionOutcome NotStarted(string reason) => new(false, null, reason);
}

// Running an extraction: start, progress, pause/skip/insist, cancel, completion, close confirmation.
public sealed partial class ExtractViewModel
{
    private ExtractionControl? _control;
    private CancellationTokenSource? _extractionCts;
    private IDisposable? _awake;
    private string? _extractionLetter;
    private bool _userPaused;
    private bool _discardOnCancel;
    private string? _lastLogged;
    private Task<ExtractionOutcome>? _runningExtraction;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isExtracting;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasResult))] private ResultSummary? _result;

    [ObservableProperty] private string _extractionTarget = "";

    public bool IsIdle => !IsExtracting;
    public bool HasResult => Result != null;
    /// <summary>The extraction currently running (completed task when idle). Lets tests await it.</summary>
    public Task<ExtractionOutcome>? RunningExtraction => _runningExtraction;

    partial void OnIsExtractingChanged(bool value)
    {
        NotifyCommands();
        UpdatePresentation();
    }

    private Task ExtractFromUiAsync() => RunExtractionAsync(batch: false);

    /// <summary>
    /// Starts the extraction of the selected items. <paramref name="batch"/> suppresses the end dialog and always ejects.
    /// Returns when the copy has finished (or could not start).
    /// </summary>
    public Task<ExtractionOutcome> RunExtractionAsync(bool batch, string? nameOverride = null)
    {
        var task = RunExtractionCoreAsync(batch, nameOverride);
        _runningExtraction = task;
        return task;
    }

    private async Task<ExtractionOutcome> RunExtractionCoreAsync(bool batch, string? nameOverride)
    {
        if (IsExtracting || Mp4.IsActive) return ExtractionOutcome.NotStarted("Já existe uma cópia em andamento.");
        var drive = SelectedDrive;
        var disc = Disc;
        if (drive == null || disc == null || Screen != ScreenState.Ready)
            return ExtractionOutcome.NotStarted("O disco não está pronto.");

        var root = (DestinationRoot ?? "").Trim();
        var requested = CleanName(nameOverride ?? FolderName);
        var entries = Content.GetSelectedEntries();
        if (root.Length == 0 || requested.Length == 0 || entries.Count == 0)
            return ExtractionOutcome.NotStarted("Faltam informações para começar.");

        IsExtracting = true; // claim the screen while prompts are open
        bool executed = false;
        ExtractionResult? result = null;
        Exception? failure = null;
        string folder = requested;
        try
        {
            // RF-04 / RF-10: never overwrite or merge; offer to resume an interrupted job.
            var (canResume, unique) = await Task.Run<(bool Resume, string Unique)>(() =>
            {
                bool resume;
                try { resume = _s.Engine.CanResume(disc, root, requested); } catch (Exception) { resume = false; }
                var u = NameSanitizer.MakeUnique(requested, n => SafeDirExists(Path.Combine(root, n)));
                return (resume, u);
            });
            folder = unique;
            if (canResume)
            {
                var choice = await ShowResumePromptAsync(requested);
                if (choice == ResumeChoice.Cancel) return ExtractionOutcome.NotStarted("O usuário cancelou.");
                folder = choice == ResumeChoice.Continue ? requested : unique;
            }

            executed = true;
            var options = SettingsService.CloneOptions(_settings.Current.Extraction);
            var request = new ExtractionRequest(drive.Drive, disc, root, folder, entries, options);
            _settings.Update(s => s.LastDestination = root);

            var control = _control = new ExtractionControl();
            var cts = _extractionCts = new CancellationTokenSource();
            _userPaused = false;
            _discardOnCancel = false;
            _lastLogged = null;
            _extractionLetter = drive.Letter;
            ExtractionTarget = Path.Combine(root, folder);
            Result = null;
            Progress.Begin();
            _probe.SetPaused(true);
            if (Content.ProbeStatus.Length > 0) Content.ProbeStatus = "As informações dos vídeos continuam depois da cópia.";
            try { _awake = _s.KeepAwake(); }
            catch (Exception ex) { _log.Warn("Não foi possível impedir a suspensão do computador: " + ex.Message); }
            _log.Info($"Iniciando a cópia para “{Path.Combine(root, folder)}” ({Fmt.Plural(entries.Count, "item", "itens")}).");
            NotifyCommands();

            var progress = new SyncProgress<ExtractionProgress>(_ui, OnProgress);
            var ct = cts.Token;
            try { result = await Task.Run(() => _s.Engine.RunAsync(request, progress, control, ct)); }
            catch (OperationCanceledException)
            {
                result = new ExtractionResult(Path.Combine(root, folder), OverallStatus.Cancelled, Array.Empty<FileResult>(), TimeSpan.Zero);
            }
            catch (Exception ex) { failure = ex; }
        }
        finally
        {
            try { _awake?.Dispose(); } catch (Exception) { /* nothing to release */ }
            _awake = null;
            if (!executed)
            {
                IsExtracting = false;
                UpdateGate();
            }
        }

        // ----- completion (executed == true) -----
        IsExtracting = false;
        Progress.End();
        _extractionCts?.Dispose();
        _extractionCts = null;
        _control = null;
        _extractionLetter = null;
        _userPaused = false;

        if (failure != null || result == null)
        {
            _log.Error("A cópia terminou com um erro inesperado.", failure);
            _shell.PlaySound(SoundKind.Error);
            ResumeProbing();
            await _dialogs.ShowAsync(new DialogRequest(
                "A cópia não terminou",
                "Algo inesperado aconteceu durante a cópia. Os arquivos que já foram copiados continuam na pasta de destino. " +
                "Tente extrair o disco novamente: o programa continua de onde parou.",
                DialogIcon.Error,
                new[] { new DialogButton("ok", "OK", IsDefault: true, IsCancel: true) })
            { Details = failure?.ToString() });
            return new ExtractionOutcome(true, null, failure?.Message);
        }

        if (result.Status == OverallStatus.Cancelled)
        {
            int removed = 0;
            if (_discardOnCancel) removed = await DiscardCopiedAsync(result);
            _log.Info(_discardOnCancel
                ? $"Cópia cancelada. {Fmt.Plural(removed, "arquivo copiado foi apagado", "arquivos copiados foram apagados")}."
                : "Cópia cancelada. Os arquivos já copiados foram mantidos.");
            ResumeProbing();
            return new ExtractionOutcome(true, result, null);
        }

        var summary = ResultSummary.From(result);
        Result = summary;
        _log.Info(summary.Headline);
        await AddToCatalogAsync(folder, disc, result);
        _shell.PlaySound(summary.Tone switch { StatusTone.Ok => SoundKind.Success, StatusTone.Warn => SoundKind.Warning, _ => SoundKind.Error });

        bool ejected = false;
        if (batch || _settings.Current.EjectWhenDone) ejected = await EjectAsync(silent: batch);
        else ResumeProbing();

        if (!batch) await RunMp4PhaseAsync(result); // RF-11, optional; reads the copy, so the disc may already be out
        if (!batch) await ShowSummaryDialogAsync(summary, ejected);
        return new ExtractionOutcome(true, result, null, ejected);
    }

    private void ResumeProbing() => _probe.SetPaused(false);

    private void OnProgress(ExtractionProgress p)
    {
        if (!IsExtracting) return;
        Progress.Update(p, _userPaused);
        if (!string.IsNullOrWhiteSpace(p.Message) && p.Message != _lastLogged)
        {
            _lastLogged = p.Message;
            _log.Info(p.Message!);
        }
    }

    private async Task AddToCatalogAsync(string folderName, DiscInfo disc, ExtractionResult result)
    {
        var entry = new CatalogEntry
        {
            Fingerprint = disc.Fingerprint,
            Name = folderName,
            ExtractedAtUtc = _s.Clock.UtcNow,
            DestinationPath = result.DiscFolder,
            TotalBytes = result.Files.Sum(f => f.BytesTotal),
            BytesLost = result.Files.Sum(f => f.BytesLost),
            FileCount = result.Files.Count,
            Status = result.Status,
        };
        try
        {
            await Task.Run(() => _s.Catalog.Add(entry));
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Warn("Não foi possível atualizar o histórico: " + ex.Message);
        }
    }

    private async Task<int> DiscardCopiedAsync(ExtractionResult result)
    {
        return await Task.Run(() =>
        {
            int removed = 0;
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in result.Files)
            {
                if (f.Status is FileStatus.Pending or FileStatus.Skipped) continue;
                try
                {
                    // Only files this job wrote (reported by the engine) are removed; nothing else is touched.
                    var p = Path.Combine(result.DiscFolder, f.WrittenPath);
                    if (_s.Fs.FileExists(p)) { _s.Fs.Delete(p); removed++; }
                    for (var dir = Path.GetDirectoryName(p);
                         !string.IsNullOrEmpty(dir) && dir.Length > result.DiscFolder.Length && dir.StartsWith(result.DiscFolder, StringComparison.OrdinalIgnoreCase);
                         dir = Path.GetDirectoryName(dir))
                        folders.Add(dir);
                }
                catch (Exception) { /* leave it; the user can delete the folder */ }
            }
            // The engine's own bookkeeping goes too: a leftover resume state would offer to "continue" a job whose files are gone.
            foreach (var name in new[] { DiscFolderFiles.JobState, DiscFolderFiles.Manifest, DiscFolderFiles.ErrorReadme })
            {
                foreach (var suffix in new[] { "", DiscFolderFiles.BackupSuffix })
                {
                    try
                    {
                        var p = Path.Combine(result.DiscFolder, name + suffix);
                        if (_s.Fs.FileExists(p)) _s.Fs.Delete(p);
                    }
                    catch (Exception) { /* best effort */ }
                }
            }
            // Leave no empty shell behind: the sub-folders this job created (deepest first), then the disc folder itself.
            // Each call removes a folder only when it is completely empty, so anything the user already had there stays.
            try
            {
                foreach (var dir in folders.OrderByDescending(d => d.Length)) _s.Fs.DeleteDirectoryIfEmpty(dir);
                _s.Fs.DeleteDirectoryIfEmpty(result.DiscFolder);
            }
            catch (Exception) { /* best effort */ }
            return removed;
        });
    }

    private async Task ShowSummaryDialogAsync(ResultSummary summary, bool ejected)
    {
        var icon = summary.Tone switch { StatusTone.Ok => DialogIcon.Success, StatusTone.Warn => DialogIcon.Warning, StatusTone.Danger => DialogIcon.Error, _ => DialogIcon.Info };
        var title = summary.Tone switch { StatusTone.Ok => "Extração concluída", StatusTone.Warn => "Extração concluída com avisos", _ => "A extração não foi concluída" };
        var buttons = new List<DialogButton> { new("open", "Abrir pasta", IsDefault: true) };
        if (!ejected) buttons.Add(new DialogButton("eject", "Ejetar disco"));
        buttons.Add(new DialogButton("close", "Fechar", IsCancel: true));
        if (_closingDuringMp4) return; // the window is closing: no dialog
        var message = summary.FullText + (ejected ? Environment.NewLine + "O disco foi ejetado." : "")
                      + (Mp4.SummaryText.Length > 0 ? Environment.NewLine + Environment.NewLine + Mp4.SummaryText : "");
        var r = await _dialogs.ShowAsync(new DialogRequest(title, message, icon, buttons));
        if (r.Key == "open") _shell.OpenFolder(summary.FolderPath);
        else if (r.Key == "eject") await EjectAsync();
    }

    // ----- controls -----

    private void TogglePause()
    {
        var c = _control;
        if (c == null) return;
        if (_userPaused) { c.Resume(); _userPaused = false; _log.Info("Cópia retomada."); }
        else { c.Pause(); _userPaused = true; _log.Info("Cópia em pausa."); }
        Progress.IsUserPaused = _userPaused;
    }

    private void SkipFile()
    {
        _control?.SkipCurrentFile();
        _log.Info("Pulando o arquivo atual.");
    }

    private void Insist()
    {
        _control?.ExtendSecondPass(TimeSpan.FromMinutes(10));
        _log.Info("Tempo extra de 10 minutos concedido para recuperar trechos com erro.");
    }

    /// <summary>Asks whether to keep what was already copied, then cancels.</summary>
    public async Task CancelAsync()
    {
        var control = _control;
        var cts = _extractionCts;
        if (!IsExtracting || control == null || cts == null) return;

        var wasPaused = _userPaused;
        control.Pause(); // stop touching the disc while the user decides
        var r = await _dialogs.ShowAsync(new DialogRequest(
            "Cancelar a cópia?",
            "Quer manter os arquivos que já foram copiados? Se mantiver, você poderá continuar depois de onde parou.",
            DialogIcon.Question,
            new[]
            {
                new DialogButton("keep", "Cancelar e manter o que foi copiado"),
                new DialogButton("discard", "Cancelar e apagar o que foi copiado", IsDanger: true),
                new DialogButton("back", "Continuar copiando", IsDefault: true, IsCancel: true),
            }));
        if (r.Key is not ("keep" or "discard"))
        {
            if (!wasPaused) control.Resume();
            return;
        }
        _discardOnCancel = r.Key == "discard";
        _log.Info("Cancelando a cópia…");
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        control.Resume(); // a paused engine must wake up to observe the cancellation
    }

    /// <summary>
    /// Closing the window: asks for confirmation during a copy. Returns true when the window may close.
    /// </summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (await StopMp4ForCloseAsync()) return true; // a conversion is running: stop ffmpeg, the copy itself is already safe
        if (!IsExtracting) return true;
        var r = await _dialogs.ShowAsync(new DialogRequest(
            "Uma cópia está em andamento",
            "Se fechar agora, a cópia será interrompida. Os arquivos já copiados ficam na pasta e, ao inserir o mesmo disco de novo, " +
            "o programa oferece continuar de onde parou.",
            DialogIcon.Warning,
            new[]
            {
                new DialogButton("stay", "Continuar copiando", IsDefault: true, IsCancel: true),
                new DialogButton("close", "Interromper e fechar", IsDanger: true),
            }));
        if (r.Key != "close") return false;

        _discardOnCancel = false;
        try { _extractionCts?.Cancel(); } catch (ObjectDisposedException) { }
        _control?.Resume();
        var running = _runningExtraction;
        if (running != null)
            await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(5))); // give the engine a moment to release the disc
        return true;
    }

    /// <summary>Called by the global exception handler: release the eject lock and the power request.</summary>
    public void EmergencyCleanup()
    {
        try { var letter = _extractionLetter ?? SelectedDrive?.Letter; if (letter != null) _s.Drives.LockMedia(letter, false); }
        catch (Exception) { /* best effort */ }
        try { _awake?.Dispose(); } catch (Exception) { /* best effort */ }
        _awake = null;
        try { _mp4Cts?.Cancel(); } catch (ObjectDisposedException) { }
    }
}




