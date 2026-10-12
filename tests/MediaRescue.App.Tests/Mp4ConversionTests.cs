using MediaRescue.App.Demo;
using MediaRescue.App.Infrastructure;
using MediaRescue.App.Services;
using MediaRescue.App.Tests.Support;
using MediaRescue.App.ViewModels;
using MediaRescue.Core;

namespace MediaRescue.App.Tests;

public sealed class FakeMp4Converter : IMp4Converter
{
    public bool Available { get; set; } = true;
    public bool IsAvailable => Available;
    public List<(string Folder, IReadOnlyList<FileResult> Files)> Calls { get; } = new();
    public Func<string, IReadOnlyList<FileResult>, IProgress<Mp4ConversionProgress>, CancellationToken, Task<Mp4ConversionResult>>? Behavior { get; set; }
    /// <summary>Runs inside ConvertAsync (e.g. to look at the state of the rest of the app while converting).</summary>
    public Action? OnConvert { get; set; }

    public Task<Mp4ConversionResult> ConvertAsync(string discFolder, IReadOnlyList<FileResult> files, IProgress<Mp4ConversionProgress> progress, CancellationToken ct)
    {
        Calls.Add((discFolder, files));
        OnConvert?.Invoke();
        if (Behavior != null) return Behavior(discFolder, files, progress, ct);
        return Task.FromResult(Done(files));
    }

    public static Mp4ConversionResult Done(IReadOnlyList<FileResult> files, string encoder = "h264_mf") => new()
    {
        Encoder = encoder,
        Jobs = Mp4ConversionPlanner.Plan(files).Select(j => new Mp4JobResult
        {
            Name = j.Name, Sources = j.Inputs, OutputPath = j.Output, Status = Mp4JobStatus.Converted, Encoder = encoder, FromDamagedSource = j.SourceDamaged,
        }).ToList(),
    };
}

/// <summary>RF-11 in the App: settings, per-extraction checkbox, the conversion phase after the copy, cancel, summary.</summary>
public class Mp4ConversionTests
{
    private static async Task<(Harness H, FakeMp4Converter Mp4)> ReadyAsync(bool setting = false, bool available = true, Action<Harness>? tweak = null)
    {
        var h = new Harness();
        var mp4 = new FakeMp4Converter { Available = available };
        h.Services.Mp4 = mp4;
        h.SettingsStore.Stored.ConvertToMp4 = setting;
        tweak?.Invoke(h);
        await h.StartAsync();
        await h.InsertAsync(TestDiscs.Dvd());
        return (h, mp4);
    }

    // ------------------------------------------------------------------ settings screen

    [Fact]
    public async Task Settings_OptionIsEnabled_WhenTheConverterExists()
    {
        var h = new Harness();
        var service = new SettingsService(h.SettingsStore);
        await service.LoadAsync();
        var vm = new SettingsViewModel(service, h.Dialogs, new ImmediateUiDispatcher(), new FakeMp4Converter());

        vm.Mp4Available.Should().BeTrue();
        vm.Mp4Note.Should().NotContain("Em breve").And.Contain("MP4").And.Contain("originais");

        vm.ConvertToMp4 = true;
        await service.FlushAsync();
        h.SettingsStore.Stored.ConvertToMp4.Should().BeTrue();
    }

    [Fact]
    public async Task Settings_OptionIsDisabledWithAReason_WhenFfmpegIsMissing()
    {
        var h = new Harness();
        var service = new SettingsService(h.SettingsStore);
        await service.LoadAsync();
        var vm = new SettingsViewModel(service, h.Dialogs, new ImmediateUiDispatcher(), new FakeMp4Converter { Available = false });

        vm.Mp4Available.Should().BeFalse();
        vm.Mp4Note.Should().Contain("ffmpeg").And.Contain("Indisponível");
    }

    [Fact]
    public async Task Settings_WithoutAnyConverter_BehavesAsUnavailable()
    {
        var h = new Harness();
        var service = new SettingsService(h.SettingsStore);
        await service.LoadAsync();
        new SettingsViewModel(service, h.Dialogs, new ImmediateUiDispatcher()).Mp4Available.Should().BeFalse();
    }

    // ------------------------------------------------------------------ per-extraction checkbox

