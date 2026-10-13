using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;

namespace DotOrbit.Storage.Sqlite;

internal sealed class SqlitePlanningSnapshotSource(
    WorkspaceTransactionCoordinator transactions) : IPlanningSnapshotSource
{
    public PlanningSnapshotContent Read() => transactions.Read<PlanningSnapshotContent>((connection, transaction) =>
    {
        var revision = WorkspaceRevisionStorage.Read(connection, transaction);
        var workspace = SqliteWorkspaceWork.ReadSnapshot(connection, transaction);
        var activeProjects = workspace.Projects
            .Where(project => !project.IsArchived)
            .OrderBy(project => project.Position)
            .ThenBy(project => project.Id, StringComparer.Ordinal)
            .ToArray();
        var activeProjectIds = activeProjects.Select(project => project.Id)
            .ToHashSet(StringComparer.Ordinal);
        var includedTasks = workspace.Tasks
            .Where(task => !task.IsArchived
                && (task.ProjectId is null || activeProjectIds.Contains(task.ProjectId)))
            .OrderBy(task => task.SharedPosition)
            .ThenBy(task => task.Id, StringComparer.Ordinal)
            .ToArray();

        var categories = workspace.Categories
            .OrderBy(category => category.Position)
            .ThenBy(category => category.Id, StringComparer.Ordinal)
            .Select(category => new PlanningCategory(category.Id, category.Name))
            .ToArray();
        var participants = workspace.Participants
            .OrderBy(participant => participant.Id, UnicodeScalarOrdinalComparer.Instance)
            .Select(participant => new PlanningParticipant(participant.Id, participant.Label))
            .ToArray();
        var projects = activeProjects.Select(project =>
        {
            var summary = ProjectWorkSummary.From(workspace, project.Id);
            var taskIds = includedTasks
                .Where(task => task.ProjectId == project.Id)
                .OrderBy(task => task.ProjectPosition)
                .ThenBy(task => task.Id, StringComparer.Ordinal)
                .Select(task => task.Id)
                .ToArray();
            var excludedArchived = workspace.Tasks.Count(task =>
                task.ProjectId == project.Id && task.IsArchived);
            return new PlanningProject(
                project.Id,
                project.Title,
                project.Description,
                project.CategoryId,
                project.TargetDate,
                summary.IsComplete
                    ? PlanningProjectStatus.Complete
                    : summary.CompletedCount > 0
                        ? PlanningProjectStatus.InProgress
                        : PlanningProjectStatus.NotStarted,
                new(summary.CompletedCount, summary.TaskCount),
                summary.CompletionDate,
                excludedArchived,
                taskIds);
        }).ToArray();
        var projectCategoryById = activeProjects.ToDictionary(
            project => project.Id,
            project => project.CategoryId,
            StringComparer.Ordinal);
        var tasks = includedTasks.Select(task =>
        {
            var inherited = task.ProjectId is not null && task.ExplicitCategoryId is null;
            var categoryId = task.ExplicitCategoryId
                ?? projectCategoryById[task.ProjectId!];
            return new PlanningTask(
                task.Id,
                task.ProjectId,
                task.Title,
                task.Description,
                new(
                    inherited ? PlanningTaskCategoryMode.Inherited : PlanningTaskCategoryMode.Explicit,
                    categoryId),
                task.DueDate,
                task.IsComplete,
                task.CompletionDate,
                task.Participants.OrderBy(id => id, UnicodeScalarOrdinalComparer.Instance).ToArray(),
                task.TodayLane);
        }).ToArray();

        return new(
            revision,
            categories,
            participants,
            projects,
            tasks,
            new(
                categories.Select(category => category.Id).ToArray(),
                projects.Select(project => project.Id).ToArray(),
                tasks.Select(task => task.Id).ToArray()));
    });
}
