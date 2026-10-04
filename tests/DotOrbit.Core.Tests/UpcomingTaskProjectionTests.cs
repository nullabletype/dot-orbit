using DotOrbit.Core.Workspaces;
using Xunit;

namespace DotOrbit.Core.Tests;

public sealed class UpcomingTaskProjectionTests
{
    private static readonly DateOnly Today = new(2026, 10, 4);

    [Fact]
    public void IncludesEveryOverdueTaskAndTheInclusiveEightDayWindow()
    {
        var tasks = new[]
        {
            Task("old-overdue", Today.AddDays(-30), 5),
            Task("yesterday", Today.AddDays(-1), 4),
            Task("today", Today, 3),
            Task("tomorrow", Today.AddDays(1), 2),
            Task("boundary", Today.AddDays(7), 1),
            Task("outside", Today.AddDays(8), 0),
        };

        var projected = UpcomingTaskProjection.Create(tasks, Today);

        Assert.Equal(
            ["old-overdue", "yesterday", "today", "tomorrow", "boundary"],
            projected.Select(task => task.Id));
    }

    [Fact]
    public void OrdersByDueDateThenSharedOrderWithoutMutatingTheSource()
    {
        var laterShared = Task("later-shared", Today.AddDays(2), 9);
        var earlierShared = Task("earlier-shared", Today.AddDays(2), 2);
        var oldest = Task("oldest", Today.AddDays(-3), 50);
        var source = new[] { laterShared, oldest, earlierShared };

        var projected = UpcomingTaskProjection.Create(source, Today);

        Assert.Equal(["oldest", "earlier-shared", "later-shared"], projected.Select(task => task.Id));
        Assert.Equal(["later-shared", "oldest", "earlier-shared"], source.Select(task => task.Id));
        Assert.Equal([9L, 50L, 2L], source.Select(task => task.SharedPosition));
    }

    [Fact]
    public void ExcludesUndatedAndCompletedTasks()
    {
        var undated = Task("undated", null, 0);
        var completed = Task("completed", Today, 1) with
        {
            CompletedAt = new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.Zero),
            CompletionDate = Today,
        };

        Assert.Empty(UpcomingTaskProjection.Create([undated, completed], Today));
    }

    [Fact]
    public void SupportsTheEndOfTheDateOnlyRange()
    {
        var task = Task("last-day", DateOnly.MaxValue, 0);

        Assert.Equal(task, Assert.Single(UpcomingTaskProjection.Create([task], DateOnly.MaxValue.AddDays(-2))));
    }

    private static TaskRecord Task(string id, DateOnly? dueDate, long sharedPosition) =>
        new(id, null, id, string.Empty, "home", dueDate, sharedPosition, null);
}
