using System.Security.Cryptography;
using System.Text.Json;

namespace MediaRescue.Core;

/// <summary>One extraction job. Orchestration, waiting states, persistence and progress; see ExtractionRun.Copy.cs for the per-file copy.</summary>
internal sealed partial class ExtractionRun
{
    private const long ReportIntervalMs = 250;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PauseInterval = TimeSpan.FromMilliseconds(250);
    private const long DriveFlapWindowMs = 120_000;

    private readonly IDiscReader _reader;
    private readonly IDestinationFileSystem _fs;
    private readonly IOpticalDriveService _drives;
    private readonly IClock _clock;
    private readonly ExtractionRequest _req;
    private readonly ExtractionOptions _opt;
    private readonly IProgress<ExtractionProgress> _progress;
    private readonly ExtractionControl _control;
    private readonly CancellationToken _ct;
    private readonly string _letter;

    private string _discFolder = "";
    private readonly List<Item> _items = new();
    private PathMap _pathMap = new();
    private DestinationInfo? _destInfo;
    private bool _resumed;
    private string _jobId = Guid.NewGuid().ToString("N");
    private DateTime _startedUtc;
    private long _startMs;
    private long _pausedMs;
    private bool _mediaLocked;

    // progress state
    private ExtractionPhase _phase = ExtractionPhase.Preparing;
    private ExtractionPhase _phaseBeforePause = ExtractionPhase.Copying;
    private readonly SpeedTracker _speed = new();
    private bool _hasReported;
    private long _lastReportMs;
    private int _fileCount;
    private int _currentIndex;
    private string? _currentName;
    private long _totalBytes;
    private long _doneCompleted;       // bytes of finished files (incl. lost/skipped ones, so the bar reaches 100%)
    private long _currentGood;         // bytes of the current file actually read
    private long _currentTotal;
    private long _currentLost;         // bytes lost (pass 1: pending; later: final) in the current file
    private long _lostCompleted;
    private int _filesIntact;
    private int _filesWithErrors;
    private bool _recovering;
    private bool _warnedMostlyUnreadable;
    private readonly List<long> _driveLosses = new();
    private long _lastStateWriteMs;
    private bool _hasWrittenState;
    private string? _lastMessage;

    private sealed class Item
    {
        public required DiscEntry Entry { get; init; }
        public required string Written { get; set; }
        public required string FinalPath { get; set; }
        /// <summary>True when a file already sitting at this name is our own: reserved by an earlier run of this very job (resume
        /// state) or renamed into place earlier in this run. Anything else found at the name belongs to somebody else.</summary>
        public bool OwnsFinalName { get; set; }
        public bool IsDirectory { get; init; }
        public int FileIndex { get; set; }
        public long Size { get; set; }
        public FileResult? Result { get; set; }
        public bool Done { get; set; }
    }

    private sealed class JobAbortedException : Exception
    {
        public JobAbortedException(string message) : base(message) { }
    }

    public ExtractionRun(IDiscReader reader, IDestinationFileSystem fs, IOpticalDriveService drives, IClock clock,
        ExtractionRequest req, IProgress<ExtractionProgress> progress, ExtractionControl control, CancellationToken ct)
    {
        _reader = reader; _fs = fs; _drives = drives; _clock = clock;
        _req = req; _opt = req.Options ?? new ExtractionOptions();
        _progress = progress; _control = control; _ct = ct;
        _letter = req.Drive.Letter;
    }

    // ------------------------------------------------------------------ top level

    public async Task<ExtractionResult> RunAsync()
    {
        _startMs = _clock.ElapsedMs;
        _startedUtc = _clock.UtcNow;
        _discFolder = Path.Combine(_req.DestinationRoot, _req.FolderName);
        try
        {
            SetPhase(ExtractionPhase.Preparing, "Preparando a cópia…");
            if (_req.Disc.IsCopyProtected)
                return await FinishAbortedAsync("Este é um disco comercial protegido contra cópia e não é suportado.", writeFiles: false).ConfigureAwait(false);

            LockMedia(true);
            Prepare();
            await CopyAllAsync().ConfigureAwait(false);
            return await CompleteAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested)
        {
            return await FinishCancelledAsync().ConfigureAwait(false);
        }
        catch (JobAbortedException ex)
        {
            return await FinishAbortedAsync(ex.Message, writeFiles: true).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return await FinishAbortedAsync($"A cópia foi interrompida por um erro inesperado: {ex.Message}", writeFiles: true).ConfigureAwait(false);
        }
        finally
        {
            RestoreSpeed();
            LockMedia(false);
        }
    }

