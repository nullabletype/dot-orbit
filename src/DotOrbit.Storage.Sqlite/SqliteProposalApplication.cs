using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;

namespace DotOrbit.Storage.Sqlite;

internal sealed class SqliteProposalApplication(
    WorkspaceTransactionCoordinator transactions,
    SqliteWorkspaceWork work,
    IProposalWorkspaceSource source,
    SqliteAgentIntegrationStore agentIntegration) : IInboundProposalApplication
{
    private readonly object authority = new();
    private readonly WorkspaceTransactionCoordinator coordinator = transactions;
    private bool closed;

    public ProposalApproval? Approve(ProposalExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return closed ? null : plan.TryApprove(authority, source);
    }

    public ProposalApplyResult Apply(
        ProposalApproval approval,
        IProposalApplicationHost host,
        CancellationToken cancellationToken = default) =>
        ApplyCore(approval, host, null, cancellationToken);

    public ProposalApplyResult ApplyInbound(
        ProposalApproval approval,
        string pendingProposalReceipt,
        IProposalApplicationHost host,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pendingProposalReceipt);
        return ApplyCore(approval, host, pendingProposalReceipt, cancellationToken);
    }

    private ProposalApplyResult ApplyCore(
        ProposalApproval approval,
        IProposalApplicationHost host,
        string? pendingProposalReceipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(approval);
        ArgumentNullException.ThrowIfNull(host);
        if (!approval.TryConsume(authority, out var plan) || closed || plan is null)
        {
            return new(ProposalApplyStatus.ApprovalUnavailable);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new(ProposalApplyStatus.Cancelled);
        }

        bool ready;
        try
        {
            ready = host.FlushPendingInspectorWork();
        }
        catch (InvalidOperationException)
        {
            return new(ProposalApplyStatus.PendingInspectorWork);
        }
        catch (WorkspaceWorkException)
        {
            return new(ProposalApplyStatus.PendingInspectorWork);
        }

        if (!ready)
        {
            return new(ProposalApplyStatus.PendingInspectorWork);
        }

        ProposalStorageApplyStatus storageStatus;
        try
        {
            storageStatus = coordinator.ExecuteProposal(
                plan,
                () => ApplyOperations(plan, cancellationToken),
                pendingProposalReceipt is null
                    ? null
                    : () => ConsumePendingProposal(plan, pendingProposalReceipt),
                cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            return new(ProposalApplyStatus.ApprovalUnavailable);
        }

        if (storageStatus is not (ProposalStorageApplyStatus.Committed
            or ProposalStorageApplyStatus.CommittedMaintenanceRequired))
        {
            return new(storageStatus switch
            {
                ProposalStorageApplyStatus.StaleSource => ProposalApplyStatus.StaleSource,
                ProposalStorageApplyStatus.RecoveryPointCreationFailed =>
                    ProposalApplyStatus.RecoveryPointCreationFailed,
                ProposalStorageApplyStatus.Cancelled => ProposalApplyStatus.Cancelled,
                _ => ProposalApplyStatus.Failed,
            });
        }

        var refreshPending = !TryRefresh(host);
        var recoveryPending = storageStatus == ProposalStorageApplyStatus.CommittedMaintenanceRequired;
        if (!refreshPending && !recoveryPending)
        {
            return new(ProposalApplyStatus.Committed);
        }

        var maintenance = new Maintenance(this, refreshPending, recoveryPending);
        return new(ProposalApplyStatus.CommittedMaintenanceRequired, maintenance);
    }

    private bool ConsumePendingProposal(ProposalExecutionPlan plan, string receipt)
    {
        var pending = agentIntegration.FindPendingProposal(receipt);
        return pending is not null
               && plan.MatchesSourcePayloadDigest(pending.PayloadDigest)
               && agentIntegration.RemovePendingProposal(receipt);
    }

    internal void Close() => closed = true;

    private void ApplyOperations(
        ProposalExecutionPlan plan,
        CancellationToken cancellationToken)
    {
        var localIds = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < plan.Operations.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyOperation(plan.Operations[index], localIds);
            coordinator.ProposalOperationCompleted(index);
        }
    }

    private void ApplyOperation(
        ChangeOperation operation,
        Dictionary<string, string> localIds)
    {
        switch (operation)
        {
            case CreateCategoryOperation item:
                localIds.Add(item.LocalId, work.CreateCategory(item.Name).Id);
                break;
            case CreateParticipantOperation item:
                localIds.Add(item.LocalId, work.CreateParticipant(item.Label).Id);
                break;
            case CreateProjectOperation item:
                localIds.Add(item.LocalId, work.CreateProject(
                    item.Title,
                    item.Description,
                    Resolve(item.Category, localIds),
                    item.TargetDate).Id);
                break;
            case EditProjectOperation item:
                ApplyProjectContent(item, localIds);
                break;
            case SetProjectCategoryOperation item:
                ApplyProjectCategory(item, localIds);
                break;
            case CreateTaskOperation item:
                localIds.Add(item.LocalId, CreateTask(item, localIds).Id);
                break;
            case EditTaskOperation item:
                ApplyTaskContent(item, localIds);
                break;
            case SetTaskCategoryOperation item:
                ApplyTaskCategory(item, localIds);
                break;
            case ReplaceTaskParticipantsOperation item:
                ApplyTaskParticipants(item, localIds);
                break;
            case AttachTaskOperation item:
                work.AttachTask(
                    Resolve(item.Task, localIds),
                    Resolve(item.Project, localIds),
                    AttachmentChoice(item.CategoryChoice));
                break;
            case DetachTaskOperation item:
                work.DetachTask(Resolve(item.Task, localIds));
                break;
            case MoveTaskOperation item:
                work.AttachTask(
                    Resolve(item.Task, localIds),
                    Resolve(item.Project, localIds),
                    AttachmentChoice(item.CategoryChoice));
                break;
            case CompleteTaskOperation item:
                work.CompleteTask(Resolve(item.Task, localIds));
                break;
            case ReopenTaskOperation item:
                work.ReopenTask(Resolve(item.Task, localIds));
                break;
            case ReorderProjectTaskOperation item:
                ApplyProjectTaskOrder(item, localIds);
                break;
            case ArchiveProjectOperation item:
                work.ArchiveProject(Resolve(item.Project, localIds));
                break;
            case ArchiveTaskOperation item:
                work.ArchiveTask(Resolve(item.Task, localIds));
                break;
            case MoveProjectToBinOperation item:
                work.MoveProjectToBin(Resolve(item.Project, localIds));
                break;
            case MoveTaskToBinOperation item:
                work.MoveTaskToBin(Resolve(item.Task, localIds));
                break;
            default:
                throw new InvalidOperationException("The proposal operation is unsupported.");
        }
    }

    private void ApplyProjectContent(
        EditProjectOperation operation,
        IReadOnlyDictionary<string, string> localIds)
    {
        var id = Resolve(operation.Project, localIds);
        var project = work.Read().Projects.Single(item => item.Id == id);
        work.UpdateProject(
            id,
            operation.Changes.Title.IsSpecified
                ? operation.Changes.Title.Value ?? throw new InvalidOperationException()
                : project.Title,
            operation.Changes.Description.IsSpecified
                ? operation.Changes.Description.Value ?? throw new InvalidOperationException()
                : project.Description,
            project.CategoryId,
            operation.Changes.TargetDate.IsSpecified
                ? operation.Changes.TargetDate.Value
                : project.TargetDate);
    }

    private void ApplyProjectCategory(
        SetProjectCategoryOperation operation,
        IReadOnlyDictionary<string, string> localIds)
    {
        var id = Resolve(operation.Project, localIds);
        var project = work.Read().Projects.Single(item => item.Id == id);
        work.UpdateProject(
            id,
            project.Title,
            project.Description,
            Resolve(operation.Category, localIds),
            project.TargetDate);
    }

    private TaskRecord CreateTask(
        CreateTaskOperation operation,
        IReadOnlyDictionary<string, string> localIds)
    {
        var projectId = operation.Project is { } project
            ? Resolve(project, localIds)
            : null;
        var categoryId = operation.Category.Mode == ProposedTaskCategoryMode.Explicit
            ? Resolve(operation.Category.Category
                ?? throw new InvalidOperationException(), localIds)
            : null;
        return work.CreateTaskDraft(
            projectId,
            operation.Title,
            operation.Description,
            categoryId,
            operation.DueDate,
            ParticipantChange(operation.Participants, localIds));
    }

    private void ApplyTaskContent(
        EditTaskOperation operation,
        IReadOnlyDictionary<string, string> localIds)
    {
        var id = Resolve(operation.Task, localIds);
        var task = work.Read().Tasks.Single(item => item.Id == id);
        work.UpdateTask(
            id,
            operation.Changes.Title.IsSpecified
                ? operation.Changes.Title.Value ?? throw new InvalidOperationException()
                : task.Title,
            operation.Changes.Description.IsSpecified
                ? operation.Changes.Description.Value ?? throw new InvalidOperationException()
                : task.Description,
            task.ExplicitCategoryId,
            operation.Changes.DueDate.IsSpecified
                ? operation.Changes.DueDate.Value
                : task.DueDate);
    }

    private void ApplyTaskCategory(
        SetTaskCategoryOperation operation,
        IReadOnlyDictionary<string, string> localIds)
    {
        var id = Resolve(operation.Task, localIds);
        var task = work.Read().Tasks.Single(item => item.Id == id);
        var categoryId = operation.Category.Mode == ProposedTaskCategoryMode.Explicit
            ? Resolve(operation.Category.Category
                ?? throw new InvalidOperationException(), localIds)
            : null;
        work.UpdateTask(
            id,
            task.Title,
            task.Description,
            categoryId,
            task.DueDate);
    }

    private void ApplyTaskParticipants(
        ReplaceTaskParticipantsOperation operation,
        IReadOnlyDictionary<string, string> localIds)
    {
        var id = Resolve(operation.Task, localIds);
        var task = work.Read().Tasks.Single(item => item.Id == id);
        work.UpdateTask(
            id,
            task.Title,
            task.Description,
            task.ExplicitCategoryId,
            task.DueDate,
            ParticipantChange(operation.Participants, localIds));
    }

    private void ApplyProjectTaskOrder(
        ReorderProjectTaskOperation operation,
        IReadOnlyDictionary<string, string> localIds)
    {
        var taskId = Resolve(operation.Task, localIds);
        var task = work.Read().Tasks.Single(item => item.Id == taskId);
        var projectId = task.ProjectId ?? throw new InvalidOperationException();
        ProposalReference? anchor = operation.Placement.Anchor is { } reference
            ? ProposalReference.Existing(Resolve(reference, localIds))
            : null;
        work.MoveTaskInIncludedProjectOrder(
            projectId,
            taskId,
            operation.Placement with { Anchor = anchor });
    }

    private static ParticipantDraftChange ParticipantChange(
        IEnumerable<ProposalReference> participants,
        IReadOnlyDictionary<string, string> localIds) => new(
        participants
            .Select(participant => Resolve(participant, localIds))
            .Order(UnicodeScalarOrdinalComparer.Instance)
            .ToArray(),
        []);

    private static TaskAttachmentCategoryChoice? AttachmentChoice(
        AttachmentCategoryChoice? choice) => choice switch
        {
            null => null,
            AttachmentCategoryChoice.AdoptProjectCategory =>
                TaskAttachmentCategoryChoice.AdoptProjectCategory,
            AttachmentCategoryChoice.PreserveAsOverride =>
                TaskAttachmentCategoryChoice.PreserveEffectiveCategory,
            _ => throw new ArgumentOutOfRangeException(nameof(choice)),
        };

    private static string Resolve(
        ProposalReference reference,
        IReadOnlyDictionary<string, string> localIds) =>
        reference.ExistingId
        ?? localIds[reference.LocalId ?? throw new InvalidOperationException()];

    private static bool TryRefresh(IProposalApplicationHost host)
    {
        try
        {
            return host.RefreshProjections();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class Maintenance(
        SqliteProposalApplication application,
        bool refreshPending,
        bool recoveryPending) : IProposalMaintenance
    {
        public ProposalMaintenanceResult Retry(
            IProposalApplicationHost host,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(host);
            if (cancellationToken.IsCancellationRequested)
            {
                return new(ProposalMaintenanceStatus.Cancelled, this);
            }

            var refreshStillPending = refreshPending && !TryRefresh(host);
            var recoveryStillPending = recoveryPending;
            if (recoveryPending)
            {
                try
                {
                    recoveryStillPending = !application.coordinator.RetryProposalRecoveryMaintenance();
                }
                catch (InvalidOperationException)
                {
                    recoveryStillPending = true;
                }
            }

            if (!refreshStillPending && !recoveryStillPending)
            {
                return new(ProposalMaintenanceStatus.Completed);
            }

            var maintenance = new Maintenance(
                application,
                refreshStillPending,
                recoveryStillPending);
            return new(ProposalMaintenanceStatus.Required, maintenance);
        }
    }
}
