using System.IO;
using System.Text;
using MediaRescue.App.Infrastructure;
using MediaRescue.App.Services;
using MediaRescue.Core;

namespace MediaRescue.App.Demo;

// In-memory fakes used by --demo / DEMO_ONLY builds. They simulate a DVD-Video disc in drive D:, a copy with one
// Partial file, history entries and diagnostics results. Nothing here touches real hardware or the real disk.

public static class DemoServices
{
    public static AppServices Create(StartupOptions options)
    {
        var state = options.DemoState;
        var hasDrive = state != "nodrive";
        var driveState = state is "nodrive" or "nodisc" ? DriveState.NoMedia : DriveState.Ready;
        var disc = DemoData.BuildDvdVideoDisc(options.DemoFiles);
        var fs = new DemoFileSystem();
        var clock = new DemoClock();
        var seconds = state switch { "partial" => 5, "converting" => 5, "converted" => 5, "copying" => 90, "batch" => 8, _ => 30 };
        // "converting" keeps the conversion running long enough to be captured; "converted" finishes quickly.
        var perJob = state switch { "converting" => TimeSpan.FromSeconds(40), "converted" => TimeSpan.FromSeconds(1.5), _ => TimeSpan.FromSeconds(4) };
        var drives = new DemoDriveService(hasDrive, driveState, disc, autoReinsert: state is null or "ready" or "batch");
        return new AppServices
        {
            Drives = drives,
            Engine = new DemoExtractionEngine(seconds),
            Catalog = new DemoCatalogStore(options.DemoDuplicate ? disc.Fingerprint : null),
            SettingsStore = new DemoSettingsStore(options.Theme, keepDisc: state is "converting" or "converted"),
            Validator = new DemoDestinationValidator(fs),
            Fs = fs,
            Probe = new DemoMediaProbe(),
            Mp4 = new DemoMp4Converter(perJob),
            Diagnostics = new DemoDriveDiagnostics(),
            Clock = clock,
            DestinationDrives = new DemoDestinationLister(),
            KeepAwake = () => new NoopDisposable(),
        };
    }
}

public sealed class NoopDisposable : IDisposable { public void Dispose() { } }

public static class DemoData
{
    public static DiscInfo BuildDvdVideoDisc(int extraFiles = 0)
    {
        var entries = new List<DiscEntry>();
        var t = new DateTime(2009, 12, 12, 18, 30, 0, DateTimeKind.Utc);
        void Dir(string p) => entries.Add(new DiscEntry { RelativePath = p, IsDirectory = true, ModifiedUtc = t });
        void File(string p, long size, string? group = null) => entries.Add(new DiscEntry
        {
            RelativePath = p, Size = size, ModifiedUtc = t.AddMinutes(entries.Count), Kind = DiscClassifier.KindOf(p), TitleGroup = group,
        });

        Dir("VIDEO_TS");
        File(@"VIDEO_TS\VIDEO_TS.IFO", 24_576);
        File(@"VIDEO_TS\VIDEO_TS.BUP", 24_576);
        File(@"VIDEO_TS\VIDEO_TS.VOB", 1_228_800);
        File(@"VIDEO_TS\VTS_01_0.IFO", 49_152);
        File(@"VIDEO_TS\VTS_01_0.BUP", 49_152);
        File(@"VIDEO_TS\VTS_01_0.VOB", 3_145_728);
        File(@"VIDEO_TS\VTS_01_1.VOB", 1_073_709_056, "VTS_01");
        File(@"VIDEO_TS\VTS_01_2.VOB", 641_728_512, "VTS_01");
        File(@"VIDEO_TS\VTS_02_0.IFO", 24_576);
        File(@"VIDEO_TS\VTS_02_0.BUP", 24_576);
        File(@"VIDEO_TS\VTS_02_1.VOB", 301_989_888, "VTS_02");
        Dir("Fotos");
        File(@"Fotos\Apresentação.jpg", 1_843_200);
        File(@"Fotos\Palco.jpg", 2_201_600);
        Dir(@"Fotos\Festa");
        for (int i = 1; i <= 6; i++) File($@"Fotos\Festa\IMG_{i:0000}.JPG", 2_300_000 + i * 41_000);
        File("LEIA-ME.txt", 1_024);

        if (extraFiles > 0)
        {
            Dir(@"Fotos\Lote");
            for (int i = 0; i < extraFiles; i++) File($@"Fotos\Lote\IMG_{i:000000}.JPG", 2_000_000 + (i % 100) * 1_000);
        }

        long used = entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
        return new DiscInfo
        {
            VolumeLabel = "FORMATURA",
            VolumeSerial = "3A5F-91C2",
            PhysicalType = "DVD-R",
            MediaState = "Finalizado",
            Sessions = 1,
            Tracks = 1,
            FileSystem = "UDF 1.02",
            UsedBytes = used,
            CapacityBytes = 4_700_000_000,
            RecordedAtUtc = t,
            LogicalFormat = LogicalFormat.DvdVideo,
            Entries = entries,
        };
    }
}

