using MediaRescue.App.Infrastructure;
using MediaRescue.App.ViewModels;
using MediaRescue.Core;

namespace MediaRescue.App.Tests;

/// <summary>RF-06: ExtractionProgress -> texts and values (ETA, speed, counters, taskbar state) and the final summary.</summary>
public class ProgressMappingTests
{
    private static ExtractionProgress P(ExtractionPhase phase = ExtractionPhase.Copying, TimeSpan? eta = null, long done = 0, long total = 1000,
        TimeSpan? elapsed = null, double speed = 0, int fileIndex = 1, int fileCount = 1, long lost = 0, int errors = 0, int intact = 0) =>
        new()
        {
            Phase = phase, Eta = eta, TotalBytesDone = done, TotalBytesTotal = total, Elapsed = elapsed ?? TimeSpan.Zero, BytesPerSecond = speed,
            FileIndex = fileIndex, FileCount = fileCount, BytesLost = lost, FilesWithErrors = errors, FilesIntact = intact,
        };

    [Fact]
    public void EtaNull_InTheFirstSeconds_ShowsCalculating()
    {
        ProgressViewModel.EtaFor(P(eta: null, elapsed: TimeSpan.FromSeconds(3))).Should().Be("calculando…");
        ProgressViewModel.EtaFor(P(ExtractionPhase.Preparing)).Should().Be("calculando…");
        ProgressViewModel.EtaFor(P(ExtractionPhase.Verifying)).Should().Be("calculando…");
    }

    [Theory]
    [InlineData(ExtractionPhase.SecondPass)]
    [InlineData(ExtractionPhase.WaitingForDrive)]
    [InlineData(ExtractionPhase.WaitingForDestination)]
    [InlineData(ExtractionPhase.DestinationFull)]
    [InlineData(ExtractionPhase.Paused)]
    public void EtaNull_DuringRecoveryOrWaiting_ShowsIndeterminate(ExtractionPhase phase) =>
        ProgressViewModel.EtaFor(P(phase, eta: null, elapsed: TimeSpan.FromMinutes(5))).Should().Be("indeterminado");

    [Theory]
    [InlineData(372, "~06:12")]
    [InlineData(59, "~00:59")]
    [InlineData(3900, "~1:05:00")]
    public void KnownEta_IsFormatted(int seconds, string expected) =>
        ProgressViewModel.EtaFor(P(eta: TimeSpan.FromSeconds(seconds))).Should().Be(expected);

    [Fact]
    public void NegativeEta_NeverShowsANegativeNumber()
    {
        var text = ProgressViewModel.EtaFor(P(eta: TimeSpan.FromSeconds(-30)));
        text.Should().NotContain("-");
    }

    [Fact]
    public void Update_MapsPercentagesSpeedAndCounters()
    {
        var vm = new ProgressViewModel();
        vm.Update(new ExtractionProgress
        {
            Phase = ExtractionPhase.Copying, CurrentFile = "a.vob", FileIndex = 3, FileCount = 7, FileBytesDone = 25, FileBytesTotal = 100,
            TotalBytesDone = 640, TotalBytesTotal = 1000, BytesPerSecond = 4_300_000, Eta = TimeSpan.FromSeconds(372),
            Elapsed = TimeSpan.FromSeconds(95), FilesIntact = 3, FilesWithErrors = 1, BytesLost = 2048,
        }, userPaused: false);

        vm.OverallFraction.Should().BeApproximately(0.64, 1e-9);
        vm.OverallText.Should().Be("64%");
        vm.FileText.Should().Be("25%");
        vm.FilesText.Should().Be("3/7 arquivos");
        vm.SpeedText.Should().Be("4,1 MB/s");
        vm.EtaText.Should().Be("~06:12");
        vm.ElapsedText.Should().Be("01:35");
        vm.IntactText.Should().Be("Íntegros 3");
        vm.ErrorsText.Should().Be("Com erro 1");
        vm.LostText.Should().Be("Irrecuperável: 2,0 KB");
        vm.CurrentFileName.Should().Be("a.vob");
        vm.CanInsist.Should().BeTrue("there are errors, so 'Insistir mais' is offered");
    }

