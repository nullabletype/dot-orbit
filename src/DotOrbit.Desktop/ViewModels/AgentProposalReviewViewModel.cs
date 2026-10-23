using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.Views;
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
    string TargetLabel,
    string TechnicalReference,
    string Before,
    string After,
    string OrderProject,
    bool IsDirectReviewItem,
    bool IsExplanation,
    WorkType? TargetWorkType,
    ProposalPreviewIdentity Identity,
    IReadOnlyList<ProposalMarkdownReviewRow> Markdown)
{
    public bool HasOrderProject => !string.IsNullOrEmpty(OrderProject);
    public bool IsCreation => Kind.StartsWith("Add ", StringComparison.Ordinal);
    public bool HasBefore => !string.IsNullOrEmpty(Before);
    public string TargetColourKey => Identity.TargetColourKey ?? IdentityColourPalette.DefaultKey;
    public string CategoryName => Identity.CategoryName ?? string.Empty;
    public string CategoryColourKey => Identity.CategoryColourKey ?? IdentityColourPalette.DefaultKey;
    public ProposalReference? ProjectReference => Identity.Project;
    public string ProjectName => Identity.ProjectName ?? string.Empty;
    public string ProjectColourKey => Identity.ProjectColourKey ?? IdentityColourPalette.DefaultKey;
    public string ProjectCategoryName => Identity.ProjectCategoryName ?? string.Empty;
    public string ProjectCategoryColourKey => Identity.ProjectCategoryColourKey ?? IdentityColourPalette.DefaultKey;
    public bool HasCategoryOverride => Identity.HasCategoryOverride;
    public string ParticipantsText => string.Join(", ", Identity.ParticipantLabels);
    public bool HasParticipants => Identity.ParticipantLabels.Count > 0;
    public string SupportingIdentityAccessibleName => $"New {Kind.Replace("Add ", string.Empty, StringComparison.Ordinal)} {TargetLabel}";

    public override string ToString() => "Proposal effect review row";
}

internal sealed record ProposalCreatedTaskReviewRow(
    ProposalEffectReviewRow Creation,
    ProposalEffectReviewRow IdentityEffect,
    string DateText,
    IReadOnlyList<ProposalEffectReviewRow> Changes)
{
    public string TargetLabel => IdentityEffect.TargetLabel;
    public ProposalReference? ProjectReference => IdentityEffect.ProjectReference;
    public IReadOnlyList<ProposalMarkdownReviewRow> Markdown => Creation.Markdown;
    public string CategoryName => IdentityEffect.CategoryName;
    public string CategoryColourKey => IdentityEffect.CategoryColourKey;
    public string ProjectName => IdentityEffect.ProjectName;
    public string ProjectColourKey => IdentityEffect.ProjectColourKey;
    public bool HasCategoryOverride => IdentityEffect.HasCategoryOverride;
    public string ParticipantsText => IdentityEffect.ParticipantsText;
    public bool IsStandalone => string.IsNullOrEmpty(ProjectName);
    public bool HasParticipants => IdentityEffect.HasParticipants;
    public bool HasChanges => Changes.Count > 0;
    public string CategoryRelationshipText => IsStandalone
        ? $"Category {CategoryName}"
        : HasCategoryOverride
            ? $"Category override {CategoryName}"
            : $"inherits Category {CategoryName}";
    public string DetailText => HasParticipants
        ? $"{DateText} · People: {ParticipantsText}"
        : DateText;
    public string AccessibleName => IsStandalone
        ? $"New standalone Task {TargetLabel}, {CategoryRelationshipText}, {DateText}{ParticipantsAccessibleText()}"
        : $"New Task {TargetLabel}, Project {ProjectName}, {CategoryRelationshipText}, {DateText}{ParticipantsAccessibleText()}";

    private string ParticipantsAccessibleText() => HasParticipants
        ? $", Participants {ParticipantsText}"
        : ", no Participants";

    public override string ToString() => "Created Task proposal review row";
}

