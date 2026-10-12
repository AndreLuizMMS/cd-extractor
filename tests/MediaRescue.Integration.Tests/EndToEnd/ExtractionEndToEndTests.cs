using System.Diagnostics;
using MediaRescue.Core;
using MediaRescue.Integration.Tests.Support;
using Xunit.Abstractions;

namespace MediaRescue.Integration.Tests.EndToEnd;

/// <summary>
/// End-to-end: the REAL ExtractionEngine + DiscReader + WindowsDestinationFileSystem + OpticalDriveService + SystemClock +
/// CatalogStore + DestinationValidator, reading mounted ISOs (virtual CD drives) and writing to a temporary NTFS folder.
/// Reported as SKIPPED (never passed) when this machine cannot mount ISO images.
/// </summary>
public sealed class ExtractionEndToEndTests : IDisposable
{
    private readonly E2eRig _rig = new();
    private readonly ITestOutputHelper _out;

    public ExtractionEndToEndTests(ITestOutputHelper output) => _out = output;

    public void Dispose() => _rig.Dispose();

    // ================================================================== (a) every disc layout

    private async Task LayoutRoundTripAsync(IsoSpec spec, string folder)
    {
        var m = _rig.Mount(spec);
        var (drive, disc) = await _rig.AnalyzeAsync(m.Letter);
        var progress = new RecordingProgress();

        var sw = Stopwatch.StartNew();
        var result = await _rig.RunAsync(_rig.Request(drive, disc, folder), progress);
        _out.WriteLine($"{spec.Label}: {result.Files.Count} file(s), {result.Files.Sum(f => f.BytesTotal):N0} bytes, format {disc.LogicalFormat}, engine {result.Duration.TotalSeconds:0.0}s, wall {sw.Elapsed.TotalSeconds:0.0}s");

        E2eRig.AssertExtractedCorrectly(spec, disc, result, m.Letter);
        AssertProgressContract(progress, spec);

        // The media lock is taken once and released once, and nothing is left locked.
        _rig.Drives.LockCalls.Should().Equal(new[] { true, false }, "lock before copying, unlock in the finally block");
        _rig.Fs.DirectoryExists(result.DiscFolder).Should().BeTrue();
        _rig.Engine.CanResume(disc, _rig.DestRoot, folder).Should().BeFalse("a completed job has nothing to resume");

        // E-22: the App records the extraction; inserting the same disc again must be recognised through the fingerprint.
        _rig.AddToCatalog(folder, disc, result);
        var (_, again) = await _rig.AnalyzeAsync(m.Letter);
        again.Fingerprint.Should().Be(disc.Fingerprint, "same disc, same fingerprint");
        var found = _rig.Catalog.FindByFingerprint(again.Fingerprint);
        found.Should().NotBeNull("E-22: already extracted");
        found!.DestinationPath.Should().Be(result.DiscFolder);
        found.Status.Should().Be(OverallStatus.Intact);
        found.FileCount.Should().Be(spec.Files.Count);
        _rig.Fs.DirectoryExists(found.DestinationPath).Should().BeTrue();
        // ... and it survives a restart of the app (the catalog is on disk).
        new CatalogStore(_rig.Fs, _rig.CatalogPath).FindByFingerprint(again.Fingerprint).Should().NotBeNull();
    }

    private static void AssertProgressContract(RecordingProgress progress, IsoSpec spec)
    {
        var items = progress.Items;
        items.Should().NotBeEmpty();
        var total = spec.Files.Values.Sum(v => (long)v.Length);
        var last = items[^1];
        last.Phase.Should().Be(ExtractionPhase.Completed);
        last.FileCount.Should().Be(spec.Files.Count);
        last.TotalBytesTotal.Should().Be(total);
        last.TotalBytesDone.Should().Be(total);
        last.FilesIntact.Should().Be(spec.Files.Count);
        last.FilesWithErrors.Should().Be(0);
        last.BytesLost.Should().Be(0);
        progress.SawPhase(ExtractionPhase.Copying).Should().BeTrue();
        progress.SawPhase(ExtractionPhase.Verifying).Should().BeTrue("RF-05: the destination is re-read and compared");

        // The App shows "FileIndex/FileCount" and assumes FileIndex is 1-based: the engine must agree.
        var named = items.Where(i => i.CurrentFile != null).ToList();
        named.Should().NotBeEmpty();
        named.Min(i => i.FileIndex).Should().Be(1, "FileIndex is 1-based");
        named.Max(i => i.FileIndex).Should().Be(spec.Files.Count);
        named.Should().OnlyContain(i => i.FileIndex >= 1 && i.FileIndex <= i.FileCount);
        named.Select(i => i.FileIndex).Zip(named.Skip(1).Select(i => i.FileIndex)).Should().OnlyContain(p => p.Second >= p.First, "files are processed in order");
        items.Should().OnlyContain(i => i.TotalBytesDone <= i.TotalBytesTotal || i.TotalBytesTotal == 0);
        items.Select(i => i.TotalBytesDone).Zip(items.Skip(1).Select(i => i.TotalBytesDone)).Should().OnlyContain(p => p.Second >= p.First, "overall progress never goes backwards");
    }

