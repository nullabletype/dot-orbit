using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using DotOrbit.Core.Interchange;
using DotOrbit.Markdown;

namespace DotOrbit.Desktop.ViewModels;

internal sealed record ProposalOperationReviewRow(
    int Number,
    string Kind,
    string OperationId,
    string Detail)
{
    public override string ToString() => "Proposal operation review row";
}

internal sealed record ProposalMarkdownReviewRow(
    string Label,
    SanitisedMarkdownDocument Document)
{
    public override string ToString() => "Proposal Markdown review row";
}

internal sealed record ProposalEffectReviewRow(
    int Number,
    string Kind,
    string Target,
    string Before,
    string After,
    string OrderProject,
    bool IsExplanation,
    IReadOnlyList<ProposalMarkdownReviewRow> Markdown)
{
    public bool HasOrderProject => !string.IsNullOrEmpty(OrderProject);

    public override string ToString() => "Proposal effect review row";
}

internal sealed class AgentProposalReviewViewModel : INotifyPropertyChanged
{
    private readonly IProposalApplication _application;
    private ProposalExecutionPlan? _plan;
    private IProposalMaintenance? _maintenance;
    private ReviewState _state;
    private string _statusMessage = string.Empty;

    public AgentProposalReviewViewModel(
        ProposalExecutionPlan plan,
        IProposalApplication application)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(application);
        _plan = plan;
        _application = application;
        SourceSnapshotId = plan.SourceSnapshotId;
        SourceWorkspaceRevision = plan.SourceWorkspaceRevision.Value.ToString(CultureInfo.InvariantCulture);
        Operations = Array.AsReadOnly(plan.Operations.Select(ToOperationRow).ToArray());
        Effects = Array.AsReadOnly(plan.Preview.Select(ToEffectRow).ToArray());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ConfirmationOpened;
    public event EventHandler? ConfirmationClosed;
    public event EventHandler? ApplyFinished;

    public string SourceSnapshotId { get; }
    public string SourceWorkspaceRevision { get; }
    public IReadOnlyList<ProposalOperationReviewRow> Operations { get; }
    public IReadOnlyList<ProposalEffectReviewRow> Effects { get; }
    public int OperationCount => Operations.Count;
    public int EffectCount => Effects.Count;
    public string CountSummary =>
        $"{OperationCount} ordered operations; {EffectCount} complete direct and derived effects";
    public string ExcludedPowers { get; } =
        "Excluded powers: no direct Today or lane changes, global ordering, hidden restore, duplication, Empty Bin, permanent deletion, or global Category or Participant mutation.";
    public bool NeedsConfirmation => _state == ReviewState.Confirming;
    public bool CanRequestApply => _state == ReviewState.Ready && _plan is not null;
    public bool CanConfirmApply => _state == ReviewState.Confirming && _plan is not null;
    public bool CanCancelConfirmation => _state == ReviewState.Confirming;
    public bool CanClose => _state != ReviewState.Applying;
    public bool CanRetryMaintenance => _maintenance is not null && _state == ReviewState.Committed;

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (string.Equals(value, _statusMessage, StringComparison.Ordinal))
            {
                return;
            }