internal sealed record ProposalCreatedProjectReviewGroup(
    ProposalEffectReviewRow Creation,
    ProposalEffectReviewRow IdentityEffect,
    string DateText,
    IReadOnlyList<ProposalCreatedTaskReviewRow> Tasks,
    IReadOnlyList<ProposalEffectReviewRow> Changes)
{
    public string TargetLabel => IdentityEffect.TargetLabel;
    public string ProjectColourKey => IdentityEffect.TargetColourKey;
    public string CategoryName => IdentityEffect.CategoryName;
    public string CategoryColourKey => IdentityEffect.CategoryColourKey;
    public IReadOnlyList<ProposalMarkdownReviewRow> Markdown => Creation.Markdown;
    public int TaskCount => Tasks.Count;
    public bool HasTasks => TaskCount > 0;
    public bool HasChanges => Changes.Count > 0;
    public string TaskCountText => $"{TaskCount} {Plural(TaskCount, "Task", "Tasks")}";
    public string DetailText => $"{DateText} · {TaskCountText}";
    public string AccessibleName =>
        $"New Project {TargetLabel}, Category {CategoryName}, {DateText}, containing {TaskCountText}";

    private static string Plural(int count, string singular, string plural) => count == 1 ? singular : plural;
    public override string ToString() => "Created Project proposal review group";
}

internal sealed record ProposalExistingProjectTaskReviewGroup(
    string ProjectName,
    string ProjectColourKey,
    string CategoryName,
    string CategoryColourKey,
    IReadOnlyList<ProposalCreatedTaskReviewRow> Tasks)
{
    public int TaskCount => Tasks.Count;
    public string TaskCountText => $"{TaskCount} {Plural(TaskCount, "new Task", "new Tasks")}";
    public string AccessibleName =>
        $"Existing Project {ProjectName}, Category {CategoryName}, containing {TaskCountText}";

    private static string Plural(int count, string singular, string plural) => count == 1 ? singular : plural;
    public override string ToString() => "Existing Project proposal Task group";
}

internal sealed record ProposalChangedItemReviewGroup(
    ProposalEffectReviewRow IdentityEffect,
    WorkType WorkType,
    IReadOnlyList<ProposalEffectReviewRow> Changes)
{
    public string TargetLabel => IdentityEffect.TargetLabel;
    public string TargetColourKey => IdentityEffect.TargetColourKey;
    public string CategoryName => IdentityEffect.CategoryName;
    public string CategoryColourKey => IdentityEffect.CategoryColourKey;
    public string ProjectName => IdentityEffect.ProjectName;
    public string ProjectColourKey => IdentityEffect.ProjectColourKey;
    public bool HasCategoryOverride => IdentityEffect.HasCategoryOverride;
    public bool IsProject => WorkType == WorkType.Project;
    public bool IsTask => WorkType == WorkType.Task;
    public string CategoryRelationshipText => HasCategoryOverride
        ? $"Category override: {CategoryName}"
        : string.Empty;
    public int ChangeCount => Changes.Count;
    public string ChangeCountText => $"{ChangeCount} direct {Plural(ChangeCount, "change", "changes")}";
    public string WorkTypeText => WorkType == WorkType.Project ? "Project" : "Task";
    public string AccessibleName => IsProject
        ? $"Existing Project {TargetLabel}, Category {CategoryName}, {ChangeCountText}"
        : string.IsNullOrEmpty(ProjectName)
            ? $"Existing standalone Task {TargetLabel}, Category {CategoryName}, {ChangeCountText}"
            : $"Existing Task {TargetLabel}, Project {ProjectName}, "
                + (HasCategoryOverride ? "Category override " : "inherits Category ")
                + $"{CategoryName}, {ChangeCountText}";

    private static string Plural(int count, string singular, string plural) => count == 1 ? singular : plural;
    public override string ToString() => "Changed proposal item review group";
}

