using DotOrbit.Core.Workspaces;
using Xunit;

namespace DotOrbit.Core.Tests;

public sealed class WorkTitleTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \t\n")]
    public void BlankTitlesAreRejected(string title) =>
        Assert.Throws<ArgumentException>(() => WorkTitle.Normalize(title));

    [Fact]
    public void TitlesTrimOnlyOutsideWhitespace() =>
        Assert.Equal("Plan  the café", WorkTitle.Normalize("  Plan  the café\n"));
}

public sealed class ProjectWorkSummaryTests
{
    [Fact]
    public void EmptyProjectHasNoCompletionDateAndZeroCounts()
    {
        var summary = ProjectWorkSummary.From(new([], [], []), "project");
        Assert.Equal("Not started", summary.Status);
        Assert.Equal(0, summary.CompletedCount);
        Assert.Equal(0, summary.TaskCount);
        Assert.Null(summary.CompletionDate);
    }

    [Fact]
    public void CapturedTasksCountOnlyTheirProjectAndRemainIncomplete()
    {
        var snapshot = new WorkspaceWorkSnapshot([], [], [
            new("a", "project", "A", "", null, null, 0, 0),
            new("b", "project", "B", "", null, null, -1, 1),
            new("c", "other", "C", "", null, null, -2, 0)]);
        var summary = ProjectWorkSummary.From(snapshot, "project");
        Assert.Equal(2, summary.TaskCount);
        Assert.Equal(0, summary.CompletedCount);
        Assert.Equal("Not started", summary.Status);
        Assert.Null(summary.CompletionDate);
    }
}
