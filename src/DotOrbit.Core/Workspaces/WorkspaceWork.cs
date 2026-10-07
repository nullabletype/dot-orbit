namespace DotOrbit.Core.Workspaces;

public interface IWorkspaceWork
{
    WorkspaceWorkSnapshot Read();
    IReadOnlyList<TaskBinRecord> ReadTaskBin();
    IReadOnlyList<ProjectBinRecord> ReadProjectBin();
    EmptyBinPreview PreviewEmptyBin();
    IReadOnlyList<ArchiveSearchResult> SearchArchive(string query);
    WorkspaceCategory CreateCategory(string name, string? colourKey = null);
    WorkspaceCategory RenameCategory(string id, string name);
    WorkspaceCategory UpdateCategory(string id, string name, string colourKey);
    void DeleteCategory(string id, string? replacementCategoryId = null);
    ParticipantRecord CreateParticipant(string label);
    ParticipantRecord RenameParticipant(string id, string label);
    void DeleteParticipant(string id);
    CategoryOrderChange MoveCategory(string id, int targetPosition);
    ProjectRecord CreateProject(string title, string description, string categoryId, DateOnly? targetDate,
        string? colourKey = null);
    TaskRecord CreateTask(string projectId, string title);
    TaskRecord CreateTaskDraft(string? projectId, string title, string description, string? categoryId,
        DateOnly? dueDate, ParticipantDraftChange? participantChange = null, TodayLane? todayLane = null);
    TaskRecord CreateStandaloneTask(string title, string description, string categoryId, DateOnly? dueDate,
        ParticipantDraftChange? participantChange = null);
    ProjectRecord UpdateProject(string id, string title, string description, string categoryId, DateOnly? targetDate,
        string? colourKey = null);
    TaskRecord UpdateTask(string id, string title, string description, string? explicitCategoryId, DateOnly? dueDate,
        ParticipantDraftChange? participantChange = null);
    TaskRecord CompleteTask(string id);
    TaskRecord ReopenTask(string id);
    TaskRecord ArchiveTask(string id);
    TaskRecord RestoreTask(string id);
    TaskBinRecord MoveTaskToBin(string id);
    TaskRecord RestoreTaskFromBin(string id);
    ProjectBinRecord MoveProjectToBin(string id);
    ProjectRecord RestoreProjectFromBin(string id);
    EmptyBinResult EmptyBin(EmptyBinPreview confirmedPreview);
    ProjectRecord ArchiveProject(string id);
    ProjectRecord RestoreProject(string id);
    BulkTaskArchivePreview PreviewBulkTaskArchive(int completedAgeDays);
    BulkTaskArchiveResult BulkArchiveTasks(BulkTaskArchivePreview confirmedPreview);
    TaskRecord SetTaskTodayLane(string id, TodayLane? lane);
    int ClearToday();
    TodayLaneOrderChange MoveTaskInTodayLane(string id, int targetPosition);
    SharedTaskOrderChange MoveTaskInSharedOrder(string id, int targetPosition);
    ProjectOrderChange MoveProject(string id, int targetPosition);
    ProjectTaskOrderChange MoveTaskInProject(string projectId, string taskId, int targetPosition);
    TaskRecord DetachTask(string id);
    TaskRecord AttachTask(string id, string projectId, TaskAttachmentCategoryChoice? categoryChoice = null);
}

public enum ArchiveSearchRecordType
{
    Project,
    Task,
}

public enum ArchiveSearchDateKind
{
    Archived,
    Completed,
}

public sealed record ArchiveSearchResult(
    ArchiveSearchRecordType RecordType,
    string Id,
    string Title,
    string? ParentProjectTitle,
    ArchiveSearchDateKind DateKind,
    DateOnly Date,
    string Excerpt);

public enum TaskAttachmentCategoryChoice
{
    PreserveEffectiveCategory,
    AdoptProjectCategory,
}

public enum TodayLane
{
    Planned,
    InProgress,
}

public sealed record WorkspaceCategory(
    string Id,
    string Name,
    long Position,
    string ColourKey = IdentityColourPalette.DefaultKey);
public sealed record ParticipantRecord(string Id, string Label);
public sealed record ParticipantDraftChange(
    IReadOnlyCollection<string> ParticipantIds,
    IReadOnlyCollection<string> NewParticipantLabels);
