using MediaRescue.App.Tests.Support;
using MediaRescue.App.ViewModels;
using MediaRescue.Core;

namespace MediaRescue.App.Tests;

/// <summary>RF-01: every reader/media state shows a clear message and gates the Extrair button with a visible reason.</summary>
public class Rf01StateTests
{
    [Fact]
    public async Task E01_NoReader_ExplainsAndDisablesExtract()
    {
        var h = new Harness();
        h.Drives.DriveList.Clear();
        await h.StartAsync();

        h.Extract.Screen.Should().Be(ScreenState.NoDrive);
        h.Extract.StatusTitle.Should().Be("Nenhum leitor encontrado");
        h.Extract.StatusMessage.Should().Contain("Conecte um leitor");
        h.Extract.ShowEmptyState.Should().BeTrue();
        h.Extract.CanExtract.Should().BeFalse();
        h.Extract.ExtractDisabledReason.Should().Be("Conecte um leitor de CD/DVD para começar.");
        h.Extract.DiscRows.Should().OnlyContain(r => r.Value == "—", "unavailable fields show a dash, never an error");
    }

    [Theory]
    [InlineData(DriveState.NoMedia, ScreenState.NoMedia, "Sem disco", "Insira um disco no leitor D:.")]
    [InlineData(DriveState.TrayOpen, ScreenState.TrayOpen, "Bandeja aberta", "Feche a bandeja com um disco dentro.")]
    [InlineData(DriveState.Loading, ScreenState.Loading, "Lendo disco…", "Aguarde: o disco ainda está sendo lido.")]
    [InlineData(DriveState.Blank, ScreenState.Blank, "Disco vazio", "O disco está vazio. Insira outro disco.")]
    [InlineData(DriveState.Unfinalized, ScreenState.Unfinalized, "Disco não finalizado", "Disco não finalizado: finalize-o no aparelho onde foi gravado.")]
    [InlineData(DriveState.Unreadable, ScreenState.Unreadable, "Disco ilegível", "Não foi possível ler o disco. Limpe-o e tente novamente.")]
    [InlineData(DriveState.Protected, ScreenState.Protected, "Disco protegido contra cópia", "Disco protegido contra cópia: não é suportado.")]
    [InlineData(DriveState.AudioCd, ScreenState.AudioCd, "CD de áudio", "CD de áudio não é suportado nesta versão.")]
    public async Task DriveState_ShowsMessage_AndExtractIsDisabledWithReason(DriveState state, ScreenState screen, string title, string reason)
    {
        var h = new Harness();
        h.Drives.States["D:"] = state;
        await h.StartAsync();

        h.Extract.Screen.Should().Be(screen);
        h.Extract.StatusTitle.Should().Be(title);
        h.Extract.StatusMessage.Should().NotBeNullOrWhiteSpace("an empty state must always explain what to do");
        h.Extract.ShowEmptyState.Should().BeTrue();
        h.Extract.CanExtract.Should().BeFalse();
        h.Extract.ExtractDisabledReason.Should().Be(reason);
        h.Extract.Disc.Should().BeNull();
        h.Drives.AnalyzeCalls.Should().Be(0, "the disc is only analysed when the media is ready");
    }

    [Fact]
    public async Task E01_ReaderConnectedLater_IsDetectedWithoutRestart()
    {
        var h = new Harness();
        h.Drives.DriveList.Clear();
        await h.StartAsync();
        h.Extract.Screen.Should().Be(ScreenState.NoDrive);

        h.Drives.DriveList.Add(FakeDrives.D);
        h.Drives.States["D:"] = DriveState.NoMedia;
        h.Drives.RaiseChanged();
        await h.Extract.RefreshAsync();

        h.Extract.Screen.Should().Be(ScreenState.NoMedia);
        h.Extract.Drives.Should().ContainSingle(d => d.Letter == "D:");
    }

    [Fact]
    public async Task Ready_DiscWithVideos_EnablesExtractAndShowsDetails()
    {
        var h = new Harness();
        await h.StartAsync();
        await h.InsertAsync(TestDiscs.Dvd());

        h.Extract.Screen.Should().Be(ScreenState.Ready);
        h.Extract.StatusTitle.Should().Be("Disco pronto");
        h.Extract.ShowEmptyState.Should().BeFalse();
        h.Extract.CanExtract.Should().BeTrue();
        h.Extract.ExtractDisabledReason.Should().BeEmpty();
        h.Extract.FolderName.Should().Be("FORMATURA");
        h.Extract.DiscRows.Should().Contain(r => r.Label == "Formato" && r.Value == "DVD-Video");
        h.Extract.DiscRows.Should().Contain(r => r.Label == "Tipo" && r.Value == "DVD-R");
        h.Extract.DiscRows.Should().Contain(r => r.Label == "Gravado em" && r.Value == "12/12/2009");
        h.Extract.DriveRows.Should().Contain(r => r.Label == "Firmware" && r.Value == "0301");
        h.Extract.Content.TotalFiles.Should().Be(6);
    }

