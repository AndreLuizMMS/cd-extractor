using System.Diagnostics;
using MediaRescue.App.Infrastructure;
using MediaRescue.App.Services;
using MediaRescue.App.Tests.Support;
using MediaRescue.App.ViewModels;
using MediaRescue.Core;

namespace MediaRescue.App.Tests;

public class ContentListTests
{
    private static ContentListViewModel Loaded(DiscInfo? disc = null)
    {
        var vm = new ContentListViewModel();
        vm.Load((disc ?? TestDiscs.Dvd()).Entries);
        return vm;
    }

    [Fact]
    public void Load_GroupsDvdTitlesAndKeepsTheMenuVobOutOfTheVideoCount()
    {
        var vm = Loaded();
        var group = vm.All.Single(i => i.RowKind == ContentRowKind.TitleGroup);
        group.Name.Should().Be("Título 1 (VTS_01)");
        group.Children.Select(c => c.Name).Should().Equal("VTS_01_1.VOB", "VTS_01_2.VOB");
        group.Size.Should().Be(1_000_000_000);
        group.TypeText.Should().Be("Título DVD");
        vm.VideoCount.Should().Be(1, "one title; the menu VOB and IFO files are not counted");
        vm.TotalFiles.Should().Be(6);
        vm.SummaryText.Should().Contain("6 arquivos").And.Contain("1 vídeo");
        vm.All.Single(i => i.Name == "VTS_01_0.VOB").IsMenuVob.Should().BeTrue();
    }

    [Fact]
    public void Folders_AreListedFirst_AndIndentedByDepth()
    {
        var vm = Loaded();
        vm.All.Where(i => i.Depth == 0).Select(i => i.Name).Should().Equal("Fotos", "VIDEO_TS");
        vm.All.Single(i => i.Name == "a.jpg").Depth.Should().Be(1);
        vm.All.Single(i => i.Name == "VTS_01_1.VOB").Depth.Should().Be(2);
        vm.All.Single(i => i.Name == "VTS_01_1.VOB").Indent.Should().Be(36);
    }

    [Fact]
    public void Everything_IsSelectedByDefault()
    {
        var vm = Loaded();
        vm.SelectedFiles.Should().Be(6);
        vm.SelectedBytes.Should().Be(vm.TotalBytes);
        vm.GetSelectedEntries().Should().HaveCount(8, "6 files and 2 real folders");
        vm.All.Should().OnlyContain(i => i.IsChecked == true);
    }

    [Fact]
    public void UntickingAFile_MakesTheParentsIndeterminate_AndTickingBackRestoresThem()
    {
        var vm = Loaded();
        var file = vm.All.Single(i => i.Name == "VTS_01_2.VOB");
        file.IsChecked = false;

        file.Parent!.IsChecked.Should().BeNull("the title group is partly selected");
        file.Parent.Parent!.IsChecked.Should().BeNull("VIDEO_TS too");
        vm.SelectedFiles.Should().Be(5);
        vm.GetSelectedEntries().Should().NotContain(e => e.RelativePath == "VIDEO_TS", "a partly selected folder is not copied as a whole");

        file.IsChecked = true;
        file.Parent.IsChecked.Should().BeTrue();
        file.Parent.Parent.IsChecked.Should().BeTrue();
    }

    [Fact]
    public void UntickingAFolder_UntickEverythingBelow()
    {
        var vm = Loaded();
        vm.All.Single(i => i.Name == "VIDEO_TS").IsChecked = false;
        vm.SelectedFiles.Should().Be(2);
        vm.All.Where(i => i.RelativePath.StartsWith("VIDEO_TS")).Should().OnlyContain(i => i.IsChecked == false);
    }

    [Fact]
    public void SelectionChanged_FiresOncePerUserAction()
    {
        var vm = Loaded();
        int fired = 0;
        vm.SelectionChanged += (_, _) => fired++;
        vm.All.Single(i => i.Name == "VIDEO_TS").IsChecked = false;
        fired.Should().Be(1);
        vm.ClearAllCommand.Execute(null);
        vm.SelectAllCommand.Execute(null);
        fired.Should().Be(3);
    }

    [Fact]
    public void CollapsingAFolder_HidesItsContent()
    {
        var vm = Loaded();
        var visibleBefore = vm.VisibleItems.Count;
        var fotos = vm.All.Single(i => i.Name == "Fotos");
        fotos.IsExpanded.Should().BeTrue("small discs open fully");
        fotos.IsExpanded = false;
        vm.VisibleItems.Count.Should().Be(visibleBefore - 2);
        vm.VisibleItems.Should().NotContain(i => i.Name == "a.jpg");
        fotos.IsExpanded = true;
        vm.VisibleItems.Count.Should().Be(visibleBefore);
    }