public sealed record ProjectRecord(
    string Id,
    string Title,
    string Description,
    string CategoryId,
    DateOnly? TargetDate,
    long Position,
    DateTimeOffset? ArchivedAt = null,
    DateOnly? ArchiveDate = null,
    string ColourKey = IdentityColourPalette.DefaultKey)
{
    public bool IsArchived => ArchivedAt is not null && ArchiveDate is not null;
}
public sealed record TaskRecord(
    string Id,
    string? ProjectId,
    string Title,
    string Description,
    string? ExplicitCategoryId,
    DateOnly? DueDate,
    long SharedPosition,
    long? ProjectPosition,
    DateTimeOffset? CompletedAt = null,
    DateOnly? CompletionDate = null,
    IReadOnlyList<string>? ParticipantIds = null,
    TodayLane? TodayLane = null,
    DateTimeOffset? ArchivedAt = null,
    DateOnly? ArchiveDate = null)
{
    public bool IsComplete => CompletedAt is not null && CompletionDate is not null;
    public bool IsInToday => TodayLane is not null;
    public bool IsArchived => ArchivedAt is not null && ArchiveDate is not null;
    public IReadOnlyList<string> Participants => ParticipantIds ?? [];
}
public sealed record WorkspaceWorkSnapshot(
    IReadOnlyList<WorkspaceCategory> Categories,
    IReadOnlyList<ProjectRecord> Projects,
    IReadOnlyList<TaskRecord> Tasks,
    IReadOnlyList<ParticipantRecord>? ParticipantRecords = null)
{
    public IReadOnlyList<ParticipantRecord> Participants => ParticipantRecords ?? [];
}
public sealed record TaskBinRecord(
    TaskRecord Task,
    DateTimeOffset RemovedAt,
    string? ProjectTitle,
    string CategoryName,
    bool CanRestore,
    string? RestoreBlockedReason);
public sealed record ProjectBinRecord(
    ProjectRecord Project,
    DateTimeOffset RemovedAt,
    string CategoryName,
    int TaskCount);
public sealed record EmptyBinPreview(
    IReadOnlyList<string> ProjectIds,
    IReadOnlyList<string> TaskIds)
{
    public int ProjectCount => ProjectIds.Count;
    public int TaskCount => TaskIds.Count;
    public bool IsEmpty => ProjectCount == 0 && TaskCount == 0;
    public bool Matches(EmptyBinPreview? other) => other is not null
        && ProjectIds.SequenceEqual(other.ProjectIds, StringComparer.Ordinal)
        && TaskIds.SequenceEqual(other.TaskIds, StringComparer.Ordinal);
}
public enum EmptyBinStatus
{
    Emptied,
    PreviewChanged,
    RecoveryPointCreationFailed,
    Failed,
}
public sealed record EmptyBinResult(EmptyBinStatus Status, EmptyBinPreview Preview);
public sealed record TaskBinOrderAnchor(string TaskId, int RelativePosition);
public sealed record SharedTaskOrderChange(string TaskId, int Position, int Count);
public sealed record TodayLaneOrderChange(string TaskId, TodayLane Lane, int Position, int Count);
public sealed record ProjectOrderChange(string ProjectId, int Position, int Count);
public sealed record ProjectTaskOrderChange(string ProjectId, string TaskId, int Position, int Count);
public sealed record CategoryOrderChange(string CategoryId, int Position, int Count);
public sealed record BulkTaskArchivePreview(
    int CompletedAgeDays,
    DateOnly EvaluatedOn,
    IReadOnlyList<string> EligibleTaskIds)
{
    public int AffectedCount => EligibleTaskIds.Count;
    public bool Matches(BulkTaskArchivePreview? other) =>
        other is not null
        && CompletedAgeDays == other.CompletedAgeDays
        && EvaluatedOn == other.EvaluatedOn
        && EligibleTaskIds.SequenceEqual(other.EligibleTaskIds, StringComparer.Ordinal);
}
public sealed record BulkTaskArchiveResult(
    bool Applied,
    BulkTaskArchivePreview Preview,
    int ArchivedCount);

