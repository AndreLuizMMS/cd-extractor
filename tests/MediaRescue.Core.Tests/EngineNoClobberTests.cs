using System.Text.Json;
using FluentAssertions;
using MediaRescue.Core;
using MediaRescue.Core.Tests.Fakes;

namespace MediaRescue.Core.Tests;

/// <summary>
/// The engine's final rename must never replace a file it did not write (IDestinationFileSystem.Move does not overwrite),
/// while its own earlier copy from an interrupted run of the same job may be replaced.
/// </summary>
public class EngineNoClobberTests
{
    [Fact]
    public async Task AForeignFileAppearingAtTheFinalName_IsNeverOverwritten_TheCopyGetsTheNextFreeName()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 200_000);
        rig.AddFile("b.avi", 100_000);
        var users = TestData.Bytes(1234, 999);
        bool planted = false;
        rig.Dest.OnMoveStart = destination =>
        {
            // The user (or an antivirus/sync client) drops a file with the very name the engine is about to use.
            if (!planted && destination.EndsWith("a.avi", StringComparison.OrdinalIgnoreCase))
            {
                planted = true;
                rig.Dest.SeedFile(destination, users);
            }
        };

        var result = await rig.RunWithTimeout();

        planted.Should().BeTrue();
        rig.Written("a.avi").Should().Equal(users, "a file that is not ours is never replaced");
        var a = result.Files.Single(f => f.RelativePath == "a.avi");
        a.Status.Should().Be(FileStatus.Intact);
        a.WrittenPath.Should().Be("a (2).avi");
        rig.Written("a (2).avi").Should().Equal(rig.Contents["a.avi"]);
        a.Sha256.Should().Be(TestData.Sha(rig.Contents["a.avi"]));
        rig.Written("b.avi").Should().Equal(rig.Contents["b.avi"]);
        rig.Dest.PartialFiles.Should().BeEmpty();
        result.Status.Should().Be(OverallStatus.Intact);

        // The manifest tells where the file went.
        using var doc = JsonDocument.Parse(rig.Dest.ReadText(rig.DestPath("mediarescue-manifest.json")));
        doc.RootElement.GetProperty("pathMap").GetProperty("a.avi").GetString().Should().Be("a (2).avi");
    }

    [Fact]
    public async Task AFileLeftByAnInterruptedRunOfTheSameJob_IsReplacedOnResume()
    {
        var rig = new Rig();
        rig.AddFile("a.avi", 200_000);
        rig.AddFile("b.avi", 100_000);
        using var cts = new CancellationTokenSource();
        rig.Progress.OnReport = p => { if (p.FilesIntact >= 1) cts.Cancel(); };
        var first = await rig.RunWithTimeout(cts.Token);
        first.Status.Should().Be(OverallStatus.Cancelled);
        rig.Written("a.avi").Should().Equal(rig.Contents["a.avi"]);

        // Crash between the rename and the state save: the state does not know a.avi is finished, and the file is damaged.
        var statePath = rig.DestPath(".mediarescue-job.json");
        var state = rig.Dest.ReadText(statePath);
        state.Should().Contain("\"completed\": true");
        var idx = state.IndexOf("\"completed\": true", StringComparison.Ordinal);
        rig.Dest.SeedFile(statePath, System.Text.Encoding.UTF8.GetBytes(state.Remove(idx, "\"completed\": true".Length).Insert(idx, "\"completed\": false")));
        rig.Dest.SeedFile(rig.DestPath("a.avi"), new byte[rig.Contents["a.avi"].Length]);
        rig.Progress.OnReport = null;

        var second = await rig.RunWithTimeout();

        second.Status.Should().Be(OverallStatus.Intact);
        rig.Written("a.avi").Should().Equal(rig.Contents["a.avi"], "our own earlier copy is replaced by a verified one");
        second.Files.Single(f => f.RelativePath == "a.avi").WrittenPath.Should().Be("a.avi");
        rig.Dest.AllFiles.Should().NotContain(f => f.Contains("(2)"));
    }
}
