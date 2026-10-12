using System.Diagnostics;
using System.Security.Cryptography;
using MediaRescue.Core;
using MediaRescue.Integration.Tests.Support;
using MediaRescue.Windows;
using Xunit.Abstractions;

namespace MediaRescue.Integration.Tests;

public sealed class PowerGuardAndSingleInstanceTests
{
    [Fact]
    public async Task PowerGuard_CanBeCreatedAndDisposedFromDifferentThreads()
    {
        var guard = new PowerGuard();
        guard.IsActive.Should().BeTrue();
        await Task.Run(() => guard.Dispose());
        guard.IsActive.Should().BeFalse();
        guard.Dispose(); // idempotent
    }

    [Fact]
    public async Task SingleInstance_SecondInstanceIsDetected_AndFirstIsAskedToShowItself()
    {
        var name = "MediaRescueTest" + Guid.NewGuid().ToString("N");
        using var first = new SingleInstance(name);
        first.IsFirstInstance.Should().BeTrue();
        var activated = new TaskCompletionSource();
        first.ActivationRequested += () => activated.TrySetResult();

        using (var second = new SingleInstance(name))
        {
            second.IsFirstInstance.Should().BeFalse();
            second.SignalExisting().Should().BeTrue();
        }
        (await Task.WhenAny(activated.Task, Task.Delay(3000))).Should().Be(activated.Task, "the first instance must be told to come to the front");
    }

    [Fact]
    public void SingleInstance_AfterFirstIsDisposed_ANewInstanceBecomesFirst()
    {
        var name = "MediaRescueTest" + Guid.NewGuid().ToString("N");
        var first = new SingleInstance(name);
        first.IsFirstInstance.Should().BeTrue();
        first.Dispose();
        using var again = new SingleInstance(name);
        again.IsFirstInstance.Should().BeTrue();
    }
}

/// <summary>[Fact] that only runs when MEDIARESCUE_REAL_DRIVE=1 (read-only checks against the physical drive).</summary>
public sealed class RealDriveFactAttribute : FactAttribute
{
    public RealDriveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MEDIARESCUE_REAL_DRIVE") != "1")
            Skip = "SKIPPED: set MEDIARESCUE_REAL_DRIVE=1 to run read-only checks against the physical optical drive.";
    }
}

/// <summary>Read-only report against the physical drive(s). Never ejects, writes or copies disc contents.</summary>
public sealed class RealDriveReport
{
    private readonly ITestOutputHelper _out;
    public RealDriveReport(ITestOutputHelper output) => _out = output;

    [RealDriveFact]
    public async Task ReportOnPhysicalDrives()
    {
        using var svc = new OpticalDriveService();
        var reader = new DiscReader();
        var drives = svc.GetDrives();
        _out.WriteLine($"GetDrives: {drives.Count} drive(s)");
        foreach (var d in drives)
        {
            _out.WriteLine($"  {d.Letter} vendor='{d.Vendor}' model='{d.Model}' firmware='{d.Firmware}' bus='{d.BusType}' virtual={d.IsVirtual}");
            var sw = Stopwatch.StartNew();
            var state = svc.GetState(d.Letter);
            _out.WriteLine($"  GetState -> {state} ({sw.ElapsedMilliseconds} ms)");
            if (d.IsVirtual) continue;

            if (state is DriveState.Ready or DriveState.Protected or DriveState.AudioCd)
            {
                sw.Restart();
                var info = await svc.AnalyzeAsync(d.Letter, CancellationToken.None);
                _out.WriteLine($"  AnalyzeAsync ({sw.Elapsed.TotalSeconds:0.0} s): " + (info == null ? "null" :
                    $"label='{info.VolumeLabel}' serial={info.VolumeSerial} fs={info.FileSystem} type={info.PhysicalType ?? "—"} state={info.MediaState ?? "—"} " +
                    $"sessions={info.Sessions?.ToString() ?? "—"} tracks={info.Tracks?.ToString() ?? "—"} used={info.UsedBytes} capacity={info.CapacityBytes?.ToString() ?? "—"} " +
                    $"recorded={info.RecordedAtUtc:u} format={info.LogicalFormat} protected={info.IsCopyProtected} entries={info.Entries.Count} " +
                    $"errors={info.Entries.Count(e => e.Error != null)} fingerprint={info.Fingerprint[..12]}"));
                if (info != null)
                {
                    foreach (var e in info.Entries.Take(15))
                        _out.WriteLine($"     {(e.IsDirectory ? "<DIR>" : e.Size.ToString().PadLeft(10))}  {e.RelativePath}  {e.Kind}{(e.Error != null ? "  ERROR=" + e.Error : "")}");
                    // A few small reads, hash only (nothing is copied anywhere).
                    foreach (var f in info.Entries.Where(e => !e.IsDirectory && e.Error == null && e.Size > 0).OrderBy(e => e.Size).Take(3))
                    {
                        try
                        {
                            using var file = reader.Open(d.Letter, f.RelativePath);
                            var buf = new byte[(int)Math.Min(file.Length, 256 * 1024)];
                            sw.Restart();
                            int n = await file.ReadAsync(0, buf, TimeSpan.FromSeconds(30), CancellationToken.None);
                            _out.WriteLine($"     read {f.RelativePath}: {n} bytes in {sw.ElapsedMilliseconds} ms, sectorSize={file.SectorSize}, sha256={Convert.ToHexString(SHA256.HashData(buf.AsSpan(0, n)))[..16]}");
                        }
                        catch (DiscReadException ex) { _out.WriteLine($"     read {f.RelativePath}: DiscReadException {ex.Kind} win32={ex.Win32Code}"); }
                    }
                }
            }

            var diag = new DriveDiagnostics(svc, reader) { IncludeTrayTest = false };
            var results = await diag.RunAsync(d.Letter, false, new Progress<DiagnosticCheck>(), CancellationToken.None);
            foreach (var r in results)
                _out.WriteLine($"  [{r.Outcome}] {r.Id}: {r.Title}\n      {r.Detail}{(r.Tip != null ? "\n      Dica: " + r.Tip : "")}");
        }
    }
}
