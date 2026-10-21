using System.Text;
using System.Text.Json;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using Xunit;

namespace DotOrbit.Interchange.Json.Tests;

public sealed class ProposalReviewBoundaryTests
{
    [Fact]
    public void NewWorkReviewReadsCurrentWorkspaceOnceAndBindsThePlanInternally()
    {
        var source = Source("");
        var input = """
            {
              "format": "dot-orbit-new-work-proposal",
              "schemaVersion": 1,
              "operations": [
                { "operation": "createCategory", "operationId": "1", "localId": "category", "name": "Fresh" },
                { "operation": "createTask", "operationId": "2", "localId": "task", "title": "Task", "description": "", "project": null, "category": { "mode": "explicit", "category": { "localId": "category" } }, "dueDate": null, "participants": [] }
              ]
            }
            """;

        var result = new ProposalReviewBoundary(source, TimeProvider.System)
            .Review(Encoding.UTF8.GetBytes(input));

        Assert.Equal(1, source.ReadCount);
        Assert.Equal(ProposalKind.NewWork, result.Plan!.Kind);
        Assert.Equal(source.State.Revision, result.Plan.SourceWorkspaceRevision);
        Assert.Null(result.Plan.SourceSnapshotId);
    }

    [Fact]
    public void UnchangedStoredMarkdownMayExceedProposalFieldNodeLimit()
    {
        var source = Source(string.Join("\n\n", Enumerable.Repeat("synthetic", 2_049)));
        var result = Review(source, new { title = "Changed synthetic task" });
        Assert.NotNull(result.Plan);
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public void ProposedMarkdownOverNodeLimitIsRejectedCompletely()
    {
        var result = Review(Source(""), new { description = string.Join("\n\n", Enumerable.Repeat("synthetic", 2_049)) });
        Assert.Null(result.Plan);
        Assert.Equal("markdown-limit", Assert.Single(result.Failures).Code);
    }

    [Fact]
    public void ExcessiveParserNestingReturnsSafeFailure()
    {
        var result = Review(Source(""), new { description = new string('>', 1_000) + " synthetic" });
        Assert.Null(result.Plan);
        Assert.Equal("markdown-limit", Assert.Single(result.Failures).Code);
    }

    [Fact]
    public void SyntaxRejectionDoesNotReadWorkspace()
    {
        var source = Source("");
        var result = new ProposalReviewBoundary(source, TimeProvider.System).Review(Encoding.UTF8.GetBytes("{} {}"));
        Assert.Null(result.Plan);
        Assert.Equal(0, source.ReadCount);
    }

    [Fact]
    public void QueuedChangeReviewRefreshesWhenOnlyAnUnrelatedRecordChanged()
    {
        var source = TwoTaskSource();
        var payload = ChangeTaskTitlePayload(source.State, "task-a", "Updated A");
        var boundary = new ProposalReviewBoundary(source, TimeProvider.System);
        var admitted = boundary.PrepareQueued(payload);
        Assert.NotNull(admitted.Plan);
        Assert.False(admitted.ReviewBasis.IsEmpty);
        source.State = source.State with
        {
            Revision = new(2),
            Workspace = source.State.Workspace with
            {
                Tasks = source.State.Workspace.Tasks
                    .Select(task => task.Id == "task-b" ? task with { Title = "Updated B" } : task)
                    .ToArray(),
            },
        };

        var refreshed = boundary.ReviewQueued(payload, admitted.ReviewBasis);

        Assert.NotNull(refreshed.Plan);
        Assert.True(refreshed.WasRefreshed);
        Assert.Equal(new WorkspaceRevision(2), refreshed.Plan.SourceWorkspaceRevision);
        Assert.Contains(refreshed.Plan.Preview, effect =>
            effect.Target.ExistingId == "task-a" && effect.After.Contains("Updated A", StringComparison.Ordinal));
    }

    [Fact]
    public void QueuedTaskEditRefreshesWhenNewTasksOnlyChangeItsOrderingMetadata()
    {
        var source = new CountingSource(new(
            new(1),
            new(
                [new("c", "Synthetic category", 0)],
                [],
                [new("task-a", null, "Task A", "", "c", null, 0, null)],
                []),
            [],
            []));
        var payload = ChangeTaskTitlePayload(source.State, "task-a", "Updated A");
        var boundary = new ProposalReviewBoundary(source, TimeProvider.System);
        var admitted = boundary.PrepareQueued(payload);
        Assert.NotNull(admitted.Plan);
        source.State = source.State with
        {
            Revision = new(3),
            Workspace = source.State.Workspace with
            {
                Tasks =
                [
                    new("new-task-1", null, "New task 1", "", "c", null, 0, null),
                    new("new-task-2", null, "New task 2", "", "c", null, 1, null),
                    new("task-a", null, "Task A", "", "c", null, 2, null),
                ],
            },
        };

        var refreshed = boundary.ReviewQueued(payload, admitted.ReviewBasis);

        Assert.NotNull(refreshed.Plan);
        Assert.True(refreshed.WasRefreshed);
        Assert.Equal(new WorkspaceRevision(3), refreshed.Plan.SourceWorkspaceRevision);
    }

    [Fact]
    public void QueuedTaskStateChangeRefreshesWhenNewTasksOnlyChangeItsOrderingMetadata()
    {
        var source = TwoTaskSource();
        var payload = CompleteTaskPayload(source.State, "task-a");
        var boundary = new ProposalReviewBoundary(source, TimeProvider.System);
        var admitted = boundary.PrepareQueued(payload);
        Assert.NotNull(admitted.Plan);
        source.State = source.State with
        {
            Revision = new(3),
            Workspace = source.State.Workspace with
            {
                Tasks =
                [
                    new("new-task-1", null, "New task 1", "", "c", null, 0, null),
                    new("new-task-2", null, "New task 2", "", "c", null, 1, null),
                    .. source.State.Workspace.Tasks.Select(task => task with
                    {
                        SharedPosition = task.SharedPosition + 2,
                    }),
                ],
            },
        };

        var refreshed = boundary.ReviewQueued(payload, admitted.ReviewBasis);

        Assert.NotNull(refreshed.Plan);
        Assert.True(refreshed.WasRefreshed);
        Assert.Equal(new WorkspaceRevision(3), refreshed.Plan.SourceWorkspaceRevision);
    }

    [Fact]
    public void QueuedChangeReviewConflictsWhenItsGuardedRecordChanged()
    {
        var source = TwoTaskSource();
        var payload = ChangeTaskTitlePayload(source.State, "task-a", "Updated A");
        var boundary = new ProposalReviewBoundary(source, TimeProvider.System);
        var admitted = boundary.PrepareQueued(payload);
        source.State = source.State with
        {
            Revision = new(2),
            Workspace = source.State.Workspace with
            {
                Tasks = source.State.Workspace.Tasks
                    .Select(task => task.Id == "task-a" ? task with { Title = "Changed elsewhere" } : task)
                    .ToArray(),
            },
        };

        var conflicted = boundary.ReviewQueued(payload, admitted.ReviewBasis);

        Assert.Null(conflicted.Plan);
        Assert.False(conflicted.WasRefreshed);
        Assert.Equal("source-conflict", Assert.Single(conflicted.Failures).Code);
    }

    [Fact]
    public void QueuedTaskTitleEditRefreshesWhenAnotherContentFieldChanged()
    {
        var source = TwoTaskSource();
        var payload = ChangeTaskTitlePayload(source.State, "task-a", "Updated A");
        var boundary = new ProposalReviewBoundary(source, TimeProvider.System);
        var admitted = boundary.PrepareQueued(payload);
        source.State = source.State with
        {
            Revision = new(2),
            Workspace = source.State.Workspace with
            {
                Tasks = source.State.Workspace.Tasks
                    .Select(task => task.Id == "task-a" ? task with { DueDate = new(2026, 10, 30) } : task)
                    .ToArray(),
            },
        };

        var refreshed = boundary.ReviewQueued(payload, admitted.ReviewBasis);

        Assert.NotNull(refreshed.Plan);
        Assert.True(refreshed.WasRefreshed);
        Assert.Equal(new WorkspaceRevision(2), refreshed.Plan.SourceWorkspaceRevision);
    }

    [Fact]
    public void ManualChangeReviewRemainsStrictAfterAnUnrelatedChange()
    {
        var source = TwoTaskSource();
        var payload = ChangeTaskTitlePayload(source.State, "task-a", "Updated A");
        source.State = source.State with { Revision = new(2) };

        var reviewed = new ProposalReviewBoundary(source, TimeProvider.System).Review(payload);

        Assert.Null(reviewed.Plan);
        Assert.Equal("stale-source", Assert.Single(reviewed.Failures).Code);
    }

    [Fact]
    public void QueuedChangeReviewNeverRefreshesWithAMissingOrCorruptBasis()
    {
        var source = TwoTaskSource();
        var payload = ChangeTaskTitlePayload(source.State, "task-a", "Updated A");
        var admitted = new ProposalReviewBoundary(source, TimeProvider.System).PrepareQueued(payload);
        using var basisDocument = JsonDocument.Parse(admitted.ReviewBasis);
        var basisRoot = basisDocument.RootElement;
        var shapedInvalidBasis = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            version = basisRoot.GetProperty("version").GetInt32(),
            payloadDigest = basisRoot.GetProperty("payloadDigest").GetString(),
            sourceRevision = basisRoot.GetProperty("sourceRevision").GetString(),
            sourceSnapshotId = basisRoot.GetProperty("sourceSnapshotId").GetString(),
            dependencies = new object?[] { null },
        }));
        source.State = source.State with { Revision = new(2) };
        var boundary = new ProposalReviewBoundary(source, TimeProvider.System);

        var missing = boundary.ReviewQueued(payload, ReadOnlyMemory<byte>.Empty);
        var corrupt = boundary.ReviewQueued(payload, "not a basis"u8.ToArray());
        var shapedInvalid = boundary.ReviewQueued(payload, shapedInvalidBasis);

        Assert.Equal("missing-review-basis", Assert.Single(missing.Failures).Code);
        Assert.Equal("source-conflict", Assert.Single(corrupt.Failures).Code);
        Assert.Equal("source-conflict", Assert.Single(shapedInvalid.Failures).Code);
        Assert.Null(missing.Plan);
        Assert.Null(corrupt.Plan);
        Assert.Null(shapedInvalid.Plan);
    }

    [Fact]
    public void QueuedChangeReviewRejectsACompatibilityBasisFromAnotherPayload()
    {
        var source = TwoTaskSource();
        var payloadA = ChangeTaskTitlePayload(source.State, "task-a", "Updated A");
        var payloadB = ChangeTaskTitlePayload(source.State, "task-a", "Different update A");
        var boundary = new ProposalReviewBoundary(source, TimeProvider.System);
        var basisB = boundary.PrepareQueued(payloadB).ReviewBasis;
        source.State = source.State with
        {
            Revision = new(2),
            Workspace = source.State.Workspace with
            {
                Tasks = source.State.Workspace.Tasks
                    .Select(task => task.Id == "task-b" ? task with { Title = "Updated B" } : task)
                    .ToArray(),
            },
        };

        var reviewed = boundary.ReviewQueued(payloadA, basisB);

        Assert.Null(reviewed.Plan);
        Assert.Equal("source-conflict", Assert.Single(reviewed.Failures).Code);
    }

    [Fact]
    public void QueuedDetachConflictsWhenItsInheritedProjectCategoryChanged()
    {
        var source = new CountingSource(new(
            new(1),
            new(
                [new("category-a", "Category A", 0), new("category-b", "Category B", 1)],
                [new("project", "Project", "", "category-a", null, 0)],
                [new("task", "project", "Task", "", null, null, 0, 0)],
                []),
            [],
            []));
        var payload = DetachTaskPayload(source.State);
        var boundary = new ProposalReviewBoundary(source, TimeProvider.System);
        var admitted = boundary.PrepareQueued(payload);
        Assert.NotNull(admitted.Plan);
        source.State = source.State with
        {
            Revision = new(2),
            Workspace = source.State.Workspace with
            {
                Projects = source.State.Workspace.Projects
                    .Select(project => project with { CategoryId = "category-b" })
                    .ToArray(),
            },
        };

        var reviewed = boundary.ReviewQueued(payload, admitted.ReviewBasis);

        Assert.Null(reviewed.Plan);
        Assert.Equal("source-conflict", Assert.Single(reviewed.Failures).Code);
    }

    private static ProposalPlanResult Review(CountingSource source, object changes)
    {
        var content = PlanningSnapshotProjection.Create(source.State.Revision, source.State.Workspace);
        using var snapshot = JsonDocument.Parse(PlanningSnapshotJsonGenerator.Generate(content).Payload!.Value);
        var input = JsonSerializer.Serialize(new
        {
            format = "dot-orbit-change-proposal",
            schemaVersion = 1,
            sourceSnapshotId = snapshot.RootElement.GetProperty("snapshotId").GetString(),
            sourceWorkspaceRevision = "1",
            operations = new[] { new { operation = "editTask", operationId = "synthetic-edit", task = new { existingId = "t" }, changes } },
        });
        return new ProposalReviewBoundary(source, TimeProvider.System).Review(Encoding.UTF8.GetBytes(input));
    }

    private static CountingSource Source(string description) => new(new(new(1),
        new([new("c", "Synthetic category", 0)], [],
            [new("t", null, "Synthetic task", description, "c", null, 0, null)], []), [], []));

    private static CountingSource TwoTaskSource() => new(new(
        new(1),
        new(
            [new("c", "Synthetic category", 0)],
            [],
            [
                new("task-a", null, "Task A", "", "c", null, 0, null),
                new("task-b", null, "Task B", "", "c", null, 1, null),
            ],
            []),
        [],
        []));

    private static byte[] ChangeTaskTitlePayload(
        ProposalWorkspaceState state,
        string taskId,
        string title)
    {
        var content = PlanningSnapshotProjection.Create(state.Revision, state.Workspace);
        using var snapshot = JsonDocument.Parse(PlanningSnapshotJsonGenerator.Generate(content).Payload!.Value);
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            format = "dot-orbit-change-proposal",
            schemaVersion = 1,
            sourceSnapshotId = snapshot.RootElement.GetProperty("snapshotId").GetString(),
            sourceWorkspaceRevision = state.Revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            operations = new[]
            {
                new
                {
                    operation = "editTask",
                    operationId = "synthetic-edit",
                    task = new { existingId = taskId },
                    changes = new { title },
                },
            },
        }));
    }

    private static byte[] DetachTaskPayload(ProposalWorkspaceState state)
    {
        var content = PlanningSnapshotProjection.Create(state.Revision, state.Workspace);
        using var snapshot = JsonDocument.Parse(PlanningSnapshotJsonGenerator.Generate(content).Payload!.Value);
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            format = "dot-orbit-change-proposal",
            schemaVersion = 1,
            sourceSnapshotId = snapshot.RootElement.GetProperty("snapshotId").GetString(),
            sourceWorkspaceRevision = state.Revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            operations = new[]
            {
                new
                {
                    operation = "detachTask",
                    operationId = "synthetic-detach",
                    task = new { existingId = "task" },
                },
            },
        }));
    }

    private static byte[] CompleteTaskPayload(ProposalWorkspaceState state, string taskId)
    {
        var content = PlanningSnapshotProjection.Create(state.Revision, state.Workspace);
        using var snapshot = JsonDocument.Parse(PlanningSnapshotJsonGenerator.Generate(content).Payload!.Value);
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            format = "dot-orbit-change-proposal",
            schemaVersion = 1,
            sourceSnapshotId = snapshot.RootElement.GetProperty("snapshotId").GetString(),
            sourceWorkspaceRevision = state.Revision.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            operations = new[]
            {
                new
                {
                    operation = "completeTask",
                    operationId = "synthetic-complete",
                    task = new { existingId = taskId },
                },
            },
        }));
    }

    private sealed class CountingSource(ProposalWorkspaceState state) : IProposalWorkspaceSource
    {
        public ProposalWorkspaceState State { get; set; } = state;
        public int ReadCount { get; private set; }
        public ProposalWorkspaceState Read() { ReadCount++; return State; }
    }
}
