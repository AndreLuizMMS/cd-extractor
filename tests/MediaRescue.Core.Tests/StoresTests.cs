using System.Text;
using FluentAssertions;
using MediaRescue.Core;
using MediaRescue.Core.Tests.Fakes;

namespace MediaRescue.Core.Tests;

public class FingerprintTests
{
    private static DiscEntry E(string path, long size = 1000, int day = 1, bool dir = false) =>
        new() { RelativePath = path, Size = size, ModifiedUtc = new DateTime(2009, 1, day, 12, 0, 0, DateTimeKind.Utc), IsDirectory = dir };

    private static DiscInfo Disc(string label = "FESTA", string serial = "AAAA-1111", long used = 5000, params DiscEntry[] entries) =>
        new() { VolumeLabel = label, VolumeSerial = serial, UsedBytes = used, Entries = entries.Length == 0 ? new[] { E("a.avi"), E(@"Dir\b.avi", 2000) } : entries };

    [Fact]
    public void SameDisc_ReadTwice_HasTheSameFingerprint()
    {
        Disc().Fingerprint.Should().Be(Disc().Fingerprint);
        Disc().Fingerprint.Should().HaveLength(64).And.MatchRegex("^[0-9A-F]+$");
    }

    [Fact]
    public void TheOrderOfTheListing_DoesNotMatter()
    {
        var a = Disc(entries: new[] { E("a.avi"), E("b.avi"), E("c.avi") });
        var b = Disc(entries: new[] { E("c.avi"), E("a.avi"), E("b.avi") });
        a.Fingerprint.Should().Be(b.Fingerprint);
    }

    [Fact]
    public void Directories_AndTheirOrder_DoNotChangeIt()
    {
        var a = Disc(entries: new[] { E("a.avi"), E("Pasta", dir: true) });
        var b = Disc(entries: new[] { E("a.avi") });
        a.Fingerprint.Should().Be(b.Fingerprint);
    }

    [Fact]
    public void PathCase_DoesNotChangeIt()
    {
        Disc(entries: new[] { E("Fotos\\A.JPG") }).Fingerprint.Should().Be(Disc(entries: new[] { E("FOTOS\\a.jpg") }).Fingerprint);
    }

    [Theory]
    [InlineData("label")]
    [InlineData("serial")]
    [InlineData("used")]
    [InlineData("size")]
    [InlineData("date")]
    [InlineData("name")]
    [InlineData("extra file")]
    [InlineData("missing file")]
    public void DifferentDiscs_HaveDifferentFingerprints(string what)
    {
        var baseline = Disc(entries: new[] { E("a.avi"), E("b.avi") }).Fingerprint;
        var other = what switch
        {
            "label" => Disc(label: "OUTRA", entries: new[] { E("a.avi"), E("b.avi") }),
            "serial" => Disc(serial: "BBBB-2222", entries: new[] { E("a.avi"), E("b.avi") }),
            "used" => Disc(used: 5001, entries: new[] { E("a.avi"), E("b.avi") }),
            "size" => Disc(entries: new[] { E("a.avi"), E("b.avi", 1001) }),
            "date" => Disc(entries: new[] { E("a.avi"), E("b.avi", day: 2) }),
            "name" => Disc(entries: new[] { E("a.avi"), E("c.avi") }),
            "extra file" => Disc(entries: new[] { E("a.avi"), E("b.avi"), E("c.avi") }),
            _ => Disc(entries: new[] { E("a.avi") }),
        };
        other.Fingerprint.Should().NotBe(baseline);
    }
}

public class CatalogStoreTests
{
    private const string Path = @"C:\Users\x\AppData\Local\MediaRescue\catalog.json";

    private static FakeDestination Fs()
    {
        var fs = new FakeDestination(@"C:\Users\x\AppData\Local\MediaRescue");
        return fs;
    }

