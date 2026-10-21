using DotOrbit.Core.Workspaces;

namespace DotOrbit.Core.Interchange;

public sealed record ProposalWorkspaceState(WorkspaceRevision Revision, WorkspaceWorkSnapshot Workspace,
    IReadOnlyList<TaskBinRecord> TaskBin, IReadOnlyList<ProjectBinRecord> ProjectBin)
{ public override string ToString() => "Proposal workspace state"; }
public interface IProposalWorkspaceSource { ProposalWorkspaceState Read(); }
internal interface IPlanningSnapshotIdentityVerifier
{
    bool Matches(PlanningSnapshotContent snapshot, string expectedIdentity);
}
public sealed record ProposalMarkdownValidation(bool IsValid, int NodeCount, int ContainerDepth);
public interface IProposalMarkdownValidator { ProposalMarkdownValidation Validate(string markdown, bool proposed); }
public sealed record ProposalValidationFailure(string Code, int? OperationIndex = null)
{
    public string Guidance => Code switch
    {
        "unsupported-version" => "Regenerate a version 1 proposal or update dot-orbit for a supported version.",
        "stale-source" => "The workspace changed. Review the proposal again, or share fresh workspace context before regenerating a proposal that changes existing work.",
        "source-conflict" => "Related work changed after this proposal arrived. Share fresh workspace context and regenerate it before applying these changes.",
        "missing-review-basis" => "This earlier proposal cannot be safely refreshed. Discard it, share fresh workspace context, and ask the client to submit it again.",
        "review-basis-limit" => "This proposal is too complex to retain safely for later review. Supply a smaller proposal.",
        "invalid-schema" => "Paste a complete New-work or Change proposal returned by the AI assistant. Workspace context is not itself a proposal.",
        "invalid-json" => "Paste one complete JSON object only, without Markdown fences or surrounding prose, and correct any JSON syntax errors.",
        "no-op" => "Remove this operation because it would not change the current workspace or an earlier result in this proposal.",
        "duplicate-category-name" => "Choose a different Category name because that name already exists in the workspace or earlier in this proposal.",
        "duplicate-participant-label" => "Choose a different Participant label because that label already exists in the workspace or earlier in this proposal.",
        "duplicate-operation-id" => "Give every operation a unique operationId.",
        "duplicate-local-id" => "Give every newly created record a unique localId.",
        "invalid-local-reference" => "Reference only a localId created by an earlier operation, with the expected record type.",
        "invalid-reference" => "Use only an existingId from the copied Planning snapshot, with the expected record type.",
        "invalid-category-state" => "A standalone Task must use an explicit Category; only a Task attached to a Project may inherit its Category.",
        "empty-change" => "Include at least one changed field in an edit operation.",
        "facet-conflict" => "Combine or remove conflicting operations that change the same part of one item more than once.",
        "terminal-target" => "Move the archive or Bin operation after every other operation for that item, or remove the later operation.",
        "archive-incomplete-task" => "Complete the Task before archiving it, or remove the archive operation.",
        "invalid-new-work" => "A New-work proposal may only create records and may reference only localIds created by earlier operations.",
        _ when Code.Contains("limit", StringComparison.Ordinal) => "Supply a smaller proposal and review it again.",
        _ => "Correct or regenerate the proposal and review it again.",
    };
}
public sealed record ProposalPreviewIdentity(
    string? TargetColourKey,
    string? CategoryName,
    string? CategoryColourKey,
    ProposalReference? Project,
    string? ProjectName,
    string? ProjectColourKey,
    string? ProjectCategoryName,
    string? ProjectCategoryColourKey,
    DateOnly? Date,
    bool HasCategoryOverride,
    IReadOnlyList<string> ParticipantLabels)
{
    public override string ToString() => "Proposal preview identity";
}
public sealed record ProposalPreviewEffect(
    string Kind,
    ProposalReference Target,
    string TargetLabel,
    string Before,
    string After,
    IReadOnlyList<string> MarkdownFields,
    ProposalPreviewIdentity Identity)
{
    public ProposalReference? OrderProject { get; init; }
    public override string ToString() => "Proposal preview effect";
}
public sealed class ProposalExecutionPlan
{
    private readonly string proposalDigest;
    private readonly string sourcePayloadDigest;
    private readonly IPlanningSnapshotIdentityVerifier? identityVerifier;
    private readonly IProposalWorkspaceSource? applicationSource;
    private int approvalIssued;
    internal ProposalExecutionPlan(ProposalKind kind, string? sourceSnapshotId, WorkspaceRevision sourceWorkspaceRevision,
        string proposalDigest, string sourcePayloadDigest,
        IReadOnlyList<ChangeOperation> operations, IReadOnlyList<ProposalPreviewEffect> preview,
        DateTimeOffset executionInstant, TimeZoneInfo executionTimeZone,
        IPlanningSnapshotIdentityVerifier? identityVerifier,
        IProposalWorkspaceSource? applicationSource)
    {
        Kind = kind; SourceSnapshotId = sourceSnapshotId; SourceWorkspaceRevision = sourceWorkspaceRevision;
        this.proposalDigest = proposalDigest; this.sourcePayloadDigest = sourcePayloadDigest;
        Operations = operations; Preview = preview;
        ExecutionInstant = executionInstant; ExecutionTimeZone = executionTimeZone;
        this.identityVerifier = identityVerifier;
        this.applicationSource = applicationSource;
    }
    public ProposalKind Kind { get; }
    public string? SourceSnapshotId { get; }
    public WorkspaceRevision SourceWorkspaceRevision { get; }
    public IReadOnlyList<ChangeOperation> Operations { get; }
    public IReadOnlyList<ProposalPreviewEffect> Preview { get; }
    public bool MatchesProposalDigest(string digest) => string.Equals(proposalDigest, digest, StringComparison.Ordinal);
    public bool MatchesSourcePayloadDigest(string digest) =>
        string.Equals(sourcePayloadDigest, digest, StringComparison.Ordinal);
    internal DateTimeOffset ExecutionInstant { get; }
    internal TimeZoneInfo ExecutionTimeZone { get; }
    internal bool MatchesCurrentSource(ProposalWorkspaceState current) =>
        current.Revision == SourceWorkspaceRevision
        && (Kind == ProposalKind.NewWork
            || SourceSnapshotId is { } snapshotId
            && identityVerifier?.Matches(
                PlanningSnapshotProjection.Create(current.Revision, current.Workspace),
                snapshotId) == true);
    internal ProposalApproval? TryApprove(object authority, IProposalWorkspaceSource source) =>
        ReferenceEquals(applicationSource, source)
        && (Kind == ProposalKind.NewWork || identityVerifier is not null)
        && Interlocked.CompareExchange(ref approvalIssued, 1, 0) == 0
            ? new ProposalApproval(this, authority)
            : null;
    public override string ToString() => "Proposal execution plan";
}
public sealed record ProposalPlanResult(ProposalExecutionPlan? Plan, IReadOnlyList<ProposalValidationFailure> Failures)
{ public override string ToString() => "Proposal planning result"; }
