using System.Diagnostics;
using MediaRescue.Core;

namespace MediaRescue.App.Demo;

/// <summary>
/// Demo-mode converter: plans the jobs for real (so the DVD titles of the demo disc show up) and simulates the conversion
/// with a progress bar. Nothing is written to disk.
/// </summary>
public sealed class DemoMp4Converter : IMp4Converter
{
    private readonly TimeSpan _perJob;

    public DemoMp4Converter(TimeSpan? perJob = null) { _perJob = perJob ?? TimeSpan.FromSeconds(4); }

    public bool IsAvailable => true;

    public async Task<Mp4ConversionResult> ConvertAsync(string discFolder, IReadOnlyList<FileResult> files, IProgress<Mp4ConversionProgress> progress, CancellationToken ct)
    {
        var jobs = Mp4ConversionPlanner.Plan(files);
        var results = new List<Mp4JobResult>();
        var clock = Stopwatch.StartNew();
        const string encoder = "h264_mf";
        try
        {
            progress.Report(new Mp4ConversionProgress { JobCount = jobs.Count, Message = "Preparando a conversão para MP4…" });
            await Task.Delay(400, ct);
            for (int i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                for (int step = 1; step <= 20; step++)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(_perJob / 20, ct);
                    var fraction = step / 20.0;
                    var overall = (i + fraction) / jobs.Count;
                    var elapsed = clock.Elapsed;
                    progress.Report(new Mp4ConversionProgress
                    {
                        JobIndex = i + 1, JobCount = jobs.Count, CurrentName = job.Name, JobFraction = fraction, OverallFraction = overall,
                        Elapsed = elapsed, Eta = overall > 0.05 ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - overall) / overall) : null,
                        Message = step == 1 ? $"Convertendo “{job.Name}” para MP4…" : null,
                    });
                }
                results.Add(new Mp4JobResult
                {
                    Name = job.Name, Sources = job.Inputs, OutputPath = job.Output, Status = Mp4JobStatus.Converted, FromDamagedSource = job.SourceDamaged,
                    Encoder = encoder, OutputBytes = (long)(job.InputBytes * 0.4), SourceDuration = TimeSpan.FromMinutes(12), OutputDuration = TimeSpan.FromMinutes(12),
                    Message = job.SourceDamaged ? "Convertido a partir de uma cópia incompleta: o vídeo pode ter saltos ou trechos faltando." : null,
                });
            }
            return new Mp4ConversionResult { Encoder = encoder, Jobs = results, Duration = clock.Elapsed };
        }
        catch (OperationCanceledException)
        {
            foreach (var job in jobs.Skip(results.Count))
                results.Add(new Mp4JobResult { Name = job.Name, Sources = job.Inputs, Status = Mp4JobStatus.Cancelled, FromDamagedSource = job.SourceDamaged });
            return new Mp4ConversionResult { Encoder = encoder, Jobs = results, WasCancelled = true, Duration = clock.Elapsed };
        }
    }
}
