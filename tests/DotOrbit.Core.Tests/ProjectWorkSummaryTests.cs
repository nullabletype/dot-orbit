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

    [Fact]
    public void ArchivedCompletedTasksRemainInProjectDerivation()
    {
        var archived = Task("archived", new DateOnly(2026, 9, 29)) with
        {
            ArchivedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
            ArchiveDate = new DateOnly(2026, 10, 1),
        };
        var current = Task("current", new DateOnly(2026, 9, 30));

        var summary = ProjectWorkSummary.From(Snapshot(archived, current), Project.Id);

        Assert.True(summary.IsComplete);
        Assert.Equal("Complete", summary.Status);
        Assert.Equal(2, summary.CompletedCount);
        Assert.Equal(2, summary.TaskCount);
        Assert.Equal(new DateOnly(2026, 9, 30), summary.CompletionDate);
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

    [Fact]
    public void TaskIsArchivedOnlyWhenBothCapturedArchiveValuesArePresent()
    {
        var complete = Task("task", new DateOnly(2026, 9, 29));
        var instantOnly = complete with
        {
            ArchivedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
        };
        var dateOnly = complete with { ArchiveDate = new DateOnly(2026, 10, 1) };

        Assert.False(instantOnly.IsArchived);
        Assert.False(dateOnly.IsArchived);
        Assert.True((instantOnly with { ArchiveDate = new DateOnly(2026, 10, 1) }).IsArchived);
    }

    private static WorkspaceWorkSnapshot Snapshot(params TaskRecord[] tasks) => new([], [Project], tasks);

    private static TaskRecord Task(string id, DateOnly? completionDate) => new(
        id, Project.Id, id, "", null, null, 0, 0,
        completionDate is null ? null : new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero),
        completionDate);
}

public sealed class BulkTaskArchivePolicyTests
{
    [Fact]
    public void PreviewMatchRequiresSameThresholdDateAndOrderedTaskIdentity()
    {
        var preview = new BulkTaskArchivePreview(7, new DateOnly(2026, 10, 10), ["first", "second"]);

        Assert.True(preview.Matches(new(7, new DateOnly(2026, 10, 10), ["first", "second"])));
        Assert.False(preview.Matches(new(6, new DateOnly(2026, 10, 10), ["first", "second"])));
        Assert.False(preview.Matches(new(7, new DateOnly(2026, 10, 11), ["first", "second"])));
        Assert.False(preview.Matches(new(7, new DateOnly(2026, 10, 10), ["second", "first"])));
        Assert.False(preview.Matches(null));
    }

    [Fact]
    public void EligibleTasksUsesStrictLocalCalendarBoundaryAndExcludesArchivedWork()
    {
        var active = new ProjectRecord("active", "Active", "", "home", null, 0);
        var archived = new ProjectRecord("archived", "Archived", "", "home", null, 1,
            new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 10, 9));
        var beforeCutoff = Completed("before", active.Id, new DateOnly(2026, 10, 6));
        var atCutoff = Completed("at", active.Id, new DateOnly(2026, 10, 7));
        var afterCutoff = Completed("after", null, new DateOnly(2026, 10, 8));
        var alreadyArchived = Completed("task-archived", active.Id, new DateOnly(2026, 10, 1)) with
        {
            ArchivedAt = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
            ArchiveDate = new DateOnly(2026, 10, 9),
        };
        var hiddenWithProject = Completed("project-archived", archived.Id, new DateOnly(2026, 10, 1));
        var incomplete = beforeCutoff with { Id = "incomplete", CompletedAt = null, CompletionDate = null };
        var snapshot = new WorkspaceWorkSnapshot([], [active, archived],
            [beforeCutoff, atCutoff, afterCutoff, alreadyArchived, hiddenWithProject, incomplete]);

        var eligible = BulkTaskArchivePolicy.EligibleTasks(snapshot, new DateOnly(2026, 10, 10), 3);

        Assert.Equal([beforeCutoff.Id], eligible.Select(task => task.Id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void ThresholdRejectsValuesOutsideOneThroughThirty(int days)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            BulkTaskArchivePolicy.EligibleTasks(new([], [], []), new DateOnly(2026, 10, 10), days));

        Assert.Equal("completedAgeDays", error.ParamName);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    public void ThresholdAcceptsInclusiveLimits(int days)
    {
        Assert.Empty(BulkTaskArchivePolicy.EligibleTasks(new([], [], []), new DateOnly(2026, 10, 10), days));
    }

    [Fact]
    public void ProjectIsArchivedOnlyWhenBothCapturedValuesArePresent()
    {
        var project = new ProjectRecord("project", "Project", "", "home", null, 0);
        var instant = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

        Assert.False((project with { ArchivedAt = instant }).IsArchived);
        Assert.False((project with { ArchiveDate = new DateOnly(2026, 10, 9) }).IsArchived);
        Assert.True((project with { ArchivedAt = instant, ArchiveDate = new DateOnly(2026, 10, 9) }).IsArchived);
    }

    private static TaskRecord Completed(string id, string? projectId, DateOnly completionDate) => new(
        id, projectId, id, "", projectId is null ? "home" : null, null, 0,
        projectId is null ? null : 0,
        new DateTimeOffset(completionDate.Year, completionDate.Month, completionDate.Day, 12, 0, 0, TimeSpan.Zero),
        completionDate);
}
