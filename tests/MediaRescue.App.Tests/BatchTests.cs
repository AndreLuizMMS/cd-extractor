using MediaRescue.App.Infrastructure;
using MediaRescue.App.Tests.Support;
using MediaRescue.App.ViewModels;
using MediaRescue.Core;

namespace MediaRescue.App.Tests;

/// <summary>RF-09: batch-mode state machine. It never starts by itself when something blocks; it pauses and asks.</summary>
public class BatchTests
{
    private static async Task<Harness> StartAsync(bool askName = false, Action<Harness>? configure = null)
    {
        var h = new Harness();
        h.SettingsStore.Stored.BatchAskName = askName;
        configure?.Invoke(h);
        await h.StartAsync();
        return h;
    }

    private static Task InsertAndSettleAsync(Harness h, DiscInfo disc) => InsertAsync(h, disc);

    private static async Task InsertAsync(Harness h, DiscInfo disc)
    {
        await h.InsertAsync(disc);
        await h.Batch.WhenIdleAsync();
    }

    [Fact]
    public async Task TurningOn_WithNoDisc_WaitsForTheFirstDisc()
    {
        var h = await StartAsync();
        h.Batch.State.Should().Be(BatchState.Off);

        h.Batch.IsEnabled = true;

        h.Batch.State.Should().Be(BatchState.WaitingForDisc);
        h.Batch.Instruction.Should().Be("Insira o primeiro disco.");
        h.Extract.Batch.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task TurningOn_WithoutAReader_AsksToConnectOne()
    {
        var h = await StartAsync(configure: x => x.Drives.DriveList.Clear());
        h.Batch.IsEnabled = true;
        h.Batch.Instruction.Should().Be("Conecte o leitor para começar.");
    }

    [Fact]
    public async Task Cycle_DiscArrives_ExtractsWithSuggestedName_Ejects_AndAsksForTheNext()
    {
        var h = await StartAsync(askName: false);
        h.Batch.IsEnabled = true;

        await InsertAsync(h, TestDiscs.Dvd());

        h.Engine.Requests.Should().ContainSingle().Which.FolderName.Should().Be("FORMATURA");
        h.Drives.Ejected.Should().Equal("D:");
        h.Batch.State.Should().Be(BatchState.WaitingForDisc);
        h.Batch.Instruction.Should().Be("Insira o próximo disco.");
        var item = h.Batch.Session.Should().ContainSingle().Subject;
        item.Name.Should().Be("FORMATURA");
        item.Status.Should().Be(BatchItemStatus.Intact);
        item.StatusLine.Should().StartWith("Concluído");
        h.Dialogs.WasShown("Extração concluída").Should().BeFalse("batch mode replaces the per-disc summary with the session list");
        h.Dialogs.NamePrompts.Should().BeEmpty();
        h.Catalog.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task AskName_PromptsOnlyForTheName_WithTheSuggestion_AndUsesTheAnswer()
    {
        var h = await StartAsync(askName: true);
        h.Dialogs.NameResponder = r => new NamePromptResult("Formatura 2009", false);
        h.Batch.IsEnabled = true;

        await InsertAsync(h, TestDiscs.Dvd());

        h.Dialogs.NamePrompts.Should().ContainSingle();
        h.Dialogs.NamePrompts[0].SuggestedName.Should().Be("FORMATURA");
        h.Dialogs.NamePrompts[0].OfferDontAsk.Should().BeTrue();
        h.Engine.Requests.Single().FolderName.Should().Be("Formatura 2009");
        h.Batch.Session.Single().Name.Should().Be("Formatura 2009");
    }

    [Fact]
    public async Task AskName_DontAskAgain_TurnsTheSettingOff_AndNextDiscUsesTheSuggestion()
    {
        var h = await StartAsync(askName: true);
        h.Dialogs.NameResponder = r => new NamePromptResult(r.SuggestedName, DontAskAgain: true);
        h.Batch.IsEnabled = true;
        await InsertAsync(h, TestDiscs.Dvd());

        h.Main.Settings.Current.BatchAskName.Should().BeFalse();
        h.Dialogs.NamePrompts.Should().HaveCount(1);

        await InsertAsync(h, TestDiscs.Dvd(label: "NATAL", serial: "AAAA-0002"));
        h.Dialogs.NamePrompts.Should().HaveCount(1, "no more questions");
        h.Engine.Requests.Select(r => r.FolderName).Should().Equal("FORMATURA", "NATAL");
    }

    [Fact]
    public async Task AskName_Cancelled_PausesInsteadOfCopying()
    {
        var h = await StartAsync(askName: true);
        h.Dialogs.NameResponder = _ => null;
        h.Batch.IsEnabled = true;

        await InsertAsync(h, TestDiscs.Dvd());

        h.Engine.Requests.Should().BeEmpty();
        h.Batch.State.Should().Be(BatchState.Blocked);
        h.Batch.Blocker.Should().Be(BatchBlocker.NameCancelled);
        h.Batch.Actions.Select(a => a.Text).Should().Contain("Tentar de novo");
    }

    [Fact]
    public async Task TwoDiscsInARow_AreBothProcessed_WithDistinctFolders()
    {
        var h = await StartAsync();
        h.Batch.IsEnabled = true;
        await InsertAsync(h, TestDiscs.Dvd(label: "CD1", serial: "S1"));
        await InsertAsync(h, TestDiscs.Dvd(label: "CD2", serial: "S2"));

        h.Engine.Requests.Select(r => r.FolderName).Should().Equal("CD1", "CD2");
        h.Batch.Session.Select(i => i.Name).Should().Equal("CD2", "CD1"); // newest first
        h.Batch.Session.Should().OnlyContain(i => i.Status == BatchItemStatus.Intact);
        h.Batch.State.Should().Be(BatchState.WaitingForDisc);
    }

    // ------------------------------------------------------------ blockers: pause and ask

    [Fact]
    public async Task Blocker_ProtectedDisc_PausesAndOffersToSkip()
    {
        var h = await StartAsync();
        h.Batch.IsEnabled = true;
        await InsertAsync(h, TestDiscs.Dvd(isProtected: true));

        h.Engine.Requests.Should().BeEmpty();
        h.Batch.State.Should().Be(BatchState.Blocked);
        h.Batch.Blocker.Should().Be(BatchBlocker.DiscRefused);
        h.Batch.BlockMessage.Should().Contain("protegido contra cópia");
        h.Batch.Actions.Should().ContainSingle().Which.Text.Should().Contain("Pular este disco");

        await h.Batch.Actions[0].Command.ExecuteAsync(null);
        await h.Batch.WhenIdleAsync();

        h.Drives.Ejected.Should().Equal("D:");
        h.Batch.Session.Should().ContainSingle(i => i.Status == BatchItemStatus.Skipped);
        h.Batch.State.Should().Be(BatchState.WaitingForDisc);
        h.Engine.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(DriveState.Unreadable)]
    [InlineData(DriveState.Blank)]
    [InlineData(DriveState.AudioCd)]
    [InlineData(DriveState.Unfinalized)]
    public async Task Blocker_UnusableDisc_NeverStartsOnItsOwn(DriveState state)
    {
        var h = await StartAsync();
        h.Batch.IsEnabled = true;
        h.Drives.States["D:"] = state;
        await h.Extract.RefreshAsync();
        await h.Batch.WhenIdleAsync();

        h.Batch.State.Should().Be(BatchState.Blocked);
        h.Engine.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Blocker_AlreadyExtractedDisc_PausesWithoutTheNormalPrompt_AndExtractAgainContinues()
    {
        var disc = TestDiscs.Dvd();
        var h = await StartAsync(configure: x => x.Catalog.Entries.Add(new CatalogEntry
        {
            Fingerprint = disc.Fingerprint, Name = "FORMATURA", ExtractedAtUtc = new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc),
            DestinationPath = @"E:\Videos\Antiga",
        }));
        h.Batch.IsEnabled = true;
        await InsertAsync(h, disc);

        h.Batch.State.Should().Be(BatchState.Blocked);
        h.Batch.Blocker.Should().Be(BatchBlocker.Duplicate);
        h.Batch.BlockMessage.Should().Contain("02/01/2026");
        h.Engine.Requests.Should().BeEmpty();
        h.Dialogs.WasShown("já foi extraído").Should().BeFalse("batch mode shows the question in its own panel");

        var again = h.Batch.Actions.Single(a => a.Text == "Extrair de novo");
        await again.Command.ExecuteAsync(null);
        await h.Batch.WhenIdleAsync();

        h.Engine.Requests.Should().ContainSingle();
        h.Batch.Session.Should().ContainSingle(i => i.Status == BatchItemStatus.Intact);
    }

    [Fact]
    public async Task Blocker_DestinationProblem_PausesAndResumesOnlyWhenTheUserConfirms()
    {
        var h = await StartAsync(configure: x => x.Validator.Rule = (_, _, _, _) =>
            new[] { new ValidationIssue(ValidationSeverity.Blocking, "Não há espaço suficiente: faltam 1,0 GB.") });
        h.Batch.IsEnabled = true;
        await InsertAsync(h, TestDiscs.Dvd());

        h.Batch.State.Should().Be(BatchState.Blocked);
        h.Batch.Blocker.Should().Be(BatchBlocker.Destination);
        h.Batch.BlockMessage.Should().Be("Não há espaço suficiente: faltam 1,0 GB.");
        h.Engine.Requests.Should().BeEmpty();

        // The user frees space / picks another destination on the main screen.
        h.Validator.Rule = null;
        await h.Extract.ValidateNowAsync();
        await h.Batch.WhenIdleAsync();

        h.Batch.State.Should().Be(BatchState.Blocked, "it asks before continuing; it does not start by itself");
        h.Batch.BlockMessage.Should().Contain("O destino está pronto");
        h.Batch.Actions.First().Text.Should().Be("Continuar");
        h.Engine.Requests.Should().BeEmpty();

        await h.Batch.Actions.First().Command.ExecuteAsync(null);
        await h.Batch.WhenIdleAsync();
        h.Engine.Requests.Should().ContainSingle();
        h.Batch.State.Should().Be(BatchState.WaitingForDisc);
    }

    [Fact]
    public async Task Blocker_NoDestinationChosen_Pauses()
    {
        var h = new Harness();
        h.SettingsStore.Stored.BatchAskName = false;
        await h.StartAsync(destination: null);
        h.Batch.IsEnabled = true;
        await InsertAsync(h, TestDiscs.Dvd());

        h.Batch.Blocker.Should().Be(BatchBlocker.Destination);
        h.Batch.BlockMessage.Should().Be("Escolha a pasta de destino.");
    }

    [Fact]
    public async Task Blocked_DiscRemoved_ReturnsToWaiting()
    {
        var h = await StartAsync();
        h.Batch.IsEnabled = true;
        await InsertAsync(h, TestDiscs.Dvd(isProtected: true));
        h.Batch.State.Should().Be(BatchState.Blocked);

        await h.RemoveDiscAsync();
        await h.Batch.WhenIdleAsync();

        h.Batch.State.Should().Be(BatchState.WaitingForDisc);
        h.Batch.Actions.Should().BeEmpty();
        h.Batch.BlockMessage.Should().BeEmpty();
    }

    [Fact]
    public async Task Blocked_ThenAGoodDiscArrives_Proceeds()
    {
        var h = await StartAsync();
        h.Batch.IsEnabled = true;
        await InsertAsync(h, TestDiscs.Dvd(isProtected: true));
        await h.RemoveDiscAsync();
        await InsertAsync(h, TestDiscs.Dvd(label: "BOM", serial: "OK1"));

        h.Engine.Requests.Should().ContainSingle().Which.FolderName.Should().Be("BOM");
    }

    // ------------------------------------------------------------ results and leaving the mode

    [Fact]
    public async Task PartialResult_IsMarkedWithWarnings()
    {
        var h = await StartAsync(configure: x => x.Engine.Behavior = (r, _, _, _) => Task.FromResult(
            FakeEngine.Result(r, OverallStatus.Partial, FileStatus.Partial, lost: 4096)));
        h.Batch.IsEnabled = true;
        await InsertAsync(h, TestDiscs.Dvd());

        var item = h.Batch.Session.Single();
        item.Status.Should().Be(BatchItemStatus.WithWarnings);
        item.Detail.Should().Contain("com problema");
        item.Icon.Should().Be("⚠");
    }

    [Fact]
    public async Task EngineFailure_IsMarkedFailed_AndTheBatchKeepsWaiting()
    {
        var h = await StartAsync(configure: x => x.Engine.Behavior = (_, _, _, _) => throw new InvalidOperationException("x"));
        h.Batch.IsEnabled = true;
        await InsertAsync(h, TestDiscs.Dvd());

        h.Batch.Session.Single().Status.Should().Be(BatchItemStatus.Failed);
        h.Batch.State.Should().Be(BatchState.WaitingForDisc);
    }

    [Fact]
    public async Task TurningOff_ShowsTheSessionSummary()
    {
        var h = await StartAsync();
        h.Batch.IsEnabled = true;
        await InsertAsync(h, TestDiscs.Dvd(label: "A", serial: "1"));
        await InsertAsync(h, TestDiscs.Dvd(label: "B", serial: "2", isProtected: true));
        h.Batch.State.Should().Be(BatchState.Blocked);
        await h.Batch.Actions[0].Command.ExecuteAsync(null); // skip B
        await h.Batch.WhenIdleAsync();

        h.Batch.IsEnabled = false;
        await h.Batch.WhenIdleAsync();

        h.Batch.State.Should().Be(BatchState.Off);
        var summary = h.Dialogs.Shown.Single(d => d.Title == "Resumo da sessão");
        summary.Message.Should().Contain("Discos nesta sessão: 2").And.Contain("Concluídos: 1").And.Contain("Pulados ou cancelados: 1");
    }

    [Fact]
    public async Task TurningOff_WithAnEmptySession_ShowsNoSummary()
    {
        var h = await StartAsync();
        h.Batch.IsEnabled = true;
        h.Batch.IsEnabled = false;
        await h.Batch.WhenIdleAsync();
        h.Dialogs.WasShown("Resumo").Should().BeFalse();
        h.Batch.State.Should().Be(BatchState.Off);
    }

    [Fact]
    public async Task TurningOff_DuringACopy_WaitsForItToFinish()
    {
        var h = await StartAsync();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource<ExtractionResult>();
        h.Engine.Behavior = (r, _, _, _) => { started.TrySetResult(); return release.Task; };
        h.Batch.IsEnabled = true;
        var insert = h.InsertAsync(TestDiscs.Dvd());
        await insert;
        await started.Task;

        h.Batch.State.Should().Be(BatchState.Extracting);
        h.Batch.IsEnabled = false;
        h.Batch.IsEnabled.Should().BeTrue("the mode stays on until the current disc is done");
        h.Dialogs.WasShown("Modo lote").Should().BeTrue();

        release.SetResult(FakeEngine.Intact(h.Engine.Requests.Single()));
        await h.Batch.WhenIdleAsync();

        h.Batch.State.Should().Be(BatchState.Off);
        h.Batch.IsEnabled.Should().BeFalse();
        h.Dialogs.WasShown("Resumo da sessão").Should().BeTrue();
    }

    [Fact]
    public async Task EnablingWhileAGoodDiscIsAlreadyInside_StartsImmediately()
    {
        var h = await StartAsync();
        await h.InsertAsync(TestDiscs.Dvd());
        h.Dialogs.Shown.Clear();

        h.Batch.IsEnabled = true;
        await h.Batch.WhenIdleAsync();

        h.Engine.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ExitCommand_TurnsTheModeOff()
    {
        var h = await StartAsync();
        h.Batch.IsEnabled = true;
        h.Batch.ExitCommand.Execute(null);
        await h.Batch.WhenIdleAsync();
        h.Batch.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task DiscThatCouldNotBeEjected_IsNotProcessedTwice()
    {
        var h = await StartAsync(configure: x => x.Drives.EjectResult = false);
        h.Batch.IsEnabled = true;
        await InsertAsync(h, TestDiscs.Dvd());

        h.Engine.Requests.Should().ContainSingle();
        h.Batch.Instruction.Should().Be("Retire o disco e insira o próximo.");
        await h.Extract.RefreshAsync();
        await h.Batch.WhenIdleAsync();
        h.Engine.Requests.Should().ContainSingle("the same disc must not start another copy");
    }
}


