using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DiscUtils.Iso9660;
using MediaRescue.Core;
using MediaRescue.Windows;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MediaRescue.Integration.Tests.Support;

internal static class TestEnv
{
    public static readonly string Root = FindRoot();
    public static string ToolsDir => Path.Combine(Root, "tools");
    public static string TestMediaDir => Path.Combine(Root, ".testmedia");
    public static bool HasTools => File.Exists(Path.Combine(ToolsDir, "ffprobe.exe")) && File.Exists(Path.Combine(ToolsDir, "ffmpeg.exe"));

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MediaRescue.sln"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    public static string NewTempDir(string prefix = "mr-it")
    {
        var p = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(p);
        return p;
    }
}

/// <summary>Describes the content of a test disc; builds an ISO 9660 + Joliet image from it.</summary>
internal sealed class IsoSpec
{
    public string Label { get; init; } = "TESTDISC";
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> EmptyDirs { get; } = new();

    public IsoSpec Add(string path, byte[] data) { Files[path] = data; return this; }
    public IsoSpec AddRandom(string path, int length, int seed) => Add(path, RandomBytes(length, seed));

    public static byte[] RandomBytes(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    public string Build(string directory, string fileName)
    {
        var builder = new CDBuilder { UseJoliet = true, VolumeIdentifier = Label };
        foreach (var d in EmptyDirs) builder.AddDirectory(d);
        foreach (var (path, data) in Files) builder.AddFile(path, data);
        var iso = Path.Combine(directory, fileName);
        builder.Build(iso);
        return iso;
    }

    public Dictionary<string, string> Hashes() =>
        Files.ToDictionary(kv => kv.Key, kv => Convert.ToHexString(SHA256.HashData(kv.Value)), StringComparer.OrdinalIgnoreCase);
}

internal static class IsoLayouts
{
    public static IsoSpec DataDisc(string label = "DADOS") => new IsoSpec { Label = label }
        .AddRandom("video1.avi", 300_000, 1)
        .AddRandom("fotos\\foto1.jpg", 12_345, 2)
        .AddRandom("fotos\\foto2.jpg", 54_321, 3)
        .AddRandom("LEIAME.txt", 100, 4);

    public static IsoSpec Vcd() => new IsoSpec { Label = "VCD_FESTA" }
        .AddRandom("MPEGAV\\AVSEQ01.DAT", 2324 * 200, 11)   // not a multiple of 2048: exercises the sector alignment path
        .AddRandom("MPEGAV\\AVSEQ02.DAT", 2324 * 77 + 5, 12)
        .AddRandom("VCD\\INFO.VCD", 2048, 13)
        .AddRandom("VCD\\ENTRIES.VCD", 2048, 14);

    public static IsoSpec VideoTs()
    {
        var spec = new IsoSpec { Label = "FORMATURA" };
        spec.EmptyDirs.Add("AUDIO_TS");
        var real = Path.Combine(TestEnv.TestMediaDir, "dvd.vob");
        spec.Add("VIDEO_TS\\VTS_01_1.VOB", File.Exists(real) ? File.ReadAllBytes(real) : IsoSpec.RandomBytes(800_000, 21));
        return spec
            .AddRandom("VIDEO_TS\\VIDEO_TS.IFO", 4096, 22)
            .AddRandom("VIDEO_TS\\VIDEO_TS.BUP", 4096, 23)
            .AddRandom("VIDEO_TS\\VIDEO_TS.VOB", 100_000, 24)
            .AddRandom("VIDEO_TS\\VTS_01_0.IFO", 8192, 25)
            .AddRandom("VIDEO_TS\\VTS_01_0.BUP", 8192, 26)
            .AddRandom("VIDEO_TS\\VTS_01_0.VOB", 50_000, 27)
            .AddRandom("VIDEO_TS\\VTS_01_2.VOB", 700_000, 28)
            .AddRandom("VIDEO_TS\\VTS_02_1.VOB", 600_000, 29);
    }

    public static IsoSpec DvdRtav() => new IsoSpec { Label = "GRAVADOR" }
        .AddRandom("DVD_RTAV\\VR_MANGR.IFO", 8192, 31)
        .AddRandom("DVD_RTAV\\VR_MOVIE.VRO", 1_500_000, 32)
        .AddRandom("DVD_RTAV\\VR_STILL.VRO", 20_000, 33);

    public static IsoSpec Mixed()
    {
        var spec = new IsoSpec { Label = "Álbum da família" };
        spec.EmptyDirs.Add("Pasta vazia");
        return spec
            .AddRandom("Férias 2009 – Praia\\Ação e emoção ☺.avi", 200_000, 41)
            .AddRandom("Férias 2009 – Praia\\ÁÉÍÓÚ ãõç.jpg", 9_999, 42)
            .AddRandom("日本語のフォルダ\\ファイル.mpg", 15_000, 43)
            .AddRandom("zero.bin", 0, 44)
            .AddRandom(LongPath(), 77_777, 45)
            .AddRandom("docs\\relatório final (versão 2).docx", 33_333, 46);
    }

    /// <summary>A path whose total length exceeds MAX_PATH (260) once the drive root is added.</summary>
    public static string LongPath()
    {
        var parts = Enumerable.Range(1, 5).Select(i => $"nivel{i}_" + new string((char)('a' + i), 50)).ToList();
        return string.Join("\\", parts) + "\\arquivo com nome bem longo para passar de 260 caracteres.mpg";
    }