    [Fact]
    public async Task Ready_WhileAnalysisRuns_ShowsReadingMessageAndBlocksExtract()
    {
        var h = new Harness();
        await h.StartAsync();
        h.Drives.AnalyzeGate = new TaskCompletionSource<bool>();
        h.Drives.Discs["D:"] = TestDiscs.Dvd();
        h.Drives.States["D:"] = DriveState.Ready;

        var refresh = h.Extract.RefreshAsync(true);
        await Harness.WaitForAsync(() => h.Extract.IsAnalyzing);

        h.Extract.Screen.Should().Be(ScreenState.Loading);
        h.Extract.StatusTitle.Should().Be("Lendo disco…");
        h.Extract.CanExtract.Should().BeFalse();
        h.Extract.ExtractDisabledReason.Should().Be("Aguarde: o disco ainda está sendo lido.");

        h.Drives.AnalyzeGate.SetResult(true);
        await refresh;
        await h.Extract.ValidateNowAsync();
        h.Extract.Screen.Should().Be(ScreenState.Ready);
        h.Extract.CanExtract.Should().BeTrue();
    }

    [Fact]
    public async Task Ready_ButAnalysisReturnsNothing_IsUnreadable()
    {
        var h = new Harness();
        await h.StartAsync();
        h.Drives.States["D:"] = DriveState.Ready; // no disc registered: AnalyzeAsync returns null
        await h.Extract.RefreshAsync();

        h.Extract.Screen.Should().Be(ScreenState.Unreadable);
        h.Extract.CanExtract.Should().BeFalse();
        h.Extract.ExtractDisabledReason.Should().Contain("Não foi possível ler o disco");
    }

    [Fact]
    public async Task Ready_ButAnalysisThrows_IsUnreadableAndDoesNotCrash()
    {
        var h = new Harness();
        await h.StartAsync();
        h.Drives.AnalyzeThrows = new IOException("boom");
        h.Drives.States["D:"] = DriveState.Ready;
        await h.Extract.RefreshAsync();

        h.Extract.Screen.Should().Be(ScreenState.Unreadable);
        h.Extract.StatusMessage.Should().Contain("Limpe-o");
        h.Main.Log.Lines.Should().Contain(l => l.Text.Contains("Não foi possível ler o disco"));
    }

    [Fact]
    public async Task E06_EmptyDisc_IsBlank()
    {
        var h = new Harness();
        await h.StartAsync();
        await h.InsertAsync(TestDiscs.Dvd(format: LogicalFormat.Empty, entries: Array.Empty<DiscEntry>()));

        h.Extract.Screen.Should().Be(ScreenState.Blank);
        h.Extract.StatusTitle.Should().Be("Disco vazio");
        h.Extract.CanExtract.Should().BeFalse();
    }

    [Fact]
    public async Task E09_CopyProtectedDisc_IsRefused()
    {
        var h = new Harness();
        await h.StartAsync();
        await h.InsertAsync(TestDiscs.Dvd(isProtected: true));

        h.Extract.Screen.Should().Be(ScreenState.Protected);
        h.Extract.StatusMessage.Should().Contain("protegido contra cópia");
        h.Extract.CanExtract.Should().BeFalse();
        h.Extract.ExtractDisabledReason.Should().Be("Disco protegido contra cópia: não é suportado.");
        h.Extract.Content.TotalFiles.Should().Be(0, "nothing is offered for a protected disc");
    }

    [Fact]
    public async Task E08_AudioCd_IsRefused()
    {
        var h = new Harness();
        await h.StartAsync();
        await h.InsertAsync(TestDiscs.Dvd(format: LogicalFormat.AudioCd));

        h.Extract.Screen.Should().Be(ScreenState.AudioCd);
        h.Extract.ExtractDisabledReason.Should().Be("CD de áudio não é suportado nesta versão.");
        h.Extract.CanExtract.Should().BeFalse();
    }