    [MountFact]
    public Task A_DataDisc_ExtractsIntact() => LayoutRoundTripAsync(IsoLayouts.DataDisc("DADOS"), "Disco de dados");

    [MountFact]
    public Task A_Vcd_FilesThatAreNotSectorMultiples_ExtractIntact() => LayoutRoundTripAsync(IsoLayouts.Vcd(), "VCD da festa");

    [MountFact]
    public Task A_VideoTs_ExtractsIntact_AndRecreatesTheEmptyAudioTsFolder() => LayoutRoundTripAsync(IsoLayouts.VideoTs(), "Formatura");

    [MountFact]
    public Task A_DvdRtav_ExtractsIntact() => LayoutRoundTripAsync(IsoLayouts.DvdRtav(), "Gravador de mesa");

    [MountFact]
    public Task A_MixedUnicodeLongNamesZeroByteAndEmptyFolder_ExtractIntact() => LayoutRoundTripAsync(IsoLayouts.Mixed(), "Álbum da família");

    [MountFact]
    public async Task A_ThreeThousandSmallFiles_ExtractIntact_E24()
    {
        var sw = Stopwatch.StartNew();
        await LayoutRoundTripAsync(IsoLayouts.ManySmallFiles(3000), "Muitos arquivos");
        _out.WriteLine($"3000 files end to end: {sw.Elapsed.TotalSeconds:0.0}s");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(5));
    }

    // ================================================================== (b) disc removed mid-copy (E-03 / E-04)

    private static IsoSpec BigDisc() => new IsoSpec { Label = "GRANDE" }
        .AddRandom("a.bin", 3_000_000, 71)
        .AddRandom("b.bin", 24_000_000, 72)
        .AddRandom("c.bin", 2_000_000, 73);

    /// <summary>Starts the run and pulls the ISO out the moment the engine asks for the 8 MB point of b.bin (deterministic).</summary>
    private Task<ExtractionResult> StartAndDismountMidFile(MountedIso m, ExtractionRequest request, RecordingProgress progress)
    {
        int fired = 0;
        _rig.Reader.BeforeRead = async (rel, offset) =>
        {
            if (rel == "b.bin" && offset >= 8_000_000 && Interlocked.Exchange(ref fired, 1) == 0)
                await Task.Run(() => m.Dismount());
        };
        return _rig.RunAsync(request, progress);
    }

    [MountFact]
    public async Task E03_E04_DismountMidCopy_ReportsWaitingForDrive_ThenRemountingTheSameDiscResumesWithCorrectHashes()
    {
        var spec = BigDisc();
        var iso = _rig.BuildIso(spec);
        var m = _rig.MountIso(iso);
        var (drive, disc) = await _rig.AnalyzeAsync(m.Letter);
        var progress = new RecordingProgress();
        var folder = "Disco removido";
        var run = StartAndDismountMidFile(m, _rig.Request(drive, disc, folder), progress);

        (await DiscHelpers.WaitUntil(() => progress.SawPhase(ExtractionPhase.WaitingForDrive), TimeSpan.FromSeconds(90)))
            .Should().BeTrue("a disc that disappears mid-file must be reported as WaitingForDrive");
        await Task.Delay(2500);
        run.IsCompleted.Should().BeFalse("the engine waits for the drive instead of failing");
        var discFolder = Path.Combine(_rig.DestRoot, folder);
        Directory.EnumerateFiles(discFolder, "b.bin*").Should().BeEmpty("the interrupted file is not left behind, neither as .partial nor with its final name");
        File.Exists(Path.Combine(discFolder, "a.bin")).Should().BeTrue("files finished before the removal stay");
        _rig.Reader.OpenCount("a.bin").Should().Be(1);

        var m2 = _rig.MountIso(iso);
        m2.Letter.Should().Be(m.Letter, "the same ISO must come back on the same drive letter for the engine to find it again");
        var result = await run;

        E2eRig.AssertExtractedCorrectly(spec, disc, result, m.Letter);
        progress.Messages.Should().Contain(s => s.Contains("reconectado"), "the engine says it is resuming");
        progress.Items.Last().Phase.Should().Be(ExtractionPhase.Completed);
        _rig.Reader.OpenCount("a.bin").Should().Be(1, "a finished file is not copied again after the drive comes back");
        _rig.Reader.OpenCount("b.bin").Should().BeGreaterThanOrEqualTo(2, "the file that was being copied is restarted (E-03)");
        _rig.Reader.OpenCount("c.bin").Should().Be(1);
        var locks = _rig.Drives.LockCalls;
        locks.Count(l => l).Should().BeGreaterThanOrEqualTo(2, "the media lock is taken again after the drive returns");
        locks[^1].Should().BeFalse("and released at the end");
    }

    [MountFact]
    public async Task E04_DifferentDiscInserted_JobEndsCleanly_WithoutCorruptFinalNamedFiles_AndTheOriginalCanStillFinish()
    {
        var spec = BigDisc();
        // Same file names, different sizes and bytes: copying it into the same names would silently corrupt the result.
        var other = new IsoSpec { Label = "GRANDE" }
            .AddRandom("a.bin", 2_900_000, 81)
            .AddRandom("b.bin", 24_500_000, 82)
            .AddRandom("c.bin", 2_100_000, 83);
        var iso = _rig.BuildIso(spec, "original.iso");
        var otherIso = _rig.BuildIso(other, "outro.iso");
        var m = _rig.MountIso(iso);
        var (drive, disc) = await _rig.AnalyzeAsync(m.Letter);
        var progress = new RecordingProgress();
        var folder = "Disco trocado";
        var request = _rig.Request(drive, disc, folder);
        var run = StartAndDismountMidFile(m, request, progress);

        (await DiscHelpers.WaitUntil(() => progress.SawPhase(ExtractionPhase.WaitingForDrive), TimeSpan.FromSeconds(90))).Should().BeTrue();
        var mOther = _rig.MountIso(otherIso);
        mOther.Letter.Should().Be(m.Letter);
        var result = await run;

        result.Status.Should().Be(OverallStatus.Failed);
        progress.Items.Last().Message.Should().Contain("diferente", "the user is told this is another disc");
        var discFolder = result.DiscFolder;
        var originalHashes = spec.Hashes();
        result.Files.Single(f => f.RelativePath == "a.bin").Status.Should().Be(FileStatus.Intact);
        result.Files.Where(f => f.RelativePath is "b.bin" or "c.bin").Should().OnlyContain(f => f.Status == FileStatus.Failed);

        // Nothing from the wrong disc was written, and nothing is left half done.
        foreach (var name in new[] { "b.bin", "c.bin" })
            File.Exists(Path.Combine(discFolder, name)).Should().BeFalse($"{name} must not exist with its final name: it was never copied completely");
        Directory.EnumerateFiles(discFolder, "*", SearchOption.AllDirectories).Where(p => p.EndsWith(".partial")).Should().BeEmpty();
        E2eRig.Sha256Of(Path.Combine(discFolder, "a.bin")).Should().Be(originalHashes["a.bin"]);
        _rig.Drives.LockCalls[^1].Should().BeFalse("the lock is released on this path too");
        File.Exists(Path.Combine(discFolder, "LEIA-ME - ERROS.txt")).Should().BeTrue("the user is told what is missing");

        // The progress was kept: the original disc can still be finished.
        _rig.Engine.CanResume(disc, _rig.DestRoot, folder).Should().BeTrue();
        mOther.Dismount();
        var m3 = _rig.MountIso(iso);
        m3.Letter.Should().Be(m.Letter);
        var (drive3, disc3) = await _rig.AnalyzeAsync(m3.Letter);
        disc3.Fingerprint.Should().Be(disc.Fingerprint);
        _rig.Reader.ResetCounts();
        _rig.Reader.BeforeRead = null;
        var second = await _rig.RunAsync(_rig.Request(drive3, disc3, folder));

        E2eRig.AssertExtractedCorrectly(spec, disc3, second, m3.Letter);
        _rig.Reader.OpenCount("a.bin").Should().Be(0, "a.bin was already finished and verified");
        _rig.Reader.OpenCount("b.bin").Should().Be(1);
        _rig.Reader.OpenCount("c.bin").Should().Be(1);
    }

    // ================================================================== (c) cancel, then resume (RF-10)

    private static IsoSpec SixFiles()
    {
        var spec = new IsoSpec { Label = "SEIS" };
        for (int i = 1; i <= 6; i++) spec.AddRandom($"f{i}.bin", 2_500_000 + i * 1000, 90 + i);
        return spec.AddRandom("sub\\f7.bin", 1_000_000, 97);
    }

    private async Task<(MountedIso M, OpticalDrive Drive, DiscInfo Disc, ExtractionResult Cancelled, string Folder)> CancelAfterThreeFilesAsync(IsoSpec spec, string folder)
    {
        var m = _rig.Mount(spec);
        var (drive, disc) = await _rig.AnalyzeAsync(m.Letter);
        using var cts = new CancellationTokenSource();
        var progress = new RecordingProgress { OnReport = p => { if (p.FilesIntact >= 3) cts.Cancel(); } };
        var cancelled = await _rig.RunAsync(_rig.Request(drive, disc, folder), progress, ct: cts.Token);
        return (m, drive, disc, cancelled, folder);
    }

    [MountFact]
    public async Task RF10_CancelMidCopy_ThenSameRequest_ResumesWithoutRecopyingFinishedFiles()
    {
        var spec = SixFiles();
        var (m, drive, disc, cancelled, folder) = await CancelAfterThreeFilesAsync(spec, "Cancelado");

        cancelled.Status.Should().Be(OverallStatus.Cancelled);
        var discFolder = cancelled.DiscFolder;
        File.Exists(Path.Combine(discFolder, ".mediarescue-job.json")).Should().BeTrue("the resume state is kept when a job is cancelled");
        _rig.Engine.CanResume(disc, _rig.DestRoot, folder).Should().BeTrue();
        var expected = spec.Hashes();
        var done = cancelled.Files.Where(f => f.Status == FileStatus.Intact).ToList();
        done.Should().HaveCount(3);
        foreach (var f in done) E2eRig.Sha256Of(Path.Combine(discFolder, f.WrittenPath)).Should().Be(expected[f.RelativePath]);
        Directory.EnumerateFiles(discFolder, "*.partial", SearchOption.AllDirectories).Should().BeEmpty("a cancelled copy leaves no .partial");
        _rig.Drives.LockCalls.Should().Equal(new[] { true, false }, "the lock is released on cancellation");
        foreach (var f in done) _rig.Reader.OpenCount(f.RelativePath).Should().Be(1);
        var doneNames = done.Select(f => f.RelativePath).ToHashSet();

        // The App's "Continuar de onde parou" sends the very same folder name: the engine must resume by itself.
        _rig.Reader.ResetCounts();
        var progress = new RecordingProgress();
        var (drive2, disc2) = await _rig.AnalyzeAsync(m.Letter);
        var result = await _rig.RunAsync(_rig.Request(drive2, disc2, folder), progress);

        E2eRig.AssertExtractedCorrectly(spec, disc2, result, m.Letter);
        foreach (var name in doneNames) _rig.Reader.OpenCount(name).Should().Be(0, $"{name} was finished before the cancel and must not be copied again");
        foreach (var name in spec.Files.Keys.Where(k => !doneNames.Contains(k))) _rig.Reader.OpenCount(name).Should().Be(1, name);
        progress.Items.First(i => i.Phase == ExtractionPhase.Copying && i.CurrentFile != null).FilesIntact.Should().BeGreaterThanOrEqualTo(3, "finished files count from the start");
        progress.Items[^1].FilesIntact.Should().Be(spec.Files.Count);
        _rig.Engine.CanResume(disc2, _rig.DestRoot, folder).Should().BeFalse();
    }

    [MountFact]
    public async Task RF10_StartOver_WithTheNextFreeFolderName_CopiesEverythingAgain_AndLeavesTheInterruptedFolderAlone()
    {
        var spec = SixFiles();
        var (m, _, disc, cancelled, folder) = await CancelAfterThreeFilesAsync(spec, "Recomecar");
        var firstFolder = cancelled.DiscFolder;
        var before = Directory.EnumerateFiles(firstFolder, "*", SearchOption.AllDirectories).OrderBy(p => p).ToList();

        // What the App does for "Começar do zero (nova pasta)": NameSanitizer.MakeUnique -> "Recomecar (2)".
        var fresh = NameSanitizer.MakeUnique(folder, n => Directory.Exists(Path.Combine(_rig.DestRoot, n)));
        fresh.Should().Be("Recomecar (2)");
        _rig.Reader.ResetCounts();
        var (drive2, disc2) = await _rig.AnalyzeAsync(m.Letter);
        var result = await _rig.RunAsync(_rig.Request(drive2, disc2, fresh));

        E2eRig.AssertExtractedCorrectly(spec, disc2, result, m.Letter);
        result.DiscFolder.Should().Be(Path.Combine(_rig.DestRoot, fresh));
        foreach (var name in spec.Files.Keys) _rig.Reader.OpenCount(name).Should().Be(1, $"{name}: a new folder means a full copy");
        Directory.EnumerateFiles(firstFolder, "*", SearchOption.AllDirectories).OrderBy(p => p).Should().Equal(before, "the interrupted folder is not touched");
        _rig.Engine.CanResume(disc, _rig.DestRoot, folder).Should().BeTrue("the interrupted job can still be continued later");
        _rig.Engine.CanResume(disc, _rig.DestRoot, fresh).Should().BeFalse();
    }

    [MountFact]
    public async Task RF10_ResumeDiscardsAStalePartial_AndRedoesThatFile()
    {
        var spec = SixFiles();
        var (m, _, disc, cancelled, folder) = await CancelAfterThreeFilesAsync(spec, "Parcial velho");
        // A crash would leave a .partial of the file that was in flight (simulated: garbage with the right name).
        var inFlight = Path.Combine(cancelled.DiscFolder, "f4.bin.partial");
        await File.WriteAllBytesAsync(inFlight, new byte[12345]);

        var (drive2, disc2) = await _rig.AnalyzeAsync(m.Letter);
        var result = await _rig.RunAsync(_rig.Request(drive2, disc2, folder));
        E2eRig.AssertExtractedCorrectly(spec, disc2, result, m.Letter);
        File.Exists(inFlight).Should().BeFalse();
    }

    // ================================================================== (d) same label, destination validation

    [MountFact]
    public async Task E21_TwoDiscsWithTheSameLabel_GoToDistinctFolders_WithDifferentFingerprints()
    {
        var specA = new IsoSpec { Label = "FERIAS" }.AddRandom("praia.avi", 300_000, 501).AddRandom("fotos\\a.jpg", 20_000, 502);
        var specB = new IsoSpec { Label = "FERIAS" }.AddRandom("serra.avi", 310_000, 503).AddRandom("fotos\\b.jpg", 21_000, 504);
        var a = _rig.Mount(specA, "a.iso");
        var b = _rig.Mount(specB, "b.iso");
        var (driveA, discA) = await _rig.AnalyzeAsync(a.Letter);
        var (driveB, discB) = await _rig.AnalyzeAsync(b.Letter);
        discA.Fingerprint.Should().NotBe(discB.Fingerprint);

        // Exactly what ExtractViewModel does: suggested name, then MakeUnique against what already exists.
        var today = DateTime.UtcNow.ToLocalTime().Date;
        var nameA = NameSanitizer.SanitizeFolderName(NameSanitizer.SuggestFolderName(discA, today));
        var nameB = NameSanitizer.SanitizeFolderName(NameSanitizer.SuggestFolderName(discB, today));
        nameA.Should().Be(nameB, "same label gives the same suggestion");
        bool Exists(string n) => Directory.Exists(Path.Combine(_rig.DestRoot, n));

        var folderA = NameSanitizer.MakeUnique(nameA, Exists);
        var resultA = await _rig.RunAsync(_rig.Request(driveA, discA, folderA));
        E2eRig.AssertExtractedCorrectly(specA, discA, resultA, a.Letter);
        _rig.AddToCatalog(folderA, discA, resultA);

        var folderB = NameSanitizer.MakeUnique(nameB, Exists);
        folderB.Should().Be(folderA + " (2)");
        var resultB = await _rig.RunAsync(_rig.Request(driveB, discB, folderB));
        E2eRig.AssertExtractedCorrectly(specB, discB, resultB, b.Letter);
        _rig.AddToCatalog(folderB, discB, resultB);

        resultA.DiscFolder.Should().NotBe(resultB.DiscFolder);
        File.Exists(Path.Combine(resultA.DiscFolder, "serra.avi")).Should().BeFalse("nothing of disc B leaks into folder A");
        File.Exists(Path.Combine(resultB.DiscFolder, "praia.avi")).Should().BeFalse();
        _rig.Catalog.FindByFingerprint(discA.Fingerprint)!.DestinationPath.Should().Be(resultA.DiscFolder);
        _rig.Catalog.FindByFingerprint(discB.Fingerprint)!.DestinationPath.Should().Be(resultB.DiscFolder);
        _rig.Catalog.GetAll().Should().HaveCount(2);

        // The validator also warns about an existing folder and offers the next name.
        var issues = _rig.Validator.Validate(_rig.DestRoot, folderA, discA.Entries, a.Letter);
        issues.Should().Contain(i => i.Severity == ValidationSeverity.Warning && i.Message.Contains(folderA + " (3)"));
        issues.Should().NotContain(i => i.Severity == ValidationSeverity.Blocking);
    }

    [MountFact]
    public async Task RF03_DestinationEqualToTheOpticalDrive_IsBlocking()
    {
        var m = _rig.Mount(IsoLayouts.DataDisc());
        var (_, disc) = await _rig.AnalyzeAsync(m.Letter);

        foreach (var dest in new[] { m.Letter + "\\", m.Letter, m.Letter.ToLowerInvariant() + "\\", m.Letter + "\\fotos" })
        {
            var issues = _rig.Validator.Validate(dest, "Teste", disc.Entries, m.Letter);
            issues.Should().ContainSingle(i => i.Severity == ValidationSeverity.Blocking, $"destination {dest}");
            issues.Single().Message.Should().Contain("leitor", dest);
        }

        // Another optical drive (a second mounted ISO) is blocked as well, even though it is not the one being read.
        var other = _rig.Mount(IsoLayouts.DvdRtav(), "outra.iso");
        var viaFs = _rig.Validator.Validate(other.Letter + "\\", "Teste", disc.Entries, m.Letter);
        viaFs.Should().Contain(i => i.Severity == ValidationSeverity.Blocking);
        // A normal folder is fine.
        _rig.Validator.Validate(_rig.DestRoot, "Teste", disc.Entries, m.Letter).Should().NotContain(i => i.Severity == ValidationSeverity.Blocking);
    }

    [Fact]
    public void RF03_InsufficientSpace_IsBlocking_AndSaysHowMuchIsMissing_OnARealVolume()
    {
        var info = _rig.Fs.GetInfo(_rig.DestRoot)!;
        info.FreeBytes.Should().BeGreaterThan(0);
        DiscEntry Big(long size) => new() { RelativePath = "enorme.avi", Size = size, Kind = EntryKind.Video };

        // The disc needs more than the volume has.
        var tooBig = _rig.Validator.Validate(_rig.DestRoot, "Teste", new[] { Big(info.FreeBytes + (1L << 30)) }, "D:");
        tooBig.Should().Contain(i => i.Severity == ValidationSeverity.Blocking && i.Message.Contains("Não há espaço suficiente") && i.Message.Contains("faltam"));

        // Exactly the free space is still not enough: the 5% + 50 MB safety margin counts (RF-03).
        _rig.Validator.Validate(_rig.DestRoot, "Teste", new[] { Big(info.FreeBytes) }, "D:")
            .Should().Contain(i => i.Severity == ValidationSeverity.Blocking && i.Message.Contains("Não há espaço suficiente"));

        // A small selection passes and the write probe leaves nothing behind.
        var ok = _rig.Validator.Validate(_rig.DestRoot, "Teste", new[] { Big(1_000_000) }, "D:");
        ok.Should().NotContain(i => i.Severity == ValidationSeverity.Blocking);
        ok.Should().Contain(i => i.Severity == ValidationSeverity.Ok);
        Directory.GetFileSystemEntries(_rig.DestRoot).Should().BeEmpty();

        // Missing destination.
        _rig.Validator.Validate(Path.Combine(_rig.DestRoot, "nao-existe"), "Teste", new[] { Big(1) }, "D:")
            .Should().Contain(i => i.Severity == ValidationSeverity.Blocking);
    }

    // ================================================================== (e) long paths (E-20)

    [MountFact]
    public async Task E20_DestinationPathLongerThan260Characters_ExtractsVerifiesAndResumes()
    {
        var spec = IsoLayouts.Mixed();            // already holds a 340+ character path inside the disc
        var m = _rig.Mount(spec);
        var (drive, disc) = await _rig.AnalyzeAsync(m.Letter);
        var longRoot = Path.Combine(_rig.DestRoot, new string('a', 70), new string('b', 70), new string('c', 70));
        _rig.Fs.CreateDirectory(longRoot);
        longRoot.Length.Should().BeGreaterThan(260 - 40);
        const string folder = "Pasta com caminho longo";

        var issues = _rig.Validator.Validate(longRoot, folder, disc.Entries, m.Letter);
        issues.Should().NotContain(i => i.Severity == ValidationSeverity.Blocking, "long paths are supported, only a warning is shown");
        issues.Should().Contain(i => i.Severity == ValidationSeverity.Warning && i.Message.Contains("240"));

        var result = await _rig.RunAsync(_rig.Request(drive, disc, folder, longRoot));
        E2eRig.AssertExtractedCorrectly(spec, disc, result, m.Letter);
        result.Files.Max(f => Path.Combine(result.DiscFolder, f.WrittenPath).Length).Should().BeGreaterThan(500);
        Path.Combine(result.DiscFolder, "mediarescue-manifest.json").Length.Should().BeGreaterThan(260 - 1);

        // Cancel + resume in the long folder works too (the state file lives beyond MAX_PATH as well).
        var spec2 = SixFiles();
        var m2 = _rig.Mount(spec2, "seis.iso");
        var (drive2, disc2) = await _rig.AnalyzeAsync(m2.Letter);
        using var cts = new CancellationTokenSource();
        var cancelled = await _rig.RunAsync(_rig.Request(drive2, disc2, "Longa 2", longRoot), new RecordingProgress { OnReport = p => { if (p.FilesIntact >= 2) cts.Cancel(); } }, ct: cts.Token);
        cancelled.Status.Should().Be(OverallStatus.Cancelled);
        _rig.Engine.CanResume(disc2, longRoot, "Longa 2").Should().BeTrue();
        var resumed = await _rig.RunAsync(_rig.Request(drive2, disc2, "Longa 2", longRoot));
        E2eRig.AssertExtractedCorrectly(spec2, disc2, resumed, m2.Letter);
    }

    // ================================================================== lock / failure paths

    [MountFact]
    public async Task MediaLock_IsReleased_WhenTheDestinationDisappearsAtTheStart()
    {
        var spec = IsoLayouts.DataDisc();
        var m = _rig.Mount(spec);
        var (drive, disc) = await _rig.AnalyzeAsync(m.Letter);
        var missing = Enumerable.Range('A', 26).Select(c => (char)c).Last(c => !Directory.Exists(c + @":\"));

        var result = await _rig.RunAsync(_rig.Request(drive, disc, "Teste", $@"{missing}:\pasta\inexistente"));

        result.Status.Should().Be(OverallStatus.Failed, "an unusable destination ends the job instead of hanging");
        _rig.Drives.LockCalls.Should().Equal(new[] { true, false });
    }

    [MountFact]
    public async Task E09_ProtectedDisc_IsRefused_NothingIsWritten_NoLockTaken()
    {
        var spec = IsoLayouts.DataDisc();
        var m = _rig.Mount(spec);
        var (drive, disc) = await _rig.AnalyzeAsync(m.Letter);
        var result = await _rig.RunAsync(_rig.Request(drive, disc with { IsCopyProtected = true }, "Protegido"));

        result.Status.Should().Be(OverallStatus.Failed);
        _rig.Drives.LockCalls.Should().BeEmpty();
        Directory.Exists(Path.Combine(_rig.DestRoot, "Protegido")).Should().BeFalse();
    }
}
