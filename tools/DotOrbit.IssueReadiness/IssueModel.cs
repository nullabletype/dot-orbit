namespace DotOrbit.IssueReadiness;

internal static class ReadinessLabels
{
    public const string Blocked = "blocked";
    public const string NeedsTriage = "needs-triage";
    public const string ReadyForAgent = "ready-for-agent";

    public static readonly string[] Managed =
    [
        Blocked,
        ReadyForAgent,
    ];

    public static readonly string[] Manual =
    [
        NeedsTriage,
        "needs-info",
        "ready-for-human",
        "wontfix",
    ];
}

internal sealed record IssueSnapshot(
    int Number,
    string Body,
    IReadOnlySet<string> Labels,
    bool IsOpen);

internal sealed record IssueSpecification(
    IReadOnlyList<int> Dependencies);

internal sealed record ParseResult(
    IssueSpecification? Specification,
    string? FailureReason)
{
    public bool IsValid => Specification is not null;

    public static ParseResult Valid(IReadOnlyList<int> dependencies) =>
        new(new IssueSpecification(dependencies), null);

    public static ParseResult Invalid(string reason) => new(null, reason);
}

internal sealed record LabelDelta(bool Add, string Label)
{
    public override string ToString() => $"{(Add ? '+' : '-')}{Label}";
}

internal sealed record ReadinessPlan(
    int IssueNumber,
    string Reason,
    IReadOnlyList<LabelDelta> Deltas);