    [Fact]
    public async Task E07_UnfinalizedDisc_ExplainsWhatToDo()
    {
        var h = new Harness();
        h.Drives.States["D:"] = DriveState.Unfinalized;
        await h.StartAsync();

        h.Extract.StatusMessage.Should().Contain("finalize");
        h.Extract.StatusMessage.Should().Contain("aparelho onde foi gravado");
        h.Extract.CanExtract.Should().BeFalse();
    }

    [Fact]
    public async Task DiscRemoved_ClearsContentAndReturnsToNoMedia()
    {
        var h = new Harness();
        await h.StartAsync();
        await h.InsertAsync(TestDiscs.Dvd());
        h.Extract.Content.TotalFiles.Should().BeGreaterThan(0);

        await h.RemoveDiscAsync();

        h.Extract.Screen.Should().Be(ScreenState.NoMedia);
        h.Extract.Disc.Should().BeNull();
        h.Extract.Content.TotalFiles.Should().Be(0);
        h.Extract.FolderName.Should().BeEmpty();
        h.Extract.CanExtract.Should().BeFalse();
    }

    [Fact]
    public async Task E02_SeveralReaders_EachHasItsOwnState_AndCanBeSelected()
    {
        var h = new Harness();
        h.Drives.DriveList.Add(FakeDrives.F);
        h.Drives.States["F:"] = DriveState.Ready;
        h.Drives.Discs["F:"] = TestDiscs.Dvd(label: "ISO MONTADA", serial: "ISO-1");
        await h.StartAsync();

        h.Extract.HasMultipleDrives.Should().BeTrue();
        h.Extract.Drives.Select(d => d.StateText).Should().Equal("Sem disco", "Disco pronto");
        h.Extract.SelectedDrive!.Letter.Should().Be("D:");
        h.Extract.Screen.Should().Be(ScreenState.NoMedia);

        h.Extract.SelectedDrive = h.Extract.Drives.Single(d => d.Letter == "F:");
        await h.Extract.RefreshAsync(true);
        await h.Extract.ValidateNowAsync();

        h.Extract.Screen.Should().Be(ScreenState.Ready);
        h.Extract.FolderName.Should().Be("ISO MONTADA");
        h.Extract.CanExtract.Should().BeTrue();
        h.Extract.Drives.Single(d => d.Letter == "F:").DisplayName.Should().Contain("(virtual)");
    }

    [Fact]
    public async Task E23_MultiSessionDisc_WarnsThatOldSessionsMayBeHidden()
    {
        var h = new Harness();
        await h.StartAsync();
        await h.InsertAsync(TestDiscs.Dvd(sessions: 3));

        h.Extract.DiscNotice.Should().Contain("3 sessões").And.Contain("sessões antigas");
        h.Extract.CanExtract.Should().BeTrue("only a warning, not a blocker");
    }

    [Fact]
    public async Task E33_PhysicalDetailsUnavailable_ShowDashesAndExplain()
    {
        var h = new Harness();
        await h.StartAsync();
        var disc = new DiscInfo
        {
            VolumeLabel = "SEMIO", VolumeSerial = "1", FileSystem = "CDFS", UsedBytes = 10, LogicalFormat = LogicalFormat.DataVideo,
            Entries = new[] { TestDiscs.File("a.avi", 10) },
        };
        await h.InsertAsync(disc);

        h.Extract.DiscRows.Should().Contain(r => r.Label == "Tipo" && r.Value == "—");
        h.Extract.DiscRows.Should().Contain(r => r.Label == "Sessões" && r.Value == "—");
        h.Extract.DiscNotice.Should().Contain("detalhes físicos");
        h.Extract.CanExtract.Should().BeTrue("copying through the file system still works");
    }

    [Fact]
    public async Task E31_AbsurdRecordingDate_IsShownAsDash_AndNameFallsBackToToday()
    {
        var h = new Harness();
        await h.StartAsync();
        var disc = new DiscInfo
        {
            VolumeLabel = "NEW", VolumeSerial = "9", FileSystem = "UDF", UsedBytes = 10, LogicalFormat = LogicalFormat.DataVideo,
            RecordedAtUtc = new DateTime(1980, 1, 1), Entries = new[] { TestDiscs.File("a.avi", 10) },
        };
        await h.InsertAsync(disc);

        h.Extract.DiscRows.Should().Contain(r => r.Label == "Gravado em" && r.Value == "—");
        h.Extract.FolderName.Should().Be("Disco " + h.Clock.Now.ToLocalTime().Date.ToString("yyyy-MM-dd"), "a generic label and a 1980 date fall back to today's date");
    }

