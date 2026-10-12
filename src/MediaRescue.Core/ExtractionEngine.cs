namespace MediaRescue.Core;

/// <summary>
/// The copy engine (RF-05/06/10). Stateless between runs: each <see cref="RunAsync"/> call builds its own job.
/// All timing goes through <see cref="IClock"/>.
/// </summary>
public sealed class ExtractionEngine : IExtractionEngine
{
    private readonly IDiscReader _reader;
    private readonly IDestinationFileSystem _fs;
    private readonly IOpticalDriveService _drives;
    private readonly IClock _clock;

    public ExtractionEngine(IDiscReader reader, IDestinationFileSystem fs, IOpticalDriveService drives, IClock clock)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _drives = drives ?? throw new ArgumentNullException(nameof(drives));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The job runs on the thread pool (never on the caller's synchronization context), so it is safe to call
    /// from the UI thread. Progress is reported from that pool thread.
    /// </remarks>
    public Task<ExtractionResult> RunAsync(ExtractionRequest request, IProgress<ExtractionProgress> progress, ExtractionControl control, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(control);
        var run = new ExtractionRun(_reader, _fs, _drives, _clock, request, progress, control, ct);
        return Task.Run(() => run.RunAsync());
    }

    /// <inheritdoc />
    public bool CanResume(DiscInfo disc, string destinationRoot, string folderName)
    {
        try
        {
            var state = JobFiles.LoadState(_fs, Path.Combine(destinationRoot, folderName));
            return state != null && string.Equals(state.Fingerprint, disc.Fingerprint, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
