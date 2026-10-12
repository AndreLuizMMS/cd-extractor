using System.Diagnostics;
using System.Globalization;
using System.Text;
using MediaRescue.Core;
using MediaRescue.Windows.Native;

namespace MediaRescue.Windows;

/// <summary>
/// RF-07: six drive checks with plain-language pt-BR results. Everything is non-destructive and works without
/// administrator rights; when raw device access is denied the affected checks report NotSupported instead of Fail.
/// </summary>
public sealed class DriveDiagnostics : IDriveDiagnostics
{
    private const int MiB = 1024 * 1024;
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    private const string TipUsbDirect = "Conecte o leitor direto em uma porta USB do computador (não use hub nem extensão).";
    private const string TipUsbY = "Se o leitor tiver cabo em Y (dois plugues USB), conecte os dois: leitores slim precisam de mais energia do que uma porta entrega.";
    private const string TipClean = "Limpe o disco com um pano macio e seco, do centro para a borda (nunca em círculos).";
    private const string TipOtherDisc = "Teste com outro disco para separar defeito do leitor de defeito do disco.";

    private readonly IOpticalDriveService _drives;
    private readonly IDiscReader _reader;

    /// <summary>
    /// When true, check 3 ejects and closes the tray, which throws the disc out of the drive. Off by default (opt-in): the
    /// UI only turns it on when the user explicitly asks for the tray test.
    /// </summary>
    public bool IncludeTrayTest { get; set; }

    public DriveDiagnostics(IOpticalDriveService drives, IDiscReader reader)
    {
        _drives = drives ?? throw new ArgumentNullException(nameof(drives));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async Task<IReadOnlyList<DiagnosticCheck>> RunAsync(string letter, bool fullSurfaceScan, IProgress<DiagnosticCheck> progress, CancellationToken ct)
    {
        var results = new List<DiagnosticCheck>();
        void Report(DiagnosticCheck c)
        {
            results.Add(c);
            try { progress?.Report(c); } catch { }
        }

        var l = DeviceIo.NormalizeLetter(letter);
        if (l == null)
        {
            Report(new DiagnosticCheck("detected", "Leitor detectado pelo Windows", CheckOutcome.Fail, "Letra de unidade inválida.", TipUsbDirect));
            foreach (var c in SkippedFrom(2, "Sem leitor selecionado.")) Report(c);
            return results;
        }

        DiscInfo? disc = null;
        DriveState state = DriveState.NoMedia;

        Report(await Guarded("detected", "Leitor detectado pelo Windows", () => CheckDetected(l, ct)).ConfigureAwait(false));
        ct.ThrowIfCancellationRequested();
        Report(await Guarded("responds", "O leitor responde a comandos", () => Task.FromResult(CheckResponds(l))).ConfigureAwait(false));
        ct.ThrowIfCancellationRequested();
        Report(await Guarded("tray", "Bandeja abre e fecha", () => CheckTray(l, ct)).ConfigureAwait(false));
        ct.ThrowIfCancellationRequested();

        var mediaCheck = await Guarded("media", "Reconhecimento do disco", async () =>
        {
            var (check, d, st) = await CheckMedia(l, ct).ConfigureAwait(false);
            disc = d; state = st;
            return check;
        }).ConfigureAwait(false);
        Report(mediaCheck);
        ct.ThrowIfCancellationRequested();

        bool hasMedia = state is DriveState.Ready or DriveState.Protected or DriveState.AudioCd;
        Report(await Guarded("sampling", "Teste de leitura por amostragem", () => CheckSampling(l, disc, hasMedia, ct)).ConfigureAwait(false));
        ct.ThrowIfCancellationRequested();

        if (!fullSurfaceScan)
            Report(new DiagnosticCheck("surface", "Teste de superfície completo", CheckOutcome.Skipped, "Não solicitado. Este teste lê o disco inteiro e pode demorar bastante."));
        else
            Report(await Guarded("surface", "Teste de superfície completo", () => CheckSurface(l, disc, hasMedia, ct)).ConfigureAwait(false));

        return results;
    }

    private static IEnumerable<DiagnosticCheck> SkippedFrom(int firstIndex, string reason)
    {
        var all = new[]
        {
            ("responds", "O leitor responde a comandos"), ("tray", "Bandeja abre e fecha"), ("media", "Reconhecimento do disco"),
            ("sampling", "Teste de leitura por amostragem"), ("surface", "Teste de superfície completo"),
        };
        return all.Skip(firstIndex - 2).Select(x => new DiagnosticCheck(x.Item1, x.Item2, CheckOutcome.Skipped, reason));
    }

    private static async Task<DiagnosticCheck> Guarded(string id, string title, Func<Task<DiagnosticCheck>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DiagnosticCheck(id, title, CheckOutcome.Fail, "Não foi possível concluir este teste: " + ex.Message, TipUsbDirect);
        }
    }

