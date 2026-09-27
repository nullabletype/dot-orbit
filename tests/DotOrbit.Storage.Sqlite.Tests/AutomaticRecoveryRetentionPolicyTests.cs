using DotOrbit.Storage.Sqlite;
using Xunit;

namespace DotOrbit.Storage.Sqlite.Tests;

public sealed class AutomaticRecoveryRetentionPolicyTests
{
    [Fact]
    public void TwentyFiveHourlyBucketsDeleteOnlyTheOldestUnrepresentedPoint()
    {
        var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var points = Enumerable.Range(0, 25)
            .Select(index => Point($"point-{index}", start.AddHours(index)))
            .ToArray();

        var deletion = AutomaticRecoveryRetentionPolicy.SelectForDeletion(points);

        Assert.Equal("point-0", Assert.Single(deletion).Path);
    }

    [Fact]
    public void TierUnionKeepsNewestPointInEachOfTheTwelveNewestUtcMonths()
    {
        var points = new List<AutomaticRecoveryPoint>();
        var firstMonth = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var month = 0; month < 13; month++)
        {
            var monthStart = firstMonth.AddMonths(month);
            for (var day = 0; day < 3; day++)
            {
                points.Add(Point(
                    $"month-{month}-day-{day}",
                    monthStart.AddDays(day)));
            }
        }

        var deletion = AutomaticRecoveryRetentionPolicy.SelectForDeletion(points);
        var retained = points.Except(deletion).ToArray();

        Assert.DoesNotContain(retained, point => point.CreatedAtUtc.Month == 1 && point.CreatedAtUtc.Year == 2025);
        Assert.Equal(
            12,
            retained
                .Select(point => (point.CreatedAtUtc.Year, point.CreatedAtUtc.Month))
                .Distinct()
                .Count());
        Assert.Contains(retained, point => point.Path == "month-1-day-2");
        Assert.DoesNotContain(retained, point => point.Path == "month-1-day-0");
    }

    [Fact]
    public void ThirtyDailyBucketsKeepTheBoundaryAndDeleteTheNextOlderDay()
    {
        var start = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var points = Enumerable.Range(0, 40)
            .Select(index => Point($"day-{index}", start.AddDays(index)))
            .ToArray();

        var deletion = AutomaticRecoveryRetentionPolicy.SelectForDeletion(points);
        var retained = points.Except(deletion).ToArray();

        Assert.Contains(retained, point => point.Path == "day-10");
        Assert.DoesNotContain(retained, point => point.Path == "day-9");
    }

    [Fact]
    public void EquivalentInstantsUseUtcBucketsRegardlessOfOffset()
    {
        var recentStart = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);
        var instant = new DateTimeOffset(2026, 1, 1, 0, 30, 0, TimeSpan.Zero);
        var equivalentPair = new[]
        {
            Point("positive-offset", instant.ToOffset(TimeSpan.FromHours(14))),
            Point("negative-offset", instant.ToOffset(TimeSpan.FromHours(-12))),
        };
        var points = Enumerable.Range(0, 30)
            .Select(index => Point($"recent-{index}", recentStart.AddDays(index)))
            .Concat(equivalentPair)
            .ToArray();

        var deletion = AutomaticRecoveryRetentionPolicy.SelectForDeletion(points);
        var retainedEquivalentPoints = equivalentPair.Except(deletion).ToArray();

        Assert.Single(retainedEquivalentPoints);
    }

    [Fact]
    public void MultiplePointsInOneBucketKeepOnlyTheNewestRepresentativeWhenOutsideOtherTiers()
    {
        var newest = new DateTimeOffset(2026, 9, 1, 12, 59, 0, TimeSpan.Zero);
        var points = Enumerable.Range(0, 40)
            .Select(index => Point($"recent-{index}", newest.AddDays(-index)))
            .Concat(
            [
                Point("old-hour-first", new DateTimeOffset(2026, 7, 1, 10, 1, 0, TimeSpan.Zero)),
                Point("old-hour-newest", new DateTimeOffset(2026, 7, 1, 10, 59, 0, TimeSpan.Zero)),
            ])
            .ToArray();

        var deletion = AutomaticRecoveryRetentionPolicy.SelectForDeletion(points);

        Assert.Contains(deletion, point => point.Path == "old-hour-first");
        Assert.Contains(deletion, point => point.Path == "old-hour-newest");
        Assert.DoesNotContain(deletion, point => point.Path == "recent-0");
    }

    [Fact]
    public void NeverDeletesTheOnlyValidatedPoint()
    {
        var only = Point("only", new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var deletion = AutomaticRecoveryRetentionPolicy.SelectForDeletion([only]);

        Assert.Empty(deletion);
    }

    private static AutomaticRecoveryPoint Point(string path, DateTimeOffset createdAtUtc) =>
        new(path, createdAtUtc);
}