public sealed class DemoClock : IClock
{
    private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
    public DateTime UtcNow => DateTime.UtcNow;
    public long ElapsedMs => _sw.ElapsedMilliseconds;
    public Task Delay(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}

public sealed class DemoDriveService : IOpticalDriveService
{
    public static readonly OpticalDrive Drive = new("D:", "hp", "CDDVDW TS-L633M", "0301", "USB", false);
    private readonly bool _hasDrive;
    private readonly DiscInfo _disc;
    private readonly bool _autoReinsert;
    private DriveState _state;

    public DemoDriveService(bool hasDrive, DriveState state, DiscInfo disc, bool autoReinsert)
    {
        _hasDrive = hasDrive;
        _state = state;
        _disc = disc;
        _autoReinsert = autoReinsert;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<OpticalDrive> GetDrives() => _hasDrive ? new[] { Drive } : Array.Empty<OpticalDrive>();
    public DriveState GetState(string letter) => _hasDrive ? _state : DriveState.NoMedia;

    public async Task<DiscInfo?> AnalyzeAsync(string letter, CancellationToken ct)
    {
        await Task.Delay(600, ct);
        return _state == DriveState.Ready ? _disc : null;
    }

    public bool Eject(string letter)
    {
        _state = DriveState.NoMedia;
        Changed?.Invoke(this, EventArgs.Empty);
        if (_autoReinsert)
        {
            // Demo convenience: "someone" puts the disc back a few seconds later so the flows can be tried again.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(6));
                    _state = DriveState.Ready;
                    Changed?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception) { /* demo only */ }
            });
        }
        return true;
    }

    public bool CloseTray(string letter) => false;
    public bool LockMedia(string letter, bool locked) => true;
    public bool SetReadSpeed(string letter, int kbPerSec) => true;
    public void Dispose() { }
}

public sealed class DemoExtractionEngine : IExtractionEngine
{
    private readonly double _seconds;
    public DemoExtractionEngine(double seconds) { _seconds = Math.Max(2, seconds); }

    public bool CanResume(DiscInfo disc, string destinationRoot, string folderName) => false;

