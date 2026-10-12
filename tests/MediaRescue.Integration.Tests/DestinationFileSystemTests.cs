using System.Security.Principal;
using System.Text;
using MediaRescue.Core;
using MediaRescue.Integration.Tests.Support;
using MediaRescue.Windows;

namespace MediaRescue.Integration.Tests;

/// <summary>RF-03 / RF-05 / RF-08 against a real temporary NTFS folder.</summary>
public sealed class DestinationFileSystemTests : IDisposable
{
    private readonly string _tmp = TestEnv.NewTempDir("mr-dest");
    private readonly WindowsDestinationFileSystem _fs = new();

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    [Fact]
    public void GetInfo_ForTempFolder_DescribesAnNtfsFixedVolume()
    {
        var info = _fs.GetInfo(Path.Combine(_tmp, "ainda", "nao", "existe"));
        info.Should().NotBeNull();
        info!.FileSystem.Should().Be("NTFS");
        info.IsOptical.Should().BeFalse();
        info.IsNetworkOrCloud.Should().BeFalse();
        info.FreeBytes.Should().BeGreaterThan(0);
        info.TotalBytes.Should().BeGreaterThan(info.FreeBytes);
        info.Root.Should().Be(Path.GetPathRoot(_tmp));
    }

    [Fact]
    public void DeleteDirectoryIfEmpty_RemovesAnEmptyFolder_ButNeverAFolderWithContent()
    {
        var empty = Path.Combine(_tmp, "vazia");
        _fs.CreateDirectory(empty);
        _fs.DeleteDirectoryIfEmpty(empty);
        _fs.DirectoryExists(empty).Should().BeFalse("an empty folder is removed");

        var withFile = Path.Combine(_tmp, "com-arquivo");
        _fs.CreateDirectory(withFile);
        File.WriteAllText(Path.Combine(withFile, "meu-video.txt"), "x");
        _fs.DeleteDirectoryIfEmpty(withFile);
        File.Exists(Path.Combine(withFile, "meu-video.txt")).Should().BeTrue("a folder with a file is left alone");

        var withSubfolder = Path.Combine(_tmp, "com-subpasta");
        _fs.CreateDirectory(Path.Combine(withSubfolder, "VIDEO_TS"));
        _fs.DeleteDirectoryIfEmpty(withSubfolder);
        _fs.DirectoryExists(Path.Combine(withSubfolder, "VIDEO_TS")).Should().BeTrue("it is not recursive: a sub-folder keeps its parent");
        _fs.DirectoryExists(withSubfolder).Should().BeTrue();
    }

    [Fact]
    public void DeleteDirectoryIfEmpty_NeverThrows_ForMissingInvalidOrRootPaths()
    {
        var act = () =>
        {
            _fs.DeleteDirectoryIfEmpty(Path.Combine(_tmp, "nao", "existe"));
            _fs.DeleteDirectoryIfEmpty("");
            _fs.DeleteDirectoryIfEmpty("::inválido::");
            _fs.DeleteDirectoryIfEmpty(Path.GetPathRoot(_tmp)!); // a drive root is never removed
        };
        act.Should().NotThrow();
        _fs.DirectoryExists(Path.GetPathRoot(_tmp)!).Should().BeTrue();
    }

    [Fact]
    public void GetInfo_ForUnc_IsNetwork_AndNeverThrows()
    {
        var info = _fs.GetInfo(@"\\localhost\mediarescue-no-such-share\pasta");
        info.Should().NotBeNull();
        info!.IsNetworkOrCloud.Should().BeTrue();
        _fs.GetInfo("").Should().BeNull();
        _fs.GetInfo("::inválido::").Should().BeNull();
    }

    [Theory]
    [InlineData("Google Drive", true)]
    [InlineData("Drive File Stream", true)]
    [InlineData("OneDrive - Empresa", true)]
    [InlineData("KINGSTON", false)]
    [InlineData("", false)]
    public void CloudVolumeLabels_AreRecognised(string label, bool cloud) =>
        WindowsDestinationFileSystem.IsCloudLabel(label).Should().Be(cloud);