    [Fact]
    public void ZeroTotals_NeverDivideByZero_AndOverflowIsClamped()
    {
        var vm = new ProgressViewModel();
        vm.Update(P(done: 0, total: 0, speed: 0, fileCount: 0), false);
        vm.OverallFraction.Should().Be(0);
        vm.OverallText.Should().Be("0%");
        vm.SpeedText.Should().Be("—");
        vm.FilesText.Should().BeEmpty();

        vm.Update(P(done: 5000, total: 1000, fileIndex: 12, fileCount: 7), false);
        vm.OverallFraction.Should().Be(1);
        vm.OverallText.Should().Be("100%");
        vm.FilesText.Should().Be("7/7 arquivos");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-5.0)]
    public void WeirdSpeeds_ShowADash(double speed) => Fmt.Speed(speed).Should().Be("—");

    [Fact]
    public void Visual_FollowsThePhase()
    {
        var vm = new ProgressViewModel();
        vm.Update(P(ExtractionPhase.Preparing), false);
        vm.Visual.Should().Be(ProgressVisual.Indeterminate);

        vm.Update(P(ExtractionPhase.Copying, done: 100), false);
        vm.Visual.Should().Be(ProgressVisual.Normal);

        vm.Update(P(ExtractionPhase.Copying, done: 100), userPaused: true);
        vm.Visual.Should().Be(ProgressVisual.Paused);
        vm.PhaseText.Should().Contain("pausa");

        vm.Update(P(ExtractionPhase.Copying, done: 100, lost: 4096), false);
        vm.Visual.Should().Be(ProgressVisual.Error, "the taskbar turns red on errors");

        vm.Update(P(ExtractionPhase.WaitingForDrive), false);
        vm.Visual.Should().Be(ProgressVisual.Error);

        vm.End();
        vm.Visual.Should().Be(ProgressVisual.None);
        vm.IsActive.Should().BeFalse();
    }

    [Fact]
    public void PhaseTexts_AreInPlainPortuguese_AndTellWhatToDo()
    {
        ProgressViewModel.DescribePhase(ExtractionPhase.WaitingForDrive, false).Text.Should().Contain("Reconecte");
        ProgressViewModel.DescribePhase(ExtractionPhase.WaitingForDestination, false).Text.Should().Contain("Reconecte");
        ProgressViewModel.DescribePhase(ExtractionPhase.DestinationFull, false).Text.Should().Contain("Libere espaço");
        ProgressViewModel.DescribePhase(ExtractionPhase.SecondPass, false).Text.Should().Contain("recuperar");
        foreach (var phase in Enum.GetValues<ExtractionPhase>())
            ProgressViewModel.DescribePhase(phase, false).Text.Should().NotBeNullOrWhiteSpace(phase.ToString());
    }

    [Fact]
    public void Begin_ResetsToAFreshState()
    {
        var vm = new ProgressViewModel();
        vm.Update(P(done: 900), false);
        vm.Begin();
        vm.IsActive.Should().BeTrue();
        vm.OverallText.Should().Be("0%");
        vm.EtaText.Should().Be("calculando…");
        vm.Visual.Should().Be(ProgressVisual.Indeterminate);
        vm.IsUserPaused.Should().BeFalse();
    }
}

