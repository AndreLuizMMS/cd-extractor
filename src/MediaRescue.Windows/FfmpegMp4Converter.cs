using System.Diagnostics;
using System.Globalization;
using System.Text;
using MediaRescue.Core;

namespace MediaRescue.Windows;

/// <summary>
/// <see cref="IMp4Converter"/> backed by ffmpeg.exe / ffprobe.exe from the tools directory (RF-11).
/// Reads only the copies in the destination folder and writes <c>MP4\*.mp4.partial</c>, renaming to <c>.mp4</c> after the output
/// has been checked with ffprobe. The H.264 encoder is detected at run time (the bundled build is LGPL, without libx264):
/// libx264, h264_mf (Windows Media Foundation), libopenh264 or, as the last resort, mpeg4 - each one proven by a tiny real encode.
/// </summary>
public sealed class FfmpegMp4Converter : IMp4Converter
{
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly IMediaProbe _probe;
    private readonly Func<string, long?> _freeSpace;
    private readonly TimeSpan _stallTimeout;
    private readonly SemaphoreSlim _encoderLock = new(1, 1);
    private IReadOnlyList<string>? _workingEncoders;

    public FfmpegMp4Converter(string toolsDirectory, IMediaProbe probe) : this(toolsDirectory, probe, null, null) { }

    internal FfmpegMp4Converter(string toolsDirectory, IMediaProbe probe, Func<string, long?>? freeSpace, TimeSpan? stallTimeout)
    {
        var dir = toolsDirectory ?? "";
        _ffmpeg = Path.Combine(dir, "ffmpeg.exe");
        _ffprobe = Path.Combine(dir, "ffprobe.exe");
        _probe = probe;
        _freeSpace = freeSpace ?? DefaultFreeSpace;
        _stallTimeout = stallTimeout ?? TimeSpan.FromMinutes(3);
    }

    /// <summary>Receives the ffmpeg argument line of every conversion run (tests and diagnostics).</summary>
    internal Action<string>? OnCommand { get; set; }

    public bool IsAvailable => SafeExists(_ffmpeg) && SafeExists(_ffprobe) && _probe.IsAvailable;

    private static bool SafeExists(string p) { try { return File.Exists(p); } catch { return false; } }

    private static long? DefaultFreeSpace(string path)
    {
        try { return new WindowsDestinationFileSystem().GetInfo(path)?.FreeBytes; }
        catch { return null; }
    }

    // =========================================================================================================
    // Encoder detection
    // =========================================================================================================

    /// <summary>Encoders that exist in this ffmpeg build AND really encoded a test clip, in preference order. Cached once found.</summary>
    internal async Task<IReadOnlyList<string>> GetWorkingEncodersAsync(CancellationToken ct)
    {
        await _encoderLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_workingEncoders != null) return _workingEncoders;

            var listing = await RunAsync(_ffmpeg, new[] { "-hide_banner", "-encoders" }, captureStdout: true, onStdoutLine: null,
                overallTimeout: TimeSpan.FromSeconds(30), stallTimeout: null, ct).ConfigureAwait(false);
            if (listing.ExitCode != 0) return Array.Empty<string>();

