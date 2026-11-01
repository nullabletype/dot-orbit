namespace DotOrbit.Core.Workspaces;

public interface IRecurringTasks
{
    IReadOnlyList<RecurringTaskDefinition> ReadRecurringTasks();
    RecurringTaskDefinition CreateRecurringTask(RecurringTaskDraft draft);
    RecurringTaskDefinition UpdateRecurringTask(string id, RecurringTaskDraft draft);
    void DeleteRecurringTask(string id);
    IReadOnlyList<TaskRecord> ProcessDueRecurringTasks();
}

public enum RecurrenceCadence { Daily, Weekly, Monthly }

public sealed record RecurringTaskDraft(
    string Title, string Description, string? ProjectId, string? ExplicitCategoryId,
    IReadOnlyList<string> ParticipantIds, RecurrenceCadence Cadence,
    DateOnly FirstOccurrenceDate, int? DueDateOffsetDays, bool IsEnabled);

public sealed record RecurringTaskDefinition(
    string Id, string Title, string Description, string? ProjectId, string? ExplicitCategoryId,
    IReadOnlyList<string> ParticipantIds, RecurrenceCadence Cadence,
    DateOnly FirstOccurrenceDate, int? DueDateOffsetDays, bool IsEnabled,
    DateOnly? NextOccurrenceDate, string? PauseReason);

/// <summary>Date-only arithmetic retaining the original monthly day anchor.</summary>
public static class RecurrenceSchedule
{
    public static DateOnly LatestOccurrence(DateOnly first, RecurrenceCadence cadence, DateOnly on)
    {
        if (!Enum.IsDefined(cadence)) throw new ArgumentOutOfRangeException(nameof(cadence));
        ArgumentOutOfRangeException.ThrowIfLessThan(on, first);
        if (cadence != RecurrenceCadence.Monthly)
        {
            var days = cadence == RecurrenceCadence.Daily ? 1 : 7;
            return first.AddDays((on.DayNumber - first.DayNumber) / days * days);
        }
        var candidate = new DateOnly(on.Year, on.Month, Math.Min(first.Day, DateTime.DaysInMonth(on.Year, on.Month)));
        if (candidate <= on) return candidate;
        var previous = on.AddMonths(-1);
        return new DateOnly(previous.Year, previous.Month, Math.Min(first.Day, DateTime.DaysInMonth(previous.Year, previous.Month)));
    }

    public static DateOnly? NextOccurrence(DateOnly first, RecurrenceCadence cadence, DateOnly occurrence)
    {
        if (!Enum.IsDefined(cadence)) throw new ArgumentOutOfRangeException(nameof(cadence));
        if (cadence == RecurrenceCadence.Monthly)
        {
            if (occurrence.Year == 9999 && occurrence.Month == 12) return null;
            var month = new DateOnly(occurrence.Year, occurrence.Month, 1).AddMonths(1);
            return new DateOnly(month.Year, month.Month, Math.Min(first.Day, DateTime.DaysInMonth(month.Year, month.Month)));
        }
        var days = cadence == RecurrenceCadence.Daily ? 1 : 7;
        return DateOnly.MaxValue.DayNumber - occurrence.DayNumber < days ? null : occurrence.AddDays(days);
    }

    public static DateOnly? DueDate(DateOnly additionDate, int? offsetDays)
    {
        if (offsetDays is null) return null;
        if (offsetDays < 0 || offsetDays > DateOnly.MaxValue.DayNumber - additionDate.DayNumber)
            throw new ArgumentOutOfRangeException(nameof(offsetDays), "The due-date offset must produce a representable calendar date.");
        return additionDate.AddDays(offsetDays.Value);
    }
}
