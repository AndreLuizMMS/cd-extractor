using MediaRescue.App.Tests.Support;
using MediaRescue.App.ViewModels;
using MediaRescue.Core;

namespace MediaRescue.App.Tests;

/// <summary>RF-03/RF-04: destination validation (blocking vs warning), names, quick list and remembered destination.</summary>
public class DestinationTests
{
    private static async Task<Harness> ReadyAsync(Action<Harness>? configure = null)
    {
        var h = new Harness();
        configure?.Invoke(h);
        await h.StartAsync();
        await h.InsertAsync(TestDiscs.Dvd());
        return h;
    }

    [Fact]
    public async Task BlockingIssue_DisablesExtract_AndShowsItsMessageBesideTheButton()
    {
        var h = await ReadyAsync(x => x.Validator.Rule = (_, _, _, _) =>
            new[] { new ValidationIssue(ValidationSeverity.Blocking, "Não há espaço suficiente: faltam 3,2 GB.") });
        await h.Extract.ValidateNowAsync();

        h.Extract.CanExtract.Should().BeFalse();
        h.Extract.ExtractDisabledReason.Should().Be("Não há espaço suficiente: faltam 3,2 GB.");
        h.Extract.Issues.Should().ContainSingle().Which.IsBlocking.Should().BeTrue();
        h.Extract.DestinationTone.Should().Be(StatusTone.Danger);
        h.Extract.DestinationIcon.Should().Be("✖");
    }

    [Fact]
    public async Task E14_NotEnoughSpace_ReportsHowMuchIsMissing_ThroughTheValidatorMessage()
    {
        var h = await ReadyAsync();
        h.Validator.Rule = (_, _, entries, _) =>
        {
            var needed = entries.Where(e => !e.IsDirectory).Sum(e => e.Size) * 105 / 100 + 50_000_000;
            return new[] { new ValidationIssue(ValidationSeverity.Blocking, $"Faltam {needed - 100_000_000} bytes no destino.") };
        };
        await h.Extract.ValidateNowAsync();
        h.Extract.CanExtract.Should().BeFalse();
        h.Extract.ExtractDisabledReason.Should().StartWith("Faltam ");
    }

    [Fact]
    public async Task WarningIssue_DoesNotBlock_ButIsShown()
    {
        var h = await ReadyAsync(x => x.Validator.Rule = (_, _, _, _) =>
            new[] { new ValidationIssue(ValidationSeverity.Warning, "Este destino é uma unidade de nuvem.") });
        await h.Extract.ValidateNowAsync();

        h.Extract.CanExtract.Should().BeTrue();
        h.Extract.ExtractDisabledReason.Should().BeEmpty();
        h.Extract.Issues.Should().ContainSingle(i => i.Message.Contains("nuvem") && !i.IsBlocking && i.Icon == "⚠");
        h.Extract.DestinationTone.Should().Be(StatusTone.Warn);
    }

    [Fact]
    public async Task OkSeverityIssues_AreNotListed()
    {
        var h = await ReadyAsync(x => x.Validator.Rule = (_, _, _, _) => new[] { new ValidationIssue(ValidationSeverity.Ok, "Tudo certo.") });
        await h.Extract.ValidateNowAsync();
        h.Extract.Issues.Should().BeEmpty();
        h.Extract.DestinationTone.Should().Be(StatusTone.Ok);
        h.Extract.DestinationIcon.Should().Be("✔");
    }

    [Fact]
    public async Task E17_Fat32BigFile_BlocksOnlyThatFile_AndCanBeUnticked()
    {
        var h = await ReadyAsync();
        h.Validator.Rule = (_, _, entries, _) => entries.Any(e => e.RelativePath.EndsWith("VTS_01_1.VOB", StringComparison.OrdinalIgnoreCase))
            ? new[] { new ValidationIssue(ValidationSeverity.Blocking, "VTS_01_1.VOB tem 4,3 GB e não cabe em FAT32. Formate o pendrive em exFAT.", @"VIDEO_TS\VTS_01_1.VOB") }
            : Array.Empty<ValidationIssue>();
        await h.Extract.ValidateNowAsync();

        h.Extract.CanExtract.Should().BeFalse();
        h.Extract.Content.FindFile(@"VIDEO_TS\VTS_01_1.VOB")!.Issue.Should().Contain("FAT32");
        h.Extract.Content.FindFile(@"VIDEO_TS\VTS_01_2.VOB")!.Issue.Should().BeNull();
        h.Extract.HasBlockedFiles.Should().BeTrue();

        h.Extract.UncheckBlockedCommand.Execute(null);
        await h.Extract.ValidateNowAsync();

        h.Extract.Content.FindFile(@"VIDEO_TS\VTS_01_1.VOB")!.IsChecked.Should().BeFalse();
        h.Extract.CanExtract.Should().BeTrue("the remaining files fit");
        h.Extract.HasBlockedFiles.Should().BeFalse();
    }

