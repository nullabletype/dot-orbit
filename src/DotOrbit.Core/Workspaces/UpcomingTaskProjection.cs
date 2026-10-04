namespace DotOrbit.Core.Workspaces;

public static class UpcomingTaskProjection
{
    public static IReadOnlyList<TaskRecord> Create(IEnumerable<TaskRecord> tasks, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var lastIncludedDayNumber = Math.Min(DateOnly.MaxValue.DayNumber, today.DayNumber + 7);
        return tasks
            .Where(task => !task.IsComplete
                && !task.IsArchived
                && task.DueDate is { } dueDate
                && dueDate.DayNumber <= lastIncludedDayNumber)
            .OrderBy(task => task.DueDate)
            .ThenBy(task => task.SharedPosition)
            .ThenBy(task => task.Id, StringComparer.Ordinal)
            .ToArray();
    }
}
