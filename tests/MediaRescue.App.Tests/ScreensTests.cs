using MediaRescue.App.Infrastructure;
using MediaRescue.App.Services;
using MediaRescue.App.Tests.Support;
using MediaRescue.App.ViewModels;
using MediaRescue.Core;

namespace MediaRescue.App.Tests;

/// <summary>"Testar leitor" (RF-07) and "Histórico" (RF-08) screens, plus small helpers.</summary>
public class DiagnosticsViewModelTests
{
    private static DiagnosticCheck Check(string id, CheckOutcome o, string? tip = null) => new(id, $"Verificação {id}", o, $"detalhe {id}", tip);

    private static async Task<Harness> ReadyAsync()
    {
        var h = new Harness();
        await h.StartAsync();
        return h;
    }

    [Fact]
    public async Task Run_ShowsEachCheckWithIconTipAndSummary()
    {
        var h = await ReadyAsync();
        h.Diagnostics.Checks.AddRange(new[]
        {
            Check("a", CheckOutcome.Pass), Check("b", CheckOutcome.NotSupported), Check("c", CheckOutcome.Warning, "Limpe o disco."),
            Check("d", CheckOutcome.Fail, "Use uma porta USB direto no computador."), Check("e", CheckOutcome.Skipped),
        });
        await h.Main.Diagnostics.RunAsync();

        var vm = h.Main.Diagnostics;
        vm.Checks.Select(c => c.Icon).Should().Equal("✔", "—", "⚠", "✖", "↷");
        vm.Checks.Select(c => c.Tone).Should().Equal(StatusTone.Ok, StatusTone.Neutral, StatusTone.Warn, StatusTone.Danger, StatusTone.Neutral);
        vm.Checks.Where(c => c.HasTip).Select(c => c.Id).Should().Equal("c", "d");
        vm.SummaryText.Should().Contain("1 problema");
        vm.SummaryTone.Should().Be(StatusTone.Danger);
        vm.ShowGeneralTips.Should().BeTrue();
        vm.GeneralTips.Should().Contain(t => t.Contains("hub")).And.Contain(t => t.Contains("outro disco"));
        vm.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task AllPassing_SaysEverythingIsFine()
    {
        var h = await ReadyAsync();
        h.Diagnostics.Checks.AddRange(new[] { Check("a", CheckOutcome.Pass), Check("b", CheckOutcome.NotSupported) });
        await h.Main.Diagnostics.RunAsync();

        h.Main.Diagnostics.SummaryTone.Should().Be(StatusTone.Ok);
        h.Main.Diagnostics.SummaryText.Should().Contain("Tudo certo");
        h.Main.Diagnostics.ShowGeneralTips.Should().BeFalse();
    }

    [Fact]
    public async Task OnlyWarnings_ProduceAWarningSummary()
    {
        var h = await ReadyAsync();
        h.Diagnostics.Checks.Add(Check("a", CheckOutcome.Warning, "Tente de novo."));
        await h.Main.Diagnostics.RunAsync();
        h.Main.Diagnostics.SummaryTone.Should().Be(StatusTone.Warn);
        h.Main.Diagnostics.SummaryText.Should().Contain("1 aviso");
    }

    [Fact]
    public async Task FullSurfaceScan_IsOptionalAndPassedThrough()
    {
        var h = await ReadyAsync();
        h.Diagnostics.Checks.Add(Check("a", CheckOutcome.Pass));
        await h.Main.Diagnostics.RunAsync();
        h.Diagnostics.LastFullScan.Should().BeFalse();

        h.Main.Diagnostics.FullScan = true;
        await h.Main.Diagnostics.RunAsync();
        h.Diagnostics.LastFullScan.Should().BeTrue();
    }

    [Fact]
    public async Task CannotRun_WithoutAReader_OrDuringACopy_AndSaysWhy()
    {
        var h = new Harness();
        h.Drives.DriveList.Clear();
        await h.StartAsync();
        var vm = h.Main.Diagnostics;
        vm.CanRun.Should().BeFalse();
        vm.RunDisabledReason.Should().Be("Conecte um leitor de CD/DVD para testar.");
        await vm.RunAsync();
        vm.HasRun.Should().BeFalse();

        var h2 = new Harness();
        await h2.StartAsync();
        await h2.InsertAsync(TestDiscs.Dvd());
        var release = new TaskCompletionSource<ExtractionResult>();
        var started = new TaskCompletionSource();
        h2.Engine.Behavior = (r, _, _, _) => { started.TrySetResult(); return release.Task; };
        var run = h2.Extract.RunExtractionAsync(batch: false);
        await started.Task;

        h2.Main.Diagnostics.CanRun.Should().BeFalse();
        h2.Main.Diagnostics.RunDisabledReason.Should().Be("Aguarde o fim da cópia para testar o leitor.");
        release.SetResult(FakeEngine.Intact(h2.Engine.Requests.Single()));
        await run;
        h2.Main.Diagnostics.CanRun.Should().BeTrue();
    }

    [Fact]
    public async Task DiagnosticsThrowing_BecomesAFailedCheck_NotACrash()
    {
        var h = await ReadyAsync();
        h.Diagnostics.Throws = new InvalidOperationException("driver exploded");
        await h.Main.Diagnostics.RunAsync();

        h.Main.Diagnostics.Checks.Should().ContainSingle(c => c.Check.Outcome == CheckOutcome.Fail && c.Id == "error");
        h.Main.Diagnostics.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task CopyReport_PutsPlainTextOnTheClipboard()
    {
        var h = await ReadyAsync();
        h.Diagnostics.Checks.AddRange(new[] { Check("a", CheckOutcome.Pass), Check("d", CheckOutcome.Fail, "Use uma porta USB direto no computador.") });
        await h.Main.Diagnostics.RunAsync();
        h.Main.Diagnostics.CopyReportCommand.Execute(null);

        var text = h.Shell.Clipboard!;
        text.Should().Contain("Relatório do leitor").And.Contain("D:  hp CDDVDW TS-L633M");
        text.Should().Contain("[OK] Verificação a").And.Contain("[FALHOU] Verificação d");
        text.Should().Contain("Dica: Use uma porta USB direto no computador.");
        text.Should().Contain("Teste de superfície completo: não");
    }

    [Fact]
    public void Report_DoesNotRepeatTipsOfPassingChecks()
    {
        var text = DiagnosticReport.Build("D:", true, new[] { Check("a", CheckOutcome.Pass, "não deveria aparecer") }, new DateTime(2026, 10, 12, 9, 5, 0));
        text.Should().NotContain("Dica");
        text.Should().Contain("sim").And.Contain("12/10/2026 09:05");
    }

    [Fact]
    public async Task CopyReportButton_IsDisabledUntilThereIsAReport()
    {
        var h = await ReadyAsync();
        h.Main.Diagnostics.CopyReportCommand.CanExecute(null).Should().BeFalse();
        h.Diagnostics.Checks.Add(Check("a", CheckOutcome.Pass));
        await h.Main.Diagnostics.RunAsync();
        h.Main.Diagnostics.CopyReportCommand.CanExecute(null).Should().BeTrue();
    }
}

public class HistoryViewModelTests
{
    private static CatalogEntry E(string name, DateTime when, OverallStatus status = OverallStatus.Intact, string? path = null, long bytes = 1_000_000_000, long lost = 0) => new()
    {
        Fingerprint = name, Name = name, ExtractedAtUtc = when, DestinationPath = path ?? $@"E:\Videos\{name}", TotalBytes = bytes, BytesLost = lost, Status = status, FileCount = 3,
    };

    private static async Task<Harness> WithEntriesAsync()
    {
        var h = new Harness();
        h.Catalog.Entries.AddRange(new[]
        {
            E("Natal 2008", new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc), OverallStatus.Partial, lost: 4096),
            E("Formatura", new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc)),
            E("Praia", new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc), OverallStatus.Failed, path: @"F:\Backup\Praia"),
        });
        await h.Main.History.LoadAsync();
        return h;
    }

    [Fact]
    public async Task List_IsNewestFirst_WithReadableColumns()
    {
        var h = await WithEntriesAsync();
        var items = h.Main.History.Items;
        items.Select(i => i.Name).Should().Equal("Formatura", "Praia", "Natal 2008");
        items[0].SizeText.Should().Be("954 MB");
        items[0].StatusText.Should().Be("Íntegro");
        items[0].LostText.Should().Be("—");
        items[2].StatusText.Should().Be("Com perdas");
        items[2].LostText.Should().Be("4,0 KB");
        items[1].Tone.Should().Be(StatusTone.Danger);
        h.Main.History.SummaryText.Should().Be("3 discos extraídos");
    }

    [Theory]
    [InlineData("natal", new[] { "Natal 2008" })]
    [InlineData("BACKUP", new[] { "Praia" })]
    [InlineData("falhou", new[] { "Praia" })]
    [InlineData("perdas", new[] { "Natal 2008" })]
    [InlineData("2026 forma", new[] { "Formatura" })]
    public async Task Search_FiltersByNameStatusFolderAndDate_IgnoringCase(string query, string[] expected)
    {
        var h = await WithEntriesAsync();
        h.Main.History.SearchText = query;
        h.Main.History.Items.Select(i => i.Name).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Search_WithNoMatch_ExplainsAndClearingRestoresTheList()
    {
        var h = await WithEntriesAsync();
        h.Main.History.SearchText = "xyz";
        h.Main.History.Items.Should().BeEmpty();
        h.Main.History.EmptyText.Should().Contain("Nenhum resultado para “xyz”");
        h.Main.History.SummaryText.Should().Be("0 de 3 discos");

        h.Main.History.SearchText = "";
        h.Main.History.Items.Should().HaveCount(3);
        h.Main.History.EmptyText.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyHistory_SaysSo_AndExportIsDisabled()
    {
        var h = new Harness();
        await h.Main.History.LoadAsync();
        h.Main.History.EmptyText.Should().Contain("Nenhum disco foi extraído ainda");
        h.Main.History.ExportCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task OpenFolder_OpensExistingFolders_AndExplainsMissingOnes()
    {
        var h = await WithEntriesAsync();
        h.Fs.Folders.Add(@"E:\Videos\Formatura");
        var existing = h.Main.History.Items.Single(i => i.Name == "Formatura");
        var missing = h.Main.History.Items.Single(i => i.Name == "Praia");

        await h.Main.History.OpenFolderCommand.ExecuteAsync(existing);
        h.Shell.OpenedFolders.Should().Equal(@"E:\Videos\Formatura");

        await h.Main.History.OpenFolderCommand.ExecuteAsync(missing);
        h.Shell.OpenedFolders.Should().HaveCount(1);
        h.Dialogs.Shown.Single().Title.Should().Be("A pasta não existe mais");
        h.Dialogs.Shown.Single().Message.Should().Contain(@"F:\Backup\Praia");
    }

    [Fact]
    public async Task ExportCsv_WritesTheCatalogCsv_WhereTheUserChose()
    {
        var h = await WithEntriesAsync();
        h.Shell.SaveFileResult = @"C:\Users\x\Documents\historico.csv";
        await h.Main.History.ExportCommand.ExecuteAsync(null);

        h.Shell.Written.Should().ContainSingle();
        h.Shell.Written[0].Path.Should().Be(@"C:\Users\x\Documents\historico.csv");
        h.Shell.Written[0].Text.Should().Contain("Formatura");
        h.Dialogs.Shown.Single().Title.Should().Be("Histórico exportado");
    }

    [Fact]
    public async Task ExportCsv_Cancelled_WritesNothing()
    {
        var h = await WithEntriesAsync();
        h.Shell.SaveFileResult = null;
        await h.Main.History.ExportCommand.ExecuteAsync(null);
        h.Shell.Written.Should().BeEmpty();
        h.Dialogs.Shown.Should().BeEmpty();
    }

    [Fact]
    public async Task E30_RecoveredCatalog_ShowsTheWarning()
    {
        var h = new Harness();
        h.Catalog.LoadWarning = "O histórico estava corrompido e foi restaurado da cópia de segurança.";
        await h.Main.History.LoadAsync();
        h.Main.History.Warning.Should().Contain("restaurado");
    }

    [Fact]
    public async Task ExtractingADisc_AddsItToTheHistory()
    {
        var h = new Harness();
        await h.StartAsync();
        await h.InsertAsync(TestDiscs.Dvd());
        await h.Extract.RunExtractionAsync(batch: false);
        await Harness.WaitForAsync(() => h.Main.History.Items.Count == 1);
        h.Main.History.Items[0].Name.Should().Be("FORMATURA");
    }
}

public class MiscTests
{
    [Fact]
    public void ReconnectTracker_TripsOnThreeWithinTwoMinutes_ButNotWhenSpreadOut()
    {
        var t0 = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var tight = new ReconnectTracker();
        tight.Record(t0); tight.Record(t0.AddSeconds(40));
        tight.IsTripped(t0.AddSeconds(41)).Should().BeFalse();
        tight.Record(t0.AddSeconds(80));
        tight.IsTripped(t0.AddSeconds(81)).Should().BeTrue();
        tight.IsTripped(t0.AddMinutes(10)).Should().BeFalse("old reconnections expire");

        var spread = new ReconnectTracker();
        spread.Record(t0); spread.Record(t0.AddMinutes(3)); spread.Record(t0.AddMinutes(6));
        spread.IsTripped(t0.AddMinutes(6)).Should().BeFalse();
    }

    [Fact]
    public void StartupOptions_ParseTheDocumentedFlags()
    {
        var o = StartupOptions.Parse(new[] { "--demo-state=PARTIAL", "--smoke", "--demo-files=500", "--theme=light" });
        o.Demo.Should().BeTrue();
        o.DemoState.Should().Be("partial");
        o.Smoke.Should().BeTrue();
        o.DemoFiles.Should().Be(500);
        o.Theme.Should().Be("Light");

        var plain = StartupOptions.Parse(Array.Empty<string>());
        plain.Demo.Should().BeFalse();
        plain.Smoke.Should().BeFalse();
        plain.DemoState.Should().BeNull();

        StartupOptions.Parse(new[] { "--demo" }).Demo.Should().BeTrue();
        StartupOptions.Parse(new[] { "--demo-state=bogus" }).DemoState.Should().Be("ready");
        foreach (var s in new[] { "nodrive", "nodisc", "ready", "copying", "partial", "diagnostics", "history" })
            StartupOptions.Parse(new[] { $"--demo-state={s}" }).DemoState.Should().Be(s);
    }

    [Fact]
    public async Task Navigation_ExposesExactlyOnePageAtATime_AndHistoryLoadsOnDemand()
    {
        var h = new Harness();
        h.Catalog.Entries.Add(new CatalogEntry { Fingerprint = "a", Name = "X", DestinationPath = @"E:\X", ExtractedAtUtc = DateTime.UtcNow });
        var main = h.Main;
        main.IsExtractPage.Should().BeTrue();

        main.IsHistoryPage = true;
        main.Page.Should().Be(AppPage.History);
        (main.IsExtractPage, main.IsDiagnosticsPage, main.IsSettingsPage).Should().Be((false, false, false));
        await Harness.WaitForAsync(() => main.History.Items.Count == 1);

        main.GoSettingsCommand.Execute(null);
        main.IsSettingsPage.Should().BeTrue();
        main.Title.Should().Be("Extrator de Mídias");
    }

    [Fact]
    public async Task ReaderDisappearingDuringACopy_DoesNotClearTheDiscNorTheList()
    {
        var h = new Harness();
        await h.StartAsync();
        await h.InsertAsync(TestDiscs.Dvd());
        var release = new TaskCompletionSource<ExtractionResult>();
        var started = new TaskCompletionSource();
        h.Engine.Behavior = (r, _, _, _) => { started.TrySetResult(); return release.Task; };
        var run = h.Extract.RunExtractionAsync(batch: false);
        await started.Task;

        // E-03: unplug the reader mid-copy. The screen must keep the content; only the engine decides what happens.
        h.Drives.DriveList.Clear();
        await h.Extract.RefreshAsync();
        h.Extract.Content.TotalFiles.Should().Be(6);
        h.Extract.Disc.Should().NotBeNull();
        h.Extract.ShowEmptyState.Should().BeFalse();
        h.Extract.IsExtracting.Should().BeTrue();

        h.Drives.DriveList.Add(FakeDrives.D);
        await h.Extract.RefreshAsync();
        h.Drives.AnalyzeCalls.Should().Be(1, "no re-analysis while copying");

        release.SetResult(FakeEngine.Intact(h.Engine.Requests.Single()));
        await run;
    }
}