internal sealed class AgentProposalReviewViewModel : INotifyPropertyChanged
{
    private readonly IProposalApplication _application;
    private readonly string? _pendingProposalReceipt;
    private readonly ProposalKind _kind;
    private ProposalExecutionPlan? _plan;
    private IProposalMaintenance? _maintenance;
    private ReviewState _state;
    private string _statusMessage = string.Empty;

    public AgentProposalReviewViewModel(
        ProposalExecutionPlan plan,
        IProposalApplication application,
        string? pendingProposalReceipt = null,
        ProposalReviewSource? source = null,
        bool proposalWasRefreshed = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(application);
        _plan = plan;
        _application = application;
        _pendingProposalReceipt = pendingProposalReceipt;
        source ??= ProposalReviewSource.TextImport;
        SourceLabel = source.Label;
        SourceDetail = source.Detail;
        ProposalWasRefreshed = proposalWasRefreshed;
        _kind = plan.Kind;
        SourceSnapshotId = plan.SourceSnapshotId ?? string.Empty;
        SourceWorkspaceRevision = plan.SourceWorkspaceRevision.Value.ToString(CultureInfo.InvariantCulture);
        Operations = Array.AsReadOnly(plan.Operations.Select(ToOperationRow).ToArray());
        var preview = plan.Preview
            .Select((effect, index) => (Effect: effect, Row: ToEffectRow(effect, index)))
            .ToArray();
        Effects = Array.AsReadOnly(preview.Select(item => item.Row).ToArray());
        ReviewEffects = Array.AsReadOnly(preview
            .Where(item => item.Row.IsDirectReviewItem)
            .Select(item => item.Row)
            .ToArray());
        var directByReference = ReviewEffects
            .GroupBy(effect => effect.TechnicalReference, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var createdReferences = ReviewEffects
            .Where(effect => effect.IsCreation)
            .Select(effect => effect.TechnicalReference)
            .ToHashSet(StringComparer.Ordinal);
        var createdTasks = plan.Operations
            .OfType<CreateTaskOperation>()
            .Select(operation => CreateTaskReviewRow(
                directByReference[Reference(ProposalReference.Local(operation.LocalId))]))
            .ToArray();
        NewProjectGroups = Array.AsReadOnly(plan.Operations
            .OfType<CreateProjectOperation>()
            .Select(operation =>
            {
                var effects = directByReference[Reference(ProposalReference.Local(operation.LocalId))];
                var creation = CreationEffect(effects, "Add project");
                var identityEffect = effects[^1];
                var tasks = createdTasks
                    .Where(task => string.Equals(
                        task.ProjectReference?.LocalId,
                        operation.LocalId,
                        StringComparison.Ordinal))
                    .ToArray();
                return new ProposalCreatedProjectReviewGroup(
                    creation,
                    identityEffect,
                    DateText(identityEffect.Identity.Date, "Target", "No target date"),
                    Array.AsReadOnly(tasks),
                    DirectChanges(effects));
            })
            .ToArray());
        NewTasksByExistingProject = Array.AsReadOnly(createdTasks
            .Where(task => task.ProjectReference?.ExistingId is not null)
            .GroupBy(
                task => task.ProjectReference!.Value.ExistingId!,
                StringComparer.Ordinal)
            .Select(group => new ProposalExistingProjectTaskReviewGroup(
                group.Select(task => task.ProjectName).FirstOrDefault(name => !string.IsNullOrEmpty(name))
                    ?? "Existing Project",
                group.First().ProjectColourKey,
                group.First().IdentityEffect.ProjectCategoryName,
                group.First().IdentityEffect.ProjectCategoryColourKey,
                Array.AsReadOnly(group.ToArray())))
            .ToArray());
        NewStandaloneTasks = Array.AsReadOnly(createdTasks
            .Where(task => task.ProjectReference is null)
            .ToArray());
        NewCategories = Array.AsReadOnly(ReviewEffects
            .Where(effect => effect.Kind == "Add category")
            .ToArray());
        NewParticipants = Array.AsReadOnly(ReviewEffects
            .Where(effect => effect.Kind == "Add person")
            .ToArray());
        ChangedItems = Array.AsReadOnly(ReviewEffects
            .Where(effect => !effect.IsCreation && !createdReferences.Contains(effect.TechnicalReference))
            .GroupBy(effect => effect.TechnicalReference, StringComparer.Ordinal)
            .Select(group => new ProposalChangedItemReviewGroup(
                group.Last(),
                group.First().TargetWorkType ?? throw new InvalidOperationException("A changed work item must have a work type."),
                Array.AsReadOnly(group.ToArray())))
            .ToArray());
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ApplyFinished;

    public string SourceSnapshotId { get; }
    public string SourceWorkspaceRevision { get; }
    public string SourceLabel { get; }
    public string SourceDetail { get; }
    public bool ProposalWasRefreshed { get; }
    public string RefreshedProposalNotice { get; } =
        "The workspace changed after this proposal arrived. The work it depends on is unchanged, so this preview was recalculated against the current workspace. Check it again before applying.";
    public IReadOnlyList<ProposalOperationReviewRow> Operations { get; }
    public IReadOnlyList<ProposalEffectReviewRow> Effects { get; }
    public IReadOnlyList<ProposalEffectReviewRow> ReviewEffects { get; }
    public IReadOnlyList<ProposalCreatedProjectReviewGroup> NewProjectGroups { get; }
    public IReadOnlyList<ProposalExistingProjectTaskReviewGroup> NewTasksByExistingProject { get; }
    public IReadOnlyList<ProposalCreatedTaskReviewRow> NewStandaloneTasks { get; }
    public IReadOnlyList<ProposalEffectReviewRow> NewCategories { get; }
    public IReadOnlyList<ProposalEffectReviewRow> NewParticipants { get; }
    public IReadOnlyList<ProposalChangedItemReviewGroup> ChangedItems { get; }
    public int OperationCount => Operations.Count;
    public int EffectCount => Effects.Count;
    public bool HasNewProjectGroups => NewProjectGroups.Count > 0;
    public bool HasNewTasksByExistingProject => NewTasksByExistingProject.Count > 0;
    public bool HasNewStandaloneTasks => NewStandaloneTasks.Count > 0;
    public bool HasNewCategories => NewCategories.Count > 0;
    public bool HasNewParticipants => NewParticipants.Count > 0;
    public bool HasSupportingIdentities => HasNewCategories || HasNewParticipants;
    public bool HasBothSupportingIdentityTypes => HasNewCategories && HasNewParticipants;
    public bool HasChangedItems => ChangedItems.Count > 0;
    public bool HasCreatedItems => HasNewProjectGroups || HasNewTasksByExistingProject
        || HasNewStandaloneTasks || HasSupportingIdentities;
    public bool HasMixedReview => HasCreatedItems && HasChangedItems;
    public bool HasSourceDetails => _kind == ProposalKind.WorkspaceChange;
    public string ProposalKindLabel => _kind == ProposalKind.NewWork || HasCreatedItems && !HasChangedItems
        ? "Adds new work"
        : HasMixedReview
            ? "Adds and updates work"
            : "Updates existing work";
    public string ProposalKindSummary => _kind == ProposalKind.NewWork
        ? $"{CreatedItemsSummary()}. No existing item is referenced or edited directly, and no workspace context was required."
        : HasMixedReview
            ? $"{CreatedItemsSummary()} and {AffectedExistingItemsSummary()}. Check both sections below; dot-orbit will make sure the reviewed workspace state has not changed before Apply."
            : HasCreatedItems
                ? $"{CreatedItemsSummary()}. Check the workspace context shown below; dot-orbit will make sure the reviewed workspace state has not changed before Apply."
                : $"{AffectedExistingItemsSummary()}. Check each item below; dot-orbit will make sure the reviewed workspace state has not changed before Apply.";
    public string ReviewHeading => HasMixedReview
        ? "What will be added and changed"
        : HasCreatedItems
            ? "What will be added"
            : "What will change";
    public string ReviewHelpText => _kind == ProposalKind.NewWork
        ? "Only the categories, people, projects, and tasks this proposal creates are listed."
        : HasMixedReview
            ? "New work is separated from the existing work this proposal directly changes."
            : HasCreatedItems
                ? "New work is shown with the existing Project or Category context it uses."
                : "Only work this proposal directly edits, moves, completes, archives, or removes is listed.";
    public string ReviewListAccessibleName => HasMixedReview
        ? "New work to be added and existing work to be changed"
        : HasCreatedItems
            ? "New work to be added"
            : "Work directly changed by this proposal";
    public bool HasImpliedChanges => Effects.Any(effect => !effect.IsDirectReviewItem && !effect.IsExplanation);
    public string ImpliedChangesNote { get; } =
        "dot-orbit will handle routine knock-on changes automatically, including where items appear and any inherited details or project totals.";
    public string ApplyButtonText => _kind == ProposalKind.NewWork
        ? "Add new work"
        : "Apply changes";
    public string ApplyButtonAccessibleName => _kind == ProposalKind.NewWork
        ? "Create a data backup and add all shown new work"
        : "Create a data backup and apply all shown changes";
    public string TechnicalCountSummary =>
        $"{OperationCount} proposal {Plural(OperationCount, "instruction", "instructions")} and {EffectCount} total {Plural(EffectCount, "effect", "effects")} were validated.";
    public string ExcludedPowers { get; } =
        "Excluded powers: no direct Today or lane changes, global ordering, hidden restore, duplication, Empty Bin, permanent deletion, or global Category or Participant mutation.";
    public bool CanApply => _state == ReviewState.Ready && _plan is not null;
    public bool CanClose => _state != ReviewState.Applying;
    public bool CanRetryMaintenance => _maintenance is not null && _state == ReviewState.Committed;
    internal bool ProposalWasCommitted => _state == ReviewState.Committed;

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

    public async Task ApplyAsync(IProposalApplicationHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (_state != ReviewState.Ready || _plan is not { } plan)
        {
            return;
        }

        _plan = null;
        _state = ReviewState.Applying;
        StatusMessage = _kind == ProposalKind.NewWork
            ? "Creating a data backup and importing the new data…"
            : "Creating a data backup and applying the changes…";
        NotifyState();

        ProposalApproval? approval;
        try
        {
            approval = _application.Approve(plan);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or ObjectDisposedException)
        {
            CompletePreCommitFailure(
                "This review has expired, so nothing was changed. Review the proposal again.");
            return;
        }

        if (approval is null)
        {
            CompletePreCommitFailure(
                "This review has expired, so nothing was changed. Review the proposal again.");
            return;
        }

        ProposalApplyResult result;
        try
        {
            result = await Task.Run(() => _pendingProposalReceipt is null
                ? _application.Apply(approval, host)
                : _application is IInboundProposalApplication inbound
                    ? inbound.ApplyInbound(approval, _pendingProposalReceipt, host)
                    : new ProposalApplyResult(ProposalApplyStatus.Failed));
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or ObjectDisposedException)
        {
            CompletePreCommitFailure(
                "We couldn't make the changes. Nothing was changed. Review the proposal and try again.");
            return;
        }

        _maintenance = result.Maintenance;
        _state = result.Committed ? ReviewState.Committed : ReviewState.Consumed;
        StatusMessage = result.Status switch
        {
            ProposalApplyStatus.Committed when _kind == ProposalKind.NewWork =>
                "Data backup created and new data imported successfully.",
            ProposalApplyStatus.Committed =>
                "Data backup created and changes applied successfully.",
            ProposalApplyStatus.CommittedMaintenanceRequired when _kind == ProposalKind.NewWork =>
                "The new data was imported, but dot-orbit still needs to finish updating. Select Retry maintenance.",
            ProposalApplyStatus.CommittedMaintenanceRequired =>
                "The changes were applied, but dot-orbit still needs to finish updating. Select Retry maintenance.",
            ProposalApplyStatus.PendingInspectorWork =>
                "Your current edits couldn't be saved, so nothing was changed. Check them and try again.",
            ProposalApplyStatus.StaleSource when _kind == ProposalKind.NewWork =>
                "The workspace changed after this review, so nothing was imported. Review the proposal again.",
            ProposalApplyStatus.StaleSource =>
                "The workspace changed after this review, so nothing was changed. Share the latest workspace and try again.",
            ProposalApplyStatus.RecoveryPointCreationFailed when _kind == ProposalKind.NewWork =>
                "A data backup couldn't be created, so nothing was imported.",
            ProposalApplyStatus.RecoveryPointCreationFailed =>
                "A data backup couldn't be created, so nothing was changed.",
            ProposalApplyStatus.Cancelled =>
                "The update was cancelled, so nothing was changed. Review the proposal and try again.",
            ProposalApplyStatus.ApprovalUnavailable =>
                "This review has expired, so nothing was changed. Review the proposal again.",
            _ =>
                "We couldn't make the changes. Nothing was changed. Review the proposal and try again.",
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
            StatusMessage = _kind == ProposalKind.NewWork
                ? "The new data is already imported, but dot-orbit couldn't finish updating. Select Retry maintenance to try again."
                : "The changes are already applied, but dot-orbit couldn't finish updating. Select Retry maintenance to try again.";
            return;
        }

        _maintenance = result.Maintenance;
        StatusMessage = result.Status == ProposalMaintenanceStatus.Completed
            ? "dot-orbit finished updating successfully."
            : _kind == ProposalKind.NewWork
                ? "The new data is already imported, but dot-orbit couldn't finish updating. Select Retry maintenance to try again."
                : "The changes are already applied, but dot-orbit couldn't finish updating. Select Retry maintenance to try again.";
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
        if (_state == ReviewState.Ready)
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
                MarkdownLabel(effect.Kind, markdownIndex, effect.MarkdownFields.Count),
                SanitisedMarkdownRenderer.Render(field)))
            .ToArray();
        return new(
            index + 1,
            EffectName(effect.Kind),
            effect.TargetLabel,
            Reference(effect.Target),
            effect.Before,
            HumanAfter(effect.Kind, effect.After),
            effect.OrderProject is { } orderProject ? Reference(orderProject) : string.Empty,
            IsDirectReviewEffect(effect.Kind),
            effect.Kind == "explanation",
            EffectWorkType(effect.Kind),
            effect.Identity,
            Array.AsReadOnly(markdown));
    }

