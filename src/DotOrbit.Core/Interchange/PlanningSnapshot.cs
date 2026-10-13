using DotOrbit.Core.Workspaces;

namespace DotOrbit.Core.Interchange;

public interface IPlanningSnapshotSource
{
    PlanningSnapshotContent Read();
}

public enum PlanningProjectStatus
{
    NotStarted,
    InProgress,
    Complete,
}

public enum PlanningTaskCategoryMode
{
    Inherited,
    Explicit,
}

public sealed record PlanningSnapshotContent(
    WorkspaceRevision WorkspaceRevision,
    IReadOnlyList<PlanningCategory> Categories,
    IReadOnlyList<PlanningParticipant> Participants,
    IReadOnlyList<PlanningProject> Projects,
    IReadOnlyList<PlanningTask> Tasks,
    PlanningOrders Orders);

public sealed record PlanningSnapshotDocument(
    string Format,
    int SchemaVersion,
    WorkspaceRevision WorkspaceRevision,
    string SnapshotId,
    IReadOnlyList<PlanningCategory> Categories,
    IReadOnlyList<PlanningParticipant> Participants,
    IReadOnlyList<PlanningProject> Projects,
    IReadOnlyList<PlanningTask> Tasks,
    PlanningOrders Orders);

public sealed record PlanningCategory(string Id, string Name);

public sealed record PlanningParticipant(string Id, string Label);

public sealed record PlanningProjectProgress(int CompletedTaskCount, int TaskCount);

public sealed record PlanningProject(
    string Id,
    string Title,
    string Description,
    string CategoryId,
    DateOnly? TargetDate,
    PlanningProjectStatus Status,
    PlanningProjectProgress Progress,
    DateOnly? CompletionDate,
    int ExcludedArchivedTaskCount,
    IReadOnlyList<string> TaskIds);

public sealed record PlanningTaskCategory(
    PlanningTaskCategoryMode Mode,
    string CategoryId);

public sealed record PlanningTask(
    string Id,
    string? ProjectId,
    string Title,
    string Description,
    PlanningTaskCategory Category,
    DateOnly? DueDate,
    bool IsComplete,
    DateOnly? CompletionDate,
    IReadOnlyList<string> ParticipantIds,
    TodayLane? TodayLane);

public sealed record PlanningOrders(
    IReadOnlyList<string> CategoryIds,
    IReadOnlyList<string> ProjectIds,
    IReadOnlyList<string> SharedTaskIds);
