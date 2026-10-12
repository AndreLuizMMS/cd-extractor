using MediaRescue.App.Demo;
using MediaRescue.App.Infrastructure;
using MediaRescue.App.Services;
using MediaRescue.App.Tests.Support;
using MediaRescue.App.ViewModels;
using MediaRescue.Core;

namespace MediaRescue.App.Tests;

/// <summary>Start, duplicate/resume prompts, controls, cancel, completion, close confirmation (RF-05/06/08/10).</summary>
public class ExtractionFlowTests
{
    private static async Task<Harness> ReadyAsync(Action<Harness>? configure = null)
    {
        var h = new Harness();
        configure?.Invoke(h);
        await h.StartAsync();
        await h.InsertAsync(TestDiscs.Dvd());
        return h;
    }

    /// <summary>An engine that runs until released (or cancelled) and hands the control object to the test.</summary>
    private sealed class HeldEngine
    {
        public TaskCompletionSource Started { get; } = new();
        public TaskCompletionSource<ExtractionResult> Release { get; } = new();
        public ExtractionControl? Control { get; private set; }
        public IProgress<ExtractionProgress>? Progress { get; private set; }
        public ExtractionRequest? Request { get; private set; }

        public void Install(FakeEngine engine) => engine.Behavior = (req, prog, ctl, ct) =>
        {
            Request = req; Control = ctl; Progress = prog;
            Started.TrySetResult();
            ct.Register(() => Release.TrySetResult(new ExtractionResult(Path.Combine(req.DestinationRoot, req.FolderName), OverallStatus.Cancelled,
                req.Entries.Where(e => !e.IsDirectory).Take(2).Select(e => new FileResult { RelativePath = e.RelativePath, WrittenPath = e.RelativePath, Status = FileStatus.Intact, BytesTotal = e.Size }).ToList(),
                TimeSpan.FromSeconds(3))));
            return Release.Task;
        };
    }

    // ---------------------------------------------------------------- completion

    [Fact]
    public async Task Success_RecordsCatalog_PlaysSound_EjectsAndShowsSummary()
    {
        var h = await ReadyAsync();
        await h.Extract.RunExtractionAsync(batch: false);

        var req = h.Engine.Requests.Should().ContainSingle().Subject;
        req.DestinationRoot.Should().Be(@"E:\Videos");
        req.FolderName.Should().Be("FORMATURA");
        req.Entries.Count(e => !e.IsDirectory).Should().Be(6);
        req.Options.VerifyAfterCopy.Should().BeTrue();

        var entry = h.Catalog.Entries.Should().ContainSingle().Subject;
        entry.Fingerprint.Should().Be(TestDiscs.Dvd().Fingerprint);
        entry.Name.Should().Be("FORMATURA");
        entry.Status.Should().Be(OverallStatus.Intact);
        entry.DestinationPath.Should().Be(@"E:\Videos\FORMATURA");
        entry.FileCount.Should().Be(6);

        h.Shell.Sounds.Should().Equal(SoundKind.Success);
        h.Drives.Ejected.Should().Equal("D:");
        h.Dialogs.Shown.Should().ContainSingle(d => d.Title == "Extração concluída");
        h.Extract.Result!.Headline.Should().Contain("Tudo certo");
        h.Extract.IsExtracting.Should().BeFalse();
        h.AwakeBegun.Should().Be(1);
        h.AwakeReleased.Should().Be(1);
        await h.Main.Settings.FlushAsync();
        h.SettingsStore.Stored.LastDestination.Should().Be(@"E:\Videos");
    }

