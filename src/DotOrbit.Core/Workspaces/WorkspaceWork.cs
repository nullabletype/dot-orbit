namespace DotOrbit.Core.Workspaces;

public interface IWorkspaceWork
{
    WorkspaceWorkSnapshot Read();
    ProjectRecord CreateProject(string title, string description, string categoryId, DateOnly? targetDate);
    TaskRecord CreateTask(string projectId, string title);
    ProjectRecord UpdateProject(string id, string title, string description, string categoryId, DateOnly? targetDate);
    TaskRecord UpdateTask(string id, string title, string description, string? categoryOverrideId, DateOnly? dueDate);
}

public sealed record WorkspaceCategory(string Id, string Name, long Position);
public sealed record ProjectRecord(string Id, string Title, string Description, string CategoryId, DateOnly? TargetDate, long Position);
public sealed record TaskRecord(string Id, string ProjectId, string Title, string Description, string? CategoryOverrideId, DateOnly? DueDate, long SharedPosition, long ProjectPosition);
public sealed record WorkspaceWorkSnapshot(IReadOnlyList<WorkspaceCategory> Categories, IReadOnlyList<ProjectRecord> Projects, IReadOnlyList<TaskRecord> Tasks);

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

public sealed record ProjectWorkSummary(int CompletedCount, int TaskCount, DateOnly? CompletionDate)
{
    public string Status { get; } = "Not started";

    public static ProjectWorkSummary From(WorkspaceWorkSnapshot snapshot, string projectId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        // This slice supports captured, incomplete Tasks only. Completion is a separate domain action.
        return new(0, snapshot.Tasks.Count(task => task.ProjectId == projectId), null);
    }
}