    public async Task<ExtractionResult> RunAsync(ExtractionRequest request, IProgress<ExtractionProgress> progress, ExtractionControl control, CancellationToken ct)
    {
        var folder = Path.Combine(request.DestinationRoot, request.FolderName);
        var files = request.Entries.Where(e => !e.IsDirectory).ToList();
        long total = Math.Max(1, files.Sum(f => f.Size));
        long done = 0;
        var results = new List<FileResult>();
        int intact = 0, errors = 0;
        long lost = 0;
        var started = DateTime.UtcNow;
        var perTick = Math.Max(1.0, total / (_seconds * 10));
        // The second video file is the one with a damaged area.
        var damaged = files.Where(f => f.Kind == EntryKind.Video).Skip(1).FirstOrDefault();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        ExtractionProgress Make(ExtractionPhase phase, int index, DiscEntry? f, long fileDone, string? message, bool etaKnown = true)
        {
            var elapsed = sw.Elapsed;
            var speed = elapsed.TotalSeconds > 0.5 ? done / elapsed.TotalSeconds : 0;
            TimeSpan? eta = etaKnown && elapsed > TimeSpan.FromSeconds(10) && speed > 0 ? TimeSpan.FromSeconds((total - done) / speed) : null;
            return new ExtractionProgress
            {
                Phase = phase, CurrentFile = f?.RelativePath, FileIndex = index, FileCount = files.Count,
                FileBytesDone = fileDone, FileBytesTotal = f?.Size ?? 0, TotalBytesDone = done, TotalBytesTotal = total,
                BytesPerSecond = speed, Eta = eta, Elapsed = elapsed, FilesIntact = intact, FilesWithErrors = errors, BytesLost = lost,
                Message = message,
            };
        }

        try
        {
            progress.Report(Make(ExtractionPhase.Preparing, 0, null, 0, "Preparando a cópia…"));
            await Task.Delay(500, ct);

            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                long fileDone = 0;
                bool secondPassDone = false;
                bool skipped = false;
                long fileLost = 0;
                var fileSw = System.Diagnostics.Stopwatch.StartNew();
                progress.Report(Make(ExtractionPhase.Copying, i + 1, f, 0, $"Copiando {f.RelativePath}…"));

                while (fileDone < f.Size)
                {
                    while (control.IsPaused && !ct.IsCancellationRequested)
                    {
                        progress.Report(Make(ExtractionPhase.Paused, i + 1, f, fileDone, null));
                        await Task.Delay(200, ct);
                    }
                    ct.ThrowIfCancellationRequested();
                    if (control.ConsumeSkip()) { skipped = true; break; }

                    await Task.Delay(100, ct);
                    var step = (long)Math.Min(Math.Max(perTick, 1), f.Size - fileDone);
                    fileDone += step;
                    done += step;

                    if (ReferenceEquals(f, damaged) && !secondPassDone && fileDone > f.Size * 0.6)
                    {
                        secondPassDone = true;
                        errors = 1;
                        progress.Report(Make(ExtractionPhase.SecondPass, i + 1, f, fileDone,
                            $"Trecho ilegível em {f.RelativePath}; tentando recuperar…", etaKnown: false));
                        for (int k = 0; k < 15; k++)
                        {
                            ct.ThrowIfCancellationRequested();
                            await Task.Delay(150, ct);
                            progress.Report(Make(ExtractionPhase.SecondPass, i + 1, f, fileDone, null, etaKnown: false));
                        }
                        fileLost = 131_072;
                        lost += fileLost;
                        progress.Report(Make(ExtractionPhase.Copying, i + 1, f, fileDone, "Não foi possível recuperar 128 KB; o arquivo será salvo incompleto."));
                    }
                    else
                    {
                        progress.Report(Make(ExtractionPhase.Copying, i + 1, f, fileDone, null));
                    }
                }

                var status = skipped ? FileStatus.Skipped : fileLost > 0 ? FileStatus.Partial : FileStatus.Intact;
                if (status == FileStatus.Intact) intact++;
                results.Add(new FileResult
                {
                    RelativePath = f.RelativePath, WrittenPath = f.RelativePath, Status = status, BytesTotal = f.Size, BytesLost = fileLost,
                    BadRanges = fileLost > 0 ? new[] { new ByteRange((long)(f.Size * 0.6), fileLost) } : Array.Empty<ByteRange>(),
                    Sha256 = status == FileStatus.Skipped ? null : "demo", Duration = fileSw.Elapsed,
                });
            }

            progress.Report(Make(ExtractionPhase.Verifying, files.Count, null, 0, "Verificando a cópia…"));
            await Task.Delay(700, ct);
            var overall = results.Any(r => r.Status == FileStatus.Partial) ? OverallStatus.Partial : OverallStatus.Intact;
            progress.Report(Make(ExtractionPhase.Completed, files.Count, null, 0, "Cópia concluída."));
            return new ExtractionResult(folder, overall, results, sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            return new ExtractionResult(folder, OverallStatus.Cancelled, results, sw.Elapsed);
        }
    }
}

