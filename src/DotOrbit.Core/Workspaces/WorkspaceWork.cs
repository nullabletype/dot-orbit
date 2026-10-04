namespace DotOrbit.Core.Workspaces;

public interface IWorkspaceWork
{
    WorkspaceWorkSnapshot Read();
    WorkspaceCategory CreateCategory(string name);
    WorkspaceCategory RenameCategory(string id, string name);
    void DeleteCategory(string id, string? replacementCategoryId = null);
    ParticipantRecord CreateParticipant(string label);
    ParticipantRecord RenameParticipant(string id, string label);
    void DeleteParticipant(string id);
    CategoryOrderChange MoveCategory(string id, int targetPosition);
    ProjectRecord CreateProject(string title, string description, string categoryId, DateOnly? targetDate);
    TaskRecord CreateTask(string projectId, string title);
    TaskRecord CreateTaskDraft(string? projectId, string title, string description, string? categoryId,
        DateOnly? dueDate, ParticipantDraftChange? participantChange = null, TodayLane? todayLane = null);
    TaskRecord CreateStandaloneTask(string title, string description, string categoryId, DateOnly? dueDate,
        ParticipantDraftChange? participantChange = null);
    ProjectRecord UpdateProject(string id, string title, string description, string categoryId, DateOnly? targetDate);
    TaskRecord UpdateTask(string id, string title, string description, string? explicitCategoryId, DateOnly? dueDate,
        ParticipantDraftChange? participantChange = null);
    TaskRecord CompleteTask(string id);
    TaskRecord ReopenTask(string id);
    TaskRecord SetTaskTodayLane(string id, TodayLane? lane);
    int ClearToday();
    TodayLaneOrderChange MoveTaskInTodayLane(string id, int targetPosition);
    SharedTaskOrderChange MoveTaskInSharedOrder(string id, int targetPosition);
    ProjectOrderChange MoveProject(string id, int targetPosition);
    ProjectTaskOrderChange MoveTaskInProject(string projectId, string taskId, int targetPosition);
    TaskRecord DetachTask(string id);
    TaskRecord AttachTask(string id, string projectId, TaskAttachmentCategoryChoice? categoryChoice = null);
}

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

public sealed record WorkspaceCategory(string Id, string Name, long Position);
public sealed record ParticipantRecord(string Id, string Label);
public sealed record ParticipantDraftChange(
    IReadOnlyCollection<string> ParticipantIds,
    IReadOnlyCollection<string> NewParticipantLabels);
public sealed record ProjectRecord(string Id, string Title, string Description, string CategoryId, DateOnly? TargetDate, long Position);
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
    TodayLane? TodayLane = null)
{
    public bool IsComplete => CompletedAt is not null && CompletionDate is not null;
    public bool IsInToday => TodayLane is not null;
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
public sealed record SharedTaskOrderChange(string TaskId, int Position, int Count);
public sealed record TodayLaneOrderChange(string TaskId, TodayLane Lane, int Position, int Count);
public sealed record ProjectOrderChange(string ProjectId, int Position, int Count);
public sealed record ProjectTaskOrderChange(string ProjectId, string TaskId, int Position, int Count);
public sealed record CategoryOrderChange(string CategoryId, int Position, int Count);

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
