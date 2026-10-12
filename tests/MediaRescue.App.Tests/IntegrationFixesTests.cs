using MediaRescue.App.Tests.Support;
using MediaRescue.App.ViewModels;
using MediaRescue.Core;

namespace MediaRescue.App.Tests;

/// <summary>Regression tests for gaps found while wiring the App to the real engine and Windows services.</summary>
public class IntegrationFixesTests
{
    private static async Task<Harness> ReadyAsync()
    {
        var h = new Harness();
        await h.StartAsync();
        return h;
    }

    // ---- RF-07: the tray test ejects the disc, so it is opt-in ----

    [Fact]
    public async Task TrayTest_IsOffByDefault_AndOnlyRunsWhenTheUserTicksTheBox()
    {
        var h = await ReadyAsync();
        h.Diagnostics.Checks.Add(new DiagnosticCheck("a", "A", CheckOutcome.Pass, "ok"));
        h.Main.Diagnostics.IncludeTrayTest.Should().BeFalse();

        await h.Main.Diagnostics.RunAsync();
        h.Diagnostics.LastIncludeTrayTest.Should().BeFalse();

        h.Main.Diagnostics.IncludeTrayTest = true;
        await h.Main.Diagnostics.RunAsync();
        h.Diagnostics.LastIncludeTrayTest.Should().BeTrue();

        h.Main.Diagnostics.IncludeTrayTest = false;
        await h.Main.Diagnostics.RunAsync();
        h.Diagnostics.LastIncludeTrayTest.Should().BeFalse("unticking it again must switch the tray test off for the next run");
    }

    // ---- E-08: mixed-mode disc ----

    [Fact]
    public async Task E08_MixedModeDisc_ShowsTheOnlyTheDataPartWillBeCopiedWarning()
    {
        var h = await ReadyAsync();
        await h.InsertAsync(TestDiscs.Dvd() with { HasAudioTracks = true });

        h.Extract.Screen.Should().Be(ScreenState.Ready);
        h.Extract.DiscNotice.Should().Contain("só a parte de dados será copiada");
    }

    [Fact]
    public async Task E08_NoWarning_ForPlainDataDiscs()
    {
        var h = await ReadyAsync();
        await h.InsertAsync(TestDiscs.Dvd());
        h.Extract.DiscNotice.Should().NotContain("áudio");
    }

    [Fact]
    public async Task E08_AudioCd_UsesTheAudioCdScreen_NotTheMixedModeNotice()
    {
        var h = await ReadyAsync();
        await h.InsertAsync(new DiscInfo { LogicalFormat = LogicalFormat.AudioCd, HasAudioTracks = true, Tracks = 12 });

        h.Extract.Screen.Should().Be(ScreenState.AudioCd);
        h.Extract.DiscNotice.Should().NotContain("modo misto");
    }

    // ---- OpticalDrive empty-string fields are shown as "—" ----

    [Fact]
    public async Task DriveWithEmptyIdentityFields_ShowsDashes_AndAGenericName()
    {
        var h = new Harness();
        h.Drives.DriveList.Clear();
        h.Drives.DriveList.Add(new OpticalDrive("D:", "", "", "", "", false));
        await h.StartAsync();

        h.Extract.DriveRows.Should().Contain(r => r.Label == "Firmware" && r.Value == "—");
        h.Extract.DriveRows.Should().Contain(r => r.Label == "Conexão" && r.Value == "—");
        h.Extract.SelectedDrive!.ModelName.Should().Be("Leitor de CD/DVD");
        h.Extract.SelectedDrive.DisplayName.Should().Contain("D:").And.Contain("Leitor de CD/DVD");
        h.Main.Diagnostics.DriveText.Should().Be("D:  Leitor de CD/DVD");
    }

    [Fact]
    public async Task DiscWithEmptyFields_ShowsDashesInEveryRow()
    {
        var h = await ReadyAsync();
        await h.InsertAsync(TestDiscs.Dvd(label: "") with { PhysicalType = null, MediaState = null, Sessions = null, Tracks = null, FileSystem = "", VolumeSerial = "", RecordedAtUtc = null, CapacityBytes = null });

        h.Extract.DiscRows.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Value));
        h.Extract.DiscRows.Single(r => r.Label == "Tipo").Value.Should().Be("—");
        h.Extract.DiscRows.Single(r => r.Label == "Rótulo").Value.Should().Be("—");
        h.Extract.DiscRows.Single(r => r.Label == "Nº de série").Value.Should().Be("—");
        h.Extract.DiscRows.Single(r => r.Label == "Gravado em").Value.Should().Be("—");
    }
}