    public static IsoSpec ManySmallFiles(int count = 3000)
    {
        var spec = new IsoSpec { Label = "MUITOS" };
        for (int i = 0; i < count; i++)
            spec.AddRandom($"pasta{i % 30:D2}\\arquivo{i:D5}.dat", 1 + (i * 37) % 700, 1000 + i);
        return spec;
    }

    public static IsoSpec Empty() => new IsoSpec { Label = "VAZIO" };
}

/// <summary>A mounted ISO (virtual CD-ROM drive). Always dismounts on Dispose.</summary>
internal sealed class MountedIso : IDisposable
{
    public string ImagePath { get; }
    public string Letter { get; }
    private bool _disposed;

    private MountedIso(string imagePath, string letter) { ImagePath = imagePath; Letter = letter; }

    public static MountedIso Mount(string isoPath)
    {
        var script = $"$ErrorActionPreference='Stop'; Mount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}' -StorageType ISO -Access ReadOnly | Out-Null; " +
                     $"$l=$null; for($i=0;$i -lt 40 -and -not $l;$i++){{ $l=(Get-DiskImage -ImagePath '{isoPath.Replace("'", "''")}' | Get-Volume).DriveLetter; if(-not $l){{Start-Sleep -Milliseconds 250}} }}; if($l){{ Write-Output \"LETTER=$l\" }} else {{ Write-Output 'LETTER=' }}";
        var (code, output) = RunPowerShell(script);
        var line = output.Split('\n').Select(s => s.Trim()).LastOrDefault(s => s.StartsWith("LETTER=", StringComparison.Ordinal));
        var letter = line?.Substring(7).Trim();
        if (code != 0 || string.IsNullOrEmpty(letter))
        {
            TryDismount(isoPath);
            throw new InvalidOperationException($"Mount-DiskImage failed (exit {code}): {output.Trim()}");
        }
        return new MountedIso(isoPath, letter.ToUpperInvariant() + ":");
    }

    public void Dismount()
    {
        if (_disposed) return;
        _disposed = true;
        TryDismount(ImagePath);
    }

    public void Dispose() => Dismount();

    private static void TryDismount(string isoPath)
    {
        try { RunPowerShell($"Dismount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}' | Out-Null"); } catch { }
    }

    public static (int ExitCode, string Output) RunPowerShell(string script)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000)) { try { p.Kill(true); } catch { } return (-1, "timeout"); }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}

/// <summary>Probes once whether this machine/user can mount ISO images (Mount-DiskImage).</summary>
internal static class MountSupport
{
    private static readonly Lazy<string?> Reason = new(Probe);

    /// <summary>Null when mounting works; otherwise the reason the integration tests are skipped.</summary>
    public static string? SkipReason => Reason.Value;

    private static string? Probe()
    {
        string dir = TestEnv.NewTempDir("mr-probe");
        try
        {
            var iso = new IsoSpec { Label = "PROBE" }.AddRandom("a.txt", 10, 1).Build(dir, "probe.iso");
            using var m = MountedIso.Mount(iso);
            return Directory.Exists(m.Letter + "\\") ? null : "Mount-DiskImage mounted the image but the drive is not accessible.";
        }
        catch (Exception ex)
        {
            return "Mounting ISO images is not available here (Mount-DiskImage): " + ex.Message;
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

/// <summary>[Fact] that is reported as SKIPPED (with the reason) when ISO mounting is not possible.</summary>
public sealed class MountFactAttribute : FactAttribute
{
    public MountFactAttribute()
    {
        var reason = MountSupport.SkipReason;
        if (reason != null) Skip = "SKIPPED: " + reason;
    }
}

/// <summary>[Fact] skipped when ffprobe/ffmpeg are not in tools\ (run build\fetch-tools.ps1).</summary>
public sealed class ToolsFactAttribute : FactAttribute
{
    public ToolsFactAttribute()
    {
        if (!TestEnv.HasTools || !Directory.Exists(TestEnv.TestMediaDir))
            Skip = "SKIPPED: tools\\ffmpeg.exe/ffprobe.exe or .testmedia\\ missing (run build\\fetch-tools.ps1 and build\\make-test-media.ps1).";
    }
}

internal static class DiscHelpers
{
    public static async Task<string> HashDiscFileAsync(IDiscReader reader, string letter, string relativePath, int blockSize = 64 * 1024)
    {
        using var file = reader.Open(letter, relativePath);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[blockSize];
        long offset = 0;
        while (offset < file.Length)
        {
            int n = await file.ReadAsync(offset, buf, TimeSpan.FromSeconds(30), CancellationToken.None);
            if (n <= 0) break;
            sha.AppendData(buf, 0, n);
            offset += n;
        }
        offset.Should().Be(file.Length, $"all bytes of {relativePath} must be read");
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    public static async Task<DiscInfo> AnalyzeOrFailAsync(IOpticalDriveService svc, string letter)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var info = await svc.AnalyzeAsync(letter, cts.Token);
        info.Should().NotBeNull($"drive {letter} holds a readable disc");
        return info!;
    }

    public static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }
}