    [Fact]
    public void E24_ZeroByteFileAndEmptyFolder_AreListedAndCopyable()
    {
        var vm = new ContentListViewModel();
        vm.Load(new[] { TestDiscs.Dir("Vazia"), TestDiscs.File("zero.avi", 0) });
        vm.TotalFiles.Should().Be(1);
        vm.GetSelectedEntries().Select(e => e.RelativePath).Should().BeEquivalentTo("Vazia", "zero.avi");
        vm.All.Single(i => i.Name == "Vazia").SizeText.Should().Be("—");
    }

    [Fact]
    public void E24_ThousandsOfEntries_LoadAndToggleQuickly()
    {
        var entries = new List<DiscEntry> { TestDiscs.Dir("Fotos") };
        for (int i = 0; i < 20_000; i++) entries.Add(TestDiscs.File($@"Fotos\IMG_{i:000000}.JPG", 1000 + i));
        var vm = new ContentListViewModel();

        var sw = Stopwatch.StartNew();
        vm.Load(entries);
        var load = sw.ElapsedMilliseconds;
        sw.Restart();
        vm.ClearAllCommand.Execute(null);
        vm.SelectAllCommand.Execute(null);
        vm.All[0].IsChecked = false;
        var toggle = sw.ElapsedMilliseconds;

        vm.TotalFiles.Should().Be(20_000);
        vm.VisibleItems.Count.Should().BeLessThan(2, "a huge folder starts collapsed so the list stays light");
        load.Should().BeLessThan(2000);
        toggle.Should().BeLessThan(2000);
        vm.SelectedFiles.Should().Be(0);
    }

    [Fact]
    public void MissingParentFolders_AreSynthesised_AndNotSentAsEntries()
    {
        var vm = new ContentListViewModel();
        vm.Load(new[] { TestDiscs.File(@"A\B\c.avi", 5) });
        vm.All.Select(i => i.Name).Should().Equal("A", "B", "c.avi");
        vm.GetSelectedEntries().Select(e => e.RelativePath).Should().Equal(@"A\B\c.avi");
    }

    [Fact]
    public void ApplyIssues_MarksOnlyBlockedFiles()
    {
        var vm = Loaded();
        vm.ApplyIssues(new[]
        {
            new ValidationIssue(ValidationSeverity.Blocking, "grande demais", @"VIDEO_TS\VTS_01_1.VOB"),
            new ValidationIssue(ValidationSeverity.Warning, "aviso", @"Fotos\a.jpg"),
            new ValidationIssue(ValidationSeverity.Blocking, "geral"),
        });
        vm.All.Where(i => i.HasIssue).Select(i => i.Name).Should().Equal("VTS_01_1.VOB");
        vm.HasBlockedFiles.Should().BeTrue();
        vm.ApplyIssues(Array.Empty<ValidationIssue>());
        vm.HasBlockedFiles.Should().BeFalse();
    }

    [Fact]
    public void Clear_EmptiesEverything()
    {
        var vm = Loaded();
        vm.Clear();
        vm.VisibleItems.Should().BeEmpty();
        vm.TotalFiles.Should().Be(0);
        vm.SelectedEntryCount.Should().Be(0);
        vm.SummaryText.Should().BeEmpty();
    }

    [Fact]
    public void ApplyProbe_FillsColumnsAndSumsTheTitleDuration()
    {
        var vm = Loaded();
        var a = vm.All.Single(i => i.Name == "VTS_01_1.VOB");
        var b = vm.All.Single(i => i.Name == "VTS_01_2.VOB");
        var group = a.Parent!;

        a.ApplyProbe(new VideoProbe(TimeSpan.FromMinutes(30), 720, 480, 29.97, "mpeg2video", "ac3", 6_000_000, true));
        a.DurationText.Should().Be("00:30:00");
        a.ResolutionText.Should().Be("720x480");
        a.ToolTipText.Should().Contain("mpeg2video").And.Contain("entrelaçado");
        group.DurationText.Should().Be("00:30:00…", "one part is still unknown");

        b.ApplyProbe(new VideoProbe(TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(10), 720, 480, 29.97, "mpeg2video", "ac3", 6_000_000, true));
        group.DurationText.Should().Be("00:42:10");
        group.ResolutionText.Should().Be("720x480");
    }

    [Fact]
    public void Probe_CandidatesExcludeMenuAndNonVideo()
    {
        var vm = Loaded();
        vm.ProbeCandidates().Select(i => i.Name).Should().BeEquivalentTo("VTS_01_1.VOB", "VTS_01_2.VOB");
    }
}