public sealed class DemoCatalogStore : ICatalogStore
{
    private readonly List<CatalogEntry> _entries = new();

    public DemoCatalogStore(string? demoDiscFingerprint)
    {
        var now = DateTime.UtcNow;
        _entries.Add(Make("Aniversário da vovó 2005", now.AddDays(-41), @"E:\Vídeos da família\Aniversário da vovó 2005", 3_912_000_000, 0, 9, OverallStatus.Intact));
        _entries.Add(Make("Festa junina 2007", now.AddDays(-33), @"E:\Vídeos da família\Festa junina 2007", 2_104_000_000, 0, 14, OverallStatus.Recovered));
        _entries.Add(Make("Natal 2008 (disco riscado)", now.AddDays(-20), @"E:\Vídeos da família\Natal 2008 (disco riscado)", 4_201_000_000, 18_350_080, 11, OverallStatus.Partial));
        _entries.Add(Make("Viagem à praia", now.AddDays(-12), @"E:\Vídeos da família\Viagem à praia", 1_287_000_000, 0, 6, OverallStatus.Intact));
        _entries.Add(Make("Apresentação de balé", now.AddDays(-5), @"F:\Backup\Apresentação de balé", 2_650_000_000, 0, 8, OverallStatus.Intact));
        _entries.Add(Make("CD ilegível 12", now.AddDays(-2), @"E:\Vídeos da família\CD ilegível 12", 0, 700_000_000, 4, OverallStatus.Failed));
        if (demoDiscFingerprint != null)
            _entries.Add(new CatalogEntry
            {
                Fingerprint = demoDiscFingerprint, Name = "FORMATURA", ExtractedAtUtc = now.AddDays(-9), DestinationPath = @"E:\Vídeos da família\FORMATURA",
                TotalBytes = 2_100_000_000, FileCount = 22, Status = OverallStatus.Intact,
            });
    }

    private static CatalogEntry Make(string name, DateTime when, string path, long bytes, long lost, int files, OverallStatus status) => new()
    {
        Fingerprint = Guid.NewGuid().ToString("N"), Name = name, ExtractedAtUtc = when, DestinationPath = path,
        TotalBytes = bytes, BytesLost = lost, FileCount = files, Status = status,
    };

    public string? LoadWarning => null;
    public IReadOnlyList<CatalogEntry> GetAll() { lock (_entries) return _entries.ToList(); }
    public CatalogEntry? FindByFingerprint(string fingerprint) { lock (_entries) return _entries.FirstOrDefault(e => e.Fingerprint == fingerprint); }
    public void Add(CatalogEntry entry) { lock (_entries) _entries.Add(entry); }

    public string ExportCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Nome,Data,Pasta,Tamanho (bytes),Perdido (bytes),Arquivos,Estado");
        lock (_entries)
            foreach (var e in _entries)
                sb.AppendLine($"\"{e.Name}\",{e.ExtractedAtUtc:yyyy-MM-dd HH:mm},\"{e.DestinationPath}\",{e.TotalBytes},{e.BytesLost},{e.FileCount},{e.Status}");
        return sb.ToString();
    }
}

public sealed class DemoSettingsStore : ISettingsStore
{
    private AppSettings _settings;
    // keepDisc: do not eject the disc when the copy ends (the MP4 demo states show the disc list behind the conversion).
    public DemoSettingsStore(string? theme = null, bool keepDisc = false)
    {
        _settings = new AppSettings { LastDestination = @"E:\Vídeos da família", Theme = theme ?? "System", EjectWhenDone = !keepDisc };
    }
    public AppSettings Load() => _settings;
    public void Save(AppSettings settings) => _settings = settings;
}