    [Fact]
    public async Task ValidatorReceivesOnlyTheSelectedEntries_AndTheOpticalLetter()
    {
        var h = await ReadyAsync();
        h.Extract.Content.ClearAllCommand.Execute(null);
        h.Extract.Content.FindFile(@"Fotos\a.jpg")!.IsChecked = true;
        await h.Extract.ValidateNowAsync();

        var call = h.Validator.Calls.Last();
        call.EntryCount.Should().Be(1);
        call.Root.Should().Be(@"E:\Videos");
        call.Folder.Should().Be("FORMATURA");
        call.EntryCount.Should().BeLessThan(8, "the whole disc was not sent");
    }

    [Fact]
    public async Task ValidatorThrowing_BecomesABlockingMessage_NotACrash()
    {
        var h = await ReadyAsync();
        h.Validator.Rule = (_, _, _, _) => throw new InvalidOperationException("kaboom");
        await h.Extract.ValidateNowAsync();

        h.Extract.CanExtract.Should().BeFalse();
        h.Extract.ExtractDisabledReason.Should().Contain("Não foi possível verificar este destino");
        h.Extract.IsValidating.Should().BeFalse();
    }

    [Fact]
    public async Task NoDestination_DisablesWithReason()
    {
        var h = new Harness();
        await h.StartAsync(destination: null);
        await h.InsertAsync(TestDiscs.Dvd());

        h.Extract.CanExtract.Should().BeFalse();
        h.Extract.ExtractDisabledReason.Should().Be("Escolha a pasta de destino.");
        h.Extract.DestinationSummary.Should().Contain("Escolha a pasta");
    }

    [Fact]
    public async Task UnknownDestination_IsReportedAsUnreachable()
    {
        var h = await ReadyAsync();
        h.Extract.DestinationRoot = @"Z:\Nada";
        await h.Extract.ValidateNowAsync();

        h.Extract.DestinationTone.Should().Be(StatusTone.Danger);
        h.Extract.DestinationSummary.Should().Contain("Não foi possível acessar");
    }

    [Fact]
    public async Task EmptyFolderName_DisablesWithReason()
    {
        var h = await ReadyAsync();
        h.Extract.FolderName = "   ";
        await h.Extract.ValidateNowAsync();

        h.Extract.CanExtract.Should().BeFalse();
        h.Extract.ExtractDisabledReason.Should().Be("Digite um nome para a pasta do disco.");
        h.Extract.FolderNameEdited.Should().BeTrue();
    }

    [Fact]
    public async Task E21_FolderAlreadyExists_SuggestsNumberedName_AndExtractUsesIt()
    {
        var h = await ReadyAsync(x => x.Fs.Folders.Add(@"E:\Videos\FORMATURA"));
        await h.Extract.ValidateNowAsync();

        h.Extract.EffectiveFolderName.Should().Be("FORMATURA (2)");
        h.Extract.NameNote.Should().Contain("Já existe uma pasta").And.Contain("FORMATURA (2)");

        await h.Extract.RunExtractionAsync(batch: false);
        h.Engine.Requests.Should().ContainSingle().Which.FolderName.Should().Be("FORMATURA (2)", "never overwrite or merge");
    }

    [Fact]
    public async Task E20_InvalidCharactersInName_AreSanitisedBeforeUse()
    {
        var h = await ReadyAsync();
        h.Extract.FolderName = "Formatura: 2009?";
        await h.Extract.ValidateNowAsync();
        await h.Extract.RunExtractionAsync(batch: false);

        h.Engine.Requests.Single().FolderName.Should().Be("Formatura_ 2009_");
    }

