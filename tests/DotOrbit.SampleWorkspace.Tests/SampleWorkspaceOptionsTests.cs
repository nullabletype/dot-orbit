using DotOrbit.Storage.Sqlite;
using Xunit;

namespace DotOrbit.SampleWorkspace.Tests;

public sealed class SampleWorkspaceOptionsTests
{
    [Fact]
    public void OptionsUseRepositoryLocalDefaultsAndCurrentDate()
    {
        using var repository = TemporaryDirectory.CreateRepository();
        var currentDate = new DateOnly(2030, 4, 5);

        var options = SampleWorkspaceOptions.Parse([], repository.Path, currentDate);

        Assert.Equal(
            System.IO.Path.Combine(repository.Path, "artifacts", "sample-workspace", "workspace.orb"),
            options.OutputPath);
        Assert.Equal(currentDate, options.AnchorDate);
    }

    [Fact]
    public void OptionsAcceptCustomOutputAndAnchorDate()
    {
        using var repository = TemporaryDirectory.CreateRepository();

        var options = SampleWorkspaceOptions.Parse(
            ["--output", "local-data/review.db", "--anchor-date", "2031-02-03"],
            repository.Path,
            new DateOnly(2030, 4, 5));

        Assert.Equal(
            System.IO.Path.Combine(repository.Path, "local-data", "review.db"),
            options.OutputPath);
        Assert.Equal(new DateOnly(2031, 2, 3), options.AnchorDate);
    }

    [Fact]
    public void OptionsAcceptApplicationDefaultWorkspace()
    {
        using var repository = TemporaryDirectory.CreateRepository();

        var options = SampleWorkspaceOptions.Parse(
            ["--default-workspace"],
            repository.Path,
            new DateOnly(2030, 4, 5));

        Assert.Equal(WorkspacePathDefaults.GetDefaultWorkspacePath(), options.OutputPath);
    }

    [Theory]
    [InlineData("--unknown")]
    [InlineData("--output")]
    [InlineData("--anchor-date")]
    [InlineData("--anchor-date|03-02-2031")]
    [InlineData("--anchor-date|2031-02-03|--anchor-date|2031-02-04")]
    [InlineData("--output|first.db|--output|second.db")]
    [InlineData("--output|")]
    [InlineData("--default-workspace|--default-workspace")]
    [InlineData("--default-workspace|--output|workspace.db")]
    public void OptionsRejectInvalidArguments(string serializedArguments)
    {
        using var repository = TemporaryDirectory.CreateRepository();
        var arguments = serializedArguments.Split('|');

        var error = Assert.Throws<SampleWorkspaceException>(() =>
            SampleWorkspaceOptions.Parse(arguments, repository.Path, new DateOnly(2030, 4, 5)));

        Assert.True(error.Reason is "invalid-arguments" or "invalid-anchor-date" or "invalid-output-path" or "conflicting-output-options");
    }
}