    [Fact]
    public async Task E05_ReaderThatKeepsReconnecting_ShowsPowerTip()
    {
        var h = new Harness();
        await h.StartAsync();
        h.Extract.PowerTip.Should().BeNull();

        for (int i = 0; i < 3; i++)
        {
            h.Drives.DriveList.Clear();
            await h.Extract.RefreshAsync();
            h.Drives.DriveList.Add(FakeDrives.D);
            await h.Extract.RefreshAsync();
        }

        h.Extract.PowerTip.Should().NotBeNull().And.Contain("energia na USB");
    }

    [Fact]
    public async Task DiscStaysReady_AcrossRefreshes_WithoutReanalysing()
    {
        var h = new Harness();
        await h.StartAsync();
        await h.InsertAsync(TestDiscs.Dvd());
        var calls = h.Drives.AnalyzeCalls;

        await h.Extract.RefreshAsync();
        await h.Extract.RefreshAsync();

        h.Drives.AnalyzeCalls.Should().Be(calls, "polling must not re-read the disc every few seconds");
        await h.Extract.RefreshAsync(forceAnalyze: true);
        h.Drives.AnalyzeCalls.Should().Be(calls + 1, "the Atualizar button forces a new analysis");
    }
}

public class ExtractGateTests
{
    private static GateInput Input(ScreenState screen = ScreenState.Ready, int selected = 3, bool dest = true, bool name = true, bool validating = false,
        bool extracting = false, bool drive = true, params ValidationIssue[] issues) =>
        new(extracting, drive, screen, "D:", selected, dest, name, validating, issues);

    [Fact] public void Ready_AndEverythingFilledIn_Enables() => ExtractGate.Evaluate(Input()).CanExtract.Should().BeTrue();

    [Fact]
    public void Busy_Wins()
    {
        var r = ExtractGate.Evaluate(Input(extracting: true));
        r.CanExtract.Should().BeFalse();
        r.Blocker.Should().Be(GateBlocker.Busy);
    }

    [Fact]
    public void NothingSelected_ExplainsToTickAnItem()
    {
        var r = ExtractGate.Evaluate(Input(selected: 0));
        r.Reason.Should().Be("Marque pelo menos um item do disco para extrair.");
        r.Blocker.Should().Be(GateBlocker.NothingSelected);
    }

    [Fact] public void NoDestination_Reason() => ExtractGate.Evaluate(Input(dest: false)).Reason.Should().Be("Escolha a pasta de destino.");
    [Fact] public void NoName_Reason() => ExtractGate.Evaluate(Input(name: false)).Reason.Should().Be("Digite um nome para a pasta do disco.");
    [Fact] public void Validating_Reason() => ExtractGate.Evaluate(Input(validating: true)).Reason.Should().Be("Verificando o destino…");

    [Fact]
    public void BlockingIssue_ShowsItsMessage_WarningDoesNot()
    {
        var blocking = ExtractGate.Evaluate(Input(issues: new ValidationIssue(ValidationSeverity.Blocking, "Faltam 2 GB.")));
        blocking.CanExtract.Should().BeFalse();
        blocking.Reason.Should().Be("Faltam 2 GB.");

        var warning = ExtractGate.Evaluate(Input(issues: new ValidationIssue(ValidationSeverity.Warning, "Nuvem.")));
        warning.CanExtract.Should().BeTrue();
    }

    [Fact]
    public void NoDrive_BeatsEverything() => ExtractGate.Evaluate(Input(drive: false, screen: ScreenState.NoDrive)).Blocker.Should().Be(GateBlocker.NoDrive);

    [Theory]
    [InlineData(ScreenState.NoMedia)]
    [InlineData(ScreenState.TrayOpen)]
    [InlineData(ScreenState.Loading)]
    [InlineData(ScreenState.Blank)]
    [InlineData(ScreenState.Unfinalized)]
    [InlineData(ScreenState.Unreadable)]
    [InlineData(ScreenState.Protected)]
    [InlineData(ScreenState.AudioCd)]
    public void EveryNotReadyState_HasANonEmptyReason_AndMessage(ScreenState s)
    {
        var r = ExtractGate.Evaluate(Input(screen: s));
        r.CanExtract.Should().BeFalse();
        r.Reason.Should().NotBeNullOrWhiteSpace();
        var info = StatusPresenter.For(s, "D:");
        info.Title.Should().NotBeNullOrWhiteSpace();
        info.Message.Should().NotBeNullOrWhiteSpace();
    }
}