public static class TaskBinRestorePolicy
{
    public static int RestoreIndex(
        IReadOnlyList<string> survivingIds,
        IReadOnlyList<TaskBinOrderAnchor> formerOrder)
    {
        ArgumentNullException.ThrowIfNull(survivingIds);
        ArgumentNullException.ThrowIfNull(formerOrder);
        var nearest = formerOrder
            .Select(anchor => (Anchor: anchor, Index: IndexOf(survivingIds, anchor.TaskId)))
            .Where(candidate => candidate.Index >= 0)
            .OrderBy(candidate => Math.Abs(candidate.Anchor.RelativePosition))
            .ThenBy(candidate => candidate.Anchor.RelativePosition > 0)
            .ThenBy(candidate => candidate.Anchor.TaskId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (nearest.Anchor is null) return survivingIds.Count;
        return nearest.Anchor.RelativePosition < 0 ? nearest.Index + 1 : nearest.Index;
    }

    public static void EnsureParentAllowsRestore(bool parentProjectIsBinned)
    {
        if (parentProjectIsBinned)
            throw new InvalidOperationException("Restore the parent Project before restoring this Task.");
    }

    private static int IndexOf(IReadOnlyList<string> ids, string id)
    {
        for (var index = 0; index < ids.Count; index++)
            if (string.Equals(ids[index], id, StringComparison.Ordinal)) return index;
        return -1;
    }
}

public static class ProjectBinRestorePolicy
{
    public static int RestoreIndex(
        IReadOnlyList<string> survivingIds,
        IReadOnlyList<TaskBinOrderAnchor> formerOrder) =>
        TaskBinRestorePolicy.RestoreIndex(survivingIds, formerOrder);
}

public static class BulkTaskArchiveThreshold
{
    public const int MinimumDays = 1;
    public const int MaximumDays = 30;

    public static int Validate(int completedAgeDays)
    {
        if (completedAgeDays is < MinimumDays or > MaximumDays)
            throw new ArgumentOutOfRangeException(nameof(completedAgeDays));
        return completedAgeDays;
    }
}

public static class BulkTaskArchivePolicy
{
    public static IReadOnlyList<TaskRecord> EligibleTasks(
        WorkspaceWorkSnapshot snapshot,
        DateOnly today,
        int completedAgeDays)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var threshold = BulkTaskArchiveThreshold.Validate(completedAgeDays);
        var cutoffDayNumber = today.DayNumber - threshold;
        var archivedProjectIds = snapshot.Projects
            .Where(project => project.IsArchived)
            .Select(project => project.Id)
            .ToHashSet(StringComparer.Ordinal);
        return snapshot.Tasks
            .Where(task => task.IsComplete
                && !task.IsArchived
                && task.CompletionDate is { } completionDate
                && completionDate.DayNumber < cutoffDayNumber
                && (task.ProjectId is null || !archivedProjectIds.Contains(task.ProjectId)))
            .ToArray();
    }
}

public sealed class WorkspaceWorkException : Exception
{
    public WorkspaceWorkException() : base("The workspace operation could not be completed.") { }
}

public static class WorkTitle
{
    public static string Normalize(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return title.Trim();
    }
}

public static class ParticipantLabel
{
    public static string Normalize(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        return label.Trim().Normalize(System.Text.NormalizationForm.FormKC);
    }

    public static string ComparisonKey(string label) => Normalize(label).ToUpperInvariant();
}

public sealed record ProjectWorkSummary(int CompletedCount, int TaskCount, DateOnly? CompletionDate)
{
    public string Status => TaskCount > 0 && CompletedCount == TaskCount
        ? "Complete"
        : CompletedCount > 0
            ? "In progress"
            : "Not started";

    public bool IsComplete => TaskCount > 0 && CompletedCount == TaskCount;

    public static ProjectWorkSummary From(WorkspaceWorkSnapshot snapshot, string projectId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var tasks = snapshot.Tasks.Where(task => task.ProjectId == projectId).ToArray();
        var completed = tasks.Where(task => task.IsComplete).ToArray();
        return new(
            completed.Length,
            tasks.Length,
            tasks.Length > 0 && completed.Length == tasks.Length
                ? completed.Max(task => task.CompletionDate)
                : null);
    }
}

public static class WorkDatePresentation
{
    public static string Relative(DateOnly? value, DateOnly today)
    {
        if (value is null) return "No date";
        if (value == today) return "Today";
        if (value == today.AddDays(1)) return "Tomorrow";
        return value.Value.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Accessible(DateOnly? value, string kind) => value is null
        ? $"No {kind.ToLowerInvariant()}"
        : $"{kind} {value.Value.ToString("dddd, d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}";

    public static bool IsOverdue(ProjectRecord project, ProjectWorkSummary summary, DateOnly today) =>
        !summary.IsComplete && project.TargetDate is { } targetDate && targetDate < today;
}