    private void LockMedia(bool locked)
    {
        if (!locked && !_mediaLocked) return;
        try { _drives.LockMedia(_letter, locked); _mediaLocked = locked; }
        catch (Exception) { /* never let the lock calls break the job; unlocking is retried by the caller's finally */ }
    }

    // ------------------------------------------------------------------ preparation

    private string ToAbs(string relative) => Path.Combine(new[] { _discFolder }.Concat(PathMap.Split(relative)).ToArray());

    private void Prepare()
    {
        JobStateDto? stored = null;
        try { stored = JobFiles.LoadState(_fs, _discFolder); } catch (Exception) { /* unreadable state = fresh job */ }
        _resumed = stored != null && string.Equals(stored.Fingerprint, _req.Disc.Fingerprint, StringComparison.OrdinalIgnoreCase);
        if (_resumed) _jobId = stored!.JobId.Length > 0 ? stored.JobId : _jobId;
        if (_resumed && stored!.StartedAtUtc.Ticks != 0) _startedUtc = stored.StartedAtUtc;

        var entries = SelectEntries();
        _pathMap = new PathMap(rel => { try { return _fs.FileExists(ToAbs(rel)); } catch (Exception) { return false; } });
        if (_resumed)
            foreach (var f in stored!.Files) _pathMap.Seed(f.RelativePath, f.WrittenPath);
        foreach (var e in entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal))
            _pathMap.Map(e.RelativePath, e.IsDirectory);

