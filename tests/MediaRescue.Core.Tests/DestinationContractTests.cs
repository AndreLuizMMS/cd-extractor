using FluentAssertions;
using MediaRescue.Core;
using MediaRescue.Core.Tests.Fakes;

namespace MediaRescue.Core.Tests;

/// <summary>Contract details of <see cref="IDestinationFileSystem"/> that older implementations (and fakes) rely on.</summary>
public class DestinationContractTests
{
    [Fact]
    public void DeleteDirectoryIfEmpty_IsADefaultMember_SoImplementationsWrittenBeforeItStillCompile_AndItDoesNothing()
    {
        // FakeDestination was written before DeleteDirectoryIfEmpty existed and does not implement it:
        // calling it through the interface must use the default (no-op) and must not touch anything.
        var fs = new FakeDestination(@"E:\Backup");
        IDestinationFileSystem dest = fs;
        dest.CreateDirectory(@"E:\Backup\Disco vazio");

        var act = () => dest.DeleteDirectoryIfEmpty(@"E:\Backup\Disco vazio");

        act.Should().NotThrow();
        dest.DirectoryExists(@"E:\Backup\Disco vazio").Should().BeTrue("the default implementation never deletes anything");
    }
}
