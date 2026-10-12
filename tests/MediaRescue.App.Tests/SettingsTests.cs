using MediaRescue.App.Infrastructure;
using MediaRescue.App.Services;
using MediaRescue.App.Tests.Support;
using MediaRescue.App.ViewModels;
using MediaRescue.Core;

namespace MediaRescue.App.Tests;

/// <summary>RF-12: settings screen, persistence round-trip, verification confirmation, theme, corrupted file.</summary>
public class SettingsTests
{
    private static async Task<(Harness H, SettingsViewModel Vm)> LoadedAsync(Action<AppSettings>? seed = null)
    {
        var h = new Harness();
        seed?.Invoke(h.SettingsStore.Stored);
        await h.Main.Settings.LoadAsync();
        return (h, h.Main.SettingsPage);
    }

    [Fact]
    public async Task Defaults_MatchTheSpec()
    {
        var (_, vm) = await LoadedAsync();
        vm.EjectWhenDone.Should().BeTrue();
        vm.VerifyAfterCopy.Should().BeTrue("verification is on by default");
        vm.RetriesPerSector.Should().Be(3);
        vm.BlockTimeoutSeconds.Should().Be(30);
        vm.SecondPassBudgetMinutes.Should().Be(20);
        vm.ReduceSpeedOnError.Should().BeTrue();
        vm.BatchAskName.Should().BeTrue();
        vm.Theme.Should().Be("System");
        vm.ConvertToMp4.Should().BeFalse();
    }

    [Fact]
    public async Task RoundTrip_EveryFieldSurvivesSaveAndReload()
    {
        var (h, vm) = await LoadedAsync();
        h.Dialogs.Script.Enqueue("yes");

        vm.EjectWhenDone = false;
        vm.RetriesPerSector = 7;
        vm.BlockTimeoutSeconds = 45;
        vm.SecondPassBudgetMinutes = 90;
        vm.ReduceSpeedOnError = false;
        vm.BatchAskName = false;
        vm.Theme = "Dark";
        vm.VerifyAfterCopy = false;
        await vm.PendingConfirmation;
        await h.Main.Settings.FlushAsync();

        var saved = h.SettingsStore.Stored;
        saved.EjectWhenDone.Should().BeFalse();
        saved.Extraction.RetriesPerSector.Should().Be(7);
        saved.Extraction.BlockTimeout.Should().Be(TimeSpan.FromSeconds(45));
        saved.Extraction.SecondPassBudget.Should().Be(TimeSpan.FromMinutes(90));
        saved.Extraction.ReduceSpeedOnError.Should().BeFalse();
        saved.Extraction.VerifyAfterCopy.Should().BeFalse();
        saved.BatchAskName.Should().BeFalse();
        saved.Theme.Should().Be("Dark");

        // A brand-new graph reading the same store shows the same values.
        var service = new SettingsService(h.SettingsStore);
        await service.LoadAsync();
        var vm2 = new SettingsViewModel(service, h.Dialogs, new ImmediateUiDispatcher());
        vm2.EjectWhenDone.Should().BeFalse();
        vm2.RetriesPerSector.Should().Be(7);
        vm2.BlockTimeoutSeconds.Should().Be(45);
        vm2.SecondPassBudgetMinutes.Should().Be(90);
        vm2.ReduceSpeedOnError.Should().BeFalse();
        vm2.VerifyAfterCopy.Should().BeFalse();
        vm2.BatchAskName.Should().BeFalse();
        vm2.Theme.Should().Be("Dark");
    }

    [Fact]
    public async Task EveryChange_IsPersistedImmediately()
    {
        var (h, vm) = await LoadedAsync();
        var before = h.SettingsStore.SaveCount;
        vm.EjectWhenDone = false;
        await h.Main.Settings.FlushAsync();
        h.SettingsStore.SaveCount.Should().Be(before + 1);
    }

    [Fact]
    public async Task Reload_DoesNotWriteBackToTheStore()
    {
        var (h, vm) = await LoadedAsync(s => s.EjectWhenDone = false);
        await h.Main.Settings.FlushAsync();
        h.SettingsStore.SaveCount.Should().Be(0, "loading must not trigger saves");
        vm.EjectWhenDone.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(50, 10)]
    [InlineData(5, 5)]
    public async Task RetriesPerSector_IsClamped(int typed, int expected)
    {
        var (h, vm) = await LoadedAsync();
        vm.RetriesPerSector = typed;
        await h.Main.Settings.FlushAsync();
        vm.RetriesPerSector.Should().Be(expected);
        h.SettingsStore.Stored.Extraction.RetriesPerSector.Should().Be(expected);
    }