            _statusMessage = value;
            Notify();
            Notify(nameof(HasStatusMessage));
        }
    }

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    public void RequestApply()
    {
        if (!CanRequestApply)
        {
            return;
        }

        _state = ReviewState.Confirming;
        NotifyState();
        ConfirmationOpened?.Invoke(this, EventArgs.Empty);
    }

    public void CancelConfirmation()
    {
        if (_state != ReviewState.Confirming)
        {
            return;
        }

        _state = ReviewState.Ready;
        StatusMessage = "Apply cancelled. Nothing was changed.";
        NotifyState();
        ConfirmationClosed?.Invoke(this, EventArgs.Empty);
    }

    public async Task ConfirmApplyAsync(IProposalApplicationHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_state != ReviewState.Confirming || _plan is not { } plan)
        {
            return;
        }

        _plan = null;
        _state = ReviewState.Applying;
        StatusMessage = "Applying the approved proposal after encrypted recovery validation…";
        NotifyState();
        ConfirmationClosed?.Invoke(this, EventArgs.Empty);

        ProposalApproval? approval;
        try
        {
            approval = _application.Approve(plan);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or ObjectDisposedException)
        {
            CompletePreCommitFailure(
                "Approval is no longer available. Nothing was applied; review the proposal again.");
            return;
        }

        if (approval is null)
        {
            CompletePreCommitFailure(
                "Approval is no longer available. Nothing was applied; review the proposal again.");
            return;
        }

        ProposalApplyResult result;
        try
        {
            result = await Task.Run(() => _application.Apply(approval, host));
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or ObjectDisposedException)
        {
            CompletePreCommitFailure(
                "The proposal could not be applied. Nothing was changed; review it again before retrying.");
            return;
        }

        _maintenance = result.Maintenance;
        _state = result.Committed ? ReviewState.Committed : ReviewState.Consumed;
        StatusMessage = result.Status switch
        {
            ProposalApplyStatus.Committed =>
                "Proposal applied atomically after a validated encrypted recovery point was created.",
            ProposalApplyStatus.CommittedMaintenanceRequired =>
                "Proposal committed, but refresh or recovery maintenance is still required. Do not apply it again; retry maintenance only.",
            ProposalApplyStatus.PendingInspectorWork =>
                "Pending edits could not be saved. Nothing was applied; review the proposal again after resolving them.",
            ProposalApplyStatus.StaleSource =>
                "The proposal is stale. Nothing was applied; copy a fresh Planning snapshot and regenerate it.",
            ProposalApplyStatus.RecoveryPointCreationFailed =>
                "A validated encrypted recovery point could not be created. Nothing was applied.",
            ProposalApplyStatus.Cancelled =>
                "Apply was cancelled before commit. Nothing was applied; review the proposal again.",
            ProposalApplyStatus.ApprovalUnavailable =>
                "Approval is no longer available. Nothing was applied; review the proposal again.",
            _ =>
                "The proposal could not be applied. Nothing was changed; review it again before retrying.",
        };
        NotifyState();
        ApplyFinished?.Invoke(this, EventArgs.Empty);
    }

    public async Task RetryMaintenanceAsync(IProposalApplicationHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_maintenance is not { } maintenance || _state != ReviewState.Committed)
        {
            return;
        }

        ProposalMaintenanceResult result;
        try
        {
            result = await Task.Run(() => maintenance.Retry(host));
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or ObjectDisposedException)
        {
            StatusMessage =
                "The proposal remains committed, but maintenance still needs attention. No proposal operation was replayed.";
            return;
        }

        _maintenance = result.Maintenance;
        StatusMessage = result.Status == ProposalMaintenanceStatus.Completed
            ? "Committed proposal maintenance completed. No proposal operation was replayed."
            : "The proposal remains committed, but maintenance still needs attention. No proposal operation was replayed.";
        Notify(nameof(CanRetryMaintenance));
        ApplyFinished?.Invoke(this, EventArgs.Empty);
    }

    public void Discard()
    {
        if (_state == ReviewState.Applying)
        {
            return;
        }

        _plan = null;
        if (_state is ReviewState.Ready or ReviewState.Confirming)
        {
            _state = ReviewState.Consumed;
            StatusMessage = "Proposal review invalidated. Review the current proposal again before Apply.";
        }

        NotifyState();
    }

    private void CompletePreCommitFailure(string status)
    {
        _state = ReviewState.Consumed;
        StatusMessage = status;
        NotifyState();
        ApplyFinished?.Invoke(this, EventArgs.Empty);
    }

    internal static ProposalOperationReviewRow ToOperationRow(ChangeOperation operation, int index)
    {
        var detail = operation switch
        {
            CreateCategoryOperation item => $"New Category · local:{item.LocalId}",
            CreateParticipantOperation item => $"New Participant · local:{item.LocalId}",
            CreateProjectOperation item => $"New Project · local:{item.LocalId}",
            CreateTaskOperation item => $"New Task · local:{item.LocalId}",
            EditProjectOperation item => Reference(item.Project),
            SetProjectCategoryOperation item => Reference(item.Project),
            ArchiveProjectOperation item => Reference(item.Project),
            MoveProjectToBinOperation item => Reference(item.Project),
            EditTaskOperation item => Reference(item.Task),
            SetTaskCategoryOperation item => Reference(item.Task),
            ReplaceTaskParticipantsOperation item => Reference(item.Task),
            AttachTaskOperation item => $"{Reference(item.Task)} → {Reference(item.Project)}",
            DetachTaskOperation item => Reference(item.Task),
            MoveTaskOperation item => $"{Reference(item.Task)} → {Reference(item.Project)}",
            CompleteTaskOperation item => Reference(item.Task),
            ReopenTaskOperation item => Reference(item.Task),
            ReorderProjectTaskOperation item => Reference(item.Task),
            ArchiveTaskOperation item => Reference(item.Task),
            MoveTaskToBinOperation item => Reference(item.Task),
            _ => "Unsupported operation",
        };
        return new(index + 1, OperationName(operation), operation.OperationId, detail);
    }

    private static ProposalEffectReviewRow ToEffectRow(ProposalPreviewEffect effect, int index)
    {
        var markdown = effect.MarkdownFields
            .Select((field, markdownIndex) => new ProposalMarkdownReviewRow(
                effect.Kind == "explanation"
                    ? "Inert explanation — not applied"
                    : $"Sanitised Markdown field {markdownIndex + 1}",
                SanitisedMarkdownRenderer.Render(field)))
            .ToArray();
        return new(
            index + 1,
            EffectName(effect.Kind),
            $"{effect.TargetLabel} · {Reference(effect.Target)}",
            effect.Before,
            effect.After,
            effect.OrderProject is { } orderProject ? Reference(orderProject) : string.Empty,
            effect.Kind == "explanation",
            Array.AsReadOnly(markdown));
    }

    private static string Reference(ProposalReference reference) =>
        reference.LocalId is { } local
            ? $"local:{local}"
            : $"existing:{reference.ExistingId}";

    private static string OperationName(ChangeOperation operation) => operation switch
    {
        CreateCategoryOperation => "Create Category",
        CreateParticipantOperation => "Create Participant",
        CreateProjectOperation => "Create Project",
        EditProjectOperation => "Edit Project",
        SetProjectCategoryOperation => "Set Project Category",
        CreateTaskOperation => "Create Task",
        EditTaskOperation => "Edit Task",
        SetTaskCategoryOperation => "Set Task Category",
        ReplaceTaskParticipantsOperation => "Replace Task Participants",
        AttachTaskOperation => "Attach Task",
        DetachTaskOperation => "Detach Task",
        MoveTaskOperation => "Move Task",
        CompleteTaskOperation => "Complete Task",
        ReopenTaskOperation => "Reopen Task",
        ReorderProjectTaskOperation => "Reorder Project Task",
        ArchiveProjectOperation => "Archive Project",
        ArchiveTaskOperation => "Archive Task",
        MoveProjectToBinOperation => "Move Project to Bin",
        MoveTaskToBinOperation => "Move Task to Bin",
        _ => "Unsupported operation",
    };

    internal static string EffectName(string kind) => kind switch
    {
        "create-category" => "Create Category",
        "create-participant" => "Create Participant",
        "create-project" => "Create Project",
        "create-task" => "Create Task",
        "project-content" => "Project content",
        "task-content" => "Task content",
        "project-category" => "Project Category",
        "inherited-task-category" => "Inherited Task Category",
        "archived-child-category" => "Archived child Category",
        "binned-child-category" => "Binned child Category",
        "task-category" => "Task Category",
        "participants" => "Task Participants",
        "relationship" => "Project relationship",
        "completion" => "Completion state and date",
        "today-removal" => "Today removal",
        "project-archive" => "Project archive",
        "project-bin" => "Project moved to Bin",
        "task-archive" => "Task archive",
        "task-bin" => "Task moved to Bin",
        "aggregate-child-bin" => "Project child moved to Bin",
        "parent-archive-visibility" => "Archived Project child visibility",
        "already-binned-child" => "Already-binned child",
        "project-status" => "Derived Project status",
        "final-project-order" => "Project Task order",
        "final-project-order-row" => "Project Task order row",
        "final-shared-order" => "Shared Task order",
        "final-shared-order-row" => "Shared Task order row",
        "explanation" => "Inert explanation",
        _ => kind,
    };

    private void NotifyState()
    {
        Notify(nameof(NeedsConfirmation));
        Notify(nameof(CanRequestApply));
        Notify(nameof(CanConfirmApply));
        Notify(nameof(CanCancelConfirmation));
        Notify(nameof(CanClose));
        Notify(nameof(CanRetryMaintenance));
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private enum ReviewState
    {
        Ready,
        Confirming,
        Applying,
        Consumed,
        Committed,
    }
}
