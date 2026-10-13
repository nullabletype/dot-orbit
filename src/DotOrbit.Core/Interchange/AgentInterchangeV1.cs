namespace DotOrbit.Core.Interchange;

public static class AgentInterchangeV1
{
    public const string PlanningSnapshotFormat = "dot-orbit-planning-snapshot";
    public const string ChangeProposalFormat = "dot-orbit-change-proposal";
    public const int SchemaVersion = 1;
    public const int MaximumOutputBytes = 16 * 1024 * 1024;
    public const int MaximumIdentifierScalars = 128;
    public const int MaximumTitleScalars = 1024;
    public const int MaximumDescriptionScalars = 65_536;
    public const int MaximumOperations = 512;

    public const string PlanningSnapshotSchemaId =
        "https://raw.githubusercontent.com/nullabletype/dot-orbit/main/schemas/interchange/v1/planning-snapshot.schema.json";

    public const string ChangeProposalSchemaId =
        "https://raw.githubusercontent.com/nullabletype/dot-orbit/main/schemas/interchange/v1/change-proposal.schema.json";
}

public readonly record struct WorkspaceRevision
{
    public WorkspaceRevision(long value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        Value = value;
    }

    public long Value { get; }

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class UnicodeScalarOrdinalComparer : IComparer<string>
{
    public static UnicodeScalarOrdinalComparer Instance { get; } = new();

    private UnicodeScalarOrdinalComparer()
    {
    }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var left = x.EnumerateRunes().GetEnumerator();
        var right = y.EnumerateRunes().GetEnumerator();
        while (true)
        {
            var hasLeft = left.MoveNext();
            var hasRight = right.MoveNext();
            if (!hasLeft || !hasRight)
                return hasLeft ? 1 : hasRight ? -1 : 0;
            var comparison = left.Current.Value.CompareTo(right.Current.Value);
            if (comparison != 0) return comparison;
        }
    }
}