    private static CatalogEntry Entry(string fp = "AAAA", string name = "Formatura 2009", int day = 12, OverallStatus status = OverallStatus.Intact, long lost = 0) => new()
    {
        Fingerprint = fp, Name = name, ExtractedAtUtc = new DateTime(2026, 10, day, 12, 0, 0, DateTimeKind.Utc),
        DestinationPath = @"E:\Videos\" + name, TotalBytes = 2_000_000_000, BytesLost = lost, FileCount = 7, Status = status,
    };

    [Fact]
    public void MissingFile_GivesAnEmptyCatalog_WithoutWarning()
    {
        var store = new CatalogStore(Fs(), Path);
        store.GetAll().Should().BeEmpty();
        store.LoadWarning.Should().BeNull();
        store.FindByFingerprint("AAAA").Should().BeNull();
    }

    [Fact]
    public void AddedEntries_SurviveARestart()
    {
        var fs = Fs();
        var store = new CatalogStore(fs, Path);
        store.Add(Entry("AAAA", "Formatura 2009"));
        store.Add(Entry("BBBB", "Aniversário", status: OverallStatus.Partial, lost: 4096));

        var reloaded = new CatalogStore(fs, Path);

        reloaded.LoadWarning.Should().BeNull();
        reloaded.GetAll().Should().HaveCount(2);
        var b = reloaded.FindByFingerprint("BBBB")!;
        b.Name.Should().Be("Aniversário");
        b.Status.Should().Be(OverallStatus.Partial);
        b.BytesLost.Should().Be(4096);
        b.TotalBytes.Should().Be(2_000_000_000);
        b.FileCount.Should().Be(7);
        b.DestinationPath.Should().Be(@"E:\Videos\Aniversário");
        b.ExtractedAtUtc.Should().Be(new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Saving_IsAtomic_AndKeepsABackupOfThePreviousVersion()
    {
        var fs = Fs();
        var store = new CatalogStore(fs, Path);
        store.Add(Entry("AAAA"));
        store.Add(Entry("BBBB"));

        fs.AllFiles.Should().Contain(Path).And.Contain(Path + ".bak");
        fs.ReadText(Path).Should().Contain("\"schemaVersion\": 1");
        var bak = new CatalogStore(fs, Path);
        bak.GetAll().Should().HaveCount(2);
        fs.ReadText(Path + ".bak").Should().Contain("AAAA").And.NotContain("BBBB", "the .bak holds the previous version");
    }

    [Fact]
    public void E22_FindByFingerprint_ReturnsTheMostRecentExtraction()
    {
        var store = new CatalogStore(Fs(), Path);
        store.Add(Entry("AAAA", "primeira", day: 1));
        store.Add(Entry("AAAA", "segunda", day: 5));
        store.Add(Entry("CCCC", "outro", day: 6));

        store.FindByFingerprint("AAAA")!.Name.Should().Be("segunda");
        store.FindByFingerprint("aaaa")!.Name.Should().Be("segunda", "fingerprints compare case-insensitively");
        store.FindByFingerprint("ZZZZ").Should().BeNull();
        store.GetAll().Should().HaveCount(3);
    }

    [Fact]
    public void E30_CorruptedCatalog_IsRestoredFromTheBackup_WithAWarning()
    {
        var fs = Fs();
        var store = new CatalogStore(fs, Path);
        store.Add(Entry("AAAA"));
        store.Add(Entry("BBBB"));
        fs.SeedFile(Path, Encoding.UTF8.GetBytes("{ \"schemaVersion\": 1, \"entries\": [ {{{ garbage"));

        var recovered = new CatalogStore(fs, Path);

        recovered.LoadWarning.Should().NotBeNullOrEmpty().And.Contain("corrompido").And.Contain("cópia de segurança");
        recovered.GetAll().Select(e => e.Fingerprint).Should().Equal("AAAA");
        fs.AllFiles.Should().Contain(Path + ".corrupt", "the damaged file is kept for diagnosis");
        // and the restored catalog is written back so the next start is clean
        new CatalogStore(fs, Path).LoadWarning.Should().BeNull();
        new CatalogStore(fs, Path).GetAll().Should().HaveCount(1);
    }

    [Fact]
    public void E30_CorruptedCatalogWithoutUsableBackup_StartsEmpty_WithAWarning_AndKeepsWorking()
    {
        var fs = Fs();
        fs.SeedFile(Path, Encoding.UTF8.GetBytes("isto não é json"));
        fs.SeedFile(Path + ".bak", Encoding.UTF8.GetBytes("também não"));

        var store = new CatalogStore(fs, Path);

        store.LoadWarning.Should().NotBeNullOrEmpty().And.Contain("do zero");
        store.GetAll().Should().BeEmpty();
        store.Add(Entry("AAAA"));
        new CatalogStore(fs, Path).GetAll().Should().HaveCount(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1,\"entries\":[{\"name\":\"sem impressão digital\"}]}")]
    [InlineData("{\"schemaVersion\":1,\"entries\":[{\"fingerprint\":\"A\",\"name\":\"x\",\"destinationPath\":\"y\",\"status\":\"Inexistente\"}]}")]
    public void E30_VariousKindsOfDamage_AreAllRecoveredFromWithoutThrowing(string content)
    {
        var fs = Fs();
        fs.SeedFile(Path, Encoding.UTF8.GetBytes(content));

        var act = () => new CatalogStore(fs, Path);

        act.Should().NotThrow();
        act().GetAll().Should().BeEmpty();
        act().LoadWarning.Should().NotBeNull();
    }

    [Fact]
    public void OnlyTheBackupSurvived_IsUsed()
    {
        var fs = Fs();
        var good = new CatalogStore(fs, Path);
        good.Add(Entry("AAAA"));
        good.Add(Entry("BBBB"));
        fs.Delete(Path);

        var store = new CatalogStore(fs, Path);

        store.LoadWarning.Should().NotBeNull();
        store.GetAll().Should().HaveCount(1);
    }

    [Fact]
    public void Schema0_BareArrayOfEntries_IsMigrated()
    {
        var fs = Fs();
        fs.SeedFile(Path, Encoding.UTF8.GetBytes(
            "[{\"fingerprint\":\"OLD1\",\"name\":\"Velho\",\"extractedAtUtc\":\"2020-01-02T03:04:05Z\",\"destinationPath\":\"D:\\\\x\",\"totalBytes\":10,\"fileCount\":1,\"status\":\"Intact\"}]"));

        var store = new CatalogStore(fs, Path);

        store.LoadWarning.Should().BeNull();
        store.FindByFingerprint("OLD1")!.Name.Should().Be("Velho");
        store.Add(Entry("NEW1"));
        fs.ReadText(Path).Should().Contain("\"schemaVersion\": 1").And.Contain("OLD1").And.Contain("NEW1");
    }

    [Fact]
    public void FileWrittenByANewerSchema_IsStillReadable()
    {
        var fs = Fs();
        fs.SeedFile(Path, Encoding.UTF8.GetBytes(
            "{\"schemaVersion\":7,\"futureField\":true,\"entries\":[{\"fingerprint\":\"F1\",\"name\":\"n\",\"destinationPath\":\"p\",\"status\":\"Partial\",\"extra\":1}]}"));

        var store = new CatalogStore(fs, Path);

        store.FindByFingerprint("F1")!.Status.Should().Be(OverallStatus.Partial);
    }

    [Fact]
    public void FailingToSave_DoesNotThrow_AndIsReported()
    {
        var fs = Fs();
        var store = new CatalogStore(fs, Path);
        fs.AtomicWriteFault = _ => new IOException("disco cheio");

        var act = () => store.Add(Entry("AAAA"));

        act.Should().NotThrow();
        store.LastSaveError.Should().Contain("disco cheio");
        store.GetAll().Should().HaveCount(1, "the entry is kept in memory");
        fs.AtomicWriteFault = null;
        store.Add(Entry("BBBB"));
        store.LastSaveError.Should().BeNull();
        new CatalogStore(fs, Path).GetAll().Should().HaveCount(2);
    }

    [Fact]
    public void ExportCsv_HasAHeader_OneLinePerDisc_AndPortugueseStatus()
    {
        var store = new CatalogStore(Fs(), Path);
        store.Add(Entry("AAAA", "Formatura 2009", day: 1));
        store.Add(Entry("BBBB", "Festa; do \"ano\"", day: 2, status: OverallStatus.Partial, lost: 99));

        var lines = store.ExportCsv().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(3);
        lines[0].Should().StartWith("Nome;Data;");
        lines[1].Should().StartWith("\"Festa; do \"\"ano\"\"\";", "cells with separators or quotes are escaped").And.Contain(";Parcial;");
        lines[2].Should().Contain("Formatura 2009").And.Contain(";Íntegro;").And.Contain("2000000000");
    }

    [Fact]
    public void ExportCsv_NeutralisesSpreadsheetFormulas()
    {
        var store = new CatalogStore(Fs(), Path);
        store.Add(Entry("AAAA", "=HYPERLINK(\"http://x\")"));
        store.Add(Entry("BBBB", "+cmd"));
        store.Add(Entry("CCCC", "@SUM(A1)"));

        var csv = store.ExportCsv();

        csv.Should().NotContain(";=HYPERLINK").And.NotContain(";+cmd").And.NotContain(";@SUM");
        csv.Should().Contain("'+cmd").And.Contain("'@SUM");
    }

    [Fact]
    public void ExportCsv_OfAnEmptyCatalog_IsJustTheHeader()
    {
        new CatalogStore(Fs(), Path).ExportCsv().Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().ContainSingle();
    }

    [Fact]
    public void ConcurrentAdds_AreAllKept()
    {
        var fs = Fs();
        var store = new CatalogStore(fs, Path);
        Parallel.For(0, 40, i => store.Add(Entry("FP" + i, "disco " + i)));
        store.GetAll().Should().HaveCount(40);
        new CatalogStore(fs, Path).GetAll().Should().HaveCount(40);
    }
}

public class SettingsStoreTests
{
    private const string Path = @"C:\Users\x\AppData\Local\MediaRescue\settings.json";

    private static FakeDestination Fs() => new(@"C:\Users\x\AppData\Local\MediaRescue");

    [Fact]
    public void MissingFile_GivesTheDefaults_WithoutWarning()
    {
        var store = new SettingsStore(Fs(), Path);

        var s = store.Load();

        store.LoadWarning.Should().BeNull();
        s.EjectWhenDone.Should().BeTrue();
        s.ConvertToMp4.Should().BeFalse();
        s.Theme.Should().Be("System");
        s.LastDestination.Should().BeNull();
        s.Extraction.VerifyAfterCopy.Should().BeTrue("verification is on by default");
        s.Extraction.RetriesPerSector.Should().Be(3);
        s.Extraction.BlockTimeout.Should().Be(TimeSpan.FromSeconds(30));
        s.Extraction.SecondPassBudget.Should().Be(TimeSpan.FromMinutes(20));
        s.Extraction.ReduceSpeedOnError.Should().BeTrue();
    }

    [Fact]
    public void SavedSettings_RoundTrip()
    {
        var fs = Fs();
        var store = new SettingsStore(fs, Path);
        var s = new AppSettings
        {
            LastDestination = @"E:\Vídeos da família", EjectWhenDone = false, ConvertToMp4 = true, BatchAskName = false, Theme = "Dark",
            Extraction = new ExtractionOptions
            {
                RetriesPerSector = 5, BlockTimeout = TimeSpan.FromSeconds(45), SecondPassBudget = TimeSpan.FromMinutes(90),
                ReduceSpeedOnError = false, VerifyAfterCopy = false,
            },
        };

        store.Save(s);
        var loaded = new SettingsStore(fs, Path).Load();

        loaded.LastDestination.Should().Be(@"E:\Vídeos da família");
        loaded.EjectWhenDone.Should().BeFalse();
        loaded.ConvertToMp4.Should().BeTrue();
        loaded.BatchAskName.Should().BeFalse();
        loaded.Theme.Should().Be("Dark");
        loaded.SchemaVersion.Should().Be(1);
        loaded.Extraction.RetriesPerSector.Should().Be(5);
        loaded.Extraction.BlockTimeout.Should().Be(TimeSpan.FromSeconds(45));
        loaded.Extraction.SecondPassBudget.Should().Be(TimeSpan.FromMinutes(90));
        loaded.Extraction.ReduceSpeedOnError.Should().BeFalse();
        loaded.Extraction.VerifyAfterCopy.Should().BeFalse();
        fs.ReadText(Path).Should().Contain("\"schemaVersion\": 1");
    }

    [Fact]
    public void Saving_KeepsABackupOfThePreviousFile()
    {
        var fs = Fs();
        var store = new SettingsStore(fs, Path);
        store.Save(new AppSettings { LastDestination = @"E:\um" });
        store.Save(new AppSettings { LastDestination = @"E:\dois" });

        fs.ReadText(Path + ".bak").Should().Contain(@"E:\\um");
        fs.ReadText(Path).Should().Contain(@"E:\\dois");
    }

    [Fact]
    public void E30_CorruptedSettings_AreRestoredFromTheBackup_WithAWarning()
    {
        var fs = Fs();
        var store = new SettingsStore(fs, Path);
        store.Save(new AppSettings { LastDestination = @"E:\um", Theme = "Light" });
        store.Save(new AppSettings { LastDestination = @"E:\dois", Theme = "Dark" });
        fs.SeedFile(Path, Encoding.UTF8.GetBytes("{ quebrado"));

        var s = store.Load();

        store.LoadWarning.Should().NotBeNullOrEmpty().And.Contain("corrompido");
        s.LastDestination.Should().Be(@"E:\um");
        s.Theme.Should().Be("Light");
    }

    [Fact]
    public void E30_CorruptedSettingsWithoutBackup_FallBackToDefaults_AndSaveStillWorks()
    {
        var fs = Fs();
        fs.SeedFile(Path, Encoding.UTF8.GetBytes("[1,2,3"));
        var store = new SettingsStore(fs, Path);

        var s = store.Load();

        store.LoadWarning.Should().NotBeNullOrEmpty();
        s.Should().BeEquivalentTo(new AppSettings());
        store.Save(new AppSettings { Theme = "Dark" });
        store.Load().Theme.Should().Be("Dark");
        store.LoadWarning.Should().BeNull();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"texto\"")]
    [InlineData("")]
    [InlineData("null")]
    public void ValidJsonOfTheWrongShape_IsAlsoInvalid(string content)
    {
        var fs = Fs();
        fs.SeedFile(Path, Encoding.UTF8.GetBytes(content));
        var store = new SettingsStore(fs, Path);

        store.Load().Should().BeEquivalentTo(new AppSettings());
        store.LoadWarning.Should().NotBeNull();
    }

    [Fact]
    public void OutOfRangeAndWronglyTypedValues_FallBackToTheirDefaults_WithoutLosingTheRest()
    {
        var fs = Fs();
        fs.SeedFile(Path, Encoding.UTF8.GetBytes(@"{
            ""schemaVersion"": 1, ""lastDestination"": ""E:\\ok"", ""theme"": ""Roxo"", ""ejectWhenDone"": ""sim"",
            ""extraction"": { ""retriesPerSector"": 0, ""blockTimeoutSeconds"": -4, ""secondPassBudgetMinutes"": 100000,
                              ""reduceSpeedOnError"": false, ""verifyAfterCopy"": 7 } }"));
        var store = new SettingsStore(fs, Path);

        var s = store.Load();

        store.LoadWarning.Should().BeNull();
        s.LastDestination.Should().Be(@"E:\ok");
        s.Theme.Should().Be("System");
        s.EjectWhenDone.Should().BeTrue();
        s.Extraction.RetriesPerSector.Should().Be(3);
        s.Extraction.BlockTimeout.Should().Be(TimeSpan.FromSeconds(30));
        s.Extraction.SecondPassBudget.Should().Be(TimeSpan.FromMinutes(20));
        s.Extraction.ReduceSpeedOnError.Should().BeFalse("valid values are kept");
        s.Extraction.VerifyAfterCopy.Should().BeTrue();
    }

    [Fact]
    public void Schema0_IsMigrated()
    {
        var fs = Fs();
        fs.SeedFile(Path, Encoding.UTF8.GetBytes(@"{ ""lastFolder"": ""F:\\Antigo"", ""eject"": false, ""theme"": ""dark"" }"));

        var s = new SettingsStore(fs, Path).Load();

        s.LastDestination.Should().Be(@"F:\Antigo");
        s.EjectWhenDone.Should().BeFalse();
        s.Theme.Should().Be("Dark", "theme names are matched case-insensitively");
        s.SchemaVersion.Should().Be(1);
    }

    [Fact]
    public void UnknownFieldsAndCommentsAreIgnored()
    {
        var fs = Fs();
        fs.SeedFile(Path, Encoding.UTF8.GetBytes("{ // comentário\n \"schemaVersion\": 3, \"coisaNova\": [1,2], \"convertToMp4\": true, }"));

        var store = new SettingsStore(fs, Path);

        store.Load().ConvertToMp4.Should().BeTrue();
        store.LoadWarning.Should().BeNull();
    }

    [Fact]
    public void Save_FailureSurfaces_BecauseTheCallerShouldKnow()
    {
        var fs = Fs();
        fs.AtomicWriteFault = _ => new IOException("somente leitura");
        var store = new SettingsStore(fs, Path);

        var act = () => store.Save(new AppSettings());

        act.Should().Throw<IOException>();
    }
}

public class SystemClockTests
{
    [Fact]
    public void ElapsedMs_IsMonotonic()
    {
        var c = new SystemClock();
        var a = c.ElapsedMs;
        var b = c.ElapsedMs;
        b.Should().BeGreaterThanOrEqualTo(a);
    }

    [Fact]
    public void UtcNow_IsUtcAndCurrent()
    {
        var now = new SystemClock().UtcNow;
        now.Kind.Should().Be(DateTimeKind.Utc);
        (DateTime.UtcNow - now).Duration().Should().BeLessThan(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Delay_OfZero_CompletesImmediately()
    {
        var t = new SystemClock().Delay(TimeSpan.Zero, default);
        t.IsCompletedSuccessfully.Should().BeTrue();
        await t;
    }

    [Fact]
    public async Task Delay_WithACancelledToken_IsCancelled_WithoutWaiting()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await new SystemClock().Delay(TimeSpan.FromHours(1), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void FakeClock_IsDeterministic()
    {
        var c = new FakeClock();
        var t0 = c.ElapsedMs;
        c.Delay(TimeSpan.FromSeconds(3), default).IsCompletedSuccessfully.Should().BeTrue();
        (c.ElapsedMs - t0).Should().Be(3000);
        (c.UtcNow - c.StartUtc).Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task FakeClock_ManualMode_CompletesDelaysOnlyWhenAdvanced()
    {
        var c = new FakeClock { AutoAdvance = false };
        var t = c.Delay(TimeSpan.FromSeconds(5), default);
        t.IsCompleted.Should().BeFalse();
        c.Advance(TimeSpan.FromSeconds(4));
        t.IsCompleted.Should().BeFalse();
        c.Advance(TimeSpan.FromSeconds(1));
        await t.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
