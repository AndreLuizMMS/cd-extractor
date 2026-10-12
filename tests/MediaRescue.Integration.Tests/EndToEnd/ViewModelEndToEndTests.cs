using MediaRescue.App.Infrastructure;
using MediaRescue.App.ViewModels;
using MediaRescue.Core;
using MediaRescue.Integration.Tests.Support;
using Xunit.Abstractions;

namespace MediaRescue.Integration.Tests.EndToEnd;

/// <summary>
/// The App's ViewModels driven through the real <c>CompositionRoot</c> graph (real OpticalDriveService, ExtractionEngine,
/// WindowsDestinationFileSystem, CatalogStore, SettingsStore, DestinationValidator, DriveDiagnostics, PowerGuard) against mounted ISOs.
/// No WPF window is needed: the ViewModels only see IUiDispatcher/IDialogService/IShellService, so a dedicated test "UI thread"
/// (own SynchronizationContext) plays the dispatcher and scripted dialogs play the user. What cannot be covered this way is
/// the XAML (bindings, templates), which is left to the smoke run.
/// </summary>
public sealed class ViewModelEndToEndTests
{
    private readonly ITestOutputHelper _out;
    public ViewModelEndToEndTests(ITestOutputHelper output) => _out = output;

    private static IsoSpec Movies() => new IsoSpec { Label = "FORMATURA" }
        .AddRandom("VIDEO_TS\\VIDEO_TS.IFO", 4096, 601)
        .AddRandom("VIDEO_TS\\VTS_01_0.VOB", 50_000, 602)
        .AddRandom("VIDEO_TS\\VTS_01_1.VOB", 1_200_000, 603)
        .AddRandom("VIDEO_TS\\VTS_01_2.VOB", 900_000, 604)
        .AddRandom("fotos\\festa.jpg", 40_000, 605);

    private static IsoSpec EightFiles()
    {
        var spec = new IsoSpec { Label = "OITO" };
        for (int i = 1; i <= 8; i++) spec.AddRandom($"v{i}.avi", 1_500_000 + i * 1000, 700 + i);
        return spec;
    }

    // ================================================================== detect -> analyze -> destination -> extract -> completed

