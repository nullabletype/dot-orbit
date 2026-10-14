using DotOrbit.Core.Workspaces;

namespace DotOrbit.Core.Interchange;

public sealed record ProposalWorkspaceState(WorkspaceRevision Revision, WorkspaceWorkSnapshot Workspace,
    IReadOnlyList<TaskBinRecord> TaskBin, IReadOnlyList<ProjectBinRecord> ProjectBin)
{ public override string ToString() => "Proposal workspace state"; }
public interface IProposalWorkspaceSource { ProposalWorkspaceState Read(); }
public sealed record ProposalMarkdownValidation(bool IsValid, int NodeCount, int ContainerDepth);
public interface IProposalMarkdownValidator { ProposalMarkdownValidation Validate(string markdown); }
public sealed record ProposalValidationFailure(string Code, int? OperationIndex = null)
{
    public string Guidance => Code switch
    {
        "unsupported-version" => "Regenerate a version 1 proposal or update dot-orbit for a supported version.",
        "stale-source" => "Copy a new planning snapshot and regenerate the proposal.",
        _ when Code.Contains("limit", StringComparison.Ordinal) => "Supply a smaller proposal and review it again.",
        _ => "Correct or regenerate the proposal and review it again.",
    };
}
public sealed record ProposalPreviewEffect(string Kind, ProposalReference Target, string TargetLabel, string Before, string After, IReadOnlyList<string> MarkdownFields)
{ public override string ToString() => "Proposal preview effect"; }
public sealed class ProposalExecutionPlan
{
    private readonly string proposalDigest;
    internal ProposalExecutionPlan(string sourceSnapshotId, WorkspaceRevision sourceWorkspaceRevision,
        string proposalDigest, IReadOnlyList<ChangeOperation> operations, IReadOnlyList<ProposalPreviewEffect> preview)
    {
        SourceSnapshotId = sourceSnapshotId; SourceWorkspaceRevision = sourceWorkspaceRevision;
        this.proposalDigest = proposalDigest; Operations = operations; Preview = preview;
    }
    public string SourceSnapshotId { get; }
    public WorkspaceRevision SourceWorkspaceRevision { get; }
    public IReadOnlyList<ChangeOperation> Operations { get; }
    public IReadOnlyList<ProposalPreviewEffect> Preview { get; }
    public bool MatchesProposalDigest(string digest) => string.Equals(proposalDigest, digest, StringComparison.Ordinal);
    public override string ToString() => "Proposal execution plan";
}
public sealed record ProposalPlanResult(ProposalExecutionPlan? Plan, IReadOnlyList<ProposalValidationFailure> Failures)
{ public override string ToString() => "Proposal planning result"; }
