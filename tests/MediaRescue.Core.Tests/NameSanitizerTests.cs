using FluentAssertions;
using MediaRescue.Core;

namespace MediaRescue.Core.Tests;

/// <summary>RF-04, E-20, E-21, E-31.</summary>
public class NameSanitizerTests
{
    private static readonly DateTime Today = new(2026, 10, 12);

    private static DiscInfo Disc(string label, DateTime? recorded = null) =>
        new() { VolumeLabel = label, VolumeSerial = "1111-2222", RecordedAtUtc = recorded };

    private static DateTime Utc(int y, int m, int d, int h = 0, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------ suggestion (RF-04, E-31)

    [Theory]
    [InlineData("FORMATURA", "FORMATURA")]
    [InlineData("  Formatura 2009  ", "Formatura 2009")]
    [InlineData("Aniversário da Vovó", "Aniversário da Vovó")]
    [InlineData("FESTA: 2009/2010", "FESTA_ 2009_2010")]
    [InlineData("CD_Maria", "CD_Maria")]
    [InlineData("VIDEO 2009", "VIDEO 2009")]
    public void MeaningfulLabel_IsKept_ButSanitized(string label, string expected)
    {
        NameSanitizer.SuggestFolderName(Disc(label, Utc(2009, 12, 12)), Today).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("NEW")]
    [InlineData("new")]
    [InlineData("New Volume")]
    [InlineData("NEW_VOLUME")]
    [InlineData("Novo Volume")]
    [InlineData("CDROM")]
    [InlineData("CD-ROM")]
    [InlineData("CD_ROM")]
    [InlineData("DVD_VIDEO")]
    [InlineData("DVD VIDEO")]
    [InlineData("DVDVIDEO")]
    [InlineData("MY DISC")]
    [InlineData("My_Disc")]
    [InlineData("DISC")]
    [InlineData("Untitled")]
    [InlineData("NO NAME")]
    [InlineData("123456")]
    [InlineData("2009-12-12")]
    [InlineData("0001")]
    [InlineData("DVD-R")]
    public void EmptyOrGenericLabel_FallsBackToTheRecordingDate(string label)
    {
        NameSanitizer.SuggestFolderName(Disc(label, Utc(2009, 12, 12)), Today).Should().Be("Disco 2009-12-12");
    }

    [Fact]
    public void TheRecordingDate_IsTakenAsUtc_WithoutTimezoneShifting()
    {
        NameSanitizer.SuggestFolderName(Disc("", Utc(2009, 12, 12, 23, 30)), Today).Should().Be("Disco 2009-12-12");
        NameSanitizer.SuggestFolderName(Disc("", Utc(2009, 12, 12, 0, 5)), Today).Should().Be("Disco 2009-12-12");
    }

    [Theory]
    [InlineData(1980, 1, 1)]   // DOS epoch: a clock that was never set
    [InlineData(1970, 1, 1)]
    [InlineData(1601, 1, 1)]
    [InlineData(1989, 12, 31)]
    [InlineData(2099, 1, 1)]
    [InlineData(2026, 10, 14)] // in the future
    [InlineData(9999, 12, 31)]
    public void E31_AbsurdRecordingDate_IsNotUsed_TodayIsUsedInstead(int y, int m, int d)
    {
        NameSanitizer.SuggestFolderName(Disc("NEW", Utc(y, m, d)), Today).Should().Be("Disco 2026-10-12");
    }

    [Fact]
    public void E31_MissingRecordingDate_UsesToday()
    {
        NameSanitizer.SuggestFolderName(Disc("", null), Today).Should().Be("Disco 2026-10-12");
        NameSanitizer.SuggestFolderName(Disc("", DateTime.MinValue), Today).Should().Be("Disco 2026-10-12");
    }

    [Theory]
    [InlineData(1990, 1, 1)]
    [InlineData(1999, 5, 20)]
    [InlineData(2026, 10, 12)]
    [InlineData(2026, 10, 13)] // tomorrow in UTC while it is still "today" locally
    public void PlausibleRecordingDates_AreAccepted(int y, int m, int d)
    {
        NameSanitizer.SuggestFolderName(Disc("", Utc(y, m, d)), Today).Should().Be($"Disco {y:0000}-{m:00}-{d:00}");
    }

    [Fact]
    public void ADateOnlyLabelDoesNotUseTheDateOfTheLabel()
    {
        NameSanitizer.SuggestFolderName(Disc("20091212", Utc(2010, 1, 2)), Today).Should().Be("Disco 2010-01-02");
    }

    // ------------------------------------------------------------------ folder sanitizing

    [Theory]
    [InlineData("a<b>c:d\"e/f\\g|h?i*j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("Nome.", "Nome")]
    [InlineData("Nome. . ", "Nome")]
    [InlineData("Nome   ", "Nome")]
    [InlineData("  Nome", "Nome")]
    [InlineData("Muitos    espaços   aqui", "Muitos espaços aqui")]
    [InlineData("tab\there", "tab_here")]
    [InlineData("ctl\u0001char", "ctl_char")]
    [InlineData("Formatura açaí 日本語", "Formatura açaí 日本語")]
    [InlineData("Festa 😀 2009", "Festa 😀 2009")]
    [InlineData("", "Disco")]
    [InlineData("   ", "Disco")]
    [InlineData("...", "Disco")]
    [InlineData("???", "___")]
    public void SanitizeFolderName_Cases(string input, string expected)
    {
        NameSanitizer.SanitizeFolderName(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("CON", "CON_")]
    [InlineData("con", "con_")]
    [InlineData("NUL", "NUL_")]
    [InlineData("PRN", "PRN_")]
    [InlineData("AUX", "AUX_")]
    [InlineData("COM1", "COM1_")]
    [InlineData("com9", "com9_")]
    [InlineData("LPT1", "LPT1_")]
    [InlineData("lpt9", "lpt9_")]
    [InlineData("CON.txt", "CON_.txt")]
    [InlineData("AUX .log", "AUX_.log")]
    public void ReservedDeviceNames_AreNeutralised(string input, string expected)
    {
        NameSanitizer.SanitizeFolderName(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("COM10")]
    [InlineData("CONSOLE")]
    [InlineData("NULL")]
    [InlineData("PRINTER")]
    [InlineData("Aux1")]
    [InlineData("COM")]
    public void NamesThatOnlyLookReserved_AreUntouched(string input)
    {
        NameSanitizer.SanitizeFolderName(input).Should().Be(input);
    }

    [Fact]
    public void FolderNames_AreLimitedTo80Characters_WithoutTrailingSpaceOrDot()
    {
        var s = NameSanitizer.SanitizeFolderName(new string('a', 200));
        s.Length.Should().Be(80);

        var withSpace = NameSanitizer.SanitizeFolderName(new string('a', 79) + " bbbbbbbbbb");
        withSpace.Length.Should().BeLessThanOrEqualTo(80);
        withSpace.Should().NotEndWith(" ");

        var withDot = NameSanitizer.SanitizeFolderName(new string('a', 79) + ".bbbbbbbbbb");
        withDot.Should().NotEndWith(".");
    }

    [Fact]
    public void Truncation_NeverSplitsASurrogatePair()
    {
        var name = new string('a', 79) + "😀😀";
        var s = NameSanitizer.SanitizeFolderName(name);
        s.Length.Should().BeLessThanOrEqualTo(80);
        s.Where(char.IsSurrogate).Count().Should().Be(0);
        char.IsLowSurrogate(s[^1]).Should().BeFalse();
        char.IsHighSurrogate(s[^1]).Should().BeFalse();
    }

    [Fact]
    public void LoneSurrogates_AreReplaced()
    {
        NameSanitizer.SanitizeFolderName("a\uD800b").Should().Be("a_b");
    }

    [Theory]
    [InlineData("FORMATURA")]
    [InlineData("a<b>:c.")]
    [InlineData("CON")]
    [InlineData("  x  y  ")]
    [InlineData("😀")]
    public void Sanitizing_IsIdempotent(string input)
    {
        var once = NameSanitizer.SanitizeFolderName(input);
        NameSanitizer.SanitizeFolderName(once).Should().Be(once);
    }

    [Fact]
    public void SanitizedNames_AreValidWindowsNames()
    {
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var input in new[] { "a/b", "x:y", "q|r", "?*", "COM1", "end.", "ends ", new string('z', 300), "tab\t", "\u0007bell" })
        {
            var s = NameSanitizer.SanitizeFolderName(input);
            s.IndexOfAny(invalid).Should().BeLessThan(0, $"'{input}' → '{s}'");
            s.Should().NotBeEmpty();
        }
    }

    // ------------------------------------------------------------------ MakeUnique (E-21)

    [Fact]
    public void MakeUnique_ReturnsTheNameWhenFree()
    {
        NameSanitizer.MakeUnique("FESTA", _ => false).Should().Be("FESTA");
    }

    [Fact]
    public void MakeUnique_AppendsIncreasingNumbers()
    {
        var taken = new HashSet<string> { "FESTA", "FESTA (2)", "FESTA (3)" };
        NameSanitizer.MakeUnique("FESTA", taken.Contains).Should().Be("FESTA (4)");
        NameSanitizer.MakeUnique("FESTA", new HashSet<string> { "FESTA" }.Contains).Should().Be("FESTA (2)");
    }

    [Fact]
    public void MakeUnique_RespectsACaseInsensitiveExistsFunction()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "festa" };
        NameSanitizer.MakeUnique("FESTA", taken.Contains).Should().Be("FESTA (2)");
    }

    [Fact]
    public void MakeUnique_KeepsTheEightyCharacterLimit()
    {
        var name = new string('n', 80);
        var taken = new HashSet<string> { name };
        var unique = NameSanitizer.MakeUnique(name, taken.Contains);
        unique.Length.Should().BeLessThanOrEqualTo(80);
        unique.Should().EndWith(" (2)");
    }

    [Fact]
    public void MakeUnique_OnEverythingTaken_StillTerminates()
    {
        var unique = NameSanitizer.MakeUnique("X", _ => true); // pathological: every candidate "exists"
        unique.Should().StartWith("X (").And.NotBe("X");
    }
}

public class PathMapTests
{
    private static string Map(string original) => NameSanitizer.BuildPathMap(new[] { original })[original];

    [Theory]
    [InlineData(@"Videos\festa.avi", @"Videos\festa.avi")]
    [InlineData(@"Festa: 2009?\video*.avi", @"Festa_ 2009_\video_.avi")]
    [InlineData("Aula.", "Aula")]
    [InlineData("Aula .txt", "Aula .txt")]
    [InlineData("CON.txt", "CON_.txt")]
    [InlineData(@"NUL\x.avi", @"NUL_\x.avi")]
    [InlineData("com1", "com1_")]
    [InlineData("açaí ☂ 日本語 😀.mp4", "açaí ☂ 日本語 😀.mp4")]
    [InlineData("a/b/c.txt", @"a\b\c.txt")]
    [InlineData(@"\leading\slash.txt", @"leading\slash.txt")]
    [InlineData(@"C:\autoexec.bat", @"C_\autoexec.bat")]
    [InlineData("x.partial", "x.partial_")]
    [InlineData("x.PARTIAL", "x.PARTIAL_")]
    public void Paths_AreSanitizedSegmentBySegment(string original, string expected)
    {
        Map(original).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"..\..\evil.txt")]
    [InlineData(@"a\..\..\evil.txt")]
    [InlineData(@".\x.txt")]
    [InlineData(@"...\x.txt")]
    public void PathTraversal_IsImpossible(string original)
    {
        var mapped = Map(original);
        mapped.Split('\\').Should().NotContain(new[] { "..", ".", "", "..." });
        Path.IsPathRooted(mapped).Should().BeFalse();
    }

    [Fact]
    public void LongNames_AreCut_KeepingTheExtension()
    {
        var mapped = Map(new string('x', 400) + ".avi");
        mapped.Length.Should().BeLessThanOrEqualTo(NameSanitizer.MaxSegmentLength);
        mapped.Should().EndWith(".avi");
    }

    [Fact]
    public void NamesDifferingOnlyByCase_GetDistinctNames_TheFirstInOrdinalOrderKeepsItsName()
    {
        var map = NameSanitizer.BuildPathMap(new[] { "video.avi", "VIDEO.avi", "Video.AVI" });
        map["VIDEO.avi"].Should().Be("VIDEO.avi");
        map["Video.AVI"].Should().Be("Video (2).AVI");
        map["video.avi"].Should().Be("video (3).avi");
        map.Values.Select(v => v.ToUpperInvariant()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void FoldersDifferingOnlyByCase_AreSeparated_AndTheirChildrenFollow()
    {
        var map = NameSanitizer.BuildPathMap(new[] { @"Fotos\a.jpg", @"fotos\a.jpg", @"fotos\b.jpg" });
        map[@"Fotos\a.jpg"].Should().Be(@"Fotos\a.jpg");
        map[@"fotos\a.jpg"].Should().Be(@"fotos (2)\a.jpg");
        map[@"fotos\b.jpg"].Should().Be(@"fotos (2)\b.jpg");
    }

    [Fact]
    public void NamesThatCollideAfterSanitizing_AreDisambiguated()
    {
        var map = NameSanitizer.BuildPathMap(new[] { "a?.txt", "a_.txt", "a*.txt", "a..txt" });
        map.Values.Select(v => v.ToUpperInvariant()).Should().OnlyHaveUniqueItems();
        map.Values.Should().OnlyContain(v => !v.Contains('?') && !v.Contains('*'));
    }

    [Fact]
    public void TheMapDoesNotDependOnTheOrderOfTheInput()
    {
        var names = new[] { "Aa.txt", "aa.txt", "AA.TXT", "b?.txt", "b*.txt", "CON", "con", @"X\y.txt", @"x\Y.txt", "mediarescue-manifest.json" };
        var rnd = new Random(42);
        var reference = NameSanitizer.BuildPathMap(names);
        for (int i = 0; i < 25; i++)
        {
            var shuffled = names.OrderBy(_ => rnd.Next()).ToArray();
            NameSanitizer.BuildPathMap(shuffled).Should().BeEquivalentTo(reference);
        }
    }

    [Fact]
    public void FilesOfTheDiscNamedLikeOurOwnFiles_AreRenamed()
    {
        var map = NameSanitizer.BuildPathMap(new[] { "mediarescue-manifest.json", ".mediarescue-job.json", "LEIA-ME - ERROS.txt", @"sub\mediarescue-manifest.json" });
        map["mediarescue-manifest.json"].Should().Be("mediarescue-manifest (2).json");
        map[".mediarescue-job.json"].Should().NotBe(".mediarescue-job.json");
        map["LEIA-ME - ERROS.txt"].Should().Be("LEIA-ME - ERROS (2).txt");
        map[@"sub\mediarescue-manifest.json"].Should().Be(@"sub\mediarescue-manifest.json", "only the disc root is reserved");
    }

    [Fact]
    public void Seeding_KeepsEarlierDecisions_AndNewNamesAvoidThem()
    {
        var map = new PathMap();
        map.Seed("old.txt", "old.txt");
        map.Seed(@"Dir\Old.txt", @"Dir\Old.txt");
        map.Map("OLD.TXT").Should().Be("OLD (2).TXT", "the seeded name is taken (case-insensitively)");
        map.Map("old.txt").Should().Be("old.txt");
        map.Map(@"Dir\Old.txt").Should().Be(@"Dir\Old.txt");
    }

    [Fact]
    public void ExistingFilesInTheDestination_AreNeverTakenOver()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a.avi", "a (2).avi", @"Dir\b.avi" };
        var map = new PathMap(existing.Contains);

        map.Map("a.avi").Should().Be("a (3).avi");
        map.Map(@"Dir\b.avi").Should().Be(@"Dir\b (2).avi");
        map.Map("fresh.avi").Should().Be("fresh.avi");
    }

    [Fact]
    public void Mapping_IsMemoized_AndStable()
    {
        var map = new PathMap();
        var first = map.Map("a?.txt");
        map.Map("a?.txt").Should().Be(first);
        map.Map("a_.txt").Should().NotBe(first);
    }

    [Fact]
    public void SanitizeSegment_KeepsExtensionOnlyForFiles()
    {
        NameSanitizer.SanitizeSegment(new string('d', 300), isFile: false).Length.Should().Be(NameSanitizer.MaxSegmentLength);
        NameSanitizer.SanitizeSegment("   ").Should().Be("_");
        NameSanitizer.SanitizeSegment("..").Should().Be("_");
    }
}
