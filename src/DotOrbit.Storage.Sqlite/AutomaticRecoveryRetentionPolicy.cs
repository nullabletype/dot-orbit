namespace DotOrbit.Storage.Sqlite;

internal readonly record struct AutomaticRecoveryPoint(
    string Path,
    DateTimeOffset CreatedAtUtc,
    long ChangeGeneration = 0);

internal static class AutomaticRecoveryRetentionPolicy
{
    internal static IReadOnlyList<AutomaticRecoveryPoint> SelectForDeletion(
        IReadOnlyCollection<AutomaticRecoveryPoint> recoveryPoints)
    {
        if (recoveryPoints.Count <= 1)
        {
            return [];
        }

        var keep = new HashSet<string>(StringComparer.Ordinal);
        KeepNewestByBucket(
            recoveryPoints,
            point => (
                point.CreatedAtUtc.UtcDateTime.Year,
                point.CreatedAtUtc.UtcDateTime.Month,
                point.CreatedAtUtc.UtcDateTime.Day,
                point.CreatedAtUtc.UtcDateTime.Hour),
            24,
            keep);
        KeepNewestByBucket(
            recoveryPoints,
            point => (
                point.CreatedAtUtc.UtcDateTime.Year,
                point.CreatedAtUtc.UtcDateTime.Month,
                point.CreatedAtUtc.UtcDateTime.Day),
            30,
            keep);
        KeepNewestByBucket(
            recoveryPoints,
            point => (
                point.CreatedAtUtc.UtcDateTime.Year,
                point.CreatedAtUtc.UtcDateTime.Month),
            12,
            keep);

        keep.Add(recoveryPoints.MaxBy(point => point.CreatedAtUtc).Path);
        return recoveryPoints
            .Where(point => !keep.Contains(point.Path))
            .OrderBy(point => point.CreatedAtUtc)
            .ToArray();
    }

    private static void KeepNewestByBucket<TKey>(
        IEnumerable<AutomaticRecoveryPoint> recoveryPoints,
        Func<AutomaticRecoveryPoint, TKey> bucketSelector,
        int bucketCount,
        HashSet<string> keep)
        where TKey : notnull
    {
        foreach (var point in recoveryPoints
                     .GroupBy(bucketSelector)
                     .Select(group => group.MaxBy(item => item.CreatedAtUtc))
                     .OrderByDescending(item => item.CreatedAtUtc)
                     .Take(bucketCount))
        {
            keep.Add(point.Path);
        }
    }
}
