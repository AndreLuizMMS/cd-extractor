using FluentAssertions;
using MediaRescue.Core;
using MediaRescue.Core.Tests.Fakes;

namespace MediaRescue.Core.Tests;

/// <summary>RF-06: counters, 10 s moving-average speed, ETA rules; no division by zero, no negatives.</summary>
public class ProgressTests
{
    private const int Block = 64 * 1024;
    private const int Mib = 1 << 20;

    // ------------------------------------------------------------------ end to end through the engine

    [Fact]
    public async Task Counters_FileIndex_AndBytes_AreConsistent()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 100_000);
        rig.AddFile("b.avi", 200_000).BadRange(10 * 2048, 2 * 2048);
        rig.AddFile("c.avi", 50_000);

        var result = await rig.RunWithTimeout();

        var items = rig.Progress.Items;
        items.Should().NotBeEmpty();
        items.Select(i => i.FileIndex).Where(i => i > 0).Distinct().Should().Equal(1, 2, 3);
        items.Where(i => i.Phase != ExtractionPhase.Preparing).Should().OnlyContain(i => i.FileCount == 3 && i.TotalBytesTotal == 350_000);
        items.Select(i => i.TotalBytesDone).Should().BeInAscendingOrder();
        items.Should().OnlyContain(i => i.TotalBytesDone <= i.TotalBytesTotal && i.FileBytesDone <= i.FileBytesTotal);
        var last = rig.Progress.Last;
        last.Phase.Should().Be(ExtractionPhase.Completed);
        last.TotalBytesDone.Should().Be(350_000);
        last.FilesIntact.Should().Be(2);
        last.FilesWithErrors.Should().Be(1);
        last.BytesLost.Should().Be(2 * 2048);
        last.Message.Should().Contain("Concluído");
        result.Duration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task Counters_ShowTheCurrentFileName_AndTheFileBytes()
    {
        var rig = new Rig();
        rig.AddFile(@"Pasta\a.avi", 3 * Block);

        await rig.RunWithTimeout();

        rig.Progress.Items.Should().Contain(i => i.CurrentFile == @"Pasta\a.avi" && i.FileBytesTotal == 3 * Block && i.FileBytesDone == 3 * Block);
    }

    [Fact]
    public async Task Speed_IsAMovingAverage_AndEtaIsHiddenForTheFirstTenSeconds()
    {
        var rig = new Rig();
        rig.AddFile("big.avi", 20 * Mib).ReadDelay = TimeSpan.FromMilliseconds(100); // 64 KiB per 100 ms = 655,360 B/s
        const double expectedSpeed = Block * 10.0;

        await rig.RunWithTimeout();

        var items = rig.Progress.Items.Where(i => i.Phase == ExtractionPhase.Copying).ToList();
        items.Where(i => i.Elapsed < TimeSpan.FromSeconds(10)).Should().NotBeEmpty().And.OnlyContain(i => i.Eta == null, "ETA is 'calculando…' during the first 10 s");
        var later = items.Where(i => i.Elapsed > TimeSpan.FromSeconds(12) && i.TotalBytesDone < i.TotalBytesTotal && i.CurrentFile != null && i.FileBytesDone > 0).ToList();
        later.Should().NotBeEmpty();
        foreach (var p in later)
        {
            p.BytesPerSecond.Should().BeApproximately(expectedSpeed, expectedSpeed * 0.06);
            p.Eta.Should().NotBeNull();
            double remaining = (p.TotalBytesTotal - p.TotalBytesDone) / expectedSpeed;
            p.Eta!.Value.TotalSeconds.Should().BeApproximately(remaining, Math.Max(1.5, remaining * 0.1));
        }
        rig.Progress.Items.Should().OnlyContain(i => i.BytesPerSecond >= 0 && !double.IsNaN(i.BytesPerSecond) && !double.IsInfinity(i.BytesPerSecond));
    }

    [Fact]
    public async Task Eta_IsUnknown_DuringTheSecondPass()
    {
        var rig = new Rig();
        var disc = rig.AddFile("a.avi", 2 * Mib);
        disc.ReadDelay = TimeSpan.FromMilliseconds(200);
        disc.BadRange(Mib, 8 * 2048);
        disc.FailDelay = TimeSpan.FromSeconds(1);

        await rig.RunWithTimeout();

        var second = rig.Progress.Items.Where(i => i.Phase == ExtractionPhase.SecondPass).ToList();
        second.Should().Contain(i => i.Elapsed > TimeSpan.FromSeconds(10), "the test must really be past the first 10 seconds");
        second.Should().OnlyContain(i => i.Eta == null);
    }

    [Fact]
    public async Task ElapsedTime_ExcludesPauses()
    {
        var rig = new Rig();
        var disc = rig.AddFile("a.avi", 5 * Block);
        disc.OnRead = (offset, _) => { if (offset == 2L * Block) rig.Control.Pause(); };
        int polls = 0;
        rig.Clock.OnDelay = _ => { if (++polls == 40) rig.Control.Resume(); }; // 40 x 250 ms = 10 s paused

        var result = await rig.RunWithTimeout();

        result.Duration.Should().BeLessThan(TimeSpan.FromSeconds(1));
        rig.Clock.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task OnlyEmptyFiles_NeverDivideByZero()
    {
        var rig = new Rig();
        rig.AddFile("a.txt", 0);
        rig.AddFile("b.txt", 0);

        await rig.RunWithTimeout();

        rig.Progress.Items.Should().OnlyContain(i => i.BytesPerSecond == 0 && (i.Eta == null || i.Eta >= TimeSpan.Zero));
        rig.Progress.Last.TotalBytesTotal.Should().Be(0);
        rig.Progress.Last.FilesIntact.Should().Be(2);
    }

    [Fact]
    public async Task FrozenClock_NeverProducesNegativeOrInfiniteSpeed()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 10 * Block); // reads cost no virtual time at all

        await rig.RunWithTimeout();

        rig.Progress.Items.Should().OnlyContain(i => i.BytesPerSecond >= 0 && double.IsFinite(i.BytesPerSecond));
        rig.Progress.Items.Should().OnlyContain(i => i.Elapsed >= TimeSpan.Zero);
    }

    [Fact]
    public async Task ProgressHandlerThatThrows_DoesNotStopTheCopy()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 100_000);
        rig.Progress.OnReport = _ => throw new InvalidOperationException("UI bug");

        var result = await rig.RunWithTimeout();

        result.Status.Should().Be(OverallStatus.Intact);
    }

    // ------------------------------------------------------------------ unit tests of the math

    [Fact]
    public void SpeedTracker_ConstantRate_GivesThatRate()
    {
        var s = new SpeedTracker();
        double v = 0;
        for (int sec = 0; sec <= 30; sec++) v = s.Add(1000 + sec * 1000L, sec * 5_000_000L);
        v.Should().BeApproximately(5_000_000, 1);
    }

    [Fact]
    public void SpeedTracker_OnlyLooksAtTheLastTenSeconds()
    {
        var s = new SpeedTracker();
        // 20 s at 1 MB/s, then 10 s at 100 kB/s
        long bytes = 0;
        double v = 0;
        for (int sec = 0; sec <= 20; sec++) { v = s.Add(sec * 1000L, bytes); bytes += 1_000_000; }
        for (int sec = 21; sec <= 31; sec++) { v = s.Add(sec * 1000L, bytes); bytes += 100_000; }
        v.Should().BeApproximately(100_000, 1_000);
    }

    [Fact]
    public void SpeedTracker_SameMillisecond_DoesNotDivideByZero_AndStaysBounded()
    {
        var s = new SpeedTracker();
        double v = 0;
        for (int i = 0; i < 10_000; i++) v = s.Add(5000, i * 1000L);
        v.Should().Be(0);
        s.Add(6000, 20_000_000).Should().BeGreaterThan(0);
    }

    [Fact]
    public void SpeedTracker_TimeOrBytesGoingBackwards_NeverGivesANegativeSpeed()
    {
        var s = new SpeedTracker();
        s.Add(1000, 1_000_000);
        s.Add(2000, 2_000_000);
        var v = s.Add(1500, 500_000);
        v.Should().BeGreaterThanOrEqualTo(0).And.Be(v);
        double.IsNaN(v).Should().BeFalse();
    }

    [Fact]
    public void SpeedTracker_Reset_ForgetsOldSamples()
    {
        var s = new SpeedTracker();
        s.Add(0, 0);
        s.Add(1000, 1_000_000);
        s.Reset();
        s.Current.Should().Be(0);
        s.Add(50_000, 1_000_000).Should().Be(0);
    }

    [Theory]
    [InlineData(5, 1000.0, 100, false, false)]      // first 10 s: calculating
    [InlineData(9.99, 1000.0, 100, false, false)]
    [InlineData(10, 1000.0, 100, false, true)]
    [InlineData(60, 1000.0, 100, true, false)]      // error recovery: undetermined
    [InlineData(60, 0.0, 100, false, false)]        // no speed: unknown
    [InlineData(60, -5.0, 100, false, false)]
    [InlineData(60, double.NaN, 100, false, false)]
    [InlineData(60, double.PositiveInfinity, 100, false, false)]
    public void Eta_Rules(double elapsedSeconds, double speed, long remaining, bool recovering, bool expectValue)
    {
        var eta = EtaMath.Eta(TimeSpan.FromSeconds(elapsedSeconds), speed, remaining, recovering);
        (eta != null).Should().Be(expectValue);
        if (eta != null) eta.Value.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public void Eta_NothingRemaining_IsZero_EvenWithZeroSpeed()
    {
        EtaMath.Eta(TimeSpan.FromMinutes(1), 0, 0, false).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Eta_AbsurdlyLong_IsUnknownInsteadOfOverflowing()
    {
        EtaMath.Eta(TimeSpan.FromMinutes(1), 1e-9, long.MaxValue, false).Should().BeNull();
    }
}