public class FormattingTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(-5, "0 B")]
    [InlineData(1536, "1,5 KB")]
    [InlineData(1_073_741_824, "1,0 GB")]
    [InlineData(2_040_109_465, "1,9 GB")]
    [InlineData(150L * 1024 * 1024, "150 MB")]
    public void Bytes_UsesBrazilianDecimalComma(long bytes, string expected) => Fmt.Bytes(bytes).Should().Be(expected);

    [Fact]
    public void Clock_AndVideoDuration()
    {
        Fmt.Clock(TimeSpan.FromSeconds(-4)).Should().Be("00:00");
        Fmt.Clock(TimeSpan.FromSeconds(75)).Should().Be("01:15");
        Fmt.Clock(TimeSpan.FromMinutes(61)).Should().Be("1:01:00");
        Fmt.VideoDuration(new TimeSpan(0, 42, 10)).Should().Be("00:42:10");
    }

    [Fact]
    public void Percent_IsClamped() { Fmt.Percent(-1).Should().Be("0%"); Fmt.Percent(0.999).Should().Be("99%"); Fmt.Percent(7).Should().Be("100%"); Fmt.Percent(double.NaN).Should().Be("0%"); }

    [Fact]
    public void Plural_Works() { Fmt.Plural(1, "arquivo", "arquivos").Should().Be("1 arquivo"); Fmt.Plural(3, "arquivo", "arquivos").Should().Be("3 arquivos"); }
}

public class ResultSummaryTests
{
    private static ExtractionResult R(OverallStatus status, params FileResult[] files) => new(@"E:\V\Disco", status, files, TimeSpan.FromMinutes(7));
    private static FileResult F(string name, FileStatus s, long total = 1000, long lost = 0, string? error = null) =>
        new() { RelativePath = name, WrittenPath = name, Status = s, BytesTotal = total, BytesLost = lost, Error = error };

    [Fact]
    public void Intact_IsHappyAndHasNoProblems()
    {
        var s = ResultSummary.From(R(OverallStatus.Intact, F("a", FileStatus.Intact), F("b", FileStatus.Intact)));
        s.Headline.Should().Be("Tudo certo! 2 arquivos copiados e verificados.");
        s.Tone.Should().Be(StatusTone.Ok);
        s.HasProblems.Should().BeFalse();
        s.CountsText.Should().Be("Íntegros 2");
        s.FolderPath.Should().Be(@"E:\V\Disco");
    }

    [Fact]
    public void Partial_ExplainsLossAndPointsToTheReport()
    {
        var s = ResultSummary.From(R(OverallStatus.Partial, F("a", FileStatus.Intact), F("v.vob", FileStatus.Partial, 1_000_000, 2048)));
        s.Tone.Should().Be(StatusTone.Warn);
        s.Headline.Should().Contain("1 arquivo ficou incompleto").And.Contain("2,0 KB");
        s.Detail.Should().Contain("LEIA-ME - ERROS.txt");
        s.Problems.Should().ContainSingle().Which.Description.Should().Contain("faltam 2,0 KB").And.Contain("0,2%");
        s.BytesLost.Should().Be(2048);
        s.CountsText.Should().Contain("Incompletos 1");
    }

    [Fact]
    public void Failed_SuggestsCleaningOrTestingTheReader_AndKeepsErrorUnderDetails()
    {
        var s = ResultSummary.From(R(OverallStatus.Failed, F("a", FileStatus.Failed, error: "Win32 1117")));
        s.Tone.Should().Be(StatusTone.Danger);
        s.Detail.Should().Contain("Testar leitor");
        s.Problems.Single().Description.Should().Contain("Detalhes: Win32 1117");
        s.Headline.Should().NotContain("1117");
    }

    [Fact]
    public void DestinationFailure_BlamesTheDrive_NotTheDisc()
    {
        var s = ResultSummary.From(R(OverallStatus.Partial, F("a", FileStatus.DestinationFailure)));
        s.Problems.Single().Description.Should().Contain("pendrive").And.Contain("defeito");
        s.CountsText.Should().Contain("Erro no destino 1");
    }

    [Fact]
    public void Recovered_IsPresentedAsGoodNews() =>
        ResultSummary.From(R(OverallStatus.Recovered, F("a", FileStatus.Recovered))).Tone.Should().Be(StatusTone.Ok);

    [Fact]
    public void FullText_ListsAtMostEightProblems()
    {
        var files = Enumerable.Range(0, 12).Select(i => F($"f{i}", FileStatus.Failed)).ToArray();
        var text = ResultSummary.From(R(OverallStatus.Failed, files)).FullText;
        text.Should().Contain("e mais 4");
    }
}