    [Fact]
    public async Task TimeoutAndBudget_AreClamped()
    {
        var (h, vm) = await LoadedAsync();
        vm.BlockTimeoutSeconds = 1;
        vm.SecondPassBudgetMinutes = 0;
        await h.Main.Settings.FlushAsync();
        vm.BlockTimeoutSeconds.Should().Be(5);
        vm.SecondPassBudgetMinutes.Should().Be(1);
        h.SettingsStore.Stored.Extraction.BlockTimeout.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TurningVerificationOff_AsksForConfirmation_AndKeepsItOnWhenDeclined()
    {
        var (h, vm) = await LoadedAsync();
        h.Dialogs.Script.Enqueue("no");

        vm.VerifyAfterCopy = false;
        await vm.PendingConfirmation;
        await h.Main.Settings.FlushAsync();

        var dialog = h.Dialogs.Shown.Single();
        dialog.Title.Should().Be("Desligar a verificação da cópia?");
        dialog.Message.Should().Contain("pendrive defeituoso");
        dialog.Buttons.Select(b => b.Text).Should().Contain("Manter ligada");
        vm.VerifyAfterCopy.Should().BeTrue();
        h.Main.Settings.Current.Extraction.VerifyAfterCopy.Should().BeTrue();
        h.SettingsStore.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task TurningVerificationOff_WhenConfirmed_IsApplied_AndTurningItBackOnNeedsNoQuestion()
    {
        var (h, vm) = await LoadedAsync();
        h.Dialogs.Script.Enqueue("yes");
        vm.VerifyAfterCopy = false;
        await vm.PendingConfirmation;
        await h.Main.Settings.FlushAsync();

        vm.VerifyAfterCopy.Should().BeFalse();
        h.SettingsStore.Stored.Extraction.VerifyAfterCopy.Should().BeFalse();

        vm.VerifyAfterCopy = true;
        await h.Main.Settings.FlushAsync();
        h.Dialogs.Shown.Should().ContainSingle("only turning it off is questioned");
        h.SettingsStore.Stored.Extraction.VerifyAfterCopy.Should().BeTrue();
    }

    [Fact]
    public async Task ExtractionRequest_UsesTheCurrentSettings()
    {
        var h = new Harness();
        h.SettingsStore.Stored.Extraction.RetriesPerSector = 6;
        h.SettingsStore.Stored.Extraction.VerifyAfterCopy = false;
        h.SettingsStore.Stored.Extraction.BlockTimeout = TimeSpan.FromSeconds(12);
        await h.StartAsync();
        await h.InsertAsync(TestDiscs.Dvd());
        await h.Extract.RunExtractionAsync(batch: false);

        var o = h.Engine.Requests.Single().Options;
        o.RetriesPerSector.Should().Be(6);
        o.VerifyAfterCopy.Should().BeFalse();
        o.BlockTimeout.Should().Be(TimeSpan.FromSeconds(12));
        ReferenceEquals(o, h.Main.Settings.Current.Extraction).Should().BeFalse("the engine gets a copy, not the live object");
    }

    [Fact]
    public async Task CorruptedSettingsFile_FallsBackToDefaults()
    {
        var h = new Harness();
        h.SettingsStore.LoadThrows = true;
        await h.Main.Settings.LoadAsync();
        h.Main.Settings.Current.EjectWhenDone.Should().BeTrue();
        h.Main.SettingsPage.VerifyAfterCopy.Should().BeTrue();
    }

    [Fact]
    public async Task ThemeChange_IsAppliedToTheWindow()
    {
        var h = new Harness();
        h.SettingsStore.Stored.Theme = "Light";
        await h.Main.InitializeAsync();
        h.Theme.Applied.Should().Equal("Light");

        h.Main.SettingsPage.Theme = "Dark";
        h.Theme.Applied.Should().Equal("Light", "Dark");

        h.Main.SettingsPage.Theme = "Dark";
        h.Theme.Applied.Should().HaveCount(2, "no re-apply when nothing changed");
        h.Extract.Dispose();
    }

    [Fact]
    public async Task Mp4Conversion_IsNoLongerComingSoon_AndIsDisabledWithAnExplanationWhenFfmpegIsMissing()
    {
        // RF-11 is implemented; the default test graph has no converter, so the option is disabled and says why.
        var (_, vm) = await LoadedAsync();
        vm.Mp4Note.Should().NotContain("Em breve");
        vm.Mp4Available.Should().BeFalse();
        vm.Mp4Note.Should().Contain("ffmpeg");
    }
}
