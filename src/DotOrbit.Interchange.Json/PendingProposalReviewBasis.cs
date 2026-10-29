using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;

namespace DotOrbit.Interchange.Json;

internal static class PendingProposalReviewBasis
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static byte[] Create(
        ChangeProposal proposal,
        ProposalWorkspaceState source,
        string payloadDigest)
    {
        if (proposal.Kind != ProposalKind.WorkspaceChange
            || proposal.SourceWorkspaceRevision is not { } revision
            || proposal.SourceSnapshotId is not { } snapshotId)
        {
            return [];
        }

        var dependencies = Collect(proposal, source)
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        var encoded = JsonSerializer.SerializeToUtf8Bytes(
            new BasisDocument(
                FormatVersion,
                payloadDigest,
                revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                snapshotId,
                dependencies),
            JsonOptions);
        if (encoded.Length > AgentInterchangeV1.MaximumPendingReviewBasisBytes)
        {
            throw new InvalidDataException();
        }

        return encoded;
    }

    internal static bool Matches(
        ReadOnlySpan<byte> encoded,
        ChangeProposal proposal,
        ProposalWorkspaceState current,
        string payloadDigest)
    {
        if (encoded.IsEmpty || encoded.Length > AgentInterchangeV1.MaximumPendingReviewBasisBytes)
        {
            return false;
        }

        BasisDocument? basis;
        try
        {
            basis = JsonSerializer.Deserialize<BasisDocument>(encoded, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (basis is null
            || basis.Version != FormatVersion
            || !string.Equals(basis.PayloadDigest, payloadDigest, StringComparison.Ordinal)
            || !string.Equals(
                basis.SourceRevision,
                proposal.SourceWorkspaceRevision?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            || !string.Equals(basis.SourceSnapshotId, proposal.SourceSnapshotId, StringComparison.Ordinal)
            || basis.Dependencies is null)
        {
            return false;
        }

        if (basis.Dependencies.Any(item => item is null))
        {
            return false;
        }

        var expected = basis.Dependencies
            .Select(item => item!)
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        if (expected.Length != expected.DistinctBy(item => (item.Kind, item.Key)).Count())
        {
            return false;
        }

        var currentDependencies = Collect(proposal, current)
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();
        if (!expected.Select(item => (item.Kind, item.Key))
                .SequenceEqual(currentDependencies.Select(item => (item.Kind, item.Key))))
        {
            return false;
        }

        foreach (var dependency in expected)
        {
            if (string.IsNullOrWhiteSpace(dependency.Kind)
                || string.IsNullOrWhiteSpace(dependency.Key)
                || dependency.Digest is null
                || dependency.Digest.Length != 64
                || !string.Equals(
                    dependency.Digest,
                    Fingerprint(dependency.Kind, dependency.Key, current),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<(string Kind, string Key), BasisDependency>.ValueCollection Collect(
        ChangeProposal proposal,
        ProposalWorkspaceState source)
    {
        var dependencies = new Dictionary<(string Kind, string Key), BasisDependency>();
        void Add(string kind, string key) => dependencies.TryAdd(
            (kind, key),
            new(kind, key, Fingerprint(kind, key, source)));
        void AddReference(ProposalReference reference, string kind)
        {
            if (reference.ExistingId is { } existingId)
            {
                Add(kind, existingId);
            }
        }

        foreach (var operation in proposal.Operations)
        {
            switch (operation)
            {
                case CreateCategoryOperation item:
                    Add("category-name", CategoryName.Create(item.Name).CategoryName!.Value);
                    break;
                case CreateParticipantOperation item:
                    Add("participant-label", ParticipantLabel.Normalize(item.Label));
                    break;
                case CreateProjectOperation item:
                    AddReference(item.Category, "category");
                    break;
                case EditProjectOperation item:
                    AddProjectContentDependencies(item.Project, item.Changes);
                    break;
                case SetProjectCategoryOperation item:
                    AddReference(item.Project, "project");
                    AddReference(item.Category, "category");
                    if (item.Project.ExistingId is { } categoryProjectId)
                    {
                        Add("project-aggregate", categoryProjectId);
                    }
                    break;
                case CreateTaskOperation item:
                    if (item.Project is { } project)
                    {
                        AddReference(project, "project");
                    }
                    if (item.Category.Category is { } taskCategory)
                    {
                        AddReference(taskCategory, "category");
                    }
                    foreach (var participant in item.Participants)
                    {
                        AddReference(participant, "participant");
                    }
                    break;
                case EditTaskOperation item:
                    AddTaskContentDependencies(item.Task, item.Changes);
                    break;
                case SetTaskCategoryOperation item:
                    AddReference(item.Task, "task");
                    if (item.Category.Category is { } category)
                    {
                        AddReference(category, "category");
                    }
                    else if (item.Task.ExistingId is { } inheritedTaskId)
                    {
                        AddCurrentParent(inheritedTaskId);
                    }
                    break;
                case ReplaceTaskParticipantsOperation item:
                    AddReference(item.Task, "task");
                    foreach (var participant in item.Participants)
                    {
                        AddReference(participant, "participant");
                    }
                    break;
                case AttachTaskOperation item:
                    AddReference(item.Task, "task");
                    AddReference(item.Project, "project");
                    break;
                case DetachTaskOperation item:
                    AddReference(item.Task, "task");
                    if (item.Task.ExistingId is { } detachedTaskId)
                    {
                        AddCurrentParent(detachedTaskId);
                    }
                    break;
                case MoveTaskOperation item:
                    AddReference(item.Task, "task");
                    AddReference(item.Project, "project");
                    if (item.Task.ExistingId is { } movedTaskId)
                    {
                        AddCurrentParent(movedTaskId);
                    }
                    break;
                case SetTaskTodayOperation item:
                    AddReference(item.Task, "task");
                    if (item.Task.ExistingId is { } todayTaskId)
                    {
                        AddCurrentParent(todayTaskId);
                    }
                    break;
                case CompleteTaskOperation item:
                    AddReference(item.Task, "task");
                    break;
                case ReopenTaskOperation item:
                    AddReference(item.Task, "task");
                    break;
                case ReorderProjectTaskOperation item:
                    AddReference(item.Task, "task");
                    if (item.Placement.Anchor is { } anchor)
                    {
                        AddReference(anchor, "task");
                    }
                    if (item.Task.ExistingId is { } taskId
                        && source.Workspace.Tasks.SingleOrDefault(task => task.Id == taskId)?.ProjectId is { } projectId)
                    {
                        Add("project-task-order", projectId);
                    }
                    break;
                case ArchiveProjectOperation item:
                    AddProjectAggregate(item.Project);
                    break;
                case MoveProjectToBinOperation item:
                    AddProjectAggregate(item.Project);
                    break;
                case ArchiveTaskOperation item:
                    AddReference(item.Task, "task");
                    break;
                case MoveTaskToBinOperation item:
                    AddReference(item.Task, "task");
                    break;
            }
        }

        return dependencies.Values;

        void AddProjectAggregate(ProposalReference project)
        {
            AddReference(project, "project");
            if (project.ExistingId is { } projectId)
            {
                Add("project-aggregate", projectId);
            }
        }

        void AddCurrentParent(string taskId)
        {
            if (source.Workspace.Tasks.SingleOrDefault(task => task.Id == taskId)?.ProjectId is { } projectId)
            {
                Add("project", projectId);
            }
        }

        void AddProjectContentDependencies(ProposalReference project, ProjectContentChanges changes)
        {
            if (project.ExistingId is not { } projectId)
            {
                return;
            }

            if (changes.Title.IsSpecified)
            {
                Add("project-title", projectId);
            }
            if (changes.Description.IsSpecified)
            {
                Add("project-description", projectId);
            }
            if (changes.TargetDate.IsSpecified)
            {
                Add("project-target-date", projectId);
            }
        }

        void AddTaskContentDependencies(ProposalReference task, TaskContentChanges changes)
        {
            if (task.ExistingId is not { } taskId)
            {
                return;
            }

            if (changes.Title.IsSpecified)
            {
                Add("task-title", taskId);
            }
            if (changes.Description.IsSpecified)
            {
                Add("task-description", taskId);
            }
            if (changes.DueDate.IsSpecified)
            {
                Add("task-due-date", taskId);
            }
        }
    }

    private static string Fingerprint(string kind, string key, ProposalWorkspaceState source)
    {
        object? value = kind switch
        {
            "category" => source.Workspace.Categories.SingleOrDefault(item => item.Id == key),
            "participant" => source.Workspace.Participants.SingleOrDefault(item => item.Id == key),
            "project" => StableProject(key),
            "task" => StableTask(key),
            "project-title" => ProjectField(key, item => item.Title),
            "project-description" => ProjectField(key, item => item.Description),
            "project-target-date" => ProjectField(key, item => item.TargetDate),
            "task-title" => TaskField(key, item => item.Title),
            "task-description" => TaskField(key, item => item.Description),
            "task-due-date" => TaskField(key, item => item.DueDate),
            "category-name" => source.Workspace.Categories
                .Where(item => StringComparer.OrdinalIgnoreCase.Equals(item.Name, key))
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ToArray(),
            "participant-label" => source.Workspace.Participants
                .Where(item => ParticipantLabel.ComparisonKey(item.Label)
                    == ParticipantLabel.ComparisonKey(key))
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ToArray(),
            "project-aggregate" => new
            {
                Tasks = source.Workspace.Tasks
                    .Where(item => item.ProjectId == key)
                    .OrderBy(item => item.Id, StringComparer.Ordinal)
                    .ToArray(),
                BinnedTasks = source.TaskBin
                    .Where(item => item.Task.ProjectId == key)
                    .OrderBy(item => item.Task.Id, StringComparer.Ordinal)
                    .ToArray(),
            },
            "project-task-order" => source.Workspace.Tasks
                .Where(item => item.ProjectId == key && !item.IsArchived)
                .OrderBy(item => item.ProjectPosition)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => new { item.Id, item.ProjectPosition })
                .ToArray(),
            _ => null,
        };
        if (value is null)
        {
            return string.Empty;
        }

        return Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)));

        object? ProjectField(string projectId, Func<ProjectRecord, object?> selector)
        {
            var project = source.Workspace.Projects.SingleOrDefault(item => item.Id == projectId);
            return project is null ? null : new { Exists = true, Value = selector(project) };
        }

        object? TaskField(string taskId, Func<TaskRecord, object?> selector)
        {
            var task = source.Workspace.Tasks.SingleOrDefault(item => item.Id == taskId);
            return task is null ? null : new { Exists = true, Value = selector(task) };
        }

        object? StableProject(string projectId)
        {
            var project = source.Workspace.Projects.SingleOrDefault(item => item.Id == projectId);
            return project is null
                ? null
                : new
                {
                    project.Id,
                    project.Title,
                    project.Description,
                    project.CategoryId,
                    project.TargetDate,
                    project.ArchivedAt,
                    project.ArchiveDate,
                    project.ColourKey,
                };
        }

        object? StableTask(string taskId)
        {
            var task = source.Workspace.Tasks.SingleOrDefault(item => item.Id == taskId);
            return task is null
                ? null
                : new
                {
                    task.Id,
                    task.ProjectId,
                    task.Title,
                    task.Description,
                    task.ExplicitCategoryId,
                    task.DueDate,
                    task.CompletedAt,
                    task.CompletionDate,
                    task.ParticipantIds,
                    task.TodayLane,
                    task.ArchivedAt,
                    task.ArchiveDate,
                };
        }
    }

    private sealed record BasisDocument(
        int Version,
        string PayloadDigest,
        string SourceRevision,
        string SourceSnapshotId,
        IReadOnlyList<BasisDependency?> Dependencies);

    private sealed record BasisDependency(string Kind, string Key, string Digest);
}