    [Fact]
    public async Task Partial_PlaysWarning_ListsProblemFiles_AndShowsWarningSummary()
    {
        var h = await ReadyAsync();
        h.Engine.Behavior = (r, _, _, _) => Task.FromResult(new ExtractionResult(@"E:\Videos\FORMATURA", OverallStatus.Partial, new[]
        {
            new FileResult { RelativePath = "a.vob", WrittenPath = "a.vob", Status = FileStatus.Intact, BytesTotal = 100 },
            new FileResult { RelativePath = @"VIDEO_TS\VTS_01_2.VOB", WrittenPath = @"VIDEO_TS\VTS_01_2.VOB", Status = FileStatus.Partial, BytesTotal = 400_000_000, BytesLost = 131_072 },
        }, TimeSpan.FromMinutes(3)));
        await h.Extract.RunExtractionAsync(batch: false);

        h.Shell.Sounds.Should().Equal(SoundKind.Warning);
        h.Extract.Result!.Partial.Should().Be(1);
        h.Extract.Result.Problems.Should().ContainSingle().Which.Description.Should().Contain("incompleto").And.Contain("128");
        h.Extract.Result.Headline.Should().Contain("perdas");
        h.Dialogs.Shown.Single().Title.Should().Be("Extração concluída com avisos");
        h.Dialogs.Shown.Single().Message.Should().Contain("VTS_01_2.VOB");
        h.Catalog.Entries.Single().Status.Should().Be(OverallStatus.Partial);
        h.Catalog.Entries.Single().BytesLost.Should().Be(131_072);
    }

    [Fact]
    public async Task EjectWhenDoneOff_KeepsTheDisc_AndOffersToEjectInTheSummary()
    {
        var h = new Harness();
        h.SettingsStore.Stored.EjectWhenDone = false;
        await h.StartAsync();
        await h.InsertAsync(TestDiscs.Dvd());
        await h.Extract.RunExtractionAsync(batch: false);

        h.Drives.Ejected.Should().BeEmpty();
        h.Dialogs.Shown.Single().Buttons.Select(b => b.Key).Should().Contain("eject");
    }

    [Fact]
    public async Task SummaryDialog_OpenFolderButton_OpensTheDiscFolder()
    {
        var h = await ReadyAsync();
        h.Dialogs.Script.Enqueue("open");
        await h.Extract.RunExtractionAsync(batch: false);
        h.Shell.OpenedFolders.Should().Equal(@"E:\Videos\FORMATURA");
    }

    [Fact]
    public async Task KeepAwake_IsHeldDuringTheCopy_AndReleasedAfterwards()
    {
        var h = await ReadyAsync();
        var held = new HeldEngine();
        held.Install(h.Engine);
        var run = h.Extract.RunExtractionAsync(batch: false);
        await held.Started.Task;

        h.AwakeBegun.Should().Be(1);
        h.AwakeReleased.Should().Be(0);
        held.Release.SetResult(FakeEngine.Intact(held.Request!));
        await run;
        h.AwakeReleased.Should().Be(1);
    }

    [Fact]
    public async Task EngineThrowing_ShowsFriendlyDialog_AndLeavesTheAppUsable()
    {
        var h = await ReadyAsync();
        h.Engine.Behavior = (_, _, _, _) => throw new InvalidOperationException("engine bug");
        var outcome = await h.Extract.RunExtractionAsync(batch: false);

        outcome.Started.Should().BeTrue();
        outcome.Result.Should().BeNull();
        h.Extract.IsExtracting.Should().BeFalse();
        h.Shell.Sounds.Should().Equal(SoundKind.Error);
        var dialog = h.Dialogs.Shown.Single();
        dialog.Title.Should().Be("A cópia não terminou");
        dialog.Message.Should().NotContain("InvalidOperationException", "technical details live under 'Detalhes'");
        dialog.Details.Should().Contain("engine bug");
        h.Catalog.Entries.Should().BeEmpty();
        h.AwakeReleased.Should().Be(1);
    }

