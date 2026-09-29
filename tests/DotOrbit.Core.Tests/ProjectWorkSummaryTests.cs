using DotOrbit.Core.Workspaces;
using Xunit;

namespace DotOrbit.Core.Tests;

public sealed class WorkCompletionDerivationTests
{
    private static readonly ProjectRecord Project = new("project", "Garden", "", "home", new DateOnly(2026, 9, 28), 0);

    [Fact]
    public void EmptyProjectIsNotStartedIncompleteAndHasNoCompletionDate()
    {
        var summary = ProjectWorkSummary.From(Snapshot(), Project.Id);

        Assert.Equal("Not started", summary.Status);
        Assert.Equal(0, summary.CompletedCount);
        Assert.Equal(0, summary.TaskCount);
        Assert.False(summary.IsComplete);
        Assert.Null(summary.CompletionDate);
        Assert.True(WorkDatePresentation.IsOverdue(Project, summary, new DateOnly(2026, 9, 29)));
    }

    [Fact]
    public void PartialAndCompleteProjectStatesUseLatestCapturedTaskDate()
    {
        var first = Task("first", new DateOnly(2026, 9, 27));
        var incomplete = Task("second", null);
        var partial = ProjectWorkSummary.From(Snapshot(first, incomplete), Project.Id);
        Assert.Equal("In progress", partial.Status);
        Assert.Null(partial.CompletionDate);

        var complete = ProjectWorkSummary.From(Snapshot(first, Task("second", new DateOnly(2026, 9, 29))), Project.Id);
        Assert.Equal("Complete", complete.Status);
        Assert.Equal(2, complete.CompletedCount);
        Assert.Equal(new DateOnly(2026, 9, 29), complete.CompletionDate);
        Assert.False(WorkDatePresentation.IsOverdue(Project, complete, new DateOnly(2026, 9, 30)));
    }

    [Theory]
    [InlineData(null, "No date")]
    [InlineData("2026-09-29", "Today")]
    [InlineData("2026-09-30", "Tomorrow")]
    [InlineData("2026-10-01", "1 Oct 2026")]
    public void DatePresentationDistinguishesUndatedRelativeAndCalendarDates(string? persisted, string expected)
    {
        var date = persisted is null ? (DateOnly?)null : DateOnly.Parse(persisted, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, WorkDatePresentation.Relative(date, new DateOnly(2026, 9, 29)));
    }

    [Fact]
    public void AccessibleDatePresentationAlwaysUsesFullUnambiguousDate()
    {
        Assert.Equal("Due Tuesday, 29 September 2026", WorkDatePresentation.Accessible(new DateOnly(2026, 9, 29), "Due"));
        Assert.Equal("No target", WorkDatePresentation.Accessible(null, "Target"));
    }

    [Fact]
    public void TaskIsCompleteOnlyWhenBothCapturedCompletionValuesArePresent()
    {
        var instantOnly = new TaskRecord("task", Project.Id, "Task", "", null, null, 0, 0,
            new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero), null);
        var dateOnly = instantOnly with { CompletedAt = null, CompletionDate = new DateOnly(2026, 9, 29) };

        Assert.False(instantOnly.IsComplete);
        Assert.False(dateOnly.IsComplete);
        Assert.True(Task("task", new DateOnly(2026, 9, 29)).IsComplete);
    }

    private static WorkspaceWorkSnapshot Snapshot(params TaskRecord[] tasks) => new([], [Project], tasks);

    private static TaskRecord Task(string id, DateOnly? completionDate) => new(
        id, Project.Id, id, "", null, null, 0, 0,
        completionDate is null ? null : new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero),
        completionDate);
}