    [Fact]
    public async Task InterruptedJob_IsAnnouncedNextToTheName()
    {
        var h = await ReadyAsync(x => x.Engine.Resumable = true);
        await h.Extract.ValidateNowAsync();

        h.Extract.WillResume.Should().BeTrue();
        h.Extract.NameNote.Should().Contain("continuar de onde parou");
        h.Extract.EffectiveFolderName.Should().Be("FORMATURA", "a resumable folder keeps its name");
    }

    [Fact]
    public async Task QuickDriveList_ShowsLabelFileSystemAndFreeSpace_AndSelectingFillsTheBox()
    {
        var h = new Harness();
        await h.StartAsync(destination: null);

        h.Extract.QuickDrives.Should().HaveCount(2);
        var usb = h.Extract.QuickDrives.Single(q => q.Root == @"E:\");
        usb.Text.Should().Be("E: KINGSTON · 18,6 GB");
        usb.ToolTip.Should().Contain("KINGSTON").And.Contain("exFAT").And.Contain("livres");

        usb.SelectCommand.Execute(null);
        h.Extract.DestinationRoot.Should().Be(@"E:\");
    }

    [Fact]
    public async Task OpticalDrivesAreNeverOfferedAsDestination()
    {
        var h = new Harness();
        h.Fs.Roots[@"D:\"] = new DestinationInfo(@"D:\", "DISCO", "UDF", 0, 4_700_000_000, true, false, true);
        h.Lister.Roots.Add(@"D:\");
        await h.StartAsync(destination: null);

        h.Extract.QuickDrives.Select(q => q.Root).Should().NotContain(@"D:\");
    }

    [Fact]
    public async Task BrowseButton_UsesThePickedFolder()
    {
        var h = new Harness();
        await h.StartAsync(destination: null);
        h.Shell.PickFolderResult = @"E:\Videos";
        h.Extract.BrowseCommand.Execute(null);
        h.Extract.DestinationRoot.Should().Be(@"E:\Videos");

        h.Shell.PickFolderResult = null; // cancelled
        h.Extract.BrowseCommand.Execute(null);
        h.Extract.DestinationRoot.Should().Be(@"E:\Videos");
    }

    [Fact]
    public async Task LastDestination_IsRestoredWhenItStillExists()
    {
        var h = new Harness();
        h.SettingsStore.Stored.LastDestination = @"E:\Videos";
        await h.Main.InitializeAsync();
        h.Extract.DestinationRoot.Should().Be(@"E:\Videos");
        h.Extract.Dispose();
    }

    [Fact]
    public async Task LastDestination_IsIgnoredWhenItIsGone()
    {
        var h = new Harness();
        h.SettingsStore.Stored.LastDestination = @"E:\Sumiu";
        await h.Main.InitializeAsync();
        h.Extract.DestinationRoot.Should().BeEmpty();
        h.Extract.Dispose();
    }

    [Fact]
    public async Task Typing_TriggersADebouncedBackgroundValidation()
    {
        var h = await ReadyAsync();
        h.Extract.AutoValidate = true;
        h.Extract.ValidationDelay = TimeSpan.FromMilliseconds(40);
        var before = h.Validator.Calls.Count;

        h.Extract.FolderName = "N";
        h.Extract.FolderName = "Na";
        h.Extract.FolderName = "Nat";
        h.Extract.IsValidating.Should().BeTrue("the gate shows 'Verificando o destino…' while waiting");
        h.Extract.CanExtract.Should().BeFalse();

        await Harness.WaitForAsync(() => !h.Extract.IsValidating);
        (h.Validator.Calls.Count - before).Should().Be(1, "three keystrokes, one validation");
        h.Validator.Calls.Last().Folder.Should().Be("Nat");
        h.Extract.CanExtract.Should().BeTrue();
    }

    [Fact]
    public async Task SelectionChange_RevalidatesDestination()
    {
        var h = await ReadyAsync();
        var before = h.Validator.Calls.Count;
        h.Extract.Content.FindFile(@"Fotos\b.jpg")!.IsChecked = false;
        await h.Extract.ValidateNowAsync();

        h.Validator.Calls.Count.Should().BeGreaterThan(before);
        h.Validator.Calls.Last().EntryCount.Should().Be(h.Extract.Content.GetSelectedEntries().Count);
    }
}