    [Fact]
    public async Task CatalogWriteFailure_DoesNotBreakTheCompletion()
    {
        var h = await ReadyAsync();
        var failing = new FailingCatalog();
        var services = new AppServices
        {
            Drives = h.Drives, Engine = h.Engine, Catalog = failing, SettingsStore = h.SettingsStore, Validator = h.Validator, Fs = h.Fs,
            Probe = h.Probe, Diagnostics = h.Diagnostics, Clock = h.Clock, DestinationDrives = h.Lister, KeepAwake = () => new NoopDisposable(),
        };
        var main = new MainViewModel(services, new ImmediateUiDispatcher(), h.Dialogs, h.Shell, h.Theme, StartupOptions.Parse(Array.Empty<string>()));
        main.Extract.ValidationDelay = TimeSpan.Zero;
        await main.Settings.LoadAsync();
        main.Extract.DestinationRoot = @"E:\Videos";
        await main.Extract.RefreshAsync(true);
        await main.Extract.ValidateNowAsync();

        var outcome = await main.Extract.RunExtractionAsync(batch: false);
        outcome.Started.Should().BeTrue();
        main.Log.Lines.Should().Contain(l => l.Text.Contains("histórico"));
    }

    // ---------------------------------------------------------------- RF-08 duplicate prompt

    private static Task<Harness> DuplicateAsync(bool folderExists, params string[] script) => DuplicateCoreAsync(folderExists, script);

    private static async Task<Harness> DuplicateCoreAsync(bool folderExists, string[] script)
    {
        var h = new Harness();
        var disc = TestDiscs.Dvd();
        h.Catalog.Entries.Add(new CatalogEntry
        {
            Fingerprint = disc.Fingerprint, Name = "FORMATURA", ExtractedAtUtc = new DateTime(2026, 3, 5, 12, 0, 0, DateTimeKind.Utc),
            DestinationPath = @"E:\Videos\Antiga", Status = OverallStatus.Intact,
        });
        if (folderExists) h.Fs.Folders.Add(@"E:\Videos\Antiga");
        foreach (var s in script) h.Dialogs.Script.Enqueue(s);
        await h.StartAsync();
        await h.InsertAsync(disc);
        await h.Extract.PendingPrompt!;
        return h;
    }

    [Fact]
    public async Task E22_DuplicateDisc_AsksWithOpenFolderExtractAgainEject()
    {
        var h = await DuplicateAsync(folderExists: true, "again");

        var dialog = h.Dialogs.Shown.Single();
        dialog.Title.Should().Be("Este disco já foi extraído");
        dialog.Message.Should().Contain("05/03/2026").And.Contain(@"E:\Videos\Antiga");
        dialog.Buttons.Select(b => b.Text).Should().Equal("Abrir pasta", "Extrair de novo", "Ejetar disco");
        h.Extract.DuplicateAcknowledged.Should().BeTrue();
        h.Drives.Ejected.Should().BeEmpty();
        h.Extract.Screen.Should().Be(ScreenState.Ready);
    }

    [Fact]
    public async Task DuplicateDisc_OpenFolder_OpensItAndAsksAgain_ThenEjects()
    {
        var h = await DuplicateAsync(folderExists: true, "open", "eject");

        h.Shell.OpenedFolders.Should().Equal(@"E:\Videos\Antiga");
        h.Dialogs.Shown.Should().HaveCount(2, "the question stays open after looking at the folder");
        h.Drives.Ejected.Should().Equal("D:");
        await h.Extract.RefreshAsync();
        h.Extract.Screen.Should().Be(ScreenState.NoMedia);
    }

    [Fact]
    public async Task DuplicateDisc_WhenRegisteredFolderIsGone_SaysSo_AndDoesNotOfferToOpenIt()
    {
        var h = await DuplicateAsync(folderExists: false, "again");

        var dialog = h.Dialogs.Shown.Single();
        dialog.Message.Should().Contain("não existe mais");
        dialog.Buttons.Select(b => b.Key).Should().NotContain("open");
    }