    private static ProposalEffectReviewRow CreationEffect(
        ProposalEffectReviewRow[] effects,
        string kind) => effects.Single(effect => effect.Kind == kind);

    private static ProposalCreatedTaskReviewRow CreateTaskReviewRow(
        ProposalEffectReviewRow[] effects)
    {
        var creation = CreationEffect(effects, "Add task");
        var identityEffect = effects[^1];
        return new(
            creation,
            identityEffect,
            DateText(identityEffect.Identity.Date, "Due", "No due date"),
            DirectChanges(effects));
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<ProposalEffectReviewRow> DirectChanges(
        ProposalEffectReviewRow[] effects) => Array.AsReadOnly(effects
            .Where(effect => !effect.IsCreation)
            .ToArray());

    private static string DateText(DateOnly? date, string label, string empty) =>
        date is { } value
            ? $"{label} {value.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}"
            : empty;

    private static WorkType? EffectWorkType(string kind) => kind switch
    {
        "create-category" => WorkType.Category,
        "create-project" or "project-content" or "project-category" or "project-archive" or "project-bin"
            => WorkType.Project,
        "create-task" or "task-content" or "task-category" or "participants" or "relationship"
            or "task-reorder" or "completion" or "task-archive" or "task-bin" => WorkType.Task,
        _ => null,
    };

    private string CreatedItemsSummary()
    {
        var counts = new[]
        {
            (Count: _plan?.Operations.Count(operation => operation is CreateTaskOperation) ?? Operations.Count(operation => operation.Kind == "Create Task"), Singular: "task", Plural: "tasks"),
            (Count: _plan?.Operations.Count(operation => operation is CreateProjectOperation) ?? Operations.Count(operation => operation.Kind == "Create Project"), Singular: "project", Plural: "projects"),
            (Count: _plan?.Operations.Count(operation => operation is CreateCategoryOperation) ?? Operations.Count(operation => operation.Kind == "Create Category"), Singular: "category", Plural: "categories"),
            (Count: _plan?.Operations.Count(operation => operation is CreateParticipantOperation) ?? Operations.Count(operation => operation.Kind == "Create Participant"), Singular: "person", Plural: "people"),
        };
        var parts = counts
            .Where(item => item.Count > 0)
            .Select(item => $"{item.Count} {Plural(item.Count, item.Singular, item.Plural)}")
            .ToArray();
        return parts.Length == 0 ? "Creates new work" : "Creates " + string.Join(", ", parts);
    }

    private string AffectedExistingItemsSummary()
    {
        var affectedItemCount = ChangedItems.Count;
        return affectedItemCount == 0
            ? "No workspace items will change"
            : $"Changes {affectedItemCount} {Plural(affectedItemCount, "item", "items")}";
    }

    private static string MarkdownLabel(string effectKind, int index, int count) =>
        effectKind == "explanation"
            ? "Why this was suggested — not applied"
            : count == 1
                ? "Description"
                : index == 0
                    ? "Current description"
                    : "Proposed description";

    private static bool IsDirectReviewEffect(string kind) => kind is
        "create-category" or
        "create-participant" or
        "create-project" or
        "create-task" or
        "project-content" or
        "task-content" or
        "project-category" or
        "task-category" or
        "participants" or
        "relationship" or
        "task-reorder" or
        "completion" or
        "project-archive" or
        "project-bin" or
        "task-archive" or
        "task-bin";

    private static string HumanAfter(string kind, string after) => kind switch
    {
        "create-category" => after.Replace(
            "; appended to Category order; colour assigned by application",
            string.Empty,
            StringComparison.Ordinal),
        "create-project" => after.Replace(
            "; appended to Project order; colour assigned by application",
            string.Empty,
            StringComparison.Ordinal),
        _ => after,
    };

    private static string Plural(int count, string singular, string plural) =>
        count == 1 ? singular : plural;

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
        "create-category" => "Add category",
        "create-participant" => "Add person",
        "create-project" => "Add project",
        "create-task" => "Add task",
        "project-content" => "Edit project",
        "task-content" => "Edit task",
        "project-category" => "Change project category",
        "inherited-task-category" => "Inherited Task Category",
        "archived-child-category" => "Archived child Category",
        "binned-child-category" => "Binned child Category",
        "task-category" => "Change task category",
        "participants" => "Change people",
        "relationship" => "Move task",
        "task-reorder" => "Reorder task",
        "completion" => "Change completion",
        "today-removal" => "Today removal",
        "project-archive" => "Archive project",
        "project-bin" => "Move project to Bin",
        "task-archive" => "Archive task",
        "task-bin" => "Move task to Bin",
        "aggregate-child-bin" => "Project child moved to Bin",
        "parent-archive-visibility" => "Archived Project child visibility",
        "already-binned-child" => "Already-binned child",
        "project-status" => "Derived Project status",
        "final-project-order" => "Project Task order",
        "final-project-order-row" => "Project Task order row",
        "final-shared-order" => "Shared Task order",
        "final-shared-order-row" => "Shared Task order row",
        "explanation" => "Why this was suggested",
        _ => kind,
    };

    private void NotifyState()
    {
        Notify(nameof(CanApply));
        Notify(nameof(CanClose));
        Notify(nameof(CanRetryMaintenance));
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private enum ReviewState
    {
        Ready,
        Applying,
        Consumed,
        Committed,
    }
}
