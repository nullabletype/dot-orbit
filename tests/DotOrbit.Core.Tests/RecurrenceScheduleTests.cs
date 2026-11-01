using DotOrbit.Core.Workspaces;
using Xunit;

namespace DotOrbit.Core.Tests;

public sealed class RecurrenceScheduleTests
{
    [Theory]
    [InlineData(RecurrenceCadence.Daily, 19, 20)]
    [InlineData(RecurrenceCadence.Weekly, 15, 22)]
    public void LatestAndNextRetainCalendarCadence(RecurrenceCadence cadence, int latestDay, int nextDay)
    {
        var first = new DateOnly(2026, 1, 1);
        var latest = RecurrenceSchedule.LatestOccurrence(first, cadence, new(2026, 1, 19));
        Assert.Equal(new(2026, 1, latestDay), latest);
        Assert.Equal(new(2026, 1, nextDay), RecurrenceSchedule.NextOccurrence(first, cadence, latest));
    }

    [Fact]
    public void MonthlyClampingPreservesOriginalDayAcrossLeapAndShortMonths()
    {
        var first = new DateOnly(2028, 1, 31);
        var february = RecurrenceSchedule.NextOccurrence(first, RecurrenceCadence.Monthly, first);
        Assert.Equal(new(2028, 2, 29), february);
        Assert.Equal(new(2028, 3, 31), RecurrenceSchedule.NextOccurrence(first, RecurrenceCadence.Monthly, february!.Value));
        Assert.Equal(new(2028, 4, 30), RecurrenceSchedule.LatestOccurrence(first, RecurrenceCadence.Monthly, new(2028, 5, 30)));
        Assert.Equal(new(2029, 2, 28), RecurrenceSchedule.LatestOccurrence(first, RecurrenceCadence.Monthly, new(2029, 3, 1)));
    }

    [Theory]
    [InlineData(RecurrenceCadence.Daily)]
    [InlineData(RecurrenceCadence.Weekly)]
    [InlineData(RecurrenceCadence.Monthly)]
    public void MaximumDateExhaustsWithoutOverflow(RecurrenceCadence cadence)
    {
        Assert.Null(RecurrenceSchedule.NextOccurrence(DateOnly.MaxValue, cadence, DateOnly.MaxValue));
        Assert.Equal(DateOnly.MaxValue, RecurrenceSchedule.LatestOccurrence(DateOnly.MaxValue, cadence, DateOnly.MaxValue));
    }

    [Fact]
    public void DueRuleSupportsNoneZeroPositiveAndExactRepresentableBoundary()
    {
        var date = new DateOnly(2026, 11, 1);
        Assert.Null(RecurrenceSchedule.DueDate(date, null));
        Assert.Equal(date, RecurrenceSchedule.DueDate(date, 0));
        Assert.Equal(new(2026, 11, 6), RecurrenceSchedule.DueDate(date, 5));
        var maximum = DateOnly.MaxValue.DayNumber - date.DayNumber;
        Assert.Equal(DateOnly.MaxValue, RecurrenceSchedule.DueDate(date, maximum));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecurrenceSchedule.DueDate(date, maximum + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecurrenceSchedule.DueDate(date, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecurrenceSchedule.DueDate(date, int.MaxValue));
    }

    [Fact]
    public void InvalidCadenceAndBeforeFirstDateFailClosed()
    {
        var date = new DateOnly(2026, 1, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => RecurrenceSchedule.LatestOccurrence(date, (RecurrenceCadence)9, date));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecurrenceSchedule.NextOccurrence(date, (RecurrenceCadence)9, date));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecurrenceSchedule.LatestOccurrence(date, RecurrenceCadence.Monthly, date.AddDays(-1)));
    }
}
