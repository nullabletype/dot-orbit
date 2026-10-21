namespace DotOrbit.Core.Interchange;

public interface IProposalApplication
{
    ProposalApproval? Approve(ProposalExecutionPlan plan);

    ProposalApplyResult Apply(
        ProposalApproval approval,
        IProposalApplicationHost host,
        CancellationToken cancellationToken = default);
}

public interface IInboundProposalApplication : IProposalApplication
{
    ProposalApplyResult ApplyInbound(
        ProposalApproval approval,
        string pendingProposalReceipt,
        IProposalApplicationHost host,
        CancellationToken cancellationToken = default);
}

public interface IProposalApplicationHost
{
    bool FlushPendingInspectorWork();

    bool RefreshProjections();
}

public interface IProposalMaintenance
{
    ProposalMaintenanceResult Retry(
        IProposalApplicationHost host,
        CancellationToken cancellationToken = default);
}

public enum ProposalApplyStatus
{
    Committed,
    CommittedMaintenanceRequired,
    PendingInspectorWork,
    StaleSource,
    RecoveryPointCreationFailed,
    Cancelled,
    Failed,
    ApprovalUnavailable,
}

public enum ProposalMaintenanceStatus
{
    Completed,
    Required,
    Cancelled,
}

public sealed record ProposalApplyResult(
    ProposalApplyStatus Status,
    IProposalMaintenance? Maintenance = null)
{
    public bool Committed => Status is ProposalApplyStatus.Committed
        or ProposalApplyStatus.CommittedMaintenanceRequired;

    public override string ToString() => $"Proposal apply: {Status}.";
}

public sealed record ProposalMaintenanceResult(
    ProposalMaintenanceStatus Status,
    IProposalMaintenance? Maintenance = null)
{
    public override string ToString() => $"Proposal maintenance: {Status}.";
}

public sealed class ProposalApproval
{
    private readonly object authority;
    private readonly ProposalExecutionPlan plan;
    private int consumed;

    internal ProposalApproval(ProposalExecutionPlan plan, object authority)
    {
        this.plan = plan;
        this.authority = authority;
    }

    public void Invalidate() => Interlocked.Exchange(ref consumed, 1);

    internal bool TryConsume(object expectedAuthority, out ProposalExecutionPlan? approvedPlan)
    {
        var available = Interlocked.Exchange(ref consumed, 1) == 0
            && ReferenceEquals(authority, expectedAuthority);
        approvedPlan = available ? plan : null;
        return available;
    }

    public override string ToString() => "Proposal approval";
}