        foreach (var e in entries.Where(e => e.IsDirectory))
        {
            var w = _pathMap.Map(e.RelativePath, true);
            _items.Add(new Item { Entry = e, Written = w, FinalPath = ToAbs(w), IsDirectory = true });
        }
        int index = 0;
        var ownedByThisJob = _resumed
            ? stored!.Files.Where(f => !f.IsDirectory).Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal)
            : null;
        foreach (var e in entries.Where(e => !e.IsDirectory))
        {
            var w = _pathMap.Map(e.RelativePath);
            _items.Add(new Item
            {
                Entry = e, Written = w, FinalPath = ToAbs(w), FileIndex = ++index, Size = Math.Max(0, e.Size),
                OwnsFinalName = ownedByThisJob?.Contains(e.RelativePath) == true,
            });
        }
        _fileCount = index;
        _totalBytes = _items.Where(i => !i.IsDirectory).Sum(i => i.Size);

        _fs.CreateDirectory(_discFolder);
        _destInfo = TryGetInfo(_req.DestinationRoot);

        if (_resumed) ApplyResume(stored!);
        _speed.Reset();
        SaveState(force: true);
    }

    /// <summary>Selected entries without blanks/duplicates, in physical order when every file knows it (RF-05 item 4).</summary>
    private List<DiscEntry> SelectEntries()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<DiscEntry>();
        foreach (var e in _req.Entries ?? Array.Empty<DiscEntry>())
        {
            if (e == null || string.IsNullOrWhiteSpace(e.RelativePath)) continue;
            if (seen.Add(e.RelativePath + (e.IsDirectory ? "\\" : ""))) list.Add(e);
        }
        var files = list.Where(e => !e.IsDirectory).ToList();
        if (files.Count > 1 && files.All(f => f.PhysicalOffset.HasValue))
        {
            var dirs = list.Where(e => e.IsDirectory);
            list = dirs.Concat(files.OrderBy(f => f.PhysicalOffset!.Value)).ToList();
        }
        return list;
    }

    private void ApplyResume(JobStateDto stored)
    {
        var byPath = stored.Files.Where(f => !f.IsDirectory).GroupBy(f => f.RelativePath).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var item in _items.Where(i => !i.IsDirectory))
        {
            if (byPath.TryGetValue(item.Entry.RelativePath, out var dto) && dto.Completed && IsStillValid(item, dto))
            {
                item.Result = FromDto(dto, item);
                item.Done = true;
                CountFinished(item.Result);
            }
            else
            {
                // Stale partial from the interrupted run: discard, it will be redone from scratch.
                TryDelete(item.FinalPath + JobFiles.PartialSuffix);
            }
        }
    }

    private bool IsStillValid(Item item, JobFileDto dto)
    {
        try { return _fs.FileExists(item.FinalPath) && _fs.GetFileSize(item.FinalPath) == dto.BytesTotal; }
        catch (Exception) { return false; }
    }

    private static FileResult FromDto(JobFileDto dto, Item item) => new()
    {
        RelativePath = item.Entry.RelativePath,
        WrittenPath = item.Written,
        Status = dto.Status,
        BytesTotal = dto.BytesTotal,
        BytesLost = dto.BytesLost,
        BadRanges = dto.BadRanges.Select(r => new ByteRange(r.Offset, r.Length)).ToList(),
        Sha256 = dto.Sha256,
        Error = dto.Error,
        Duration = TimeSpan.FromSeconds(dto.DurationSeconds),
    };

    private DestinationInfo? TryGetInfo(string path)
    {
        try { return _fs.GetInfo(path); }
        catch (Exception) { return null; }
    }

    // ------------------------------------------------------------------ main loop

    private async Task CopyAllAsync()
    {
        int cursor = 0;
        while (true)
        {
            while (cursor < _items.Count && _items[cursor].Done) cursor++;
            if (cursor >= _items.Count) break;
            _ct.ThrowIfCancellationRequested();
            var item = _items[cursor];

            Func<Task>? recovery = null;
            try
            {
                if (item.IsDirectory) ProcessDirectory(item);
                else await ProcessFileAsync(item).ConfigureAwait(false);
            }
            catch (DiscReadException e) when (e.Kind == ReadErrorKind.DeviceGone)
            {
                recovery = () => RecoverFromDriveLossAsync();
            }
            catch (DestinationException e) when (e.Kind == DestinationErrorKind.DeviceGone)
            {
                recovery = async () => { await RecoverFromDestinationLossAsync().ConfigureAwait(false); cursor = 0; };
            }
            catch (DestinationException e) when (e.Kind == DestinationErrorKind.DiskFull)
            {
                recovery = () => RecoverFromDiskFullAsync(item);
            }
            catch (DestinationException e)
            {
                // Directory creation failed for a non-recoverable reason: record it and keep going.
                if (item.Result == null && item.IsDirectory)
                {
                    item.Result = FailedResult(item, "Não foi possível criar a pasta no destino: " + e.Message);
                    item.Done = true;
                }
                else throw;
            }

            if (recovery != null) await recovery().ConfigureAwait(false);
        }
    }

    private void ProcessDirectory(Item item)
    {
        if (item.Entry.Error != null)
        {
            item.Result = new FileResult
            {
                RelativePath = item.Entry.RelativePath, WrittenPath = item.Written, Status = FileStatus.Failed,
                Error = "Pasta ilegível no disco: " + item.Entry.Error,
            };
            item.Done = true;
            return;
        }
        Dest(() => _fs.CreateDirectory(item.FinalPath));
        item.Done = true;
    }

    // ------------------------------------------------------------------ waiting states (E-03, E-04, E-05, E-15, E-16)

    private async Task RecoverFromDriveLossAsync()
    {
        long now = _clock.ElapsedMs;
        _driveLosses.Add(now);
        _driveLosses.RemoveAll(t => now - t > DriveFlapWindowMs);
        string msg = "Leitor desconectado — reconecte o leitor para continuar.";
        if (_driveLosses.Count >= 3)
            msg += " O leitor desconectou várias vezes em pouco tempo: ele pode estar sem energia suficiente. Use uma porta USB direto no computador (não em hub) ou um cabo com duas conexões USB.";
        SetPhase(ExtractionPhase.WaitingForDrive, msg);

        long waitStart = _clock.ElapsedMs;
        int differentInARow = 0;
        try
        {
            while (true)
            {
                await _clock.Delay(PollInterval, _ct).ConfigureAwait(false);
                DiscInfo? info = null;
                try { info = await _drives.AnalyzeAsync(_letter, _ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_ct.IsCancellationRequested) { throw; }
                catch (Exception) { info = null; }

                if (info == null) { differentInARow = 0; Report(force: false); continue; }
                if (string.Equals(info.Fingerprint, _req.Disc.Fingerprint, StringComparison.OrdinalIgnoreCase)) break;
                // Two consecutive different readings: a half-listed disc that is still spinning up must not abort the job.
                if (++differentInARow >= 2)
                    throw new JobAbortedException("O disco no leitor é diferente do disco que estava sendo copiado. Reinsira o disco original para continuar de onde parou (o progresso foi guardado).");
            }
        }
        finally
        {
            _pausedMs += _clock.ElapsedMs - waitStart;
            _speed.Reset();
        }
        LockMedia(true);
        SetPhase(ExtractionPhase.Copying, "Leitor reconectado — recomeçando o arquivo atual.");
    }

    private async Task RecoverFromDestinationLossAsync()
    {
        SetPhase(ExtractionPhase.WaitingForDestination, "Destino desconectado — reconecte o pendrive ou HD para continuar.");
        long waitStart = _clock.ElapsedMs;
        try
        {
            while (true)
            {
                await _clock.Delay(PollInterval, _ct).ConfigureAwait(false);
                var info = TryGetInfo(_req.DestinationRoot);
                if (info == null) { Report(force: false); continue; }

                if (_destInfo != null &&
                    (!string.Equals(info.VolumeLabel, _destInfo.VolumeLabel, StringComparison.Ordinal) || info.TotalBytes != _destInfo.TotalBytes))
                    throw new JobAbortedException("O destino que voltou parece ser outro pendrive ou HD. A cópia foi interrompida e o progresso foi guardado; reconecte o destino original e retome.");
                break;
            }
        }
        finally
        {
            _pausedMs += _clock.ElapsedMs - waitStart;
            _speed.Reset();
        }

        // Validate what was already finished (by size) before going on; anything missing is redone.
        int redo = 0;
        foreach (var it in _items)
        {
            if (it.IsDirectory) { it.Done = false; continue; }   // cheap and idempotent
            if (!it.Done || it.Result == null) continue;
            bool ok;
            try { ok = it.Result.Status is FileStatus.Skipped or FileStatus.Failed or FileStatus.DestinationFailure
                       || (_fs.FileExists(it.FinalPath) && _fs.GetFileSize(it.FinalPath) == it.Result.BytesTotal); }
            catch (Exception) { ok = false; }
            if (!ok) { RollBack(it); redo++; }
        }
        SetPhase(ExtractionPhase.Copying, redo > 0
            ? $"Destino reconectado — {redo} arquivo(s) já copiado(s) não foram encontrados e serão copiados de novo."
            : "Destino reconectado — continuando.");
    }

    private void RollBack(Item it)
    {
        if (it.Result != null) UncountFinished(it.Result);
        it.Result = null;
        it.Done = false;
    }

    private async Task RecoverFromDiskFullAsync(Item item)
    {
        SetPhase(ExtractionPhase.DestinationFull,
            "O destino ficou sem espaço. Libere espaço no destino; a cópia continua sozinha assim que houver espaço.");
        long needed = item.Size + (1L << 20);
        long waitStart = _clock.ElapsedMs;
        try
        {
            while (true)
            {
                await _clock.Delay(PollInterval, _ct).ConfigureAwait(false);
                var info = TryGetInfo(_discFolder) ?? TryGetInfo(_req.DestinationRoot);
                if (info == null || info.FreeBytes >= needed) break;
                Report(force: false);
            }
        }
        finally
        {
            _pausedMs += _clock.ElapsedMs - waitStart;
            _speed.Reset();
        }
        SetPhase(ExtractionPhase.Copying, "Espaço liberado — recomeçando o arquivo atual.");
    }

    // ------------------------------------------------------------------ pause / checkpoints

    private sealed class SkipFileException : Exception { }

    /// <summary>Honors cancel, pause and (optionally) "skip current file". Called between blocks.</summary>
    private async Task CheckpointAsync(bool allowSkip = true)
    {
        _ct.ThrowIfCancellationRequested();
        if (_control.IsPaused) await WaitWhilePausedAsync().ConfigureAwait(false);
        if (allowSkip && _control.ConsumeSkip()) throw new SkipFileException();
    }

    private async Task WaitWhilePausedAsync()
    {
        var before = _phase == ExtractionPhase.Paused ? _phaseBeforePause : _phase;
        _phaseBeforePause = before;
        SetPhase(ExtractionPhase.Paused, "Pausado.");
        long start = _clock.ElapsedMs;
        try
        {
            while (_control.IsPaused)
                await _clock.Delay(PauseInterval, _ct).ConfigureAwait(false);
        }
        finally
        {
            _pausedMs += _clock.ElapsedMs - start;
            _speed.Reset();
        }
        SetPhase(before, "Continuando.");
    }

    // ------------------------------------------------------------------ progress

    private void SetPhase(ExtractionPhase phase, string? message)
    {
        _phase = phase;
        Report(message, force: true);
    }

    private void Report(string? message = null, bool force = false)
    {
        long now = _clock.ElapsedMs;
        if (!force && _hasReported && now - _lastReportMs < ReportIntervalMs) return;
        _hasReported = true;
        _lastReportMs = now;
        if (message != null) _lastMessage = message;

        long done = Math.Min(_doneCompleted + _currentGood, Math.Max(_totalBytes, 0));
        double bps = _speed.Add(now, done);
        var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, now - _startMs - _pausedMs));
        bool recovering = _recovering || _phase == ExtractionPhase.SecondPass;
        var eta = EtaMath.Eta(elapsed, bps, Math.Max(0, _totalBytes - done), recovering);

        var p = new ExtractionProgress
        {
            Phase = _phase,
            CurrentFile = _currentName,
            FileIndex = _currentIndex,
            FileCount = _fileCount,
            FileBytesDone = Math.Min(_currentGood, Math.Max(_currentTotal, 0)),
            FileBytesTotal = Math.Max(_currentTotal, 0),
            TotalBytesDone = done,
            TotalBytesTotal = Math.Max(_totalBytes, 0),
            BytesPerSecond = bps,
            Eta = eta,
            Elapsed = elapsed,
            FilesIntact = _filesIntact,
            FilesWithErrors = _filesWithErrors,
            BytesLost = _lostCompleted + _currentLost,
            Message = message,
        };
        try { _progress.Report(p); }
        catch (Exception) { /* a faulty UI handler must never abort the copy */ }
    }

    private void CountFinished(FileResult r)
    {
        _doneCompleted += r.BytesTotal;
        _lostCompleted += r.BytesLost;
        if (r.Status is FileStatus.Intact or FileStatus.Recovered) _filesIntact++;
        else if (r.Status is FileStatus.Partial or FileStatus.Failed or FileStatus.DestinationFailure) _filesWithErrors++;
    }

    private void UncountFinished(FileResult r)
    {
        _doneCompleted -= r.BytesTotal;
        _lostCompleted -= r.BytesLost;
        if (r.Status is FileStatus.Intact or FileStatus.Recovered) _filesIntact--;
        else if (r.Status is FileStatus.Partial or FileStatus.Failed or FileStatus.DestinationFailure) _filesWithErrors--;
    }

    // ------------------------------------------------------------------ persistence (state file, manifest, README)

    private bool IsCompleted(Item i) =>
        !i.IsDirectory && i.Done && i.Result is { Status: FileStatus.Intact or FileStatus.Recovered or FileStatus.Partial };

    private void SaveState(bool force)
    {
        long now = _clock.ElapsedMs;
        // With thousands of tiny files rewriting the whole state after each one is quadratic: throttle those jobs.
        bool small = _items.Count <= 200;
        if (!force && !small && _hasWrittenState && now - _lastStateWriteMs < 2000) return;
        try
        {
            var dto = new JobStateDto
            {
                JobId = _jobId,
                Fingerprint = _req.Disc.Fingerprint,
                VolumeLabel = _req.Disc.VolumeLabel,
                StartedAtUtc = _startedUtc,
                UpdatedAtUtc = _clock.UtcNow,
                AppVersion = JobFiles.AppVersion,
            };
            foreach (var i in _items)
            {
                var r = i.Result;
                dto.Files.Add(new JobFileDto
                {
                    RelativePath = i.Entry.RelativePath,
                    WrittenPath = i.Written,
                    IsDirectory = i.IsDirectory,
                    Completed = IsCompleted(i),
                    Status = r?.Status ?? FileStatus.Pending,
                    BytesTotal = r?.BytesTotal ?? i.Size,
                    BytesLost = r?.BytesLost ?? 0,
                    BadRanges = r?.BadRanges.Select(b => new RangeDto { Offset = b.Offset, Length = b.Length }).ToList() ?? new(),
                    Sha256 = r?.Sha256,
                    Error = r?.Error,
                    DurationSeconds = r?.Duration.TotalSeconds ?? 0,
                });
            }
            _fs.WriteAllTextAtomic(Path.Combine(_discFolder, JobFiles.StateName), JsonSerializer.Serialize(dto, JsonDefaults.Options));
            _hasWrittenState = true;
            _lastStateWriteMs = now;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // The state file is a convenience for resuming; failing to write it must not stop the copy.
        }
    }

    private List<FileResult> CollectResults() => _items.Where(i => i.Result != null).Select(i => i.Result!).ToList();

    private bool WriteManifestAndReadme(OverallStatus overall, List<FileResult> results, string? note, bool writeReadme)
    {
        bool ok = true;
        var finished = _clock.UtcNow;
        var duration = Elapsed();
        try
        {
            var map = _items.ToDictionary(i => i.Entry.RelativePath, i => i.Written, StringComparer.Ordinal);
            var modified = _items.Where(i => !i.IsDirectory).ToDictionary(i => i.Entry.RelativePath, i => i.Entry.ModifiedUtc, StringComparer.Ordinal);
            var manifest = ReportBuilder.BuildManifest(_req, _jobId, _startedUtc, finished, duration, overall, results, map, modified, note);
            var path = Path.Combine(_discFolder, JobFiles.ManifestName);
            _fs.WriteAllTextAtomic(path, JsonSerializer.Serialize(manifest, JsonDefaults.Options));
            TryDelete(path + ".bak");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ok = false;
            Report("Atenção: não foi possível gravar o relatório mediarescue-manifest.json: " + ex.Message, force: true);
        }

        var readmePath = Path.Combine(_discFolder, JobFiles.ReadmeName);
        try
        {
            if (writeReadme && (ReportBuilder.HasProblems(results) || !string.IsNullOrEmpty(note)))
            {
                _fs.WriteAllTextAtomic(readmePath, ReportBuilder.BuildReadme(_req, results, overall, finished, note));
                TryDelete(readmePath + ".bak");
            }
            else if (_resumed && _fs.FileExists(readmePath))
            {
                _fs.Delete(readmePath); // stale from the interrupted run; this run ended clean
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ok = false;
            Report("Atenção: não foi possível gravar o arquivo LEIA-ME - ERROS.txt: " + ex.Message, force: true);
        }
        return ok;
    }

    private TimeSpan Elapsed() => TimeSpan.FromMilliseconds(Math.Max(0, _clock.ElapsedMs - _startMs - _pausedMs));

    // ------------------------------------------------------------------ endings

    private async Task<ExtractionResult> CompleteAsync()
    {
        var results = CollectResults();
        var overall = ReportBuilder.Overall(results, cancelled: false);
        bool written = WriteManifestAndReadme(overall, results, note: null, writeReadme: true);
        if (written)
        {
            var state = Path.Combine(_discFolder, JobFiles.StateName);
            TryDelete(state);
            TryDelete(state + ".bak");
        }
        _currentName = null;
        _currentGood = 0; _currentTotal = 0; _currentLost = 0; _recovering = false;
        _doneCompleted = _totalBytes;
        string summary = $"Concluído: {_filesIntact} arquivo(s) íntegro(s), {_filesWithErrors} com erro" +
                         (_lostCompleted > 0 ? $", {Fmt.Bytes(_lostCompleted)} irrecuperáveis." : ".");
        SetPhase(ExtractionPhase.Completed, summary);
        await Task.CompletedTask.ConfigureAwait(false);
        return new ExtractionResult(_discFolder, overall, results, Elapsed());
    }

    private async Task<ExtractionResult> FinishCancelledAsync()
    {
        // Nothing here may observe the (already cancelled) token.
        var results = CollectResults();
        SaveState(force: true);
        WriteManifestAndReadme(OverallStatus.Cancelled, results, "A cópia foi cancelada antes de terminar.", writeReadme: false);
        _currentName = null;
        SetPhase(ExtractionPhase.Cancelled, "Cópia cancelada.");
        await Task.CompletedTask.ConfigureAwait(false);
        return new ExtractionResult(_discFolder, OverallStatus.Cancelled, results, Elapsed());
    }

    private async Task<ExtractionResult> FinishAbortedAsync(string message, bool writeFiles)
    {
        // Files that never ran are reported as failed so nothing is silently missing; the resume state keeps them pending.
        foreach (var i in _items.Where(i => !i.IsDirectory && i.Result == null))
            i.Result = FailedResult(i, "Não copiado: " + message);
        var results = CollectResults();
        if (writeFiles)
        {
            // These results are not "Completed" in the state file, so a later run redoes them.
            SaveState(force: true);
            WriteManifestAndReadme(OverallStatus.Failed, results, message, writeReadme: true);
        }
        _currentName = null;
        SetPhase(ExtractionPhase.Completed, message);
        await Task.CompletedTask.ConfigureAwait(false);
        return new ExtractionResult(_discFolder, OverallStatus.Failed, results, Elapsed());
    }

    private FileResult FailedResult(Item item, string error) => new()
    {
        RelativePath = item.Entry.RelativePath,
        WrittenPath = item.Written,
        Status = FileStatus.Failed,
        BytesTotal = item.Size,
        BytesLost = item.IsDirectory ? 0 : item.Size,
        Error = error,
    };

    // ------------------------------------------------------------------ destination helpers

    private void TryDelete(string path)
    {
        try { if (_fs.FileExists(path)) _fs.Delete(path); }
        catch (Exception) { /* best effort */ }
    }

    private static bool TryTranslate(Exception e, out Exception translated)
    {
        translated = e;
        switch (e)
        {
            case DestinationException:
            case DiscReadException:
            case OperationCanceledException:
            case SkipFileException:
                return false;
            case UnauthorizedAccessException:
                translated = new DestinationException(DestinationErrorKind.AccessDenied, "Sem permissão para gravar no destino: " + e.Message, e);
                return true;
            case DriveNotFoundException:
            case DirectoryNotFoundException:
                translated = new DestinationException(DestinationErrorKind.DeviceGone, "O destino não está disponível: " + e.Message, e);
                return true;
            case IOException io:
                {
                    int code = io.HResult & 0xFFFF;
                    var kind = code is 112 or 39 ? DestinationErrorKind.DiskFull
                             : code is 21 or 1112 or 1167 ? DestinationErrorKind.DeviceGone
                             : DestinationErrorKind.Other;
                    translated = new DestinationException(kind, "Erro ao acessar o destino: " + e.Message, e);
                    return true;
                }
            default:
                return false;
        }
    }

    private static T Dest<T>(Func<T> action)
    {
        try { return action(); }
        catch (Exception e) when (TryTranslate(e, out var t)) { throw t; }
    }

    private static void Dest(Action action)
    {
        try { action(); }
        catch (Exception e) when (TryTranslate(e, out var t)) { throw t; }
    }
}
