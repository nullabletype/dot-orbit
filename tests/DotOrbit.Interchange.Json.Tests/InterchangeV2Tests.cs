using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using Xunit;

namespace DotOrbit.Interchange.Json.Tests;

public sealed class InterchangeV2Tests
{
    [Theory]
    [InlineData("planned", TodayLane.Planned)]
    [InlineData("inProgress", TodayLane.InProgress)]
    [InlineData("none", null)]
    public void ChangeParsesEachExplicitTodayTransition(string lane, TodayLane? expected)
    {
        var parsed = ChangeProposalJsonParser.Parse(ChangePayload(State(TodayLane.Planned), TodayOperation(lane)));

        Assert.True(parsed.IsValid, parsed.ErrorCode);
        Assert.Equal(2, parsed.Proposal!.SchemaVersion);
        var operation = Assert.IsType<SetTaskTodayOperation>(Assert.Single(parsed.Proposal.Operations));
        Assert.Equal(ProposalReference.Existing("task-a"), operation.Task);
        Assert.Equal(expected, operation.TodayLane);
        Assert.Equal("review metadata", operation.Explanation);
    }

    [Theory]
    [InlineData(false, "planned", TodayLane.Planned)]
    [InlineData(false, "inProgress", TodayLane.InProgress)]
    [InlineData(false, "null", null)]
    [InlineData(false, "absent", null)]
    [InlineData(true, "planned", TodayLane.Planned)]
    [InlineData(true, "inProgress", TodayLane.InProgress)]
    [InlineData(true, "null", null)]
    [InlineData(true, "absent", null)]
    public void CreateTaskParsesOptionalTodayPlacementForBothProposalKinds(bool newWork, string lane, TodayLane? expected)
    {
        var operation = CreateTask(lane, newWork);
        var input = newWork ? NewWorkPayload(operation) : ChangePayload(State(), operation);

        var parsed = ChangeProposalJsonParser.Parse(input);

        Assert.True(parsed.IsValid, parsed.ErrorCode);
        Assert.Equal(newWork ? ProposalKind.NewWork : ProposalKind.WorkspaceChange, parsed.Proposal!.Kind);
        Assert.Equal(expected, Assert.IsType<CreateTaskOperation>(Assert.Single(parsed.Proposal.Operations)).TodayLane);
    }

    [Theory]
    [InlineData("Planned")]
    [InlineData("inprogress")]
    [InlineData("")]
    [InlineData("tomorrow")]
    public void TodayLanesRejectUnknownValuesAndCasing(string lane)
    {
        Assert.Equal("invalid-schema", ChangeProposalJsonParser.Parse(ChangePayload(State(), TodayOperation(lane))).ErrorCode);
        Assert.Equal("invalid-schema", ChangeProposalJsonParser.Parse(NewWorkPayload(CreateTask(lane, true))).ErrorCode);
    }

    [Fact]
    public void TodayChangeRequiresExistingReferenceAndExplicitNonNullLane()
    {
        var local = TodayOperation("planned");
        local["task"] = new JsonObject { ["localId"] = "task-a" };
        var missing = TodayOperation("planned");
        missing.Remove("todayLane");
        var nullLane = TodayOperation("planned");
        nullLane["todayLane"] = null;
        var ambiguous = TodayOperation("planned");
        ambiguous["task"]!["localId"] = "also-local";
        var unknown = TodayOperation("planned");
        unknown["sharedPosition"] = 0;

        foreach (var operation in new[] { local, missing, nullLane, ambiguous, unknown })
            Assert.Equal("invalid-schema", ChangeProposalJsonParser.Parse(ChangePayload(State(), operation)).ErrorCode);
        Assert.Equal("invalid-schema", ChangeProposalJsonParser.Parse(NewWorkPayload(TodayOperation("planned"))).ErrorCode);
        Assert.Equal("invalid-schema", ChangeProposalJsonParser.Parse(NewWorkPayload(CreateTask("none", true))).ErrorCode);
    }