    [MountFact]
    public async Task Full_Detect_Analyze_ChooseDestination_Extract_Completed_ThenDuplicateIsRecognised()
    {
        using var h = new VmRig();
        var spec = Movies();
        var iso = h.BuildIso(spec);
        var uiThread = h.Ui.ThreadId;
        var filesTexts = new List<string>();

        await h.Ui.RunAsync(async () =>
        {
            var vm = h.Main;
            var ext = vm.Extract;
            await vm.InitializeAsync();

            // --- detect + analyze ---
            await h.MountAndSelectAsync(iso);
            ext.StatusTitle.Should().Be("Disco pronto");
            ext.ShowEmptyState.Should().BeFalse();
            var disc = ext.Disc!;
            disc.LogicalFormat.Should().Be(LogicalFormat.DvdVideo);
            ext.DiscRows.Single(r => r.Label == "Formato").Value.Should().Be("DVD-Video");
            ext.DiscRows.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Value), "unavailable fields show an em dash, never an empty cell");
            ext.DriveRows.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Value));
            ext.DriveRows.Should().Contain(r => r.Label == "Firmware").And.Contain(r => r.Label == "Conexão");
            ext.SelectedDrive!.ModelName.Should().NotBeNullOrWhiteSpace();
            ext.SelectedDrive.DisplayName.Should().Contain("virtual");
            ext.Content.GetSelectedEntries().Count(e => !e.IsDirectory).Should().Be(spec.Files.Count, "everything on the disc is selected by default");
            ext.Duplicate.Should().BeNull();
            ext.FolderName.Should().Be(NameSanitizer.SanitizeFolderName(NameSanitizer.SuggestFolderName(disc, h.Services.Clock.UtcNow.ToLocalTime().Date)));
            ext.CanExtract.Should().BeFalse();
            ext.ExtractDisabledReason.Should().Be("Escolha a pasta de destino.", "the disabled button always says why");

            // --- destination ---
            await h.ChooseDestinationAsync();
            ext.CanExtract.Should().BeTrue(ext.ExtractDisabledReason);
            ext.DestinationSummary.Should().Contain("NTFS");
            ext.Issues.Should().NotContain(i => i.IsBlocking);
            vm.Settings.Update(s => s.EjectWhenDone = false);       // do not eject the virtual drive after the copy
            var folderName = ext.FolderName;

            // --- extract ---
            ext.Progress.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ProgressViewModel.FilesText)) filesTexts.Add(ext.Progress.FilesText);
            };
            await ext.ExtractCommand.ExecuteAsync(null);

            // --- completed ---
            ext.IsExtracting.Should().BeFalse();
            ext.Result.Should().NotBeNull();
            ext.Result!.Status.Should().Be(OverallStatus.Intact);
            ext.Result.Intact.Should().Be(spec.Files.Count);
            ext.Result.FolderPath.Should().Be(Path.Combine(h.DestRoot, folderName));
            var outcome = await ext.RunningExtraction!;
            outcome.Started.Should().BeTrue();
            E2eRig.AssertExtractedCorrectly(spec, disc, outcome.Result!, ext.SelectedDrive.Letter);

            ext.Progress.IsActive.Should().BeFalse();
            ext.Progress.Visual.Should().Be(ProgressVisual.None);
            ext.Progress.OverallFraction.Should().Be(1.0);
            ext.Progress.FilesText.Should().Be($"{spec.Files.Count}/{spec.Files.Count} arquivos");
            ext.Progress.IntactText.Should().Be($"Íntegros {spec.Files.Count}");
            ext.Progress.ErrorsText.Should().Be("Com erro 0");
            h.Dialogs.WasShown("Extração concluída").Should().BeTrue("the end-of-copy summary is shown");
            h.Shell.Sounds.Should().Contain(SoundKind.Success);
            ext.CanExtract.Should().BeTrue("the screen is usable again");

            // catalog + settings written by the App
            var entry = h.Services.Catalog.FindByFingerprint(disc.Fingerprint);
            entry.Should().NotBeNull("the App records every finished extraction");
            entry!.DestinationPath.Should().Be(outcome.Result!.DiscFolder);
            entry.Status.Should().Be(OverallStatus.Intact);
            vm.Settings.Current.LastDestination.Should().Be(h.DestRoot);
            await vm.Settings.FlushAsync();
            File.Exists(Path.Combine(h.DataDir, "settings.json")).Should().BeTrue();
            File.Exists(Path.Combine(h.DataDir, "catalog.json")).Should().BeTrue();

            // --- E-22: the same disc again is recognised, and extracting it again never merges into the old folder ---
            h.Dialogs.Responder = d => d.Title == "Este disco já foi extraído" ? "again" : null;
            await ext.RefreshAsync(true);
            (await VmRig.UiWaitUntil(() => ext.Duplicate != null && !ext.IsAnalyzing, TimeSpan.FromSeconds(60))).Should().BeTrue();
            ext.Duplicate!.FolderExists.Should().BeTrue();
            ext.Duplicate.Entry.DestinationPath.Should().Be(outcome.Result.DiscFolder);
            await ext.PendingPrompt!;
            h.Dialogs.WasShown("Este disco já foi extraído").Should().BeTrue();
            ext.DuplicateAcknowledged.Should().BeTrue("'Extrair de novo' was chosen");
            await ext.ValidateNowAsync();
            ext.EffectiveFolderName.Should().Be(folderName + " (2)");
            await ext.ExtractCommand.ExecuteAsync(null);
            var second = (await ext.RunningExtraction!).Result!;
            second.DiscFolder.Should().Be(Path.Combine(h.DestRoot, folderName + " (2)"));
            E2eRig.AssertExtractedCorrectly(spec, disc, second, null);
            E2eRig.AssertExtractedCorrectly(spec, disc, outcome.Result, null);
            return 0;
        });

        h.Ui.Unhandled.Should().BeEmpty("no exception may escape on the UI thread");

        // PowerGuard (SetThreadExecutionState) was held for each copy and released after it.
        h.Awake.Begun.Should().Be(2);
        h.Awake.Released.Should().Be(2);
        h.Awake.MaxHeld.Should().Be(1);
        h.Awake.InactiveGuards.Should().Be(0, "the OS accepted the keep-awake request");
        // The media lock: taken and released for each copy.
        h.Drives.LockCalls.Should().Equal(true, false, true, false);

        // GetState can block for up to 4 s: it (and the other slow drive calls) must never run on the UI thread.
        var calls = h.Drives.Calls;
        calls.Count(c => c.Method == "GetState").Should().BeGreaterThan(0);
        calls.Where(c => c.Method is "GetState" or "GetDrives" or "AnalyzeAsync" or "Eject").Should()
            .OnlyContain(c => c.ThreadId != uiThread, "drive queries are made off the UI thread");

        filesTexts.Where(t => t.Length > 0).Should().OnlyContain(t => t.EndsWith($"/{spec.Files.Count} arquivos"));
        var indices = filesTexts.Where(t => t.Length > 0).Select(t => int.Parse(t.Split('/')[0])).ToList();
        indices.Min().Should().BeGreaterThanOrEqualTo(0);
        indices.Max().Should().Be(spec.Files.Count, "the App shows file X of Y with X 1-based, ending at Y");
        _out.WriteLine("FilesText sequence: " + string.Join(" ", filesTexts.Distinct()));
    }

    // ================================================================== cancel, then "Continuar de onde parou" / "Começar do zero"

    private static async Task<(string Folder, DiscInfo Disc, ExtractionResult Cancelled)> StartAndCancelAfterThreeAsync(VmRig h, IsoSpec spec, string iso, string answer = "keep")
    {
        var ext = h.Main.Extract;
        await h.Main.InitializeAsync();
        await h.MountAndSelectAsync(iso);
        await h.ChooseDestinationAsync();
        h.Main.Settings.Update(s => s.EjectWhenDone = false);
        var disc = ext.Disc!;
        var folder = ext.FolderName;
        bool cancelled = false;
        // Slow every file down a little so the (asynchronous) UI can react while the copy is still in progress.
        h.Reader.BeforeRead = async (_, offset) => { if (offset == 0) await Task.Delay(300); };
        h.Dialogs.Responder = d => d.Title == "Cancelar a cópia?" ? answer : null;
        ext.Progress.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProgressViewModel.IntactText) && ext.Progress.IntactText == "Íntegros 3" && !cancelled)
            {
                cancelled = true;
                _ = ext.CancelAsync();
            }
        };
        await ext.ExtractCommand.ExecuteAsync(null);
        var outcome = await ext.RunningExtraction!;
        outcome.Started.Should().BeTrue();
        outcome.Status.Should().Be(OverallStatus.Cancelled);
        ext.IsExtracting.Should().BeFalse();
        ext.Result.Should().BeNull("a cancelled copy does not show a result card");
        h.Services.Catalog.FindByFingerprint(disc.Fingerprint).Should().BeNull("a cancelled copy is not recorded as extracted");
        h.Reader.BeforeRead = null;
        return (folder, disc, outcome.Result!);
    }

    [MountFact]
    public async Task Cancel_KeepFiles_ThenContinue_ResumesInTheSameFolder_WithoutRecopying()
    {
        using var h = new VmRig();
        var spec = EightFiles();
        var iso = h.BuildIso(spec);

        await h.Ui.RunAsync(async () =>
        {
            var ext = h.Main.Extract;
            var (folder, disc, cancelled) = await StartAndCancelAfterThreeAsync(h, spec, iso);
            cancelled.Files.Count(f => f.Status == FileStatus.Intact).Should().BeInRange(3, 7);
            h.Awake.Begun.Should().Be(1);
            h.Awake.Released.Should().Be(1, "PowerGuard is released when the copy is cancelled");
            h.Drives.LockCalls.Should().Equal(true, false);

            // The App now offers to continue: it sends the very same folder name.
            await ext.ValidateNowAsync();
            ext.WillResume.Should().BeTrue();
            ext.NameNote.Should().Contain("interrompida");
            ext.EffectiveFolderName.Should().Be(folder, "a resumable folder is reused, not renamed");
            h.Reader.ResetCounts();
            h.Dialogs.Responder = d => d.Title == "Continuar de onde parou?" ? "continue" : null;
            await ext.ExtractCommand.ExecuteAsync(null);

            h.Dialogs.WasShown("Continuar de onde parou?").Should().BeTrue();
            var outcome = await ext.RunningExtraction!;
            outcome.Status.Should().Be(OverallStatus.Intact);
            outcome.Result!.DiscFolder.Should().Be(Path.Combine(h.DestRoot, folder), "'continue' resumes in the same folder");
            E2eRig.AssertExtractedCorrectly(spec, disc, outcome.Result, null);
            var finishedBefore = cancelled.Files.Where(f => f.Status == FileStatus.Intact).Select(f => f.RelativePath).ToHashSet();
            foreach (var name in finishedBefore) h.Reader.OpenCount(name).Should().Be(0, $"{name} was finished before the cancel");
            foreach (var name in spec.Files.Keys.Where(k => !finishedBefore.Contains(k))) h.Reader.OpenCount(name).Should().Be(1, name);
            Directory.GetDirectories(h.DestRoot).Should().HaveCount(1, "no second folder was created");
            ext.Result!.Status.Should().Be(OverallStatus.Intact);
            h.Services.Catalog.FindByFingerprint(disc.Fingerprint).Should().NotBeNull();
            h.Awake.Begun.Should().Be(2);
            h.Awake.Released.Should().Be(2);
            h.Drives.LockCalls.Should().Equal(true, false, true, false);
            return 0;
        });
        h.Ui.Unhandled.Should().BeEmpty();
    }

    [MountFact]
    public async Task Cancel_KeepFiles_ThenStartOver_UsesTheNextFreeFolderName_AndCopiesEverything()
    {
        using var h = new VmRig();
        var spec = EightFiles();
        var iso = h.BuildIso(spec);

        await h.Ui.RunAsync(async () =>
        {
            var ext = h.Main.Extract;
            var (folder, disc, cancelled) = await StartAndCancelAfterThreeAsync(h, spec, iso);
            var firstFolderFiles = Directory.EnumerateFiles(cancelled.DiscFolder, "*", SearchOption.AllDirectories).OrderBy(p => p).ToList();

            await ext.ValidateNowAsync();
            h.Reader.ResetCounts();
            h.Dialogs.Responder = d => d.Title == "Continuar de onde parou?" ? "restart" : null;
            await ext.ExtractCommand.ExecuteAsync(null);

            var outcome = await ext.RunningExtraction!;
            outcome.Status.Should().Be(OverallStatus.Intact);
            outcome.Result!.DiscFolder.Should().Be(Path.Combine(h.DestRoot, folder + " (2)"), "'start over' sends the next free name");
            E2eRig.AssertExtractedCorrectly(spec, disc, outcome.Result, null);
            foreach (var name in spec.Files.Keys) h.Reader.OpenCount(name).Should().Be(1, $"{name}: a new folder means a full copy");
            Directory.EnumerateFiles(cancelled.DiscFolder, "*", SearchOption.AllDirectories).OrderBy(p => p).Should().Equal(firstFolderFiles, "the interrupted folder is left alone");
            return 0;
        });
        h.Ui.Unhandled.Should().BeEmpty();
    }

    [MountFact]
    public async Task Cancel_Discard_RemovesTheCopiedFilesAndTheResumeState_SoNothingOffersToContinue()
    {
        using var h = new VmRig();
        var spec = EightFiles();
        var iso = h.BuildIso(spec);

        await h.Ui.RunAsync(async () =>
        {
            var ext = h.Main.Extract;
            var (folder, disc, cancelled) = await StartAndCancelAfterThreeAsync(h, spec, iso, "discard");

            Directory.Exists(cancelled.DiscFolder).Should().BeFalse("everything the job wrote is gone, including state, manifest and the now-empty disc folder");
            h.Services.Engine.CanResume(disc, h.DestRoot, folder).Should().BeFalse("there is nothing left to continue");
            await ext.ValidateNowAsync();
            ext.WillResume.Should().BeFalse();
            h.Awake.Released.Should().Be(1);
            return 0;
        });
        h.Ui.Unhandled.Should().BeEmpty();
    }

    // ================================================================== failure path releases everything

    [MountFact]
    public async Task EngineCrash_StillReleasesPowerGuard_ShowsAFriendlyError_AndTheScreenIsUsableAgain()
    {
        using var h = new VmRig(inner => new ThrowingEngine());
        var iso = h.BuildIso(Movies());

        await h.Ui.RunAsync(async () =>
        {
            var ext = h.Main.Extract;
            await h.Main.InitializeAsync();
            await h.MountAndSelectAsync(iso);
            await h.ChooseDestinationAsync();
            h.Main.Settings.Update(s => s.EjectWhenDone = false);

            await ext.ExtractCommand.ExecuteAsync(null);

            h.Awake.Begun.Should().Be(1);
            h.Awake.Released.Should().Be(1, "the power request is released even when the engine blows up");
            ext.IsExtracting.Should().BeFalse();
            h.Dialogs.WasShown("A cópia não terminou").Should().BeTrue();
            h.Shell.Sounds.Should().Contain(SoundKind.Error);
            ext.Result.Should().BeNull();
            ext.Progress.IsActive.Should().BeFalse();
            await ext.ValidateNowAsync();
            ext.CanExtract.Should().BeTrue("the user can try again");
            return 0;
        });
        h.Ui.Unhandled.Should().BeEmpty();
    }

    // ================================================================== diagnostics screen on a real (virtual) drive

    [MountFact]
    public async Task Diagnostics_TrayTestIsOptIn_AndTheChoiceReachesTheRealDiagnostics()
    {
        using var h = new VmRig();
        var iso = h.BuildIso(Movies());

        await h.Ui.RunAsync(async () =>
        {
            await h.Main.InitializeAsync();
            await h.MountAndSelectAsync(iso);
            var diag = h.Main.Diagnostics;

            diag.IncludeTrayTest.Should().BeFalse("the tray test throws the disc out, so it is never on by default");
            (await VmRig.UiWaitUntil(() => diag.CanRun, TimeSpan.FromSeconds(10))).Should().BeTrue(diag.RunDisabledReason);
            await diag.RunAsync();
            h.Services.Diagnostics.IncludeTrayTest.Should().BeFalse();
            diag.Checks.Select(c => c.Id).Should().Equal("detected", "responds", "tray", "media", "sampling", "surface");
            diag.Checks.Single(c => c.Id == "tray").Check.Outcome.Should().BeOneOf(CheckOutcome.Skipped, CheckOutcome.NotSupported);
            diag.Checks.Should().NotContain(c => c.Check.Outcome == CheckOutcome.Fail, string.Join("; ", diag.Checks.Select(c => c.Id + "=" + c.Check.Detail)));
            diag.SummaryText.Should().NotBeNullOrWhiteSpace();

            diag.IncludeTrayTest = true;   // explicit opt-in (a virtual drive has no tray, so nothing is ejected here)
            await diag.RunAsync();
            h.Services.Diagnostics.IncludeTrayTest.Should().BeTrue();
            diag.Checks.Single(c => c.Id == "tray").Check.Outcome.Should().Be(CheckOutcome.NotSupported);
            h.Drives.Calls.Where(c => c.Method == "Eject").Should().BeEmpty("a virtual drive is never ejected by the tray test");
            return 0;
        });
        h.Ui.Unhandled.Should().BeEmpty();
    }
}