    [Fact]
    public async Task DuplicateDisc_DialogDismissed_LeavesTheDiscAlone()
    {
        var h = await DuplicateAsync(folderExists: true /* no script: dismissed */);
        h.Extract.DuplicateAcknowledged.Should().BeFalse();
        h.Drives.Ejected.Should().BeEmpty();
        h.Engine.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task NewDisc_IsNotReportedAsDuplicate()
    {
        var h = await ReadyAsync();
        h.Extract.Duplicate.Should().BeNull();
        h.Dialogs.Shown.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- RF-10 resume prompt

    [Fact]
    public async Task Resume_Continue_KeepsTheSameFolder()
    {
        var h = await ReadyAsync(x => { x.Engine.Resumable = true; x.Fs.Folders.Add(@"E:\Videos\FORMATURA"); x.Dialogs.Script.Enqueue("continue"); });
        await h.Extract.RunExtractionAsync(batch: false);

        h.Dialogs.Shown.First().Title.Should().Be("Continuar de onde parou?");
        h.Dialogs.Shown.First().Buttons.Select(b => b.Text).Should().Contain("Continuar de onde parou");
        h.Engine.Requests.Single().FolderName.Should().Be("FORMATURA");
    }

    [Fact]
    public async Task Resume_StartOver_UsesANewNumberedFolder()
    {
        var h = await ReadyAsync(x => { x.Engine.Resumable = true; x.Fs.Folders.Add(@"E:\Videos\FORMATURA"); x.Dialogs.Script.Enqueue("restart"); });
        await h.Extract.RunExtractionAsync(batch: false);
        h.Engine.Requests.Single().FolderName.Should().Be("FORMATURA (2)");
    }

    [Fact]
    public async Task Resume_Cancel_DoesNotStartAnything()
    {
        var h = await ReadyAsync(x => { x.Engine.Resumable = true; x.Dialogs.Script.Enqueue("cancel"); });
        var outcome = await h.Extract.RunExtractionAsync(batch: false);

        outcome.Started.Should().BeFalse();
        h.Engine.Requests.Should().BeEmpty();
        h.Extract.IsExtracting.Should().BeFalse();
        h.Extract.CanExtract.Should().BeTrue();
    }

    [Fact]
    public async Task NoResumePrompt_WhenThereIsNothingToResume()
    {
        var h = await ReadyAsync();
        await h.Extract.RunExtractionAsync(batch: false);
        h.Dialogs.WasShown("Continuar de onde parou").Should().BeFalse();
    }

    // ---------------------------------------------------------------- controls

    [Fact]
    public async Task PauseSkipInsist_TalkToTheEngineControl()
    {
        var h = await ReadyAsync();
        var held = new HeldEngine();
        held.Install(h.Engine);
        var run = h.Extract.RunExtractionAsync(batch: false);
        await held.Started.Task;

        h.Extract.PauseCommand.Execute(null);
        held.Control!.IsPaused.Should().BeTrue();
        h.Extract.Progress.PauseButtonText.Should().Be("Continuar");
        h.Extract.PauseCommand.Execute(null);
        held.Control.IsPaused.Should().BeFalse();
        h.Extract.Progress.PauseButtonText.Should().Be("Pausar");

        h.Extract.SkipCommand.Execute(null);
        held.Control.ConsumeSkip().Should().BeTrue();

        h.Extract.InsistCommand.Execute(null);
        held.Control.ExtraSecondPassBudget.Should().Be(TimeSpan.FromMinutes(10));

        held.Release.SetResult(FakeEngine.Intact(held.Request!));
        await run;
    }

    [Fact]
    public async Task WhileCopying_ExtractIsHiddenFromTheGate_AndOtherControlsAreLocked()
    {
        var h = await ReadyAsync();
        var held = new HeldEngine();
        held.Install(h.Engine);
        var run = h.Extract.RunExtractionAsync(batch: false);
        await held.Started.Task;

        h.Extract.IsExtracting.Should().BeTrue();
        h.Extract.CanExtract.Should().BeFalse();
        h.Extract.ExtractDisabledReason.Should().Be("Uma cópia está em andamento.");
        h.Extract.EjectCommand.CanExecute(null).Should().BeFalse();
        h.Extract.BrowseCommand.CanExecute(null).Should().BeFalse();
        h.Extract.PauseCommand.CanExecute(null).Should().BeTrue();
        h.Extract.ExtractionTarget.Should().Be(@"E:\Videos\FORMATURA");
        h.Extract.Progress.IsActive.Should().BeTrue();

        held.Release.SetResult(FakeEngine.Intact(held.Request!));
        await run;
        h.Extract.Progress.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task EngineProgress_FlowsIntoTheProgressViewModel_AndLogsMessagesOnce()
    {
        var h = await ReadyAsync();
        var held = new HeldEngine();
        held.Install(h.Engine);
        var run = h.Extract.RunExtractionAsync(batch: false);
        await held.Started.Task;

        var p = new ExtractionProgress
        {
            Phase = ExtractionPhase.Copying, CurrentFile = @"VIDEO_TS\VTS_01_1.VOB", FileIndex = 3, FileCount = 7,
            FileBytesDone = 50, FileBytesTotal = 100, TotalBytesDone = 640, TotalBytesTotal = 1000, BytesPerSecond = 4_300_000,
            Eta = TimeSpan.FromSeconds(372), Elapsed = TimeSpan.FromSeconds(45), FilesIntact = 3, FilesWithErrors = 0,
            Message = "Copiando VTS_01_1.VOB…",
        };
        held.Progress!.Report(p);
        held.Progress.Report(p);

        h.Extract.Progress.OverallText.Should().Be("64%");
        h.Extract.Progress.FileText.Should().Be("50%");
        h.Extract.Progress.FilesText.Should().Be("3/7 arquivos");
        h.Extract.Progress.EtaText.Should().Be("~06:12");
        h.Extract.Progress.SpeedText.Should().Be("4,1 MB/s");
        h.Extract.Progress.Visual.Should().Be(ProgressVisual.Normal);
        h.Extract.Progress.TaskbarValue.Should().BeApproximately(0.64, 0.0001);
        h.Main.Log.Lines.Count(l => l.Text == "Copiando VTS_01_1.VOB…").Should().Be(1);

        held.Release.SetResult(FakeEngine.Intact(held.Request!));
        await run;
    }

    // ---------------------------------------------------------------- cancel / close

    [Fact]
    public async Task Cancel_Keep_StopsTheCopy_KeepsFiles_AndDoesNotCatalogTheDisc()
    {
        var h = await ReadyAsync();
        var held = new HeldEngine();
        held.Install(h.Engine);
        var run = h.Extract.RunExtractionAsync(batch: false);
        await held.Started.Task;

        h.Dialogs.Script.Enqueue("keep");
        await h.Extract.CancelAsync();
        var outcome = await run;

        outcome.Result!.Status.Should().Be(OverallStatus.Cancelled);
        h.Fs.Deleted.Should().BeEmpty();
        h.Catalog.Entries.Should().BeEmpty();
        h.Dialogs.Shown.First().Title.Should().Be("Cancelar a cópia?");
        h.Dialogs.Shown.First().Buttons.Select(b => b.Text).Should().Contain(t => t.Contains("manter"));
        h.Extract.IsExtracting.Should().BeFalse();
        h.Dialogs.WasShown("Extração concluída").Should().BeFalse("no success summary for a cancelled copy");
    }

    [Fact]
    public async Task Cancel_Discard_RemovesOnlyTheFilesTheJobReported()
    {
        var h = await ReadyAsync();
        var held = new HeldEngine();
        held.Install(h.Engine);
        var run = h.Extract.RunExtractionAsync(batch: false);
        await held.Started.Task;
        foreach (var e in held.Request!.Entries.Where(e => !e.IsDirectory).Take(2)) h.Fs.Files.Add(Path.Combine(@"E:\Videos\FORMATURA", e.RelativePath));
        h.Fs.Files.Add(@"E:\Videos\FORMATURA\outro-arquivo-do-usuario.txt");

        h.Dialogs.Script.Enqueue("discard");
        await h.Extract.CancelAsync();
        await run;

        h.Fs.Deleted.Should().HaveCount(2);
        h.Fs.Deleted.Should().NotContain(p => p.EndsWith("outro-arquivo-do-usuario.txt"));
    }

    [Fact]
    public async Task Cancel_Discard_AlsoRemovesTheResumeStateAndManifest_SoNoBogusContinuePromptRemains()
    {
        var h = await ReadyAsync();
        var held = new HeldEngine();
        held.Install(h.Engine);
        var run = h.Extract.RunExtractionAsync(batch: false);
        await held.Started.Task;
        var folder = @"E:\Videos\FORMATURA";
        h.Fs.Files.Add(Path.Combine(folder, ".mediarescue-job.json"));
        h.Fs.Files.Add(Path.Combine(folder, "mediarescue-manifest.json"));
        h.Fs.Files.Add(Path.Combine(folder, "LEIA-ME - ERROS.txt"));
        h.Fs.Files.Add(Path.Combine(folder, "outro-arquivo-do-usuario.txt"));

        h.Dialogs.Script.Enqueue("discard");
        await h.Extract.CancelAsync();
        await run;

        h.Fs.Deleted.Should().Contain(Path.Combine(folder, ".mediarescue-job.json"), "a leftover state would offer to continue a job whose files are gone");
        h.Fs.Deleted.Should().Contain(Path.Combine(folder, "mediarescue-manifest.json"));
        h.Fs.Deleted.Should().Contain(Path.Combine(folder, "LEIA-ME - ERROS.txt"));
        h.Fs.Deleted.Should().NotContain(p => p.EndsWith("outro-arquivo-do-usuario.txt"));
    }

    [Fact]
    public async Task Cancel_Discard_RemovesTheEmptyDiscFolderAndItsSubfolders_DeepestFirst()
    {
        var h = await ReadyAsync();
        var held = new HeldEngine();
        held.Install(h.Engine);
        var run = h.Extract.RunExtractionAsync(batch: false);
        await held.Started.Task;
        var folder = @"E:\Videos\FORMATURA";
        h.Fs.Folders.Add(folder);
        h.Fs.Files.Add(Path.Combine(folder, ".mediarescue-job.json"));
        var copied = held.Request!.Entries.Where(e => !e.IsDirectory && e.RelativePath.Contains('\\')).Take(1).ToList();
        copied.Should().NotBeEmpty("the demo disc has files inside sub-folders");
        h.Fs.Files.Add(Path.Combine(folder, copied[0].RelativePath));
        h.Fs.Folders.Add(Path.GetDirectoryName(Path.Combine(folder, copied[0].RelativePath))!);

        h.Dialogs.Script.Enqueue("discard");
        await h.Extract.CancelAsync();
        await run;

        var requests = h.Fs.EmptyFolderRequests;
        requests.Should().Contain(folder, "the disc folder itself is removed when nothing is left in it");
        requests.Last().Should().Be(folder, "the disc folder is the last one, after its sub-folders");
        h.Fs.Folders.Should().NotContain(folder, "the empty shell is gone");
    }

    [Fact]
    public async Task Cancel_Discard_KeepsTheDiscFolder_WhenTheUserAlreadyHadFilesInIt()
    {
        var h = await ReadyAsync();
        var held = new HeldEngine();
        held.Install(h.Engine);
        var run = h.Extract.RunExtractionAsync(batch: false);
        await held.Started.Task;
        var folder = @"E:\Videos\FORMATURA";
        h.Fs.Folders.Add(folder);
        h.Fs.Files.Add(Path.Combine(folder, "outro-arquivo-do-usuario.txt"));

        h.Dialogs.Script.Enqueue("discard");
        await h.Extract.CancelAsync();
        await run;

        h.Fs.Folders.Should().Contain(folder, "a folder that still holds the user's file is not removed");
        h.Fs.Files.Should().Contain(Path.Combine(folder, "outro-arquivo-do-usuario.txt"));
    }

    [Fact]
    public async Task Cancel_Keep_NeverAsksToRemoveAnyFolder()
    {
        var h = await ReadyAsync();
        var held = new HeldEngine();
        held.Install(h.Engine);
        var run = h.Extract.RunExtractionAsync(batch: false);
        await held.Started.Task;

        h.Dialogs.Script.Enqueue("keep");
        await h.Extract.CancelAsync();
        await run;

        h.Fs.EmptyFolderRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_Back_KeepsCopyingAndResumesTheEngine()
    {
        var h = await ReadyAsync();
        var held = new HeldEngine();
        held.Install(h.Engine);
        var run = h.Extract.RunExtractionAsync(batch: false);
        await held.Started.Task;

        h.Dialogs.Script.Enqueue("back");
        await h.Extract.CancelAsync();

        h.Extract.IsExtracting.Should().BeTrue();
        held.Control!.IsPaused.Should().BeFalse("it was only paused while the question was open");
        held.Release.SetResult(FakeEngine.Intact(held.Request!));
        await run;
    }

    [Fact]
    public async Task Close_WhileIdle_IsAllowedWithoutAsking()
    {
        var h = await ReadyAsync();
        (await h.Main.ConfirmCloseAsync()).Should().BeTrue();
        h.Dialogs.Shown.Should().BeEmpty();
    }

    [Fact]
    public async Task Close_DuringCopy_AsksForConfirmation_StayKeepsCopying()
    {
        var h = await ReadyAsync();
        var held = new HeldEngine();
        held.Install(h.Engine);
        var run = h.Extract.RunExtractionAsync(batch: false);
        await held.Started.Task;

        h.Dialogs.Script.Enqueue("stay");
        (await h.Main.ConfirmCloseAsync()).Should().BeFalse();
        h.Extract.IsExtracting.Should().BeTrue();
        h.Dialogs.Shown.Last().Title.Should().Be("Uma cópia está em andamento");

        held.Release.SetResult(FakeEngine.Intact(held.Request!));
        await run;
    }

    [Fact]
    public async Task Close_DuringCopy_Confirmed_StopsTheEngine()
    {
        var h = await ReadyAsync();
        var held = new HeldEngine();
        held.Install(h.Engine);
        var run = h.Extract.RunExtractionAsync(batch: false);
        await held.Started.Task;

        h.Dialogs.Script.Enqueue("close");
        (await h.Main.ConfirmCloseAsync()).Should().BeTrue();
        (await run).Result!.Status.Should().Be(OverallStatus.Cancelled);
    }

    [Fact]
    public async Task EmergencyCleanup_ReleasesTheEjectLock()
    {
        var h = await ReadyAsync();
        var held = new HeldEngine();
        held.Install(h.Engine);
        var run = h.Extract.RunExtractionAsync(batch: false);
        await held.Started.Task;

        h.Extract.EmergencyCleanup();
        h.Drives.Locks.Should().Contain(("D:", false));
        h.AwakeReleased.Should().Be(1);

        held.Release.SetResult(FakeEngine.Intact(held.Request!));
        await run;
    }

    // ---------------------------------------------------------------- selection

    [Fact]
    public async Task NothingSelected_DisablesExtract_WithReason()
    {
        var h = await ReadyAsync();
        h.Extract.Content.ClearAllCommand.Execute(null);
        await h.Extract.ValidateNowAsync();

        h.Extract.CanExtract.Should().BeFalse();
        h.Extract.ExtractDisabledReason.Should().Be("Marque pelo menos um item do disco para extrair.");
        h.Extract.Content.SelectionText.Should().Contain("0 de 6");
    }

    [Fact]
    public async Task UntickedItems_AreNotSentToTheEngine()
    {
        var h = await ReadyAsync();
        h.Extract.Content.FindFile(@"Fotos\b.jpg")!.IsChecked = false;
        await h.Extract.ValidateNowAsync();
        await h.Extract.RunExtractionAsync(batch: false);

        h.Engine.Requests.Single().Entries.Select(e => e.RelativePath).Should().NotContain(@"Fotos\b.jpg").And.Contain(@"Fotos\a.jpg");
    }
}