public sealed class DemoDestinationLister : IDestinationDriveLister
{
    public IReadOnlyList<string> ListRoots() => new[] { @"C:\", @"E:\", @"G:\" };
}

public sealed class DemoFileSystem : IDestinationFileSystem
{
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\", @"E:\", @"G:\", @"E:\Vídeos da família", @"E:\Vídeos da família\FORMATURA", @"C:\Users",
    };
    private readonly Dictionary<string, string> _texts = new(StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string p) => p.Length > 3 ? p.TrimEnd('\\') : p;

    public DestinationInfo? GetInfo(string path)
    {
        var root = (Path.GetPathRoot(path) ?? "").ToUpperInvariant();
        return root switch
        {
            @"E:\" => new DestinationInfo(root, "KINGSTON", "exFAT", 22_870_000_000, 31_900_000_000, true, false, false),
            @"C:\" => new DestinationInfo(root, "Windows", "NTFS", 8_700_000_000, 511_000_000_000, false, false, false),
            @"G:\" => new DestinationInfo(root, "Google Drive", "FAT32", 96_000_000_000, 200_000_000_000, false, true, false),
            @"D:\" => new DestinationInfo(root, "FORMATURA", "UDF", 0, 4_700_000_000, true, false, true),
            _ => null,
        };
    }

    public bool DirectoryExists(string path) => _folders.Contains(Normalize(path));
    public bool FileExists(string path) => false;
    public void CreateDirectory(string path) => _folders.Add(Normalize(path));
    public Stream Create(string path) => new MemoryStream();
    public Stream OpenRead(string path) => new MemoryStream();
    public void FlushToDisk(Stream stream) { }
    public void Move(string source, string destination) { }
    public void Delete(string path) { }
    public long GetFileSize(string path) => 0;
    public void SetTimes(string path, DateTime modifiedUtc) { }
    public bool ProbeWritable(string directory, out string? error) { error = null; return true; }
    public string? ReadAllText(string path) => _texts.GetValueOrDefault(path);
    public void WriteAllTextAtomic(string path, string content) => _texts[path] = content;
}

public sealed class DemoDestinationValidator : IDestinationValidator
{
    private const long FourGiB = 4L * 1024 * 1024 * 1024;
    private readonly IDestinationFileSystem _fs;
    public DemoDestinationValidator(IDestinationFileSystem fs) { _fs = fs; }

    public IReadOnlyList<ValidationIssue> Validate(string destinationRoot, string folderName, IReadOnlyList<DiscEntry> entries, string opticalDriveLetter)
    {
        var issues = new List<ValidationIssue>();
        var info = _fs.GetInfo(destinationRoot);
        if (info == null)
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Blocking, "Essa pasta ou unidade não existe. Escolha outra pasta de destino."));
            return issues;
        }
        if (info.IsOptical || (opticalDriveLetter.Length > 0 && destinationRoot.StartsWith(opticalDriveLetter, StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Blocking, "O destino não pode ser a própria unidade do leitor. Escolha um pendrive, HD ou pasta do computador."));
            return issues;
        }
        var bytes = entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
        var needed = (long)(bytes * 1.05) + 50L * 1024 * 1024;
        if (info.FreeBytes < needed)
            issues.Add(new ValidationIssue(ValidationSeverity.Blocking,
                $"Não há espaço suficiente no destino: faltam {Infrastructure.Fmt.Bytes(needed - info.FreeBytes)}. Libere espaço ou escolha outro destino."));
        if (info.FileSystem.Equals("FAT32", StringComparison.OrdinalIgnoreCase))
            foreach (var big in entries.Where(e => !e.IsDirectory && e.Size >= FourGiB))
                issues.Add(new ValidationIssue(ValidationSeverity.Blocking,
                    $"“{big.RelativePath}” tem {Infrastructure.Fmt.Bytes(big.Size)} e não cabe em um destino FAT32 (limite de 4 GB). Formate o pendrive em exFAT ou escolha outro destino.", big.RelativePath));
        if (info.IsNetworkOrCloud)
            issues.Add(new ValidationIssue(ValidationSeverity.Warning,
                "Este destino é uma unidade de nuvem ou de rede: a cópia usa espaço temporário no disco C: e pode falhar se faltar espaço lá."));
        if (destinationRoot.StartsWith("C:", StringComparison.OrdinalIgnoreCase) && info.FreeBytes - needed < 10_000_000_000L)
            issues.Add(new ValidationIssue(ValidationSeverity.Warning,
                "O disco C: ficará com menos de 10 GB livres depois da cópia. O Windows pode ficar lento."));
        return issues;
    }
}

