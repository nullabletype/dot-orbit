using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotOrbit.Core.Interchange;

namespace DotOrbit.Interchange.Json;

public sealed record InterchangeGenerationResult(
    bool Succeeded,
    ReadOnlyMemory<byte>? Payload,
    string? ErrorCode)
{
    public static InterchangeGenerationResult Success(byte[] payload) =>
        new(true, payload, null);

    public static InterchangeGenerationResult TooLarge() =>
        new(false, null, "workspace-too-large-for-v1-interchange");
}

public static class PlanningSnapshotJsonGenerator
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static InterchangeGenerationResult Generate(IPlanningSnapshotSource source) =>
        Generate(source, AgentInterchangeV2.SchemaVersion);

    public static InterchangeGenerationResult Generate(IPlanningSnapshotSource source, int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Generate(source.Read(), schemaVersion);
    }

    public static InterchangeGenerationResult Generate(PlanningSnapshotContent snapshot) =>
        Generate(snapshot, AgentInterchangeV2.SchemaVersion);

    public static InterchangeGenerationResult Generate(PlanningSnapshotContent snapshot, int schemaVersion)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EnsureSupportedVersion(schemaVersion);
        if (StoredTextExceedsLimit(snapshot)) return InterchangeGenerationResult.TooLarge();
        var root = CreateRoot(snapshot, schemaVersion);
        using var withoutId = JsonDocument.Parse(root.ToJsonString());
        var canonicalWithoutId = Rfc8785Canonicalizer.Canonicalize(withoutId.RootElement);
        var digest = Convert.ToHexString(SHA256.HashData(canonicalWithoutId)).ToLowerInvariant();
        root["snapshotId"] = $"sha256:{digest}";
        using var finalDocument = JsonDocument.Parse(root.ToJsonString());
        var payload = Rfc8785Canonicalizer.Canonicalize(finalDocument.RootElement);
        return payload.Length <= AgentInterchangeV1.MaximumOutputBytes
            ? InterchangeGenerationResult.Success(payload)
            : InterchangeGenerationResult.TooLarge();
    }

    internal static void EnsureSupportedVersion(int schemaVersion)
    {
        if (schemaVersion is not (AgentInterchangeV1.SchemaVersion or AgentInterchangeV2.SchemaVersion))
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "Unsupported interchange version.");
    }

    private static bool StoredTextExceedsLimit(PlanningSnapshotContent snapshot)
    {
        long bytes = 0;
        foreach (var value in EnumerateStrings(snapshot))
        {
            try
            {
                bytes += StrictUtf8.GetByteCount(value);
            }
            catch (EncoderFallbackException exception)
            {
                throw new InvalidDataException("Snapshot text contains an invalid Unicode scalar value.", exception);
            }
            if (bytes > AgentInterchangeV1.MaximumOutputBytes) return true;
        }
        return false;
    }

    private static IEnumerable<string> EnumerateStrings(PlanningSnapshotContent snapshot)
    {
        foreach (var category in snapshot.Categories)
        {
            yield return category.Id;
            yield return category.Name;
        }
        foreach (var participant in snapshot.Participants)
        {
            yield return participant.Id;
            yield return participant.Label;
        }
        foreach (var project in snapshot.Projects)
        {
            yield return project.Id;
            yield return project.Title;
            yield return project.Description;
            yield return project.CategoryId;
            foreach (var taskId in project.TaskIds) yield return taskId;
        }
        foreach (var task in snapshot.Tasks)
        {
            yield return task.Id;
            if (task.ProjectId is not null) yield return task.ProjectId;
            yield return task.Title;
            yield return task.Description;
            yield return task.Category.CategoryId;
            foreach (var participantId in task.ParticipantIds) yield return participantId;
        }
        foreach (var id in snapshot.Orders.CategoryIds) yield return id;
        foreach (var id in snapshot.Orders.ProjectIds) yield return id;
        foreach (var id in snapshot.Orders.SharedTaskIds) yield return id;
    }

    private static JsonObject CreateRoot(PlanningSnapshotContent snapshot, int schemaVersion) => new()
    {
        ["format"] = AgentInterchangeV1.PlanningSnapshotFormat,
        ["schemaVersion"] = schemaVersion,
        ["workspaceRevision"] = snapshot.WorkspaceRevision.ToString(),
        ["categories"] = new JsonArray(snapshot.Categories.Select(category => (JsonNode)new JsonObject
        {
            ["id"] = category.Id,
            ["name"] = category.Name,
        }).ToArray()),
        ["participants"] = new JsonArray(snapshot.Participants.Select(participant => (JsonNode)new JsonObject
        {
            ["id"] = participant.Id,
            ["label"] = participant.Label,
        }).ToArray()),
        ["projects"] = new JsonArray(snapshot.Projects.Select(project => (JsonNode)new JsonObject
        {
            ["id"] = project.Id,
            ["title"] = project.Title,
            ["description"] = project.Description,
            ["categoryId"] = project.CategoryId,
            ["targetDate"] = DateNode(project.TargetDate),
            ["status"] = ProjectStatus(project.Status),
            ["progress"] = new JsonObject
            {
                ["completedTaskCount"] = project.Progress.CompletedTaskCount,
                ["taskCount"] = project.Progress.TaskCount,
            },
            ["completionDate"] = DateNode(project.CompletionDate),
            ["excludedArchivedTaskCount"] = project.ExcludedArchivedTaskCount,
            ["taskIds"] = StringArray(project.TaskIds),
        }).ToArray()),
        ["tasks"] = new JsonArray(snapshot.Tasks.Select(task => (JsonNode)new JsonObject
        {
            ["id"] = task.Id,
            ["projectId"] = task.ProjectId,
            ["title"] = task.Title,
            ["description"] = task.Description,
            ["category"] = new JsonObject
            {
                ["mode"] = task.Category.Mode == PlanningTaskCategoryMode.Inherited
                    ? "inherited"
                    : "explicit",
                ["categoryId"] = task.Category.CategoryId,
            },
            ["dueDate"] = DateNode(task.DueDate),
            ["isComplete"] = task.IsComplete,
            ["completionDate"] = DateNode(task.CompletionDate),
            ["participantIds"] = StringArray(task.ParticipantIds),
            ["todayLane"] = task.TodayLane switch
            {
                Core.Workspaces.TodayLane.Planned => "planned",
                Core.Workspaces.TodayLane.InProgress => "inProgress",
                null => null,
                _ => throw new InvalidDataException(),
            },
        }).ToArray()),
        ["orders"] = new JsonObject
        {
            ["categoryIds"] = StringArray(snapshot.Orders.CategoryIds),
            ["projectIds"] = StringArray(snapshot.Orders.ProjectIds),
            ["sharedTaskIds"] = StringArray(snapshot.Orders.SharedTaskIds),
        },
    };

    private static JsonArray StringArray(IReadOnlyList<string> values) =>
        new(values.Select(value => (JsonNode?)value).ToArray());

    private static JsonValue? DateNode(DateOnly? value) =>
        value is null
            ? null
            : JsonValue.Create(value.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    private static string ProjectStatus(PlanningProjectStatus status) => status switch
    {
        PlanningProjectStatus.NotStarted => "notStarted",
        PlanningProjectStatus.InProgress => "inProgress",
        PlanningProjectStatus.Complete => "complete",
        _ => throw new InvalidDataException(),
    };
}