            var working = new List<string>();
            foreach (var candidate in Mp4Encoders.Candidates(listing.Stdout))
            {
                ct.ThrowIfCancellationRequested();
                if (await TestEncoderAsync(candidate, ct).ConfigureAwait(false)) working.Add(candidate);
            }
            if (working.Count > 0) _workingEncoders = working;
            return working;
        }
        finally { _encoderLock.Release(); }
    }

    private async Task<bool> TestEncoderAsync(string encoder, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "mediarescue-enc-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            var r = await RunAsync(_ffmpeg, Mp4CommandLine.BuildEncoderTest(encoder, tmp), captureStdout: false, onStdoutLine: null,
                overallTimeout: TimeSpan.FromSeconds(45), stallTimeout: null, ct).ConfigureAwait(false);
            return r.ExitCode == 0 && SafeLength(tmp) > 0;
        }
        finally { TryDelete(tmp); }
    }

    // =========================================================================================================
    // Conversion
    // =========================================================================================================

    public async Task<Mp4ConversionResult> ConvertAsync(string discFolder, IReadOnlyList<FileResult> files, IProgress<Mp4ConversionProgress> progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var results = new List<Mp4JobResult>();
        IReadOnlyList<Mp4Job> jobs = Array.Empty<Mp4Job>();
        string preferred = "";
        bool createdFolder = false;
        var outputRoot = Path.Combine(discFolder ?? "", Mp4ConversionPlanner.OutputFolder);

        Mp4ConversionResult Done(string? error = null, bool cancelled = false) => new()
        {
            Encoder = preferred, Jobs = results, WasCancelled = cancelled, Error = error, Duration = clock.Elapsed,
        };

        try
        {
            if (!IsAvailable)
                return Done("A conversão para MP4 precisa do ffmpeg, que não foi encontrado nesta instalação.");
            if (string.IsNullOrWhiteSpace(discFolder) || !Directory.Exists(discFolder))
                return Done("A pasta do disco não foi encontrada.");

            jobs = Mp4ConversionPlanner.Plan(files);
            if (jobs.Count == 0) return Done();

            var needed = Mp4ConversionPlanner.EstimateOutputBytes(jobs);
            var free = _freeSpace(discFolder);
            if (free is { } available && available < needed)
                return Done($"Não há espaço suficiente para converter: são necessários cerca de {Bytes(needed)} e há {Bytes(available)} livres no destino.");

            progress.Report(new Mp4ConversionProgress { JobCount = jobs.Count, Message = "Preparando a conversão para MP4…" });
            var encoders = await GetWorkingEncodersAsync(ct).ConfigureAwait(false);
            if (encoders.Count == 0)
                return Done("Nenhum codificador de vídeo disponível funcionou neste computador, então não foi possível converter para MP4.");
            preferred = encoders[0];

            createdFolder = !Directory.Exists(outputRoot);
            Directory.CreateDirectory(outputRoot);

            long totalBytes = Math.Max(1, jobs.Sum(j => j.InputBytes));
            long doneBytes = 0;
            for (int i = 0; i < jobs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var job = jobs[i];
                var index = i + 1;
                long before = doneBytes;

                void Report(double? jobFraction, string? message)
                {
                    var overall = Math.Clamp((before + (jobFraction ?? 0) * job.InputBytes) / totalBytes, 0, 1);
                    var elapsed = clock.Elapsed;
                    TimeSpan? eta = overall > 0.03 && elapsed > TimeSpan.FromSeconds(3)
                        ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - overall) / overall) : null;
                    progress.Report(new Mp4ConversionProgress
                    {
                        JobIndex = index, JobCount = jobs.Count, CurrentName = job.Name, JobFraction = jobFraction,
                        OverallFraction = overall, Elapsed = elapsed, Eta = eta, Message = message,
                    });
                }

                Report(0, $"Convertendo “{job.Name}” para MP4…");
                Mp4JobResult result;
                try
                {
                    result = await RunJobAsync(job, discFolder, encoders, f => Report(f, null), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result = new Mp4JobResult
                    {
                        Name = job.Name, Sources = job.Inputs, Status = Mp4JobStatus.Failed, FromDamagedSource = job.SourceDamaged,
                        Message = "Não foi possível converter este vídeo. (Detalhes: " + ex.Message + ")",
                    };
                }
                results.Add(result);
                doneBytes += job.InputBytes;
                Report(1, result.Status == Mp4JobStatus.Converted ? $"“{job.Name}” convertido." : $"Não foi possível converter “{job.Name}”.");
            }
            return Done();
        }
        catch (OperationCanceledException)
        {
            // The job in flight already removed its partial output. Jobs that never ran are reported as cancelled.
            foreach (var job in jobs.Skip(results.Count))
                results.Add(new Mp4JobResult { Name = job.Name, Sources = job.Inputs, Status = Mp4JobStatus.Cancelled, FromDamagedSource = job.SourceDamaged });
            return Done(cancelled: true);
        }
        catch (Exception ex)
        {
            return Done("A conversão para MP4 foi interrompida por um erro inesperado. (Detalhes: " + ex.Message + ")");
        }
        finally
        {
            if (createdFolder) TryDeleteEmptyDirectory(outputRoot);
        }
    }

    private async Task<Mp4JobResult> RunJobAsync(Mp4Job job, string discFolder, IReadOnlyList<string> encoders, Action<double?> onFraction, CancellationToken ct)
    {
        var inputs = job.Inputs.Select(p => Path.Combine(discFolder, p)).ToList();
        var present = inputs.Where(SafeExists).ToList();
        bool damaged = job.SourceDamaged || present.Count != inputs.Count;

        Mp4JobResult Failed(string message, string? encoder = null, TimeSpan sourceDuration = default) => new()
        {
            Name = job.Name, Sources = job.Inputs, Status = Mp4JobStatus.Failed, FromDamagedSource = damaged, Encoder = encoder,
            SourceDuration = sourceDuration, Message = message,
        };

        if (present.Count == 0) return Failed("Os arquivos copiados deste vídeo não foram encontrados na pasta do disco.");

        // Never overwrite anything that is already there.
        var relOutput = UniqueOnDisk(discFolder, job.Output);
        var output = Path.Combine(discFolder, relOutput);
        var partial = output + ".partial";
        var outputDir = Path.GetDirectoryName(output)!;
        var createdDirs = new List<string>(); // deepest first, so they can be removed again if nothing is produced
        for (var d = outputDir; d.Length > discFolder.Length && !Directory.Exists(d); d = Path.GetDirectoryName(d) ?? "") createdDirs.Add(d);
        Directory.CreateDirectory(outputDir);

        var source = await _probe.ProbeAsync(Mp4CommandLine.InputSpec(present), ct).ConfigureAwait(false);
        var sourceDuration = source?.Duration ?? TimeSpan.Zero;

        bool success = false;
        try
        {
            string? lastError = null;
            foreach (var encoder in encoders)
            {
                ct.ThrowIfCancellationRequested();
                TryDelete(partial);

                var args = Mp4CommandLine.Build(present, partial, encoder, source, damaged);
                try { OnCommand?.Invoke(Mp4CommandLine.ToText(args)); } catch { /* observer only */ }
                var run = await RunAsync(_ffmpeg, args, captureStdout: false,
                    onStdoutLine: line =>
                    {
                        if (Mp4Checks.TryParseOutTime(line, out var pos))
                            onFraction(sourceDuration > TimeSpan.Zero ? Math.Clamp(pos.TotalSeconds / sourceDuration.TotalSeconds, 0, 1) : null);
                    },
                    overallTimeout: null, stallTimeout: _stallTimeout, ct).ConfigureAwait(false);

                if (run.Stalled) { lastError = "O conversor parou de responder e foi interrompido."; continue; }
                if (run.ExitCode != 0 || SafeLength(partial) <= 0)
                {
                    lastError = "O conversor não conseguiu converter este vídeo." + Detail(run.StderrTail);
                    continue;
                }

                // Validate with ffprobe before the file gets its final name.
                var produced = await _probe.ProbeAsync(partial, ct).ConfigureAwait(false);
                if (produced == null || (produced.Width <= 0 && string.IsNullOrEmpty(produced.VideoCodec)))
                {
                    lastError = "O arquivo gerado não tem vídeo e foi descartado.";
                    continue;
                }
                string? warning = null;
                if (sourceDuration > TimeSpan.Zero && !Mp4Checks.DurationAcceptable(sourceDuration, produced.Duration))
                {
                    var text = $"A duração do MP4 ({Clock(produced.Duration)}) não confere com a do original ({Clock(sourceDuration)}).";
                    if (!damaged) { TryDelete(partial); return Failed(text + " O arquivo foi descartado.", encoder, sourceDuration); }
                    warning = text + " Isso é esperado quando a cópia tem trechos perdidos.";
                }
                else if (produced.Duration <= TimeSpan.Zero)
                {
                    lastError = "O arquivo gerado está vazio e foi descartado.";
                    continue;
                }

                File.Move(partial, output);
                success = true;
                var notes = new List<string>();
                if (damaged) notes.Add("Convertido a partir de uma cópia incompleta: o vídeo pode ter saltos ou trechos faltando.");
                if (warning != null) notes.Add(warning);
                return new Mp4JobResult
                {
                    Name = job.Name, Sources = job.Inputs, OutputPath = relOutput, Status = Mp4JobStatus.Converted, FromDamagedSource = damaged,
                    Encoder = encoder, OutputBytes = SafeLength(output), SourceDuration = sourceDuration, OutputDuration = produced.Duration,
                    Message = notes.Count == 0 ? null : string.Join(" ", notes),
                };
            }
            return Failed(lastError ?? "O conversor não conseguiu converter este vídeo.", sourceDuration: sourceDuration);
        }
        finally
        {
            if (!success)
            {
                TryDelete(partial);
                foreach (var d in createdDirs) TryDeleteEmptyDirectory(d);
            }
        }
    }

    // =========================================================================================================
    // Helpers
    // =========================================================================================================

    private static string UniqueOnDisk(string discFolder, string relative)
    {
        string Full(string r) => Path.Combine(discFolder, r);
        var candidate = relative;
        var stem = relative.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ? relative[..^4] : relative;
        for (int i = 2; SafeExists(Full(candidate)) || SafeExists(Full(candidate) + ".partial"); i++)
            candidate = stem + $" ({i}).mp4";
        return candidate;
    }


    private static string Detail(string stderrTail) =>
        string.IsNullOrWhiteSpace(stderrTail) ? "" : " (Detalhes: " + stderrTail.Trim() + ")";

    private static string Clock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }

    private static string Bytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = Math.Max(0, bytes);
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : v.ToString(v >= 100 ? "0" : "0.0", CultureInfo.GetCultureInfo("pt-BR")) + " " + units[i];
    }

    private static long SafeLength(string path) { try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; } }

    private static void TryDelete(string path)
    {
        // The process may still be releasing the file right after being killed.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try { if (File.Exists(path)) File.Delete(path); return; }
            catch (IOException) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) { Thread.Sleep(100); }
            catch { return; }
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
        catch { /* leave it */ }
    }

    // =========================================================================================================
    // Process runner
    // =========================================================================================================

    private sealed record ProcessResult(int ExitCode, string Stdout, string StderrTail, bool Stalled);

    /// <summary>
    /// Runs a tool and waits for it. Cancellation kills the whole process tree and throws <see cref="OperationCanceledException"/>.
    /// <paramref name="overallTimeout"/> and <paramref name="stallTimeout"/> (no stdout line for that long) also kill it.
    /// </summary>
    private async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, bool captureStdout, Action<string>? onStdoutLine,
        TimeSpan? overallTimeout, TimeSpan? stallTimeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        try { if (!process.Start()) return new ProcessResult(-1, "", "não foi possível iniciar o ffmpeg", false); }
        catch (Exception ex) { return new ProcessResult(-1, "", ex.Message, false); }
        try { process.StandardInput.Close(); } catch { }

        long lastActivity = Environment.TickCount64;
        var stdout = new StringBuilder();
        var stdoutTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                Interlocked.Exchange(ref lastActivity, Environment.TickCount64);
                if (captureStdout) stdout.AppendLine(line);
                try { onStdoutLine?.Invoke(line); } catch { /* a progress callback must never break the conversion */ }
            }
        });
        var tail = new Queue<string>();
        var stderrTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                lock (tail) { tail.Enqueue(line.Trim()); while (tail.Count > 3) tail.Dequeue(); }
            }
        });

        bool stalled = false;
        using var registration = ct.Register(() => Kill(process));
        var exited = process.WaitForExitAsync(CancellationToken.None);
        var started = Environment.TickCount64;
        while (!exited.IsCompleted)
        {
            await Task.WhenAny(exited, Task.Delay(500)).ConfigureAwait(false);
            if (exited.IsCompleted || ct.IsCancellationRequested) break;
            var now = Environment.TickCount64;
            if (stallTimeout is { } stall && now - Interlocked.Read(ref lastActivity) > stall.TotalMilliseconds) { stalled = true; Kill(process); break; }
            if (overallTimeout is { } total && now - started > total.TotalMilliseconds) { stalled = true; Kill(process); break; }
        }
        try { await exited.ConfigureAwait(false); } catch { }
        await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        string stderrTail;
        lock (tail) stderrTail = string.Join(" | ", tail);
        int code;
        try { code = process.ExitCode; } catch { code = -1; }
        return new ProcessResult(code, stdout.ToString(), stderrTail, stalled);
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }
}
