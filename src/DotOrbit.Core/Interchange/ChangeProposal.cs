namespace DotOrbit.Core.Interchange;

public readonly record struct OptionalChange<T>
{
    public OptionalChange(bool isSpecified, T? value)
    {
        IsSpecified = isSpecified;
        Value = value;
    }

    public bool IsSpecified { get; }
    public T? Value { get; }
}

public readonly record struct ProposalReference
{
    private ProposalReference(string? existingId, string? localId)
    {
        ExistingId = existingId;
        LocalId = localId;
    }

    public string? ExistingId { get; }
    public string? LocalId { get; }

    public static ProposalReference Existing(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return new(id, null);
    }

    public static ProposalReference Local(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return new(null, id);
    }
}

public enum ProposalKind
{
    NewWork,
    WorkspaceChange,
}

public sealed record ChangeProposal(
    ProposalKind Kind,
    string Format,
    int SchemaVersion,
    string? SourceSnapshotId,
    WorkspaceRevision? SourceWorkspaceRevision,
    IReadOnlyList<ChangeOperation> Operations)
{
    public ChangeProposal(
        string format,
        int schemaVersion,
        string sourceSnapshotId,
        WorkspaceRevision sourceWorkspaceRevision,
        IReadOnlyList<ChangeOperation> operations)
        : this(
            ProposalKind.WorkspaceChange,
            format,
            schemaVersion,
            sourceSnapshotId,
            sourceWorkspaceRevision,
            operations)
    {
    }
}

public abstract record ChangeOperation(string OperationId, string? Explanation);

public sealed record CreateCategoryOperation(
    string OperationId,
    string LocalId,
    string Name,
    string? Explanation = null) : ChangeOperation(OperationId, Explanation);

public sealed record CreateParticipantOperation(
    string OperationId,
    string LocalId,
    string Label,
    string? Explanation = null) : ChangeOperation(OperationId, Explanation);

public sealed record CreateProjectOperation(
    string OperationId,
    string LocalId,
    string Title,
    string Description,
    ProposalReference Category,
    DateOnly? TargetDate,
    string? Explanation = null) : ChangeOperation(OperationId, Explanation);

public sealed record ProjectContentChanges(
    OptionalChange<string> Title,
    OptionalChange<string> Description,
    OptionalChange<DateOnly?> TargetDate);

public sealed record EditProjectOperation(
    string OperationId,
    ProposalReference Project,
    ProjectContentChanges Changes,
    string? Explanation = null) : ChangeOperation(OperationId, Explanation);

public sealed record SetProjectCategoryOperation(
    string OperationId,
    ProposalReference Project,
    ProposalReference Category,
    string? Explanation = null) : ChangeOperation(OperationId, Explanation);

public enum ProposedTaskCategoryMode
{
    Inherit,
    Explicit,
}

public sealed record ProposedTaskCategory(
    ProposedTaskCategoryMode Mode,
    ProposalReference? Category = null);

public sealed record CreateTaskOperation(
    string OperationId,
    string LocalId,
    string Title,
    string Description,
    ProposalReference? Project,
    ProposedTaskCategory Category,
    DateOnly? DueDate,
    IReadOnlyList<ProposalReference> Participants,
    string? Explanation = null) : ChangeOperation(OperationId, Explanation);

public sealed record TaskContentChanges(
    OptionalChange<string> Title,
    OptionalChange<string> Description,
    OptionalChange<DateOnly?> DueDate);

public sealed record EditTaskOperation(
    string OperationId,
    ProposalReference Task,
    TaskContentChanges Changes,
    string? Explanation = null) : ChangeOperation(OperationId, Explanation);

public sealed record SetTaskCategoryOperation(
    string OperationId,
    ProposalReference Task,
    ProposedTaskCategory Category,
    string? Explanation = null) : ChangeOperation(OperationId, Explanation);

public sealed record ReplaceTaskParticipantsOperation(
    string OperationId,
    ProposalReference Task,
    IReadOnlyList<ProposalReference> Participants,
    string? Explanation = null) : ChangeOperation(OperationId, Explanation);

public enum AttachmentCategoryChoice
{
    AdoptProjectCategory,
    PreserveAsOverride,
}

public sealed record AttachTaskOperation(
    string OperationId,
    ProposalReference Task,
    ProposalReference Project,
    AttachmentCategoryChoice? CategoryChoice,
    string? Explanation = null) : ChangeOperation(OperationId, Explanation);

public sealed record DetachTaskOperation(
    string OperationId,
    ProposalReference Task,
    string? Explanation = null) : ChangeOperation(OperationId, Explanation);

public sealed record MoveTaskOperation(
    string OperationId,
    ProposalReference Task,
    ProposalReference Project,
    AttachmentCategoryChoice? CategoryChoice,
    string? Explanation = null) : ChangeOperation(OperationId, Explanation);

public sealed record CompleteTaskOperation(
    string OperationId,
    ProposalReference Task,
    string? Explanation = null) : ChangeOperation(OperationId, Explanation);

public sealed record ReopenTaskOperation(
    string OperationId,
    ProposalReference Task,
    string? Explanation = null) : ChangeOperation(OperationId, Explanation);

public enum ProjectTaskPlacementKind
{
    First,
    Last,
    Before,
    After,
}

public sealed record ProjectTaskPlacement(
    ProjectTaskPlacementKind Kind,
    ProposalReference? Anchor = null);

public sealed record ReorderProjectTaskOperation(
    string OperationId,
    ProposalReference Task,
    ProjectTaskPlacement Placement,
    string? Explanation = null) : ChangeOperation(OperationId, Explanation);

public sealed record ArchiveProjectOperation(
    string OperationId,
    ProposalReference Project,
    string? Explanation = null) : ChangeOperation(OperationId, Explanation);

public sealed record ArchiveTaskOperation(
    string OperationId,
    ProposalReference Task,
    string? Explanation = null) : ChangeOperation(OperationId, Explanation);

public sealed record MoveProjectToBinOperation(
    string OperationId,
    ProposalReference Project,
    string? Explanation = null) : ChangeOperation(OperationId, Explanation);

public sealed record MoveTaskToBinOperation(
    string OperationId,
    ProposalReference Task,
    string? Explanation = null) : ChangeOperation(OperationId, Explanation);