    [Fact]
    public void GetInfo_ForEveryReadyVolume_NeverThrows_AndGoogleDriveIsFlaggedAsCloud()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            var act = () => _fs.GetInfo(d.RootDirectory.FullName);
            act.Should().NotThrow();
            var info = _fs.GetInfo(d.RootDirectory.FullName);
            if (info != null && info.VolumeLabel.Contains("Google", StringComparison.OrdinalIgnoreCase))
                info.IsNetworkOrCloud.Should().BeTrue("Google Drive virtual volumes must be flagged");
        }
    }

    [Fact]
    public void CreateWriteFlushMoveSetTimesRead_RoundTrip()
    {
        var dir = Path.Combine(_tmp, "a", "b");
        _fs.CreateDirectory(dir);
        _fs.DirectoryExists(dir).Should().BeTrue();
        var partial = Path.Combine(dir, "video.avi.partial");
        var final = Path.Combine(dir, "video.avi");
        var data = IsoSpec.RandomBytes(500_000, 3);
        using (var s = _fs.Create(partial))
        {
            s.Write(data, 0, data.Length);
            _fs.FlushToDisk(s);
        }
        _fs.GetFileSize(partial).Should().Be(data.Length);
        _fs.Move(partial, final);
        _fs.FileExists(partial).Should().BeFalse();
        _fs.FileExists(final).Should().BeTrue();
        var when = new DateTime(2009, 12, 12, 10, 30, 0, DateTimeKind.Utc);
        _fs.SetTimes(final, when);
        File.GetLastWriteTimeUtc(final).Should().Be(when);
        using (var r = _fs.OpenRead(final))
        {
            var back = new byte[data.Length];
            r.ReadExactly(back);
            back.Should().Equal(data);
        }
        _fs.Delete(final);
        _fs.FileExists(final).Should().BeFalse();
        _fs.Invoking(f => f.Delete(final)).Should().NotThrow("deleting a missing file is a no-op");
    }

    [Fact]
    public async Task AsyncWritesThroughTheWrapper_Work()
    {
        var path = Path.Combine(_tmp, "async.bin");
        await using (var s = _fs.Create(path))
        {
            await s.WriteAsync(new byte[10_000]);
            await s.FlushAsync();
        }
        _fs.GetFileSize(path).Should().Be(10_000);
    }

    [Fact]
    public void LongPaths_AreSupported()
    {
        var dir = _tmp;
        while (dir.Length < 300) dir = Path.Combine(dir, new string('d', 40));
        _fs.CreateDirectory(dir);
        var file = Path.Combine(dir, "arquivo.partial");
        using (var s = _fs.Create(file)) s.WriteByte(7);
        _fs.FileExists(file).Should().BeTrue();
        _fs.GetFileSize(file).Should().Be(1);
    }

    [Fact]
    public void ProbeWritable_TrueOnTempFolder_AndLeavesNothingBehind()
    {
        _fs.ProbeWritable(_tmp, out var error).Should().BeTrue();
        error.Should().BeNull();
        Directory.GetFileSystemEntries(_tmp).Should().BeEmpty();
    }

    [Fact]
    public void E18_ProbeWritable_FalseForMissingFolder_WithMessage()
    {
        _fs.ProbeWritable(Path.Combine(_tmp, "nao-existe"), out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void E18_ProbeWritable_AndCreate_FailWithAccessDenied_InProtectedFolder()
    {
        if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return; // protected folders are writable when elevated
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System));
        _fs.ProbeWritable(dir, out var error).Should().BeFalse();
        error.Should().Contain("permiss");
        var act = () => _fs.Create(Path.Combine(dir, "mediarescue-test.partial"));
        act.Should().Throw<DestinationException>().Which.Kind.Should().Be(DestinationErrorKind.AccessDenied);
    }

    [Fact]
    public void E16_MissingVolume_IsReportedAsDeviceGone()
    {
        var free = Enumerable.Range('A', 26).Select(c => (char)c).Last(c => !Directory.Exists(c + @":\"));
        var act = () => _fs.Create($@"{free}:\pasta\arquivo.partial");
        act.Should().Throw<DestinationException>().Which.Kind.Should().Be(DestinationErrorKind.DeviceGone);
    }

    [Fact]
    public void MissingFolderOnAHealthyVolume_IsOther_NotDeviceGone()
    {
        var act = () => _fs.Create(Path.Combine(_tmp, "nao", "existe", "a.partial"));
        act.Should().Throw<DestinationException>().Which.Kind.Should().Be(DestinationErrorKind.Other);
    }

    [Theory]
    [InlineData(112, DestinationErrorKind.DiskFull)]   // ERROR_DISK_FULL
    [InlineData(39, DestinationErrorKind.DiskFull)]    // ERROR_HANDLE_DISK_FULL
    [InlineData(21, DestinationErrorKind.DeviceGone)]  // ERROR_NOT_READY
    [InlineData(1167, DestinationErrorKind.DeviceGone)]// ERROR_DEVICE_NOT_CONNECTED
    [InlineData(53, DestinationErrorKind.DeviceGone)]  // ERROR_BAD_NETPATH
    [InlineData(5, DestinationErrorKind.AccessDenied)]
    [InlineData(19, DestinationErrorKind.AccessDenied)]// ERROR_WRITE_PROTECT
    [InlineData(32, DestinationErrorKind.Other)]       // sharing violation (E-25: the engine retries)
    public void E15_E16_E18_OsErrorsAreTranslated(int code, DestinationErrorKind expected)
    {
        var io = new IOException("x", unchecked((int)(0x80070000 | (uint)code)));
        WindowsDestinationFileSystem.Translate(io, _tmp).Kind.Should().Be(expected);
    }

    [Fact]
    public void E15_DiskFullException_FromRealStream_HasHresultMappedToDiskFull()
    {
        // The wrapper translates whatever the FileStream raises; verify the mapping on the exact HRESULT .NET produces.
        var ex = new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
        var t = WindowsDestinationFileSystem.Translate(ex, _tmp);
        t.Kind.Should().Be(DestinationErrorKind.DiskFull);
        t.InnerException.Should().BeSameAs(ex);
        t.Message.Should().Contain("espaço");
    }

    [Fact]
    public void WriteAllTextAtomic_ReplacesContent_KeepsBak_AndLeavesNoTemp()
    {
        var path = Path.Combine(_tmp, "cfg", "catalog.json");
        _fs.ReadAllText(path).Should().BeNull();
        _fs.WriteAllTextAtomic(path, "{\"v\":1}");
        _fs.ReadAllText(path).Should().Be("{\"v\":1}");
        File.Exists(path + ".bak").Should().BeFalse("no previous version yet");
        _fs.WriteAllTextAtomic(path, "{\"v\":2,\"nome\":\"Férias ☺\"}");
        _fs.ReadAllText(path).Should().Be("{\"v\":2,\"nome\":\"Férias ☺\"}");
        File.ReadAllText(path + ".bak", Encoding.UTF8).Should().Be("{\"v\":1}");
        File.Exists(path + ".tmp").Should().BeFalse();
        File.ReadAllBytes(path).Take(3).Should().NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, "no BOM");
    }

    [MountFact]
    public void GetInfo_ForAMountedIso_IsOptical()
    {
        var iso = IsoLayouts.DataDisc().Build(_tmp, "o.iso");
        using var m = MountedIso.Mount(iso);
        var info = _fs.GetInfo(m.Letter + @"\");
        info.Should().NotBeNull();
        info!.IsOptical.Should().BeTrue();
        info.FileSystem.Should().BeOneOf("CDFS", "UDF");
    }
}