    // ---------------------------------------------------------------- 1. detected

    private async Task<DiagnosticCheck> CheckDetected(string l, CancellationToken ct)
    {
        const string title = "Leitor detectado pelo Windows";
        var drive = _drives.GetDrives().FirstOrDefault(d => string.Equals(d.Letter, l, StringComparison.OrdinalIgnoreCase));
        if (drive == null)
            return new DiagnosticCheck("detected", title, CheckOutcome.Fail, $"O Windows não encontrou nenhum leitor de CD/DVD em {l}.",
                TipUsbDirect + " " + TipUsbY + " Depois de reconectar, aguarde alguns segundos e teste de novo.");

        string Dash(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;
        var sb = new StringBuilder();
        sb.Append($"Modelo: {Dash((drive.Vendor + " " + drive.Model).Trim())} · Firmware: {Dash(drive.Firmware)} · Barramento: {Dash(drive.BusType)}");
        if (drive.IsVirtual) sb.Append(" · Unidade virtual (imagem de disco montada)");

        var outcome = CheckOutcome.Pass;
        string? tip = null;
        if (!drive.IsVirtual)
        {
            var wmi = await Task.Run(() => WmiInventory.Query(TimeSpan.FromSeconds(6)), ct).ConfigureAwait(false);
            var entry = wmi.FirstOrDefault(w => w.Letter.Equals(l, StringComparison.OrdinalIgnoreCase));
            if (entry != null)
            {
                if (entry.ConfigErrorCode != 0)
                {
                    outcome = CheckOutcome.Warning;
                    sb.Append($" · Driver: o Windows reportou um problema (código {entry.ConfigErrorCode})");
                    tip = "Desconecte e reconecte o leitor. Se persistir, tente outra porta USB ou reinicie o computador.";
                }
                else
                {
                    sb.Append($" · Driver: {(string.IsNullOrEmpty(entry.Status) ? "OK" : entry.Status)}");
                }
            }
            else
            {
                sb.Append(" · Driver: sem informação");
            }
        }
        return new DiagnosticCheck("detected", title, outcome, sb.ToString(), tip);
    }

    // ---------------------------------------------------------------- 2. responds

    private static DiagnosticCheck CheckResponds(string l)
    {
        const string title = "O leitor responde a comandos";
        var sw = Stopwatch.StartNew();
        using var h = DeviceIo.OpenDevice(l, out int error);
        if (h == null)
        {
            if (error == Win32Errors.ERROR_ACCESS_DENIED)
                return new DiagnosticCheck("responds", title, CheckOutcome.NotSupported,
                    "O Windows não permitiu o acesso direto ao leitor sem privilégios de administrador. A cópia de arquivos continua funcionando normalmente.");
            return new DiagnosticCheck("responds", title, CheckOutcome.Fail,
                $"Não foi possível abrir o leitor (código {error}).", TipUsbDirect + " " + TipUsbY);
        }

        int cv = DeviceIo.CheckVerify(h);
        sw.Stop();
        // "No media" / "not ready" are normal answers of a healthy drive.
        if (cv is 0 or Win32Errors.ERROR_NO_MEDIA_IN_DRIVE or Win32Errors.ERROR_NOT_READY or Win32Errors.ERROR_MEDIA_CHANGED or 1785 or 1129)
        {
            var what = cv == 0 ? "com disco pronto" : "sem disco pronto";
            return new DiagnosticCheck("responds", title, CheckOutcome.Pass, $"O leitor respondeu em {sw.ElapsedMilliseconds} ms ({what}).");
        }
        return new DiagnosticCheck("responds", title, CheckOutcome.Fail,
            $"O leitor não respondeu corretamente (código {cv}).", TipUsbDirect + " " + TipUsbY);
    }

    // ---------------------------------------------------------------- 3. tray

    private async Task<DiagnosticCheck> CheckTray(string l, CancellationToken ct)
    {
        const string title = "Bandeja abre e fecha";
        var drive = _drives.GetDrives().FirstOrDefault(d => string.Equals(d.Letter, l, StringComparison.OrdinalIgnoreCase));
        if (drive is { IsVirtual: true })
            return new DiagnosticCheck("tray", title, CheckOutcome.NotSupported, "Unidade virtual: não tem bandeja.");
        if (!IncludeTrayTest)
            return new DiagnosticCheck("tray", title, CheckOutcome.Skipped, "Teste da bandeja desativado para não ejetar o disco que está no leitor.");

        var before = _drives.GetState(l);
        bool hadMedia = before is DriveState.Ready or DriveState.Protected or DriveState.AudioCd or DriveState.Blank or DriveState.Unfinalized or DriveState.Unreadable;

        if (!_drives.Eject(l))
            return new DiagnosticCheck("tray", title, CheckOutcome.Warning,
                "O leitor não abriu a bandeja por comando. Isso pode acontecer se outro programa estiver usando o disco.",
                "Feche programas que possam estar usando o disco (Explorador, player de vídeo) e tente de novo; ou use o botão físico do leitor.");

        bool opened = await WaitForState(l, s => s is DriveState.NoMedia or DriveState.TrayOpen, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (!opened)
            return new DiagnosticCheck("tray", title, CheckOutcome.Warning, "O comando de abrir foi aceito, mas o leitor não confirmou a abertura da bandeja.");

        bool closeAccepted = _drives.CloseTray(l);
        // Wait for the user's disc to come back (also covers a manual push on slim drives).
        bool back = !hadMedia || await WaitForState(l, s => s is not (DriveState.NoMedia or DriveState.TrayOpen), TimeSpan.FromSeconds(closeAccepted ? 25 : 12), ct).ConfigureAwait(false);

        if (!closeAccepted)
            return new DiagnosticCheck("tray", title, CheckOutcome.NotSupported,
                "A bandeja abriu por comando, mas este leitor não fecha sozinho (comum em leitores slim). Isso é normal.",
                "Feche a bandeja empurrando-a com a mão" + (hadMedia ? "; o disco foi ejetado durante este teste." : "."));
        return back
            ? new DiagnosticCheck("tray", title, CheckOutcome.Pass, "A bandeja abriu e fechou por comando.")
            : new DiagnosticCheck("tray", title, CheckOutcome.Warning, "A bandeja abriu e recebeu o comando de fechar, mas o disco não voltou a ser reconhecido.", TipOtherDisc);
    }

    private async Task<bool> WaitForState(string l, Func<DriveState, bool> predicate, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (predicate(_drives.GetState(l))) return true;
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        return predicate(_drives.GetState(l));
    }

    // ---------------------------------------------------------------- 4. media

    private async Task<(DiagnosticCheck, DiscInfo?, DriveState)> CheckMedia(string l, CancellationToken ct)
    {
        const string title = "Reconhecimento do disco";
        var state = _drives.GetState(l);
        switch (state)
        {
            case DriveState.NoMedia:
            case DriveState.TrayOpen:
                return (new DiagnosticCheck("media", title, CheckOutcome.Skipped,
                    "O Windows não identificou nenhum disco no leitor. Insira um disco para executar este teste.",
                    "Se já há um disco no leitor, ele não foi reconhecido: confira se a bandeja fechou por completo e se o lado gravado está para baixo. " +
                    TipUsbDirect + " " + TipUsbY + " " + TipOtherDisc), null, state);
            case DriveState.Blank:
                return (new DiagnosticCheck("media", title, CheckOutcome.Pass, "O leitor reconheceu um disco virgem (vazio)."), null, state);
            case DriveState.Unfinalized:
                return (new DiagnosticCheck("media", title, CheckOutcome.Warning, "O leitor reconheceu um disco gravado, mas não finalizado.",
                    "Finalize o disco no aparelho ou programa em que foi gravado."), null, state);
            case DriveState.Unreadable:
                return (new DiagnosticCheck("media", title, CheckOutcome.Fail, "O leitor detectou o disco, mas não conseguiu ler o índice dele.",
                    TipClean + " " + TipOtherDisc), null, state);
        }

        var disc = await _drives.AnalyzeAsync(l, ct).ConfigureAwait(false);
        if (disc == null)
        {
            var st = _drives.GetState(l);
            return (new DiagnosticCheck("media", title, st == DriveState.Loading ? CheckOutcome.Warning : CheckOutcome.Fail,
                "O disco não ficou pronto para leitura.", TipClean + " " + TipOtherDisc), null, st);
        }

        string Dash(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s;
        var detail = $"Tipo: {Dash(disc.PhysicalType)} · Estado: {Dash(disc.MediaState)} · Sistema de arquivos: {Dash(disc.FileSystem)} · " +
                     $"Rótulo: {Dash(disc.VolumeLabel)} · Sessões: {disc.Sessions?.ToString(Pt) ?? "—"} · Usado: {FormatBytes(disc.UsedBytes)}";
        if (state == DriveState.AudioCd) detail = $"CD de áudio com {disc.Tracks?.ToString(Pt) ?? "—"} faixas (não suportado pelo extrator, mas o leitor o reconheceu).";
        if (state == DriveState.Protected || disc.IsCopyProtected)
            return (new DiagnosticCheck("media", title, CheckOutcome.Warning, detail + " · Disco com proteção contra cópia.",
                "Discos comerciais protegidos não são suportados por este aplicativo."), disc, state);
        return (new DiagnosticCheck("media", title, CheckOutcome.Pass, detail), disc, state);
    }

    // ---------------------------------------------------------------- 5. sampling

    private async Task<DiagnosticCheck> CheckSampling(string l, DiscInfo? disc, bool hasMedia, CancellationToken ct)
    {
        const string title = "Teste de leitura por amostragem";
        if (!hasMedia)
            return new DiagnosticCheck("sampling", title, CheckOutcome.Skipped, "Sem disco para ler. Insira um disco para executar este teste.");

        using var raw = OverlappedHandle.Open(DeviceIo.DevicePath(l), NativeMethods.GENERIC_READ, NativeMethods.FILE_SHARE_ALL,
            NativeMethods.FILE_FLAG_NO_BUFFERING, out int error);

        long size = 0;
        Func<long, int, CancellationToken, Task<int>> readAt;
        string via = "";
        if (raw != null)
        {
            var len = raw.GetLength();
            size = len > 0 ? len : (disc?.UsedBytes ?? 0);
            if (disc?.UsedBytes > 0 && (size <= 0 || disc.UsedBytes < size)) size = disc.UsedBytes;
            var buf = new byte[MiB];
            readAt = (pos, count, token) => raw.ReadAsync(pos, count, 0, buf.AsMemory(0, count), TimeSpan.FromSeconds(30), token);
        }
        else if (error == Win32Errors.ERROR_ACCESS_DENIED)
        {
            // Raw access denied: sample through the biggest file with the regular reader if there is one.
            var biggest = disc?.Entries.Where(e => !e.IsDirectory && e.Error == null).OrderByDescending(e => e.Size).FirstOrDefault();
            if (biggest == null || biggest.Size < 64 * 1024)
                return new DiagnosticCheck("sampling", title, CheckOutcome.NotSupported,
                    "O Windows não permitiu leitura direta do disco sem privilégios de administrador, e não há arquivo grande para amostrar. A cópia de arquivos continua funcionando.");
            var file = _reader.Open(l, biggest.RelativePath);
            try
            {
                return await SampleCore(file.Length, (pos, count, token) =>
                {
                    var b = new byte[count];
                    return file.ReadAsync(pos, b, TimeSpan.FromSeconds(30), token);
                }, 1, $" (amostragem pelo arquivo \"{biggest.RelativePath}\", pois o acesso direto ao leitor foi negado)", ct).ConfigureAwait(false);
            }
            finally { file.Dispose(); }
        }
        else
        {
            return new DiagnosticCheck("sampling", title, CheckOutcome.Fail, $"Não foi possível abrir o leitor para leitura (código {error}).", TipUsbDirect + " " + TipUsbY);
        }

        if (size <= 0)
            return new DiagnosticCheck("sampling", title, CheckOutcome.NotSupported, "Não foi possível determinar o tamanho do disco para amostrar.");
        return await SampleCore(size, readAt, Iso9660.SectorSize, via, ct).ConfigureAwait(false);
    }

    private static async Task<DiagnosticCheck> SampleCore(long size, Func<long, int, CancellationToken, Task<int>> readAt, int align, string via, CancellationToken ct)
    {
        const string title = "Teste de leitura por amostragem";
        int points = (int)Math.Clamp(size / MiB, 1, 20);
        int ok = 0, failed = 0;
        long bytes = 0;
        double totalSeconds = 0, minSpeed = double.MaxValue;
        int minPoint = 0;
        var errors = new List<string>();

        for (int i = 0; i < points; i++)
        {
            ct.ThrowIfCancellationRequested();
            long span = Math.Max(0, size - MiB);
            long pos = points == 1 ? 0 : span * i / (points - 1);
            pos -= pos % align;
            int count = (int)Math.Min(MiB, size - pos);
            if (align > 1) count -= count % align;
            if (count <= 0) { count = (int)Math.Min(size - pos, MiB); }

            var sw = Stopwatch.StartNew();
            try
            {
                int n = await readAt(pos, count, ct).ConfigureAwait(false);
                sw.Stop();
                if (n <= 0) throw new IOException("leitura vazia");
                ok++;
                bytes += n;
                double secs = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                totalSeconds += secs;
                double speed = n / (double)MiB / secs;
                if (speed < minSpeed) { minSpeed = speed; minPoint = i + 1; }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"ponto {i + 1}: {(ex is DiscReadException d ? $"código {d.Win32Code}" : ex.Message)}");
            }
        }

        if (ok == 0)
            return new DiagnosticCheck("sampling", title, CheckOutcome.Fail,
                $"Nenhum dos {points} pontos pôde ser lido ({string.Join("; ", errors.Take(3))}).{via}",
                TipUsbDirect + " " + TipUsbY + " " + TipClean + " " + TipOtherDisc);

        double avg = bytes / (double)MiB / Math.Max(totalSeconds, 0.001);
        var detail = $"{ok} de {points} pontos lidos ({FormatBytes(bytes)}). Velocidade média: {avg.ToString("0.0", Pt)} MB/s; mínima: {minSpeed.ToString("0.0", Pt)} MB/s (ponto {minPoint}).{via}";
        if (failed > 0)
        {
            detail += $" Falhas: {failed} ({string.Join("; ", errors.Take(3))}).";
            return new DiagnosticCheck("sampling", title, failed > 2 || failed * 4 > points ? CheckOutcome.Fail : CheckOutcome.Warning, detail,
                TipClean + " " + TipOtherDisc + " Se outros discos também falharem, o problema é do leitor: " + TipUsbDirect.ToLowerInvariant());
        }
        if (minSpeed < 0.3)
            return new DiagnosticCheck("sampling", title, CheckOutcome.Warning, detail + " Algumas áreas foram muito lentas.",
                TipClean + " " + TipOtherDisc);
        return new DiagnosticCheck("sampling", title, CheckOutcome.Pass, detail);
    }

    // ---------------------------------------------------------------- 6. surface

    private async Task<DiagnosticCheck> CheckSurface(string l, DiscInfo? disc, bool hasMedia, CancellationToken ct)
    {
        const string title = "Teste de superfície completo";
        if (!hasMedia)
            return new DiagnosticCheck("surface", title, CheckOutcome.Skipped, "Sem disco para ler. Insira um disco para executar este teste.");

        using var raw = OverlappedHandle.Open(DeviceIo.DevicePath(l), NativeMethods.GENERIC_READ, NativeMethods.FILE_SHARE_ALL,
            NativeMethods.FILE_FLAG_NO_BUFFERING, out int error);
        if (raw == null)
        {
            return error == Win32Errors.ERROR_ACCESS_DENIED
                ? new DiagnosticCheck("surface", title, CheckOutcome.NotSupported, "O Windows não permitiu leitura direta do disco sem privilégios de administrador.")
                : new DiagnosticCheck("surface", title, CheckOutcome.Fail, $"Não foi possível abrir o leitor para leitura (código {error}).", TipUsbDirect + " " + TipUsbY);
        }

        long size = raw.GetLength();
        if (size <= 0) size = disc?.UsedBytes ?? 0;
        if (disc?.UsedBytes > 0 && disc.UsedBytes < size) size = disc.UsedBytes;
        if (size <= 0)
            return new DiagnosticCheck("surface", title, CheckOutcome.NotSupported, "Não foi possível determinar o tamanho do disco.");

        const int chunk = 256 * 1024;
        const int columns = 60, rows = 4, cells = columns * rows;
        var map = new byte[cells]; // 0 good, 1 slow, 2 bad (worst wins), 255 untouched
        Array.Fill(map, (byte)255);
        int good = 0, slow = 0, bad = 0;
        long chunks = (size + chunk - 1) / chunk;
        var buf = new byte[chunk];
        var total = Stopwatch.StartNew();

        for (long c = 0; c < chunks; c++)
        {
            ct.ThrowIfCancellationRequested();
            long pos = c * chunk;
            int count = (int)Math.Min(chunk, size - pos);
            count = (count + 2047) & ~2047;
            byte status;
            var sw = Stopwatch.StartNew();
            try
            {
                int n = await raw.ReadAsync(pos, count, 0, buf.AsMemory(0, count), TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                sw.Stop();
                status = n <= 0 ? (byte)2 : sw.Elapsed > TimeSpan.FromSeconds(1.5) ? (byte)1 : (byte)0;
            }
            catch (OperationCanceledException) { throw; }
            catch { status = 2; }

            if (status == 0) good++; else if (status == 1) slow++; else bad++;
            int cell = (int)Math.Min(cells - 1, c * cells / chunks);
            if (map[cell] == 255 || status > map[cell]) map[cell] = status;
        }

        var sb = new StringBuilder();
        sb.Append($"Leitura de {FormatBytes(size)} em {total.Elapsed:mm\\:ss}. Trechos bons: {good}, lentos: {slow}, ilegíveis: {bad}.\n");
        sb.Append("Mapa do disco (início → fim; . = bom, ~ = lento, X = ilegível):\n");
        for (int r = 0; r < rows; r++)
        {
            for (int col = 0; col < columns; col++)
            {
                var v = map[r * columns + col];
                sb.Append(v switch { 0 => '.', 1 => '~', 2 => 'X', _ => ' ' });
            }
            sb.Append('\n');
        }
        var detail = sb.ToString().TrimEnd();
        if (bad == 0 && slow == 0) return new DiagnosticCheck("surface", title, CheckOutcome.Pass, detail);
        double badPct = bad * 100.0 / chunks;
        return new DiagnosticCheck("surface", title, bad > 0 && badPct >= 1 ? CheckOutcome.Fail : CheckOutcome.Warning, detail,
            TipClean + " " + TipOtherDisc + " Trechos ilegíveis serão preenchidos com zeros na cópia, e o relatório dirá o que faltou.");
    }

    private static string FormatBytes(long b)
    {
        if (b < 1024) return b.ToString(Pt) + " B";
        double v = b;
        string[] units = { "KB", "MB", "GB", "TB" };
        int i = -1;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return v.ToString(v >= 100 ? "0" : "0.0", Pt) + " " + units[i];
    }
}
