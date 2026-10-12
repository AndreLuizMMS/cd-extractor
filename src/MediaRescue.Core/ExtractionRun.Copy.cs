using System.Security.Cryptography;

namespace MediaRescue.Core;

/// <summary>Per-file copy: two-pass tolerant reading, streaming hashes, verification and the atomic rename.</summary>
internal sealed partial class ExtractionRun
{
    private const int BaseBlock = 64 * 1024;
    private const long MaxSkip = 4L * 1024 * 1024;
    private const int ReducedSpeedKbPerSec = 600;
    private const long RenameBudgetMs = 10_000;
    private static readonly TimeSpan ReadGuardGrace = TimeSpan.FromSeconds(5);

    private byte[] _buf = new byte[BaseBlock];
    private int _readErrors;
    private DiscReadException? _lastReadError;
    private long _passTwoSpentMs;
    private bool _speedReduced;

    private sealed class AttemptFailedException : Exception
    {
        public AttemptFailedException(string message, Exception? inner = null) : base(message, inner) { }
    }

    private sealed class RenameFailedException : Exception
    {
        public RenameFailedException(string message, Exception? inner = null) : base(message, inner) { }
    }

    private sealed class FileCtx
    {
        public required IDiscFile Disc { get; init; }
        public required Stream Out { get; init; }
        public long Length { get; init; }
        public int Block { get; init; }
        public int Sector { get; init; }
        public ExtentList Extents { get; } = new();
        public IncrementalHash Whole { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public bool WholeValid { get; set; } = true;
        public long WholePos { get; set; }
        public List<ByteRange> Pending { get; } = new();
    }

    // ------------------------------------------------------------------ per file

    private async Task ProcessFileAsync(Item item)
    {
        _currentName = item.Entry.RelativePath;
        _currentIndex = item.FileIndex;
        _currentTotal = item.Size;
        _currentGood = 0;
        _currentLost = 0;
        _recovering = false;
        SetPhase(ExtractionPhase.Copying, $"Copiando {item.Entry.RelativePath}");
        await CheckpointAsync(allowSkip: false).ConfigureAwait(false);

        long started = _clock.ElapsedMs;
        FileResult result;
        try
        {
            result = await CopyWithRetriesAsync(item).ConfigureAwait(false);
        }
        catch (SkipFileException)
        {
            result = new FileResult
            {
                RelativePath = item.Entry.RelativePath, WrittenPath = item.Written, Status = FileStatus.Skipped, BytesTotal = item.Size,
            };
        }
        result = result with { Duration = TimeSpan.FromMilliseconds(Math.Max(0, _clock.ElapsedMs - started)) };

        item.Result = result;
        item.Done = true;
        CountFinished(result);
        _currentGood = 0;
        _currentLost = 0;
        _recovering = false;
        SaveState(force: result.BytesTotal >= (1 << 20));
        SetPhase(ExtractionPhase.Copying, $"{ReportBuilder.StatusLabel(result.Status)}: {item.Entry.RelativePath}");
    }

    private async Task<FileResult> CopyWithRetriesAsync(Item item)
    {
        string? lastError = null;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                return await CopyAttemptAsync(item).ConfigureAwait(false);
            }
            catch (AttemptFailedException ex)
            {
                lastError = ex.Message;
            }
            catch (DestinationException ex) when (ex.Kind is DestinationErrorKind.Other or DestinationErrorKind.AccessDenied)
            {
                lastError = ex.Message;
            }
            catch (RenameFailedException ex)
            {
                lastError = ex.Message;
                break; // the data is fine, the destination refuses the final name: re-copying would not help
            }
            if (attempt == 1)
                SetPhase(ExtractionPhase.Copying, $"Falha ao gravar ou conferir {item.Entry.RelativePath} no destino; tentando de novo.");
        }