public sealed class DemoMediaProbe : IMediaProbe
{
    public bool IsAvailable => true;

    public async Task<VideoProbe?> ProbeAsync(string fullPath, CancellationToken ct)
    {
        await Task.Delay(350, ct);
        var h = (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(fullPath);
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (ext is ".vob" or ".dat" or ".mpg")
            return new VideoProbe(TimeSpan.FromSeconds(600 + h % 2400), 720, 480, 29.97, "mpeg2video", "ac3", 6_000_000, true);
        return new VideoProbe(TimeSpan.FromSeconds(60 + h % 300), 1280, 720, 30, "h264", "aac", 4_000_000, false);
    }

    public Task<byte[]?> ThumbnailAsync(string fullPath, TimeSpan at, CancellationToken ct) => Task.FromResult<byte[]?>(null);
}

public sealed class DemoDriveDiagnostics : IDriveDiagnostics
{
    /// <summary>Like the real diagnostics: the tray test (which ejects the disc) only runs when the user ticked the box.</summary>
    public bool IncludeTrayTest { get; set; }

    public async Task<IReadOnlyList<DiagnosticCheck>> RunAsync(string letter, bool fullSurfaceScan, IProgress<DiagnosticCheck> progress, CancellationToken ct)
    {
        var list = new List<DiagnosticCheck>();
        async Task Add(DiagnosticCheck c)
        {
            await Task.Delay(450, ct);
            list.Add(c);
            progress.Report(c);
        }

        await Add(new DiagnosticCheck("detected", "Leitor detectado pelo Windows", CheckOutcome.Pass,
            $"hp CDDVDW TS-L633M, firmware 0301, conectado por USB em {letter}. O driver está funcionando."));
        await Add(new DiagnosticCheck("responds", "O leitor responde aos comandos", CheckOutcome.Pass,
            "O dispositivo foi aberto e respondeu à verificação de mídia em 14 ms."));
        if (IncludeTrayTest)
            await Add(new DiagnosticCheck("tray", "Bandeja abre e fecha", CheckOutcome.NotSupported,
                "Leitores finos (slim) não fecham a bandeja por comando. Isso é normal e não indica defeito."));
        else
            await Add(new DiagnosticCheck("tray", "Bandeja abre e fecha", CheckOutcome.Skipped,
                "Teste da bandeja desativado para não ejetar o disco que está no leitor."));
        await Add(new DiagnosticCheck("media", "Reconhece o disco", CheckOutcome.Pass, "DVD-R finalizado, 1 sessão, UDF 1.02."));
        await Add(new DiagnosticCheck("sampling", "Leitura de amostra (20 pontos do disco)", CheckOutcome.Warning,
            "18 de 20 pontos foram lidos. Velocidade média 6,1 MB/s; a mais baixa foi 0,4 MB/s.",
            "Alguns trechos do disco estão lentos. Limpe o disco com um pano macio, do centro para a borda, e teste de novo. " +
            "Se continuar, teste com outro disco para saber se o problema é o disco ou o leitor."));
        if (fullSurfaceScan)
            await Add(new DiagnosticCheck("surface", "Teste completo da superfície", CheckOutcome.Fail,
                "47 de 2.241 blocos não puderam ser lidos (2,1% do disco), concentrados perto do final.",
                "O disco tem uma área danificada. A extração ainda tenta recuperar o máximo possível; vídeos nessa área podem ficar com pequenos “saltos”."));
        else
            await Add(new DiagnosticCheck("surface", "Teste completo da superfície", CheckOutcome.Skipped,
                "Teste opcional e demorado; marque “Teste completo” para executá-lo."));
        return list;
    }
}