    [Fact]
    public async Task Checkbox_DefaultsToTheSetting()
    {
        var (on, _) = await ReadyAsync(setting: true);
        on.Extract.ConvertThisExtraction.Should().BeTrue();
        on.Extract.Mp4Available.Should().BeTrue();

        var (off, _) = await ReadyAsync(setting: false);
        off.Extract.ConvertThisExtraction.Should().BeFalse();
    }

    [Fact]
    public async Task Checkbox_IsOff_AndHidden_WhenTheConverterIsMissing_EvenIfTheSettingIsOn()
    {
        var (h, _) = await ReadyAsync(setting: true, available: false);
        h.Extract.Mp4Available.Should().BeFalse();
        h.Extract.ConvertThisExtraction.Should().BeFalse();
        h.Extract.Mp4OptionEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Checkbox_FollowsTheSetting_WhenItChanges_ButKeepsTheUsersChoiceOtherwise()
    {
        var (h, _) = await ReadyAsync(setting: false);
        h.Main.SettingsPage.ConvertToMp4 = true;
        h.Extract.ConvertThisExtraction.Should().BeTrue("turning the setting on makes it the new default");

        h.Extract.ConvertThisExtraction = false;               // the user opts out for this extraction
        h.Main.SettingsPage.EjectWhenDone = false;             // an unrelated setting changes
        h.Extract.ConvertThisExtraction.Should().BeFalse("only a change of the MP4 setting resets the checkbox");

        h.Main.SettingsPage.ConvertToMp4 = false;
        h.Extract.ConvertThisExtraction.Should().BeFalse();
    }

    // ------------------------------------------------------------------ the flow

    [Fact]
    public async Task WhenChecked_AConversionRunsAfterTheCopy_ReadingTheCopiedFiles()
    {
        var (h, mp4) = await ReadyAsync(setting: true);
        h.Extract.Progress.IsActive.Should().BeFalse();

        var outcome = await h.Extract.RunExtractionAsync(batch: false);

        outcome.Started.Should().BeTrue();
        outcome.Status.Should().Be(OverallStatus.Intact);
        var call = mp4.Calls.Should().ContainSingle().Subject;
        call.Folder.Should().Be(outcome.Result!.DiscFolder, "the conversion reads the copy in the destination folder");
        call.Files.Should().BeEquivalentTo(outcome.Result.Files);
        h.Extract.Mp4.IsActive.Should().BeFalse();
        h.Extract.Mp4.SummaryText.Should().Contain("1 de 1 vídeo convertido");
        h.Extract.IsExtracting.Should().BeFalse();
    }

    [Fact]
    public async Task WhenUnchecked_NothingIsConverted()
    {
        var (h, mp4) = await ReadyAsync(setting: true);
        h.Extract.ConvertThisExtraction = false;

        await h.Extract.RunExtractionAsync(batch: false);

        mp4.Calls.Should().BeEmpty();
        h.Extract.Mp4.SummaryText.Should().BeEmpty();
    }

    [Fact]
    public async Task TheSummaryDialog_MentionsWhatWasConverted_AndWithWhichEncoder()
    {
        var (h, mp4) = await ReadyAsync(setting: true);
        mp4.Behavior = (_, files, _, _) => Task.FromResult(FakeMp4Converter.Done(files, "h264_mf"));

        await h.Extract.RunExtractionAsync(batch: false);

        var dialog = h.Dialogs.Shown.Single(d => d.Title.Contains("Extração concluída"));
        dialog.Message.Should().Contain("Conversão para MP4").And.Contain("Windows Media Foundation").And.Contain("originais");
        h.Extract.Mp4.SummaryTone.Should().Be(StatusTone.Ok);
    }

    [Fact]
    public async Task TheDiscIsEjectedBeforeTheConversionStarts_BecauseItReadsTheCopy()
    {
        var (h, mp4) = await ReadyAsync(setting: true);
        int ejectedWhenConverting = -1;
        mp4.OnConvert = () => ejectedWhenConverting = h.Drives.Ejected.Count;

        await h.Extract.RunExtractionAsync(batch: false);

        ejectedWhenConverting.Should().Be(1);
    }

    [Fact]
    public async Task APartialExtraction_IsAlsoConverted_AndTheDamagedSourceIsReported()
    {
        var (h, mp4) = await ReadyAsync(setting: true);
        h.Engine.Behavior = (r, _, _, _) =>
        {
            var res = FakeEngine.Result(r, OverallStatus.Partial, FileStatus.Partial, lost: 4096);
            return Task.FromResult(res);
        };

        await h.Extract.RunExtractionAsync(batch: false);

        mp4.Calls.Should().ContainSingle();
        h.Extract.Mp4.SummaryText.Should().Contain("cópia incompleta");
        h.Extract.Mp4.SummaryTone.Should().Be(StatusTone.Warn);
    }

    [Theory]
    [InlineData(OverallStatus.Failed, FileStatus.Failed)]
    public async Task AFailedExtraction_IsNotConverted(OverallStatus overall, FileStatus status)
    {
        var (h, mp4) = await ReadyAsync(setting: true);
        h.Engine.Behavior = (r, _, _, _) => Task.FromResult(FakeEngine.Result(r, overall, status));

        await h.Extract.RunExtractionAsync(batch: false);

        mp4.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ACancelledExtraction_IsNotConverted()
    {
        var (h, mp4) = await ReadyAsync(setting: true);
        h.Engine.Behavior = (r, _, _, _) => Task.FromResult(new ExtractionResult(Path.Combine(r.DestinationRoot, r.FolderName), OverallStatus.Cancelled, Array.Empty<FileResult>(), TimeSpan.Zero));

        await h.Extract.RunExtractionAsync(batch: false);

        mp4.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task BatchMode_DoesNotConvert()
    {
        var (h, mp4) = await ReadyAsync(setting: true);

        await h.Extract.RunExtractionAsync(batch: true);

        mp4.Calls.Should().BeEmpty("batch mode keeps the disc cycle fast");
    }

    // ------------------------------------------------------------------ failures never affect the extraction

    [Fact]
    public async Task AConversionError_DoesNotChangeTheExtractionResult()
    {
        var (h, mp4) = await ReadyAsync(setting: true);
        mp4.Behavior = (_, _, _, _) => Task.FromResult(new Mp4ConversionResult { Error = "Não há espaço suficiente para converter." });

        var outcome = await h.Extract.RunExtractionAsync(batch: false);

        outcome.Status.Should().Be(OverallStatus.Intact);
        h.Extract.Result.Should().NotBeNull();
        h.Extract.Result!.Status.Should().Be(OverallStatus.Intact);
        h.Catalog.Entries.Should().ContainSingle("the disc is in the history");
        h.Extract.Mp4.SummaryText.Should().Contain("Não há espaço suficiente").And.Contain("originais");
        h.Extract.Mp4.SummaryTone.Should().Be(StatusTone.Warn);
    }

    [Fact]
    public async Task AConverterThatThrows_DoesNotChangeTheExtractionResult_NorLeaveTheScreenBusy()
    {
        var (h, mp4) = await ReadyAsync(setting: true);
        mp4.Behavior = (_, _, _, _) => throw new InvalidOperationException("boom");

        var outcome = await h.Extract.RunExtractionAsync(batch: false);

        outcome.Status.Should().Be(OverallStatus.Intact);
        h.Extract.Mp4.IsActive.Should().BeFalse();
        h.Extract.Mp4.SummaryText.Should().Contain("erro inesperado");
        h.Extract.IsIdle.Should().BeTrue();
    }

    [Fact]
    public async Task FailedJobs_AreListedByName()
    {
        var (h, mp4) = await ReadyAsync(setting: true);
        mp4.Behavior = (_, files, _, _) => Task.FromResult(new Mp4ConversionResult
        {
            Encoder = "h264_mf",
            Jobs = new[]
            {
                new Mp4JobResult { Name = "Título 01 (VTS_01)", Status = Mp4JobStatus.Converted, Encoder = "h264_mf" },
                new Mp4JobResult { Name = "Festa\\ruim.avi", Status = Mp4JobStatus.Failed, Message = "O conversor não conseguiu converter este vídeo." },
            },
        });

        await h.Extract.RunExtractionAsync(batch: false);

        var text = h.Extract.Mp4.SummaryText;
        text.Should().Contain("1 de 2 vídeos convertidos").And.Contain("Festa\\ruim.avi").And.Contain("não conseguiu");
        h.Extract.Mp4.SummaryTone.Should().Be(StatusTone.Warn);
    }

    // ------------------------------------------------------------------ progress, gate and cancel

    [Fact]
    public async Task WhileConverting_TheCardShowsProgress_AndAnotherExtractionCannotStart()
    {
        var (h, mp4) = await ReadyAsync(setting: true);
        bool? canExtractWhileConverting = null;
        string? reasonWhileConverting = null;
        ExtractionOutcome? secondAttempt = null;
        bool wasActive = false;
        double fraction = 0; string job = "", percent = "", eta = "", elapsed = "";
        mp4.Behavior = async (_, files, progress, ct) =>
        {
            progress.Report(new Mp4ConversionProgress { JobIndex = 1, JobCount = 2, CurrentName = "Título 01 (VTS_01)", OverallFraction = 0.25, JobFraction = 0.5, Elapsed = TimeSpan.FromSeconds(75), Eta = TimeSpan.FromSeconds(200), Message = "Convertendo “Título 01 (VTS_01)” para MP4…" });
            fraction = h.Extract.Mp4.Fraction; job = h.Extract.Mp4.JobText; percent = h.Extract.Mp4.PercentText; eta = h.Extract.Mp4.EtaText; elapsed = h.Extract.Mp4.ElapsedText;
            canExtractWhileConverting = h.Extract.CanExtract;
            reasonWhileConverting = h.Extract.ExtractDisabledReason;
            secondAttempt = await h.Extract.RunExtractionAsync(batch: false);
            await Task.Yield();
            return FakeMp4Converter.Done(files);
        };
        mp4.OnConvert = () => wasActive = h.Extract.Mp4.IsActive;
        await h.Extract.RunExtractionAsync(batch: false);

        wasActive.Should().BeTrue();
        canExtractWhileConverting.Should().BeFalse();
        reasonWhileConverting.Should().Contain("conversão para MP4");
        secondAttempt!.Started.Should().BeFalse("no second copy while converting");
        fraction.Should().Be(0.25);
        job.Should().Be("Vídeo 1 de 2");
        percent.Should().Be("25%");
        elapsed.Should().Be("01:15");
        eta.Should().Be("~03:20");
        h.Extract.Mp4.IsActive.Should().BeFalse();
        h.Extract.ExtractDisabledReason.Should().NotContain("conversão para MP4", "the MP4 block on the button is lifted when the conversion ends");
    }

    [Fact]
    public async Task CancelButton_StopsTheConversion_KeepsTheExtraction_AndSaysSo()
    {
        var (h, mp4) = await ReadyAsync(setting: true);
        var started = new TaskCompletionSource();
        mp4.Behavior = async (_, files, _, ct) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
            return new Mp4ConversionResult
            {
                WasCancelled = true,
                Jobs = new[]
                {
                    new Mp4JobResult { Name = "a", Status = Mp4JobStatus.Converted, Encoder = "h264_mf" },
                    new Mp4JobResult { Name = "b", Status = Mp4JobStatus.Cancelled },
                },
            };
        };

        var run = h.Extract.RunExtractionAsync(batch: false);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        h.Extract.Mp4.IsActive.Should().BeTrue();
        h.Extract.Mp4.CancelCommand.CanExecute(null).Should().BeTrue();

        h.Extract.Mp4.CancelCommand.Execute(null);
        h.Extract.Mp4.IsCancelling.Should().BeTrue();
        h.Extract.Mp4.CancelButtonText.Should().Be("Cancelando…");
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(5));

        outcome.Status.Should().Be(OverallStatus.Intact, "cancelling the conversion does not undo the copy");
        h.Extract.Mp4.IsActive.Should().BeFalse();
        h.Extract.Mp4.SummaryText.Should().Contain("cancelada").And.Contain("1 vídeo já convertido foi mantido");
        h.Catalog.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task ClosingTheWindowDuringTheConversion_CancelsIt_WithoutAskingAboutTheCopy()
    {
        var (h, mp4) = await ReadyAsync(setting: true);
        var started = new TaskCompletionSource();
        bool sawCancellation = false;
        mp4.Behavior = async (_, files, _, ct) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { sawCancellation = true; }
            return new Mp4ConversionResult { WasCancelled = true };
        };
        var run = h.Extract.RunExtractionAsync(batch: false);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shownBefore = h.Dialogs.Shown.Count;

        var mayClose = await h.Main.ConfirmCloseAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        mayClose.Should().BeTrue();
        sawCancellation.Should().BeTrue("ffmpeg must not outlive the app");
        h.Dialogs.Shown.Skip(shownBefore).Should().BeEmpty("no 'copy in progress' question and no summary dialog while closing");
    }

    // ------------------------------------------------------------------ the result card text

    [Fact]
    public async Task ANewExtraction_ForgetsThePreviousConversionText()
    {
        var (h, _) = await ReadyAsync(setting: true);
        await h.Extract.RunExtractionAsync(batch: false);
        h.Extract.Mp4.SummaryText.Should().NotBeEmpty();

        h.Extract.Result = null;   // what a new disc / extraction does

        h.Extract.Mp4.SummaryText.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ pure view-model pieces

    [Fact]
    public void Summary_ForNothingToConvert_ExplainsIt()
    {
        var (text, tone) = Mp4ConversionSummary.Describe(new Mp4ConversionResult());
        text.Should().Contain("não havia vídeos");
        tone.Should().Be(StatusTone.Neutral);
    }

    [Fact]
    public void Summary_NamesEveryEncoderUsed()
    {
        var (text, _) = Mp4ConversionSummary.Describe(new Mp4ConversionResult
        {
            Jobs = new[]
            {
                new Mp4JobResult { Name = "a", Status = Mp4JobStatus.Converted, Encoder = "h264_mf" },
                new Mp4JobResult { Name = "b", Status = Mp4JobStatus.Converted, Encoder = "mpeg4" },
            },
        });
        text.Should().Contain("Windows Media Foundation").And.Contain("MPEG-4");
    }

    [Fact]
    public void Summary_WhenEverythingFailed_IsDanger()
    {
        var (text, tone) = Mp4ConversionSummary.Describe(new Mp4ConversionResult
        {
            Jobs = new[] { new Mp4JobResult { Name = "a.avi", Status = Mp4JobStatus.Failed, Message = "ruim" } },
        });
        tone.Should().Be(StatusTone.Danger);
        text.Should().Contain("0 de 1 vídeo convertido").And.Contain("a.avi");
    }

    [Fact]
    public void ProgressViewModel_MapsFractionAndTexts()
    {
        var vm = new Mp4ConversionViewModel();
        vm.Begin();
        vm.IsActive.Should().BeTrue();
        vm.Update(new Mp4ConversionProgress { JobIndex = 3, JobCount = 4, CurrentName = "x.avi", OverallFraction = 1.7, Elapsed = TimeSpan.FromMinutes(61) });
        vm.Fraction.Should().Be(1, "clamped");
        vm.JobText.Should().Be("Vídeo 3 de 4");
        vm.CurrentName.Should().Be("x.avi");
        vm.ElapsedText.Should().Be("1:01:00");
        vm.EtaText.Should().Be("calculando…");
        vm.End();
        vm.IsActive.Should().BeFalse();
    }

    // ------------------------------------------------------------------ demo mode

    [Fact]
    public async Task DemoConverter_PlansTheDemoDiscTitles_AndCanBeCancelled()
    {
        var disc = DemoData.BuildDvdVideoDisc();
        var files = disc.Entries.Where(e => !e.IsDirectory).Select(e => new FileResult
        { RelativePath = e.RelativePath, WrittenPath = e.RelativePath, Status = FileStatus.Intact, BytesTotal = e.Size }).ToList();
        var demo = new DemoMp4Converter(TimeSpan.FromMilliseconds(40));
        demo.IsAvailable.Should().BeTrue();

        var reports = new List<Mp4ConversionProgress>();
        var result = await demo.ConvertAsync(@"E:\Vídeos da família\FORMATURA", files, new Progress<Mp4ConversionProgress>(reports.Add), CancellationToken.None);

        result.Jobs.Select(j => j.Name).Should().Equal("Título 01 (VTS_01)", "Título 02 (VTS_02)");
        result.Jobs.Should().OnlyContain(j => j.Status == Mp4JobStatus.Converted);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = await demo.ConvertAsync(@"E:\x", files, new Progress<Mp4ConversionProgress>(_ => { }), cts.Token);
        cancelled.WasCancelled.Should().BeTrue();
    }

    [Fact]
    public async Task NullConverter_IsUnavailable_AndReportsItWithoutThrowing()
    {
        NullMp4Converter.Instance.IsAvailable.Should().BeFalse();
        var r = await NullMp4Converter.Instance.ConvertAsync("x", Array.Empty<FileResult>(), new Progress<Mp4ConversionProgress>(), CancellationToken.None);
        r.Error.Should().NotBeNull();
    }
}