        SetPhase(ExtractionPhase.Copying,
            $"ATENÇÃO: não foi possível gravar {item.Entry.RelativePath} com segurança no destino. O pendrive ou HD de destino pode estar com defeito.");
        return new FileResult
        {
            RelativePath = item.Entry.RelativePath,
            WrittenPath = item.Written,
            Status = FileStatus.DestinationFailure,
            BytesTotal = item.Size,
            Error = lastError ?? "Falha ao gravar no destino.",
        };
    }

    private async Task<FileResult> CopyAttemptAsync(Item item)
    {
        var entry = item.Entry;
        IDiscFile disc;
        try
        {
            disc = _reader.Open(_letter, entry.RelativePath);
        }
        catch (DiscReadException e) when (e.Kind == ReadErrorKind.DeviceGone) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            return FailedReadResult(item, item.Size, "Não foi possível abrir o arquivo no disco: " + e.Message);
        }

        using (disc)
        {
            long length = Math.Max(0, disc.Length);
            if (length != item.Size)
            {
                _totalBytes += length - item.Size;
                item.Size = length;
                _currentTotal = length;
            }
            _currentGood = 0;
            _currentLost = 0;
            _recovering = false;

            int sector = Math.Max(1, disc.SectorSize);
            int block = sector >= BaseBlock ? sector : BaseBlock / sector * sector;
            if (_buf.Length < block) _buf = new byte[block];

            string partial = item.FinalPath + JobFiles.PartialSuffix;
            int errorsBefore = _readErrors;
            Stream? stream = null;
            FileCtx? ctx = null;
            bool committed = false;
            try
            {
                Dest(() => _fs.CreateDirectory(Path.GetDirectoryName(item.FinalPath)!));
                TryDelete(partial);
                stream = Dest(() => _fs.Create(partial));
                ctx = new FileCtx { Disc = disc, Out = stream, Length = length, Block = block, Sector = sector };

                await Pass1Async(ctx).ConfigureAwait(false);

                IReadOnlyList<ByteRange> bad = ctx.Pending;
                if (ctx.Pending.Count > 0)
                {
                    WarnIfMostlyUnreadable(ctx.Pending.Sum(r => r.Length), length);
                    bad = stream.CanSeek ? await Pass2Async(ctx, item).ConfigureAwait(false) : ctx.Pending.ToList();
                }
                long lost = bad.Sum(r => r.Length);
                _currentLost = lost;
                if (ctx.Pending.Count > 0) WarnIfMostlyUnreadable(lost, length);

                if (length > 0 && lost >= length)
                {
                    var err = _lastReadError;
                    return FailedReadResult(item, length,
                        "Nenhum dado pôde ser lido do disco." + (err != null ? $" Último erro: {err.Message} (código {err.Win32Code})." : ""));
                }

                // Flush to the device, close, then re-read and compare (RF-05 item 2).
                SetPhase(ExtractionPhase.Verifying, $"Conferindo {entry.RelativePath}");
                Dest(() => _fs.FlushToDisk(stream));
                Dest(stream.Dispose);
                stream = null;

                string? expected = ctx.WholeValid ? Fmt.Hex(ctx.Whole.GetHashAndReset()) : null;
                string? sha;
                if (_opt.VerifyAfterCopy || !ctx.WholeValid)
                    sha = VerifyDestination(partial, length, expected, ctx.Extents, bad);
                else
                    sha = expected;

                await MoveWithRetryAsync(item, partial).ConfigureAwait(false);
                committed = true;
                item.OwnsFinalName = true; // from now on the file at this name is ours (it may have to be redone, E-16)
                if (entry.ModifiedUtc.Ticks != 0)
                    await SetTimesAsync(item.FinalPath, entry.ModifiedUtc).ConfigureAwait(false);

                _currentGood = length - lost;
                _currentLost = lost;
                var status = bad.Count > 0 ? FileStatus.Partial
                           : _readErrors > errorsBefore ? FileStatus.Recovered
                           : FileStatus.Intact;
                return new FileResult
                {
                    RelativePath = entry.RelativePath,
                    WrittenPath = item.Written,
                    Status = status,
                    BytesTotal = length,
                    BytesLost = lost,
                    BadRanges = bad.ToList(),
                    Sha256 = sha,
                };
            }
            finally
            {
                try { stream?.Dispose(); } catch (Exception) { /* closing a broken destination stream */ }
                ctx?.Whole.Dispose();
                if (!committed) TryDelete(partial);
                RestoreSpeed();
            }
        }
    }

    private FileResult FailedReadResult(Item item, long length, string error) => new()
    {
        RelativePath = item.Entry.RelativePath,
        WrittenPath = item.Written,
        Status = FileStatus.Failed,
        BytesTotal = length,
        BytesLost = length,
        BadRanges = length > 0 ? new[] { new ByteRange(0, length) } : Array.Empty<ByteRange>(),
        Error = error,
    };

    private void WarnIfMostlyUnreadable(long lost, long length)
    {
        if (_warnedMostlyUnreadable || length <= 0 || lost * 2 <= length) return;
        _warnedMostlyUnreadable = true;
        Report("Mais da metade deste arquivo está ilegível. Você pode parar a cópia, limpar o disco ou tentar em outro leitor; a recuperação continua dentro do tempo limite.", force: true);
    }

    // ------------------------------------------------------------------ pass 1

    private async Task Pass1Async(FileCtx c)
    {
        long pos = 0;
        int streak = 0;
        bool reportedError = false;
        while (pos < c.Length)
        {
            await CheckpointAsync().ConfigureAwait(false);
            int want = (int)Math.Min(c.Block, c.Length - pos);
            int n = await ReadWithRetryAsync(c.Disc, pos, want, attempts: 2).ConfigureAwait(false);
            if (n > 0)
            {
                WriteAt(c.Out, pos, _buf.AsSpan(0, n));
                c.Extents.Append(pos, _buf.AsSpan(0, n));
                if (c.WholeValid && c.WholePos == pos)
                {
                    c.Whole.AppendData(_buf, 0, n);
                    c.WholePos += n;
                }
                _currentGood += n;
                pos += n;
                streak = 0;
                Report();
            }
            else
            {
                streak++;
                // 64 KiB after the first consecutive failure, doubling up to 4 MiB; resets on the first good read.
                long skip = Math.Min(MaxSkip, (long)BaseBlock << Math.Min(streak - 1, 6));
                long lostEnd = Math.Min(c.Length, pos + want + skip);
                AddRange(c.Pending, pos, lostEnd - pos);
                WriteZeros(c.Out, pos, lostEnd - pos);
                c.WholeValid = false;
                _currentLost += lostEnd - pos;
                pos = lostEnd;
                if (!reportedError)
                {
                    reportedError = true;
                    Report($"Erro de leitura em {_currentName}; pulando o trecho e continuando (será retomado depois).", force: true);
                }
                else Report();
            }
        }
    }

    // ------------------------------------------------------------------ pass 2

    private bool BudgetExhausted() =>
        _passTwoSpentMs >= (long)(_opt.SecondPassBudget + _control.ExtraSecondPassBudget).TotalMilliseconds;

    private async Task<int> TimedReadAsync(IDiscFile disc, long offset, int count, int attempts)
    {
        long t0 = _clock.ElapsedMs;
        try { return await ReadWithRetryAsync(disc, offset, count, attempts).ConfigureAwait(false); }
        finally { _passTwoSpentMs += Math.Max(0, _clock.ElapsedMs - t0); }
    }

    private async Task<IReadOnlyList<ByteRange>> Pass2Async(FileCtx c, Item item)
    {
        if (BudgetExhausted()) return c.Pending.ToList(); // no time left (or none granted): the pending ranges stay lost
        _recovering = true;
        SetPhase(ExtractionPhase.SecondPass, $"Tentando recuperar os trechos ilegíveis de {item.Entry.RelativePath}…");
        ReduceSpeed();

        var remaining = new List<ByteRange>();
        int attempts = Math.Max(1, _opt.RetriesPerSector);
        foreach (var range in c.Pending)
        {
            long pos = range.Offset;
            long end = range.Offset + range.Length;
            while (pos < end)
            {
                await CheckpointAsync().ConfigureAwait(false);
                if (BudgetExhausted())
                {
                    AddRange(remaining, pos, end - pos);
                    break;
                }

                int chunk = (int)Math.Min(c.Block, end - pos);
                int n = await TimedReadAsync(c.Disc, pos, chunk, attempts: 1).ConfigureAwait(false);
                if (n > 0)
                {
                    // Good data (possibly a short read): store it and carry on from where it ended.
                    StoreRecovered(c, pos, n);
                    pos += n;
                    continue;
                }

                // The whole chunk failed: go sector by sector, each with its own retries.
                long chunkEnd = pos + chunk;
                for (long s = pos; s < chunkEnd; s += c.Sector)
                {
                    int sl = (int)Math.Min(c.Sector, chunkEnd - s);
                    bool ok = false;
                    for (int a = 0; a < attempts && !BudgetExhausted(); a++)
                    {
                        await CheckpointAsync().ConfigureAwait(false);
                        int got = await TimedReadAsync(c.Disc, s, sl, attempts: 1).ConfigureAwait(false);
                        if (got == sl) { StoreRecovered(c, s, sl); ok = true; break; }
                    }
                    if (!ok) AddRange(remaining, s, sl);
                    else Report();
                }
                pos = chunkEnd;
            }
        }

        _recovering = false;
        RestoreSpeed();
        return remaining;
    }

    private void StoreRecovered(FileCtx c, long offset, int count)
    {
        WriteAt(c.Out, offset, _buf.AsSpan(0, count));
        c.Extents.Append(offset, _buf.AsSpan(0, count));
        _currentGood += count;
        _currentLost = Math.Max(0, _currentLost - count);
        Report();
    }

    private void ReduceSpeed()
    {
        if (!_opt.ReduceSpeedOnError || _speedReduced) return;
        try { _speedReduced = _drives.SetReadSpeed(_letter, ReducedSpeedKbPerSec); }
        catch (Exception) { _speedReduced = false; }
    }

    private void RestoreSpeed()
    {
        if (!_speedReduced) return;
        try { _drives.SetReadSpeed(_letter, 0); }
        catch (Exception) { /* nothing more to do */ }
        _speedReduced = false;
    }

    private static void AddRange(List<ByteRange> list, long offset, long length)
    {
        if (length <= 0) return;
        if (list.Count > 0)
        {
            var last = list[^1];
            if (last.Offset + last.Length == offset)
            {
                list[^1] = new ByteRange(last.Offset, last.Length + length);
                return;
            }
        }
        list.Add(new ByteRange(offset, length));
    }

    // ------------------------------------------------------------------ disc reads (timeout guard, cancel, retry)

    private async Task<int> ReadWithRetryAsync(IDiscFile disc, long offset, int count, int attempts)
    {
        for (int a = 0; a < attempts; a++)
        {
            try
            {
                int n = await ReadGuardedAsync(disc, offset, count).ConfigureAwait(false);
                if (n > 0) return Math.Min(n, count);
                _readErrors++; // 0 bytes before the end of the file: treat as a failed read
            }
            catch (DiscReadException e) when (e.Kind != ReadErrorKind.DeviceGone)
            {
                _readErrors++;
                _lastReadError = e;
            }
        }
        return 0;
    }

    private Exception TranslateRead(Exception e)
    {
        if (e is DiscReadException) return e;
        if (e is OperationCanceledException)
            return _ct.IsCancellationRequested ? e : new DiscReadException(ReadErrorKind.BadSector, 121, "Tempo esgotado na leitura do disco.", e);
        return new DiscReadException(ReadErrorKind.Other, 0, e.Message, e);
    }

    /// <summary>
    /// One read with a safety net: if the device ignores both its own timeout and cancellation, the engine stops
    /// waiting (timeout + 5 s grace, or immediately on cancel) and abandons the read, so Cancel always works (E-12).
    /// </summary>
    private async Task<int> ReadGuardedAsync(IDiscFile disc, long offset, int count)
    {
        _ct.ThrowIfCancellationRequested();
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        Task<int> task;
        try { task = disc.ReadAsync(offset, _buf.AsMemory(0, count), _opt.BlockTimeout, readCts.Token); }
        catch (Exception e) { throw TranslateRead(e); }

        if (!task.IsCompleted)
        {
            using var guardCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            var guard = _clock.Delay(_opt.BlockTimeout + ReadGuardGrace, guardCts.Token);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var reg = _ct.Register(() => cancelled.TrySetResult());
            await Task.WhenAny(task, guard, cancelled.Task).ConfigureAwait(false);
            if (!task.IsCompleted)
            {
                readCts.Cancel();
                _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                _buf = new byte[_buf.Length]; // the abandoned read may still write into the old buffer
                _ct.ThrowIfCancellationRequested();
                throw new DiscReadException(ReadErrorKind.BadSector, 121, "Tempo esgotado na leitura do disco.");
            }
            guardCts.Cancel();
        }

        try { return await task.ConfigureAwait(false); }
        catch (Exception e) { throw TranslateRead(e); }
    }

    // ------------------------------------------------------------------ destination writes / verify / rename

    private static readonly byte[] ZeroBlock = new byte[BaseBlock];

    private static void WriteAt(Stream s, long offset, ReadOnlySpan<byte> data)
    {
        try
        {
            if (s.CanSeek && s.Position != offset) s.Seek(offset, SeekOrigin.Begin);
            s.Write(data);
        }
        catch (Exception e) when (TryTranslate(e, out var t)) { throw t; }
    }

    private static void WriteZeros(Stream s, long offset, long count)
    {
        try
        {
            if (s.CanSeek && s.Position != offset) s.Seek(offset, SeekOrigin.Begin);
            while (count > 0)
            {
                int n = (int)Math.Min(ZeroBlock.Length, count);
                s.Write(ZeroBlock, 0, n);
                count -= n;
            }
        }
        catch (Exception e) when (TryTranslate(e, out var t)) { throw t; }
    }

    /// <summary>
    /// Re-reads the destination. With an unbroken stream the whole-file hash must equal the one computed while reading;
    /// otherwise every extent actually read from the disc must match its own hash and the lost ranges must be zeros.
    /// Returns the SHA-256 of what is on the destination.
    /// </summary>
    private string VerifyDestination(string path, long length, string? expectedWholeHash, ExtentList extents, IReadOnlyList<ByteRange> bad)
    {
        Stream s = Dest(() => _fs.OpenRead(path));
        using (s)
        {
            long actual;
            try { actual = s.Length; }
            catch (Exception e) when (TryTranslate(e, out var t)) { throw t; }
            if (actual != length)
                throw new AttemptFailedException($"Tamanho incorreto no destino após a gravação ({actual} em vez de {length} bytes).");

            using var whole = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[1 << 20];
            try
            {
                if (expectedWholeHash != null)
                {
                    ReadSegment(s, length, buf, whole, null, requireZeros: false);
                }
                else
                {
                    long p = 0;
                    foreach (var e in extents.Sealed())
                    {
                        if (e.Offset > p) { ReadSegment(s, e.Offset - p, buf, whole, null, requireZeros: true); p = e.Offset; }
                        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        ReadSegment(s, e.Length, buf, whole, h, requireZeros: false);
                        p += e.Length;
                        if (!CryptographicOperations.FixedTimeEquals(h.GetHashAndReset(), e.Hash))
                            throw new AttemptFailedException($"A conferência encontrou dados diferentes no destino (por volta da posição {e.Offset}).");
                    }
                    if (p < length) ReadSegment(s, length - p, buf, whole, null, requireZeros: true);
                }
            }
            catch (Exception e) when (e is not AttemptFailedException && TryTranslate(e, out var t)) { throw t; }

            var hash = Fmt.Hex(whole.GetHashAndReset());
            if (expectedWholeHash != null && !string.Equals(hash, expectedWholeHash, StringComparison.Ordinal))
                throw new AttemptFailedException("A conferência encontrou dados diferentes no destino (o SHA-256 do arquivo gravado não bate com o lido do disco).");
            return hash;
        }
    }

    private static void ReadSegment(Stream s, long count, byte[] buf, IncrementalHash whole, IncrementalHash? extent, bool requireZeros)
    {
        while (count > 0)
        {
            int want = (int)Math.Min(buf.Length, count);
            int n = s.Read(buf, 0, want);
            if (n <= 0) throw new AttemptFailedException("O arquivo gravado no destino terminou antes do esperado.");
            whole.AppendData(buf, 0, n);
            extent?.AppendData(buf, 0, n);
            if (requireZeros && buf.AsSpan(0, n).IndexOfAnyExcept((byte)0) >= 0)
                throw new AttemptFailedException("A conferência encontrou dados inesperados em um trecho que deveria estar zerado.");
            count -= n;
        }
    }

    /// <summary>Rename with exponential back-off for up to 10 s (E-25: antivirus/Explorer holding the file).</summary>
    private async Task MoveWithRetryAsync(Item item, string source)
    {
        long waited = 0;
        long delay = 100;
        while (true)
        {
            try
            {
                Dest(() =>
                {
                    EnsureFinalNameFree(item);
                    _fs.Move(source, item.FinalPath); // never overwrites (IDestinationFileSystem.Move contract)
                });
                return;
            }
            catch (DestinationException e) when (e.Kind is DestinationErrorKind.Other or DestinationErrorKind.AccessDenied)
            {
                if (waited >= RenameBudgetMs)
                    throw new RenameFailedException(
                        "Não foi possível dar o nome final ao arquivo: ele continua em uso por outro programa (antivírus ou Explorer?). " + e.Message, e);
                long d = Math.Min(delay, RenameBudgetMs - waited);
                await _clock.Delay(TimeSpan.FromMilliseconds(d), _ct).ConfigureAwait(false);
                waited += d;
                delay *= 2;
            }
        }
    }

    /// <summary>
    /// Makes sure the final name is free before the rename. A file left there by an earlier run of this same job is ours and
    /// is replaced; anything else showed up after the names were reserved (the user, another program) and is never touched or
    /// replaced: this file simply gets the next free "name (2).ext" instead.
    /// </summary>
    private void EnsureFinalNameFree(Item item)
    {
        if (!_fs.FileExists(item.FinalPath)) return;
        if (item.OwnsFinalName) { _fs.Delete(item.FinalPath); return; }

        var segs = PathMap.Split(item.Written);
        var dir = segs.Length > 1 ? string.Join('\\', segs[..^1]) : "";
        var taken = new HashSet<string>(_items.Select(i => i.Written), StringComparer.OrdinalIgnoreCase);
        for (int n = 2; n < 10_000; n++)
        {
            var name = NameSanitizer.WithSuffix(segs[^1], $" ({n})", NameSanitizer.MaxSegmentLength, keepExtension: true);
            var rel = dir.Length == 0 ? name : dir + "\\" + name;
            var abs = ToAbs(rel);
            if (taken.Contains(rel) || _fs.FileExists(abs) || _fs.DirectoryExists(abs)) continue;
            item.Written = rel;
            item.FinalPath = abs;
            return;
        }
        throw new RenameFailedException("Não foi possível encontrar um nome livre para " + item.Entry.RelativePath + " no destino.");
    }

    private async Task SetTimesAsync(string path, DateTime modifiedUtc)
    {
        long waited = 0;
        long delay = 100;
        while (true)
        {
            try { Dest(() => _fs.SetTimes(path, modifiedUtc)); return; }
            catch (DestinationException e) when (e.Kind is DestinationErrorKind.Other or DestinationErrorKind.AccessDenied)
            {
                if (waited >= 3000) return; // the file is safe; only its timestamp could not be preserved
                await _clock.Delay(TimeSpan.FromMilliseconds(delay), _ct).ConfigureAwait(false);
                waited += delay;
                delay *= 2;
            }
        }
    }
}

/// <summary>Hashes of the byte runs actually read from the disc, used to verify files that contain lost ranges.</summary>
internal sealed class ExtentList
{
    private const long MaxExtentLength = 16L * 1024 * 1024;

    internal readonly record struct Extent(long Offset, long Length, byte[] Hash);

    private readonly List<Extent> _items = new();
    private IncrementalHash? _current;
    private long _start;
    private long _length;

    public void Append(long offset, ReadOnlySpan<byte> data)
    {
        if (_current != null && (offset != _start + _length || _length >= MaxExtentLength)) Flush();
        if (_current == null)
        {
            _current = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            _start = offset;
            _length = 0;
        }
        _current.AppendData(data);
        _length += data.Length;
    }

    private void Flush()
    {
        if (_current == null) return;
        _items.Add(new Extent(_start, _length, _current.GetHashAndReset()));
        _current.Dispose();
        _current = null;
    }

    /// <summary>Finishes the open run and returns all extents ordered by offset.</summary>
    public IReadOnlyList<Extent> Sealed()
    {
        Flush();
        _items.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return _items;
    }
}