public class ProbeTests
{
    [Fact]
    public async Task Probing_RunsInTheBackground_OneAtATime_AndFillsTheColumns()
    {
        var h = new Harness();
        int running = 0, maxRunning = 0;
        h.Probe.Handler = async (path, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            maxRunning = Math.Max(maxRunning, now);
            await Task.Delay(30, ct);
            Interlocked.Decrement(ref running);
            return new VideoProbe(TimeSpan.FromMinutes(10), 720, 480, 29.97, "mpeg2video", "ac3", 6_000_000, true);
        };
        await h.StartAsync();
        await h.InsertAsync(TestDiscs.Dvd());

        var group = h.Extract.Content.All.Single(i => i.RowKind == ContentRowKind.TitleGroup);
        await Harness.WaitForAsync(() => group.DurationText == "00:20:00");

        maxRunning.Should().Be(1, "one ffprobe process at a time (the disc is slow)");
        h.Probe.Probed.Should().BeEquivalentTo(@"D:\VIDEO_TS\VTS_01_1.VOB", @"D:\VIDEO_TS\VTS_01_2.VOB");
        await Harness.WaitForAsync(() => h.Extract.Content.ProbeStatus == "");
    }

    [Fact]
    public async Task E28_ProbeUnavailable_ShowsADiscreetNotice_AndNeverProbes()
    {
        var h = new Harness();
        h.Probe.Available = false;
        await h.StartAsync();
        await h.InsertAsync(TestDiscs.Dvd());

        h.Extract.ProbeNotice.Should().Contain("ffprobe");
        h.Probe.Probed.Should().BeEmpty();
        h.Extract.CanExtract.Should().BeTrue("the copy does not depend on metadata");
    }

    [Fact]
    public async Task E28_ProbeFailingForOneFile_DoesNotStopTheOthers()
    {
        var h = new Harness();
        h.Probe.Handler = (path, ct) => path.EndsWith("VTS_01_1.VOB") ? throw new InvalidOperationException("ffprobe died")
            : Task.FromResult<VideoProbe?>(new VideoProbe(TimeSpan.FromMinutes(5), 640, 480, 25, "mpeg1video", "mp2", 1_000_000, false));
        await h.StartAsync();
        await h.InsertAsync(TestDiscs.Dvd());

        var second = h.Extract.Content.All.Single(i => i.Name == "VTS_01_2.VOB");
        await Harness.WaitForAsync(() => second.DurationText == "00:05:00");
        h.Extract.Content.All.Single(i => i.Name == "VTS_01_1.VOB").DurationText.Should().BeEmpty();
    }

    [Fact]
    public async Task ProbingIsPausedWhileCopying_AndTheInFlightProbeIsCancelled()
    {
        var h = new Harness();
        var firstStarted = new TaskCompletionSource();
        int attempts = 0;
        h.Probe.Handler = async (path, ct) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // hangs until cancelled
            }
            return new VideoProbe(TimeSpan.FromMinutes(1), 1, 1, 1, "x", "y", 1, false);
        };
        await h.StartAsync();
        await h.InsertAsync(TestDiscs.Dvd());
        await firstStarted.Task;

        var release = new TaskCompletionSource<ExtractionResult>();
        var started = new TaskCompletionSource();
        h.Engine.Behavior = (r, _, _, _) => { started.TrySetResult(); return release.Task; };
        var run = h.Extract.RunExtractionAsync(batch: false);
        await started.Task;
        await Task.Delay(300);

        var callsDuringCopy = attempts;
        callsDuringCopy.Should().Be(1, "no further probing while the copy runs");
        h.Extract.Content.ProbeStatus.Should().Contain("depois da cópia");

        release.SetResult(FakeEngine.Intact(h.Engine.Requests.Single()));
        await run;
    }

    [Fact]
    public async Task NewDisc_CancelsTheOldProbingSession()
    {
        var h = new Harness();
        var gate = new TaskCompletionSource();
        h.Probe.Handler = async (path, ct) => { await gate.Task.WaitAsync(ct); return new VideoProbe(TimeSpan.FromMinutes(1), 1, 1, 1, "x", "y", 1, false); };
        await h.StartAsync();
        await h.InsertAsync(TestDiscs.Dvd());
        var oldGroup = h.Extract.Content.All.Single(i => i.RowKind == ContentRowKind.TitleGroup);

        await h.RemoveDiscAsync();
        gate.SetResult();
        await Task.Delay(150);

        oldGroup.DurationText.Should().BeEmpty("results of a cancelled session are discarded");
    }
}