    [Fact]
    public void V1NeverAcquiresTodayAuthorityIncludingExplicitNull()
    {
        Assert.Equal("invalid-schema", ChangeProposalJsonParser.Parse(ChangePayload(State(), TodayOperation("planned"), 1)).ErrorCode);
        Assert.Equal("invalid-schema", ChangeProposalJsonParser.Parse(ChangePayload(State(), CreateTask("planned", false), 1)).ErrorCode);
        Assert.Equal("invalid-schema", ChangeProposalJsonParser.Parse(NewWorkPayload(CreateTask("null", true), 1)).ErrorCode);
        var omitted = ChangeProposalJsonParser.Parse(NewWorkPayload(CreateTask("absent", true), 1));
        Assert.True(omitted.IsValid);
        Assert.Null(Assert.IsType<CreateTaskOperation>(Assert.Single(omitted.Proposal!.Operations)).TodayLane);
    }

    [Fact]
    public void V2StillRejectsDuplicateMembersAndUnknownFields()
    {
        var payload = Encoding.UTF8.GetString(ChangePayload(State(), TodayOperation("planned")));
        var duplicate = payload.Replace("\"todayLane\":\"planned\"", "\"todayLane\":\"planned\",\"todayLane\":\"none\"", StringComparison.Ordinal);
        var unknown = TodayOperation("planned");
        unknown["unknown"] = "sensitive marker";

        Assert.Equal("duplicate-member", ChangeProposalJsonParser.Parse(duplicate).ErrorCode);
        var rejected = ChangeProposalJsonParser.Parse(ChangePayload(State(), unknown));
        Assert.Equal("invalid-schema", rejected.ErrorCode);
        Assert.DoesNotContain("sensitive marker", rejected.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LatestSnapshotAndInstructionsUseV2WhileV1InstructionsRemainByteIdentical()
    {
        var content = PlanningSnapshotProjection.Create(State().Revision, State().Workspace);
        var latest = PlanningSnapshotJsonGenerator.Generate(content);
        var v1 = PlanningSnapshotJsonGenerator.Generate(content, 1);
        using var latestSnapshot = JsonDocument.Parse(latest.Payload!.Value);
        using var v1Snapshot = JsonDocument.Parse(v1.Payload!.Value);

        Assert.Equal(2, latestSnapshot.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(1, v1Snapshot.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("6a548b78bd33133c6f91bb3c0bb1e02797f52f8a11ea875c3687ff357de04ec2", Convert.ToHexStringLower(SHA256.HashData(v1.Payload.Value.Span)));
        Assert.True(InterchangeSchemaCatalogue.Validate(AgentInterchangeV2.PlanningSnapshotSchemaId, latestSnapshot.RootElement).IsValid);
        Assert.NotEqual(v1Snapshot.RootElement.GetProperty("snapshotId").GetString(), latestSnapshot.RootElement.GetProperty("snapshotId").GetString());
        Assert.Equal("b7e0d0ca95b581117f396695e0647b3c47560f0423d6d6b251bb2af9c758ae76", Convert.ToHexStringLower(SHA256.HashData(
            AgentInterchangeDocumentComposer.ComposeReusableInstructionsAndSchemas(1).Payload!.Value.Span)));
        var instructions = Encoding.UTF8.GetString(AgentInterchangeDocumentComposer.ComposeReusableInstructionsAndSchemas().Payload!.Value.Span);
        Assert.Contains("Always use schemaVersion 2.", instructions, StringComparison.Ordinal);
        Assert.Contains("setTaskToday", instructions, StringComparison.Ordinal);
        Assert.Contains("Only active incomplete Tasks may end in Today", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("Do not propose Today membership", instructions, StringComparison.Ordinal);
        foreach (var id in new[] { AgentInterchangeV2.PlanningSnapshotSchemaId, AgentInterchangeV2.ChangeProposalSchemaId, AgentInterchangeV2.NewWorkProposalSchemaId })
            Assert.Contains(Encoding.UTF8.GetString(InterchangeSchemaCatalogue.GetApplicationSchema(id).Span), instructions, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void IdentityVerifierAndReviewBindTheProposalsOwnVersion(int version)
    {
        var state = State();
        var content = PlanningSnapshotProjection.Create(state.Revision, state.Workspace);
        using var own = JsonDocument.Parse(PlanningSnapshotJsonGenerator.Generate(content, version).Payload!.Value);
        using var other = JsonDocument.Parse(PlanningSnapshotJsonGenerator.Generate(content, version == 1 ? 2 : 1).Payload!.Value);
        var ownId = own.RootElement.GetProperty("snapshotId").GetString()!;
        var otherId = other.RootElement.GetProperty("snapshotId").GetString()!;
        var verifier = new PlanningSnapshotIdentityVerifier(version);
        Assert.True(verifier.Matches(content, ownId));
        Assert.False(verifier.Matches(content, otherId));
        var payload = ChangePayload(state, EditOperation(), version);
        var boundary = new ProposalReviewBoundary(new MutableSource(state), TimeProvider.System);
        Assert.NotNull(boundary.Review(payload).Plan);
        var mismatched = JsonNode.Parse(payload)!;
        mismatched["sourceSnapshotId"] = otherId;
        var rejected = boundary.Review(JsonSerializer.SerializeToUtf8Bytes(mismatched));
        Assert.Null(rejected.Plan);
        Assert.Equal("stale-source", Assert.Single(rejected.Failures).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void ExplicitVersionApisRejectUnsupportedContracts(int version)
    {
        var content = PlanningSnapshotProjection.Create(State().Revision, State().Workspace);
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanningSnapshotJsonGenerator.Generate(content, version));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanningSnapshotJsonGenerator.Generate(new UnreadableSnapshotSource(), version));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentInterchangeDocumentComposer.ComposeReusableInstructionsAndSchemas(version));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentInterchangeDocumentComposer.ComposeWithSnapshot("{}"u8.ToArray(), version));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanningInterchangeCopyComposer.ComposeSnapshot(content, false, version));
    }

    [Fact]
    public void V2ExamplesConformAndSnapshotIdentityIsCorrect()
    {
        foreach (var (name, id) in new[]
        {
            ("planning-snapshot", AgentInterchangeV2.PlanningSnapshotSchemaId),
            ("change-proposal", AgentInterchangeV2.ChangeProposalSchemaId),
            ("new-work-proposal", AgentInterchangeV2.NewWorkProposalSchemaId),
        })
        {
            var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Examples", "v2", name + ".json"));
            using var document = JsonDocument.Parse(bytes);
            Assert.True(InterchangeSchemaCatalogue.Validate(id, document.RootElement).IsValid);
            if (name == "planning-snapshot")
            {
                var root = JsonNode.Parse(bytes)!.AsObject();
                var identity = root["snapshotId"]!.GetValue<string>();
                root.Remove("snapshotId");
                Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(Rfc8785Canonicalizer.Canonicalize(Encoding.UTF8.GetBytes(root.ToJsonString())))), identity);
            }
            else
            {
                Assert.True(ChangeProposalJsonParser.Parse(bytes).IsValid);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void QueueRefreshRetainsEachVersionAndOriginalBasisLayout(int version)
    {
        var source = new MutableSource(State());
        var boundary = new ProposalReviewBoundary(source, TimeProvider.System);
        var payload = ChangePayload(source.State, EditOperation(), version);
        var admitted = boundary.PrepareQueued(payload);
        Assert.NotNull(admitted.Plan);
        if (version == 1)
            Assert.Equal("f72e4ed61165fd5c351fd3fd284ccb5969ef965add8bfc9fc823af69c75ef773", Convert.ToHexStringLower(SHA256.HashData(admitted.ReviewBasis.Span)));
        using var basis = JsonDocument.Parse(admitted.ReviewBasis);
        Assert.Equal(1, basis.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(["version", "payloadDigest", "sourceRevision", "sourceSnapshotId", "dependencies"], basis.RootElement.EnumerateObject().Select(property => property.Name));
        source.State = source.State with { Revision = new(2) };

        var refreshed = boundary.ReviewQueued(payload, admitted.ReviewBasis);

        Assert.NotNull(refreshed.Plan);
        Assert.True(refreshed.WasRefreshed);
        Assert.Equal(new WorkspaceRevision(2), refreshed.Plan.SourceWorkspaceRevision);
        using var snapshot = JsonDocument.Parse(PlanningSnapshotJsonGenerator.Generate(
            PlanningSnapshotProjection.Create(source.State.Revision, source.State.Workspace), version).Payload!.Value);
        Assert.Equal(snapshot.RootElement.GetProperty("snapshotId").GetString(), refreshed.Plan.SourceSnapshotId);
    }

    [Fact]
    public void QueuedTodayChangeRefreshesAfterUnrelatedOrderOrRecordChanges()
    {
        var source = new MutableSource(State());
        var payload = ChangePayload(source.State, TodayOperation("planned"));
        var boundary = new ProposalReviewBoundary(source, TimeProvider.System);
        var admitted = boundary.PrepareQueued(payload);
        Assert.NotNull(admitted.Plan);
        source.State = source.State with
        {
            Revision = new(2),
            Workspace = source.State.Workspace with
            {
                Tasks = source.State.Workspace.Tasks.Select(task => task.Id == "task-a"
                    ? task with { SharedPosition = 1 }
                    : task with { SharedPosition = 0, Title = "Updated B" }).ToArray(),
            },
        };

        var refreshed = boundary.ReviewQueued(payload, admitted.ReviewBasis);

        Assert.NotNull(refreshed.Plan);
        Assert.True(refreshed.WasRefreshed);
        var effect = Assert.Single(refreshed.Plan.Preview, effect => effect.Kind == "task-today");
        Assert.Equal("Not in Today", effect.Before);
        Assert.Equal("Today: Planned", effect.After);
    }

    [Theory]
    [InlineData("lane")]
    [InlineData("completion")]
    [InlineData("archive")]
    [InlineData("missing")]
    [InlineData("parent")]
    public void QueuedTodayChangeConflictsWhenItsTaskOrParentStateChanges(string change)
    {
        var state = State();
        state = state with
        {
            Workspace = state.Workspace with
            {
                Projects = [new("project", "Project", "", "c", null, 0)],
                Tasks = state.Workspace.Tasks.Select(task => task.Id == "task-a" ? task with { ProjectId = "project", ProjectPosition = 0 } : task).ToArray(),
            },
        };
        var source = new MutableSource(state);
        var payload = ChangePayload(source.State, TodayOperation("planned"));
        var boundary = new ProposalReviewBoundary(source, TimeProvider.System);
        var admitted = boundary.PrepareQueued(payload);
        Assert.NotNull(admitted.Plan);
        source.State = source.State with
        {
            Revision = new(2),
            Workspace = source.State.Workspace with
            {
                Tasks = source.State.Workspace.Tasks.Where(task => change != "missing" || task.Id != "task-a").Select(task => task.Id != "task-a" ? task : change switch
                {
                    "lane" => task with { TodayLane = TodayLane.InProgress },
                    "completion" => task with { CompletedAt = DateTimeOffset.UnixEpoch, CompletionDate = new(2026, 10, 29) },
                    "archive" => task with { ArchivedAt = DateTimeOffset.UnixEpoch, ArchiveDate = new(2026, 10, 29) },
                    _ => task,
                }).ToArray(),
                Projects = source.State.Workspace.Projects.Select(project => change == "parent" ? project with { ArchivedAt = DateTimeOffset.UnixEpoch, ArchiveDate = new(2026, 10, 29) } : project).ToArray(),
            },
        };

        var reviewed = boundary.ReviewQueued(payload, admitted.ReviewBasis);

        Assert.Null(reviewed.Plan);
        Assert.False(reviewed.WasRefreshed);
        Assert.Equal("source-conflict", Assert.Single(reviewed.Failures).Code);
    }

    [Fact]
    public void V1QueueBasisCannotAuthorizeV2Payload()
    {
        var source = new MutableSource(State());
        var boundary = new ProposalReviewBoundary(source, TimeProvider.System);
        var v1 = ChangePayload(source.State, EditOperation(), 1);
        var basis = boundary.PrepareQueued(v1).ReviewBasis;
        var v2 = ChangePayload(source.State, TodayOperation("planned"));
        source.State = source.State with { Revision = new(2) };

        var reviewed = boundary.ReviewQueued(v2, basis);

        Assert.Null(reviewed.Plan);
        Assert.Equal("source-conflict", Assert.Single(reviewed.Failures).Code);
    }

    [Theory]
    [InlineData(null, "planned", "Not in Today", "Today: Planned")]
    [InlineData(TodayLane.Planned, "inProgress", "Today: Planned", "Today: In progress")]
    [InlineData(TodayLane.InProgress, "none", "Today: In progress", "Not in Today")]
    public void ManualReviewPresentsEveryTodayTransitionAsADirectEffect(TodayLane? before, string requested, string beforeText, string afterText)
    {
        var state = State(before);
        var result = new ProposalReviewBoundary(new MutableSource(state), TimeProvider.System)
            .Review(ChangePayload(state, TodayOperation(requested)));

        Assert.NotNull(result.Plan);
        var effect = Assert.Single(result.Plan.Preview, effect => effect.Kind == "task-today");
        Assert.Equal("Task A", effect.TargetLabel);
        Assert.Equal(ProposalReference.Existing("task-a"), effect.Target);
        Assert.Equal(beforeText, effect.Before);
        Assert.Equal(afterText, effect.After);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReviewIncludesTheNewTasksExplicitTodayPlacement(bool newWork)
    {
        var create = CreateTask("inProgress", newWork);
        var payload = newWork
            ? JsonSerializer.SerializeToUtf8Bytes(new JsonObject
            {
                ["format"] = AgentInterchangeV1.NewWorkProposalFormat,
                ["schemaVersion"] = 2,
                ["operations"] = new JsonArray(new JsonObject
                {
                    ["operation"] = "createCategory",
                    ["operationId"] = "category",
                    ["localId"] = "c",
                    ["name"] = "New category",
                }, create),
            })
            : ChangePayload(State(), create);

        var reviewed = new ProposalReviewBoundary(new MutableSource(State()), TimeProvider.System).Review(payload);

        Assert.NotNull(reviewed.Plan);
        var effect = Assert.Single(reviewed.Plan.Preview, effect => effect.Kind == "task-today");
        Assert.Equal(ProposalReference.Local("new-task"), effect.Target);
        Assert.Equal("New task", effect.TargetLabel);
        Assert.Equal("Not in Today", effect.Before);
        Assert.Equal("Today: In progress", effect.After);
    }

    [Theory]
    [InlineData("no-op", "no-op")]
    [InlineData("completed", "invalid-today-state")]
    [InlineData("archived", "invalid-reference")]
    [InlineData("binned", "invalid-reference")]
    [InlineData("missing", "invalid-reference")]
    public void ManualReviewRejectsNoOpAndIneligibleTodayChanges(string scenario, string expectedCode)
    {
        var state = State(scenario == "no-op" ? TodayLane.Planned : null);
        var task = state.Workspace.Tasks[0];
        state = state with
        {
            Workspace = state.Workspace with
            {
                Tasks = state.Workspace.Tasks.Where(task => task.Id != "task-a" || scenario is not ("missing" or "binned"))
                    .Select(task => task.Id != "task-a" ? task : scenario switch
                    {
                        "completed" => task with { CompletedAt = DateTimeOffset.UnixEpoch, CompletionDate = new(2026, 10, 29) },
                        "archived" => task with { ArchivedAt = DateTimeOffset.UnixEpoch, ArchiveDate = new(2026, 10, 29) },
                        _ => task,
                    }).ToArray(),
            },
            TaskBin = scenario == "binned" ? [new(task, DateTimeOffset.UnixEpoch, null, "Synthetic category", true, null)] : [],
        };

        var reviewed = new ProposalReviewBoundary(new MutableSource(state), TimeProvider.System)
            .Review(ChangePayload(state, TodayOperation("planned")));

        Assert.Null(reviewed.Plan);
        Assert.Equal(expectedCode, Assert.Single(reviewed.Failures).Code);
    }

    [Theory]
    [InlineData("setTaskToday", "facet-conflict")]
    [InlineData("completeTask", "today-conflict")]
    [InlineData("archiveTask", "archive-incomplete-task")]
    [InlineData("moveTaskToBin", "today-conflict")]
    public void ManualReviewRejectsRepeatedAndContradictoryFinalTodayState(string secondOperation, string expectedCode)
    {
        var second = secondOperation == "setTaskToday" ? TodayOperation("inProgress") : new JsonObject
        {
            ["operation"] = secondOperation,
            ["task"] = new JsonObject { ["existingId"] = "task-a" },
        };
        second["operationId"] = "second";
        var payload = JsonNode.Parse(ChangePayload(State(), TodayOperation("planned")))!;
        payload["operations"]!.AsArray().Add(second);

        var reviewed = new ProposalReviewBoundary(new MutableSource(State()), TimeProvider.System)
            .Review(JsonSerializer.SerializeToUtf8Bytes(payload));

        Assert.Null(reviewed.Plan);
        Assert.Equal(expectedCode, Assert.Single(reviewed.Failures).Code);
    }

    [Theory]
    [InlineData(512, true)]
    [InlineData(513, false)]
    public void V2KeepsThePublishedOperationBound(int count, bool valid)
    {
        var root = JsonNode.Parse(ChangePayload(State(), TodayOperation("planned")))!;
        root["operations"] = new JsonArray(Enumerable.Range(0, count).Select(index =>
        {
            var operation = TodayOperation("planned");
            operation["operationId"] = "operation-" + index;
            return (JsonNode)operation;
        }).ToArray());

        var parsed = ChangeProposalJsonParser.Parse(JsonSerializer.SerializeToUtf8Bytes(root));

        Assert.Equal(valid, parsed.IsValid);
        if (valid) Assert.Equal(count, parsed.Proposal!.Operations.Count);
        else Assert.Equal("invalid-schema", parsed.ErrorCode);
    }

    [Theory]
    [InlineData(null, "none")]
    [InlineData(TodayLane.Planned, "planned")]
    [InlineData(TodayLane.InProgress, "inProgress")]
    public void EveryUnchangedTodayLaneIsRejected(TodayLane? current, string requested)
    {
        var state = State(current);
        var reviewed = new ProposalReviewBoundary(new MutableSource(state), TimeProvider.System)
            .Review(ChangePayload(state, TodayOperation(requested)));

        Assert.Null(reviewed.Plan);
        Assert.Equal("no-op", Assert.Single(reviewed.Failures).Code);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void TodayLaneRejectsEveryNonStringNonNullJsonKind(string raw)
    {
        var change = TodayOperation("planned");
        change["todayLane"] = JsonNode.Parse(raw);
        var create = CreateTask("planned", true);
        create["todayLane"] = JsonNode.Parse(raw);

        Assert.Equal("invalid-schema", ChangeProposalJsonParser.Parse(ChangePayload(State(), change)).ErrorCode);
        Assert.Equal("invalid-schema", ChangeProposalJsonParser.Parse(NewWorkPayload(create)).ErrorCode);
    }

    private static JsonObject TodayOperation(string lane) => new()
    {
        ["operation"] = "setTaskToday",
        ["operationId"] = "today",
        ["task"] = new JsonObject { ["existingId"] = "task-a" },
        ["todayLane"] = lane,
        ["explanation"] = "review metadata",
    };

    private static JsonObject EditOperation() => new()
    {
        ["operation"] = "editTask",
        ["operationId"] = "edit",
        ["task"] = new JsonObject { ["existingId"] = "task-a" },
        ["changes"] = new JsonObject { ["title"] = "Updated A" },
    };

    private static JsonObject CreateTask(string lane, bool local)
    {
        var operation = new JsonObject
        {
            ["operation"] = "createTask",
            ["operationId"] = "create",
            ["localId"] = "new-task",
            ["title"] = "New task",
            ["description"] = "",
            ["project"] = null,
            ["category"] = new JsonObject { ["mode"] = "explicit", ["category"] = new JsonObject { [local ? "localId" : "existingId"] = "c" } },
            ["dueDate"] = null,
            ["participants"] = new JsonArray(),
        };
        if (lane != "absent") operation["todayLane"] = lane == "null" ? null : lane;
        return operation;
    }

    private static byte[] NewWorkPayload(JsonObject operation, int version = 2) => JsonSerializer.SerializeToUtf8Bytes(new JsonObject
    {
        ["format"] = AgentInterchangeV1.NewWorkProposalFormat,
        ["schemaVersion"] = version,
        ["operations"] = new JsonArray(operation),
    });

    private static byte[] ChangePayload(ProposalWorkspaceState state, JsonObject operation, int version = 2)
    {
        var content = PlanningSnapshotProjection.Create(state.Revision, state.Workspace);
        using var snapshot = JsonDocument.Parse(PlanningSnapshotJsonGenerator.Generate(content, version).Payload!.Value);
        return JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["format"] = AgentInterchangeV1.ChangeProposalFormat,
            ["schemaVersion"] = version,
            ["sourceSnapshotId"] = snapshot.RootElement.GetProperty("snapshotId").GetString(),
            ["sourceWorkspaceRevision"] = state.Revision.ToString(),
            ["operations"] = new JsonArray(operation),
        });
    }

    private static ProposalWorkspaceState State(TodayLane? lane = null) => new(new(1),
        new([new("c", "Synthetic category", 0)], [],
            [new("task-a", null, "Task A", "", "c", null, 0, null, TodayLane: lane), new("task-b", null, "Task B", "", "c", null, 1, null)], []), [], []);

    private sealed class UnreadableSnapshotSource : IPlanningSnapshotSource
    {
        public PlanningSnapshotContent Read() => throw new InvalidOperationException("An unsupported version must not read workspace content.");
    }

    private sealed class MutableSource(ProposalWorkspaceState state) : IProposalWorkspaceSource
    {
        public ProposalWorkspaceState State { get; set; } = state;
        public ProposalWorkspaceState Read() => State;
    }
}
