using Xunit;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;

namespace DotOrbit.Core.Tests;

public sealed class ProposalSemanticPlannerTests
{
    private const string Identity = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly ProposalReference TaskA = ProposalReference.Existing("a");
    private static readonly ProposalReference Project = ProposalReference.Existing("p");
    private static ProposalWorkspaceState Source() => new(new(1), new(
        [new("c", "Category", 0), new("d", "Other", 1)],
        [new("p", "Project", "", "c", null, 0), new("q", "Other project", "", "d", null, 1)],
        [new("a", "p", "Task A", "", null, null, 0, 0, TodayLane: TodayLane.Planned),
         new("hidden", "p", "Archived task", "", null, null, 1, 1,
             CompletedAt: DateTimeOffset.UnixEpoch, CompletionDate: new(2020, 1, 1),
             ArchivedAt: DateTimeOffset.UnixEpoch, ArchiveDate: new(2020, 1, 1)),
         new("b", "p", "Task B", "", null, null, 2, 2)], []), [], []);
    private static ProposalPlanResult Validate(params ChangeOperation[] operations) =>
        new ProposalSemanticPlanner(TimeProvider.System, new Markdown()).Validate(
            new(AgentInterchangeV1.ChangeProposalFormat, 1, Identity, new(1), operations), Source(), Identity, "digest");
    private static ProposalPlanResult ValidateNewWork(params ChangeOperation[] operations) =>
        new ProposalSemanticPlanner(TimeProvider.System, new Markdown()).Validate(
            new(
                ProposalKind.NewWork,
                AgentInterchangeV1.NewWorkProposalFormat,
                1,
                null,
                null,
                operations),
            Source(),
            null,
            "digest");
    private static ProposalPlanResult ValidateV2(ProposalWorkspaceState source, params ChangeOperation[] operations) =>
        new ProposalSemanticPlanner(TimeProvider.System, new Markdown()).Validate(
            new(AgentInterchangeV1.ChangeProposalFormat, 2, Identity, source.Revision, operations), source, Identity, "digest");

    [Fact]
    public void VersionOneCannotAcquireTodayAuthorityThroughTypedOperations()
    {
        Assert.Equal("unsupported-operation", Assert.Single(Validate(new SetTaskTodayOperation("1", TaskA, null)).Failures).Code);
        Assert.Equal("unsupported-operation", Assert.Single(Validate(new CreateTaskOperation("1", "new", "Task", "", null,
            new(ProposedTaskCategoryMode.Explicit, ProposalReference.Existing("c")), null, [], TodayLane: TodayLane.Planned)).Failures).Code);
    }

    [Theory]
    [InlineData(null, TodayLane.Planned, "Not in Today", "Today: Planned")]
    [InlineData(null, TodayLane.InProgress, "Not in Today", "Today: In progress")]
    [InlineData(TodayLane.Planned, TodayLane.InProgress, "Today: Planned", "Today: In progress")]
    [InlineData(TodayLane.InProgress, TodayLane.Planned, "Today: In progress", "Today: Planned")]
    [InlineData(TodayLane.Planned, null, "Today: Planned", "Not in Today")]
    [InlineData(TodayLane.InProgress, null, "Today: In progress", "Not in Today")]
    public void VersionTwoPreviewsEveryTodayTransitionWithoutReordering(TodayLane? before, TodayLane? after, string beforeText, string afterText)
    {
        var source = Source();
        source = source with { Workspace = source.Workspace with
        { Tasks = source.Workspace.Tasks.Select(task => task.Id == "a" ? task with { TodayLane = before } : task).ToArray() } };
        var result = ValidateV2(source, new SetTaskTodayOperation("1", TaskA, after, "Reviewed placement"));
        var effect = Assert.Single(result.Plan!.Preview, item => item.Kind == "task-today");
        Assert.Equal(TaskA, effect.Target);
        Assert.Equal(beforeText, effect.Before);
        Assert.Equal(afterText, effect.After);
        Assert.Contains(result.Plan.Preview, item => item.Kind == "explanation");
        Assert.DoesNotContain(result.Plan.Preview, item => item.Kind.Contains("order", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(TodayLane.Planned)]
    [InlineData(TodayLane.InProgress)]
    public void VersionTwoNewWorkCreatesWithOptionalTodayPlacement(TodayLane? lane)
    {
        var result = new ProposalSemanticPlanner(TimeProvider.System, new Markdown()).Validate(
            new(ProposalKind.NewWork, AgentInterchangeV1.NewWorkProposalFormat, 2, null, null,
                [new CreateCategoryOperation("1", "category", "Fresh"),
                 new CreateTaskOperation("2", "task", "Task", "", null,
                     new(ProposedTaskCategoryMode.Explicit, ProposalReference.Local("category")), null, [], TodayLane: lane)]),
            Source(), null, "digest");
        var task = Assert.IsType<CreateTaskOperation>(result.Plan!.Operations[1]);
        Assert.Equal(lane, task.TodayLane);
        if (lane is null) Assert.DoesNotContain(result.Plan.Preview, item => item.Kind == "task-today");
        else
        {
            var effect = Assert.Single(result.Plan.Preview, item => item.Kind == "task-today");
            Assert.Equal("task", effect.Target.LocalId);
            Assert.Equal("Not in Today", effect.Before);
            Assert.Equal(lane == TodayLane.Planned ? "Today: Planned" : "Today: In progress", effect.After);
        }
    }

    [Fact]
    public void TodayRequiresExistingActiveIncompleteTaskAndDefinedLane()
    {
        Assert.Equal("invalid-reference", Assert.Single(ValidateV2(Source(), new SetTaskTodayOperation("1", ProposalReference.Existing("missing"), TodayLane.Planned)).Failures).Code);
        Assert.Equal("invalid-reference", Assert.Single(ValidateV2(Source(), new SetTaskTodayOperation("1", ProposalReference.Existing("hidden"), TodayLane.Planned)).Failures).Code);
        Assert.Equal("invalid-reference", Assert.Single(ValidateV2(Source(), new SetTaskTodayOperation("1", Project, TodayLane.Planned)).Failures).Code);
        Assert.Equal("invalid-today-lane", Assert.Single(ValidateV2(Source(), new SetTaskTodayOperation("1", TaskA, (TodayLane)99)).Failures).Code);
        Assert.Equal("invalid-today-lane", Assert.Single(ValidateV2(Source(), new CreateTaskOperation("1", "new", "Task", "", null,
            new(ProposedTaskCategoryMode.Explicit, ProposalReference.Existing("c")), null, [], TodayLane: (TodayLane)99)).Failures).Code);
        var source = Source();
        source = source with { Workspace = source.Workspace with { Tasks = source.Workspace.Tasks.Select(task => task.Id == "a"
            ? task with { CompletedAt = DateTimeOffset.UnixEpoch, CompletionDate = new(2020, 1, 1), TodayLane = null } : task).ToArray() } };
        Assert.Equal("invalid-today-state", Assert.Single(ValidateV2(source, new SetTaskTodayOperation("1", TaskA, TodayLane.Planned)).Failures).Code);
        var reopened = ValidateV2(source, new ReopenTaskOperation("1", TaskA), new SetTaskTodayOperation("2", TaskA, TodayLane.InProgress));
        Assert.NotNull(reopened.Plan);
        Assert.Contains(reopened.Plan.Preview, item => item.Kind == "task-today" && item.After == "Today: In progress");
    }

    [Fact]
    public void TodayCannotTargetBinnedTaskOrChildOfArchivedProject()
    {
        var source = Source();
        var binned = source.Workspace.Tasks.Single(task => task.Id == "a");
        var binSource = source with
        {
            Workspace = source.Workspace with { Tasks = source.Workspace.Tasks.Where(task => task.Id != "a").ToArray() },
            TaskBin = [new(binned, DateTimeOffset.UnixEpoch, "Project", "Category", true, null)],
        };
        Assert.Equal("invalid-reference", Assert.Single(ValidateV2(binSource, new SetTaskTodayOperation("1", TaskA, TodayLane.InProgress)).Failures).Code);
        var archivedSource = source with
        {
            Workspace = source.Workspace with
            {
                Projects = source.Workspace.Projects.Select(project => project.Id == "p"
                    ? project with { ArchivedAt = DateTimeOffset.UnixEpoch, ArchiveDate = new(2020, 1, 1) } : project).ToArray(),
                Tasks = source.Workspace.Tasks.Select(task => task with { TodayLane = null }).ToArray(),
            },
        };
        Assert.Equal("invalid-reference", Assert.Single(ValidateV2(archivedSource, new SetTaskTodayOperation("1", TaskA, TodayLane.Planned)).Failures).Code);
    }

    [Fact]
    public void TodayExistingOperationCannotTargetCreatedLocalTaskOrAppearInNewWork()
    {
        var operation = new SetTaskTodayOperation("2", ProposalReference.Local("new"), TodayLane.Planned);
        var result = ValidateV2(Source(), new CreateTaskOperation("1", "new", "Task", "", null,
            new(ProposedTaskCategoryMode.Explicit, ProposalReference.Existing("c")), null, []), operation);
        Assert.Equal("invalid-reference", Assert.Single(result.Failures).Code);
        var newWork = new ProposalSemanticPlanner(TimeProvider.System, new Markdown()).Validate(
            new(ProposalKind.NewWork, AgentInterchangeV1.NewWorkProposalFormat, 2, null, null,
                [new SetTaskTodayOperation("1", TaskA, TodayLane.InProgress)]), Source(), null, "digest");
        Assert.Equal("invalid-new-work", Assert.Single(newWork.Failures).Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(TodayLane.Planned)]
    [InlineData(TodayLane.InProgress)]
    public void UnchangedTodayStateIsRejected(TodayLane? lane)
    {
        var source = Source();
        source = source with { Workspace = source.Workspace with
        { Tasks = source.Workspace.Tasks.Select(task => task.Id == "a" ? task with { TodayLane = lane } : task).ToArray() } };
        Assert.Equal("no-op", Assert.Single(ValidateV2(source, new SetTaskTodayOperation("1", TaskA, lane)).Failures).Code);
    }

    [Theory]
    [InlineData(TodayLane.InProgress)]
    [InlineData(TodayLane.Planned)]
    [InlineData(null)]
    public void RepeatedOrContradictoryTodayChangesAreRejected(TodayLane? second)
    {
        var result = ValidateV2(Source(), new SetTaskTodayOperation("1", TaskA, TodayLane.InProgress),
            new SetTaskTodayOperation("2", TaskA, second));
        Assert.Equal("facet-conflict", Assert.Single(result.Failures).Code);
    }

    [Fact]
    public void RequestedTodayPlacementMustSurviveLaterCompletionBinAndParentArchive()
    {
        var add = new SetTaskTodayOperation("1", TaskA, TodayLane.InProgress);
        foreach (var terminal in new ChangeOperation[]
        {
            new CompleteTaskOperation("2", TaskA),
            new MoveTaskToBinOperation("2", TaskA),
            new ArchiveProjectOperation("2", Project),
            new MoveProjectToBinOperation("2", Project),
        })
            Assert.Equal("today-conflict", Assert.Single(ValidateV2(Source(), add, terminal).Failures).Code);
        Assert.Equal("today-conflict", Assert.Single(ValidateV2(Source(), add,
            new CompleteTaskOperation("2", TaskA), new ArchiveTaskOperation("3", TaskA)).Failures).Code);
        Assert.Equal("today-conflict", Assert.Single(ValidateV2(Source(),
            new CreateTaskOperation("1", "new", "Task", "", null,
                new(ProposedTaskCategoryMode.Explicit, ProposalReference.Existing("c")), null, [], TodayLane: TodayLane.Planned),
            new CompleteTaskOperation("2", ProposalReference.Local("new"))).Failures).Code);
    }

    [Fact]
    public void ExplicitTodayRemovalCanPrecedeCompletion()
    {
        var result = ValidateV2(Source(), new SetTaskTodayOperation("1", TaskA, null), new CompleteTaskOperation("2", TaskA));
        Assert.NotNull(result.Plan);
        Assert.Single(result.Plan.Preview, item => item.Kind == "task-today");
        Assert.DoesNotContain(result.Plan.Preview, item => item.Kind == "today-removal");
    }

    private sealed class Markdown : IProposalMarkdownValidator
    { public ProposalMarkdownValidation Validate(string markdown, bool proposed) => new(true, markdown.Length == 0 ? 0 : 1, 0); }

    [Fact]
    public void NewWorkCreatesALocalGraphWithoutAWorkspaceSource()
    {
        var result = ValidateNewWork(
            new CreateCategoryOperation("1", "category", "Fresh"),
            new CreateParticipantOperation("2", "participant", "Person"),
            new CreateProjectOperation("3", "project", "Project", "", ProposalReference.Local("category"), null),
            new CreateTaskOperation("4", "task", "Task", "", ProposalReference.Local("project"),
                new(ProposedTaskCategoryMode.Inherit), null, [ProposalReference.Local("participant")]));

        Assert.Equal(ProposalKind.NewWork, result.Plan!.Kind);
        Assert.Null(result.Plan.SourceSnapshotId);
        Assert.Equal(new WorkspaceRevision(1), result.Plan.SourceWorkspaceRevision);
        Assert.Contains(result.Plan.Preview, effect => effect.Kind == "create-task" && effect.Target.LocalId == "task");
    }

    [Fact]
    public void CoreRejectsNewWorkThatSmugglesExistingReferencesOrMutationOperations()
    {
        var existingReference = ValidateNewWork(
            new CreateProjectOperation("1", "project", "Project", "", ProposalReference.Existing("c"), null));
        var existingTaskReferences = ValidateNewWork(
            new CreateTaskOperation("1", "task", "Task", "", ProposalReference.Existing("p"),
                new(ProposedTaskCategoryMode.Explicit, ProposalReference.Existing("c")), null,
                [ProposalReference.Existing("participant")]));
        var mutation = ValidateNewWork(new CompleteTaskOperation("1", TaskA));

        Assert.Equal("invalid-new-work", Assert.Single(existingReference.Failures).Code);
        Assert.Equal("invalid-new-work", Assert.Single(existingTaskReferences.Failures).Code);
        Assert.Equal("invalid-new-work", Assert.Single(mutation.Failures).Code);
    }

    [Fact]
    public void NewWorkKeepsCatalogueNamesUniqueButAllowsDuplicateWorkTitles()
    {
        var duplicateCategory = ValidateNewWork(
            new CreateCategoryOperation("1", "category", " category "));
        var duplicateParticipant = ValidateNewWork(
            new CreateParticipantOperation("1", "first", "Person"),
            new CreateParticipantOperation("2", "second", " person "));
        var duplicateWorkTitles = ValidateNewWork(
            new CreateCategoryOperation("1", "category", "Fresh"),
            new CreateProjectOperation("2", "project", "Project", "", ProposalReference.Local("category"), null),
            new CreateTaskOperation("3", "task", "Task A", "", ProposalReference.Local("project"),
                new(ProposedTaskCategoryMode.Inherit), null, []));

        Assert.Equal("duplicate-category-name", Assert.Single(duplicateCategory.Failures).Code);
        Assert.Equal("duplicate-participant-label", Assert.Single(duplicateParticipant.Failures).Code);
        Assert.NotNull(duplicateWorkTitles.Plan);
    }

    [Fact]
    public void CreatedProjectPreviewUsesThePersistedPositionAcrossBinnedProjects()
    {
        var binnedProject = new ProjectRecord("binned", "Binned", "", "c", null, 4, ColourKey: "lime");
        var source = Source() with
        {
            ProjectBin = [new(binnedProject, DateTimeOffset.UnixEpoch, "Category", 0)],
        };
        var result = new ProposalSemanticPlanner(TimeProvider.System, new Markdown()).Validate(
            new(
                AgentInterchangeV1.ChangeProposalFormat,
                1,
                Identity,
                new(1),
                [new CreateProjectOperation(
                    "create",
                    "created",
                    "Created",
                    "",
                    ProposalReference.Existing("c"),
                    null)]),
            source,
            Identity,
            "digest");

        var created = Assert.Single(result.Plan!.Preview, effect => effect.Kind == "create-project");
        Assert.Equal(IdentityColourPalette.KeyForProjectPosition(5), created.Identity.TargetColourKey);
    }

    [Fact]
    public void OrderedLocalCreationAndPostCreateCompletionProduceTypedPlan()
    {
        var result = Validate(new CreateCategoryOperation("1", "category", "Fresh"),
            new CreateTaskOperation("2", "task", "Created", "", null,
                new(ProposedTaskCategoryMode.Explicit, ProposalReference.Local("category")), null, []),
            new CompleteTaskOperation("3", ProposalReference.Local("task")));
        Assert.NotNull(result.Plan);
        Assert.Contains(result.Plan.Preview, effect => effect.Target.LocalId == "task" && effect.Kind == "completion");
        Assert.All(result.Plan.Preview, effect => Assert.DoesNotContain("\u0000", effect.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void PreviewIdentityUsesFinalCrossRecordProjectNameAndInheritedCategory()
    {
        var result = Validate(
            new CreateProjectOperation("1", "project", "Created", "", ProposalReference.Existing("c"), null),
            new CreateTaskOperation("2", "task", "Task", "", ProposalReference.Local("project"),
                new(ProposedTaskCategoryMode.Inherit), null, []),
            new EditProjectOperation("3", ProposalReference.Local("project"),
                new(new(true, "Renamed"), default, default)),
            new SetProjectCategoryOperation("4", ProposalReference.Local("project"), ProposalReference.Existing("d")));

        var taskEffects = result.Plan!.Preview.Where(effect => effect.Target.LocalId == "task").ToArray();
        Assert.NotEmpty(taskEffects);
        Assert.All(taskEffects, effect =>
        {
            Assert.Equal("Renamed", effect.Identity.ProjectName);
            Assert.Equal("Other", effect.Identity.CategoryName);
            Assert.Equal("Other", effect.Identity.ProjectCategoryName);
        });
    }
    [Fact]
    public void ForwardLocalReferenceIsRejected() => Assert.Equal("invalid-local-reference", Validate(
        new CreateProjectOperation("1", "project", "Created", "", ProposalReference.Local("later"), null),
        new CreateCategoryOperation("2", "later", "Fresh")).Failures[0].Code);
    [Fact]
    public void DuplicateContentFacetIsRejected() => Assert.Equal("facet-conflict", Validate(
        Edit("1", "First"), Edit("2", "Second")).Failures[0].Code);
    private static EditTaskOperation Edit(string operation, string title) => new(operation, TaskA,
        new(new(true, title), default, default));
    [Fact]
    public void NormalizedNoOpIsRejected() => Assert.Equal("no-op", Validate(Edit("1", " Task A ")).Failures[0].Code);
    [Fact]
    public void HiddenArchivedSlotsRemainFixedInFinalOrder()
    {
        var result = Validate(new ReorderProjectTaskOperation("1", ProposalReference.Existing("b"),
            new(ProjectTaskPlacementKind.Before, TaskA)));
        Assert.NotNull(result.Plan);
        var direct = Assert.Single(result.Plan.Preview, effect => effect.Kind == "task-reorder");
        Assert.Equal("Position 2", direct.Before);
        Assert.Equal("Position 1", direct.After);
        var rows = result.Plan.Preview.Where(effect => effect.Kind == "final-project-order-row").ToArray();
        Assert.Equal("Position 1; active", rows.Single(row => row.Target.ExistingId == "b").After);
        Assert.Equal("Position 2; individually archived", rows.Single(row => row.Target.ExistingId == "hidden").After);
        Assert.Equal("Position 3; active", rows.Single(row => row.Target.ExistingId == "a").After);
    }
    [Fact]
    public void FinalOrderRowsRemainDistinctForDuplicateAndMultilineTitles()
    {
        var source = Source();
        var tasks = source.Workspace.Tasks.Select(task => task with { Title = "Same\nsynthetic title" }).ToArray();
        source = source with { Workspace = source.Workspace with { Tasks = tasks } };
        var result = new ProposalSemanticPlanner(new FixedTime(), new Markdown()).Validate(
            new(AgentInterchangeV1.ChangeProposalFormat, 1, Identity, new(1),
                [new ReorderProjectTaskOperation("1", ProposalReference.Existing("b"), new(ProjectTaskPlacementKind.Before, TaskA))]),
            source, Identity, "digest");
        var rows = result.Plan!.Preview.Where(effect => effect.Kind == "final-project-order-row").ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal(3, rows.Select(row => row.Target.ExistingId).Distinct().Count());
        Assert.All(rows, row => Assert.Equal(Project, row.OrderProject));
    }

    [Fact]
    public void ExpandedOrderRowsOverEffectBudgetRejectEntireProposal()
    {
        var source = Source();
        var tasks = Enumerable.Range(0, 2_000).Select(index =>
            new TaskRecord("synthetic-" + index, "p", "Synthetic task", "", null, null, index, index)).ToArray();
        source = source with { Workspace = source.Workspace with { Tasks = tasks } };
        var result = new ProposalSemanticPlanner(new FixedTime(), new Markdown()).Validate(
            new(AgentInterchangeV1.ChangeProposalFormat, 1, Identity, new(1),
                [new ReorderProjectTaskOperation("1", ProposalReference.Existing("synthetic-1999"), new(ProjectTaskPlacementKind.First))]),
            source, Identity, "digest");
        Assert.Null(result.Plan);
        Assert.Equal("preview-effect-limit", Assert.Single(result.Failures).Code);
    }

    [Fact]
    public void OrderRowsUseOriginalLifecycleBeforeAndProspectiveLifecycleAfter()
    {
        var source = Source();
        var tasks = source.Workspace.Tasks.ToArray();
        tasks[0] = tasks[0] with { CompletedAt = DateTimeOffset.UnixEpoch, CompletionDate = new(2020, 1, 1), TodayLane = null };
        source = source with { Workspace = source.Workspace with { Tasks = tasks } };
        var result = new ProposalSemanticPlanner(new FixedTime(), new Markdown()).Validate(
            new(AgentInterchangeV1.ChangeProposalFormat, 1, Identity, new(1),
                [new ArchiveTaskOperation("1", TaskA),
                 new CreateTaskOperation("2", "new-task", "Synthetic new task", "", null,
                     new(ProposedTaskCategoryMode.Explicit, ProposalReference.Existing("c")), null, [])]),
            source, Identity, "digest");
        var row = Assert.Single(result.Plan!.Preview, effect => effect.Kind == "final-shared-order-row" && effect.Target == TaskA);
        Assert.Equal("Position 1; active", row.Before);
        Assert.Equal("Position 2; individually archived", row.After);
    }

    [Fact]
    public void ArchivedAnchorIsRejected() => Assert.Equal("invalid-reference", Validate(
        new ReorderProjectTaskOperation("1", TaskA, new(ProjectTaskPlacementKind.After, ProposalReference.Existing("hidden")))).Failures[0].Code);
    [Fact]
    public void MoveRequiresCategoryChoice() => Assert.Equal("category-choice-required", Validate(
        new MoveTaskOperation("1", TaskA, ProposalReference.Existing("q"), null)).Failures[0].Code);
    [Fact]
    public void MoveConsumesCategoryFacet() => Assert.Equal("facet-conflict", Validate(
        new MoveTaskOperation("1", TaskA, ProposalReference.Existing("q"), AttachmentCategoryChoice.PreserveAsOverride),
        new SetTaskCategoryOperation("2", TaskA, new(ProposedTaskCategoryMode.Inherit))).Failures[0].Code);
    [Fact]
    public void CompleteClearsTodayAndUpdatesProjectStatus()
    {
        var result = Validate(new CompleteTaskOperation("1", TaskA));
        Assert.NotNull(result.Plan);
        Assert.Contains(result.Plan.Preview, effect => effect.Kind == "today-removal");
        Assert.Contains(result.Plan.Preview, effect => effect.Kind == "project-status");
    }
    [Fact]
    public void IncompleteTaskCannotArchive() => Assert.Equal("archive-incomplete-task", Validate(new ArchiveTaskOperation("1", TaskA)).Failures[0].Code);
    [Fact]
    public void ParentTerminalAndChildEditConflictInEitherOrder()
    {
        Assert.Equal("aggregate-child-conflict", Validate(Edit("1", "Changed"), new ArchiveProjectOperation("2", Project)).Failures[0].Code);
        Assert.Equal("invalid-reference", Validate(new ArchiveProjectOperation("1", Project), Edit("2", "Changed")).Failures[0].Code);
    }
    [Fact]
    public void NewTaskCannotEnterBin() => Assert.Equal("new-record-lifecycle", Validate(
        new CreateTaskOperation("1", "new", "Created", "", null,
            new(ProposedTaskCategoryMode.Explicit, ProposalReference.Existing("c")), null, []),
        new MoveTaskToBinOperation("2", ProposalReference.Local("new"))).Failures[0].Code);
    [Fact]
    public void PlanCopiesCallerOwnedOperationsAndAssociations()
    {
        var operations = new List<ChangeOperation> { Edit("1", "Changed") };
        var result = new ProposalSemanticPlanner(TimeProvider.System, new Markdown()).Validate(
            new(AgentInterchangeV1.ChangeProposalFormat, 1, Identity, new(1), operations), Source(), Identity, "digest");
        operations.Clear(); Assert.Single(result.Plan!.Operations);
        Assert.DoesNotContain("digest", result.Plan.ToString(), StringComparison.Ordinal);
    }
    [Fact]
    public void ExistingLargeMarkdownUsesAggregateBudgetWhenTitleChanges()
    {
        var source = Source(); var tasks = source.Workspace.Tasks.ToArray();
        tasks[0] = tasks[0] with { Description = new string('x', 5000) };
        source = source with { Workspace = source.Workspace with { Tasks = tasks } };
        var planner = new ProposalSemanticPlanner(TimeProvider.System, new CountMarkdown());
        var result = planner.Validate(new(AgentInterchangeV1.ChangeProposalFormat, 1, Identity, new(1), [Edit("1", "Changed")]), source, Identity, "digest");
        Assert.NotNull(result.Plan);
    }
    private sealed class CountMarkdown : IProposalMarkdownValidator
    { public ProposalMarkdownValidation Validate(string markdown, bool proposed) => new(true, markdown.Length, 0); }
    [Fact]
    public void AggregateBinShowsAlreadyBinnedChildRestoreRestriction()
    {
        var source = Source() with
        {
            TaskBin = [new(new("bin", "p", "Binned", "", null, null, 0, 0),
            DateTimeOffset.UnixEpoch, "Project", "Category", true, null)]
        };
        var result = new ProposalSemanticPlanner(TimeProvider.System, new Markdown()).Validate(
            new(AgentInterchangeV1.ChangeProposalFormat, 1, Identity, new(1), [new MoveProjectToBinOperation("1", Project)]), source, Identity, "digest");
        Assert.NotNull(result.Plan);
        Assert.Contains(result.Plan.Preview, effect => effect.Kind == "already-binned-child" && effect.After.Contains("remains in Bin after parent restore", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("create-participant")]
    [InlineData("create-project")]
    [InlineData("edit-project")]
    [InlineData("project-category")]
    [InlineData("task-category")]
    [InlineData("participants")]
    [InlineData("attach")]
    [InlineData("detach")]
    [InlineData("move")]
    [InlineData("reopen")]
    [InlineData("archive-task")]
    [InlineData("task-bin")]
    [InlineData("archive-project")]
    [InlineData("project-bin")]
    public void EveryRemainingIntentProducesAuthoritativeEffects(string intent)
    {
        ChangeOperation[] operations = intent switch
        {
            "create-participant" => [new CreateParticipantOperation("1", "participant", "Person")],
            "create-project" => [new CreateProjectOperation("1", "project", "Created", "Description", ProposalReference.Existing("c"), new(2030, 1, 1))],
            "edit-project" => [new EditProjectOperation("1", Project, new(new(true, "Edited"), new(true, "Description"), new(true, new(2030, 1, 1))))],
            "project-category" => [new SetProjectCategoryOperation("1", Project, ProposalReference.Existing("d"))],
            "task-category" => [new SetTaskCategoryOperation("1", TaskA, new(ProposedTaskCategoryMode.Explicit, ProposalReference.Existing("d")))],
            "participants" => [new CreateParticipantOperation("1", "person", "Person"), new ReplaceTaskParticipantsOperation("2", TaskA, [ProposalReference.Local("person")])],
            "attach" => [new DetachTaskOperation("1", TaskA), new AttachTaskOperation("2", ProposalReference.Existing("b"), Project, null)],
            "detach" => [new DetachTaskOperation("1", TaskA)],
            "move" => [new MoveTaskOperation("1", TaskA, ProposalReference.Existing("q"), AttachmentCategoryChoice.AdoptProjectCategory)],
            "reopen" => [new CompleteTaskOperation("1", TaskA), new ReopenTaskOperation("2", TaskA)],
            "archive-task" => [new CompleteTaskOperation("1", TaskA), new ArchiveTaskOperation("2", TaskA)],
            "task-bin" => [new MoveTaskToBinOperation("1", TaskA)],
            "archive-project" => [new ArchiveProjectOperation("1", Project)],
            "project-bin" => [new MoveProjectToBinOperation("1", Project)],
            _ => throw new InvalidOperationException(),
        };
        var source = Source();
        if (intent == "attach")
        {
            var tasks = source.Workspace.Tasks.ToArray(); tasks[2] = tasks[2] with { ProjectId = null, ExplicitCategoryId = "c", ProjectPosition = null };
            source = source with { Workspace = source.Workspace with { Tasks = tasks } };
        }
        if (intent == "reopen")
        {
            var tasks = source.Workspace.Tasks.ToArray(); tasks[0] = tasks[0] with { CompletedAt = DateTimeOffset.UnixEpoch, CompletionDate = new(2020, 1, 1), TodayLane = null };
            source = source with { Workspace = source.Workspace with { Tasks = tasks } }; operations = [new ReopenTaskOperation("1", TaskA)];
        }
        var result = new ProposalSemanticPlanner(new FixedTime(), new Markdown()).Validate(
            new(AgentInterchangeV1.ChangeProposalFormat, 1, Identity, new(1), operations), source, Identity, "digest");
        Assert.NotNull(result.Plan); Assert.NotEmpty(result.Plan.Preview);
    }
    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2030, 4, 5, 12, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
    [Fact]
    public void CreationCanEditMoveReorderAndCompleteExactlyOnce()
    {
        var local = ProposalReference.Local("new");
        var result = Validate(new CreateTaskOperation("1", "new", "Created", "", Project,
            new(ProposedTaskCategoryMode.Inherit), null, []),
            new EditTaskOperation("2", local, new(new(true, "Edited"), default, default)),
            new MoveTaskOperation("3", local, ProposalReference.Existing("q"), AttachmentCategoryChoice.AdoptProjectCategory),
            new CreateTaskOperation("4", "anchor", "Anchor", "", ProposalReference.Existing("q"), new(ProposedTaskCategoryMode.Inherit), null, []),
            new ReorderProjectTaskOperation("5", local, new(ProjectTaskPlacementKind.After, ProposalReference.Local("anchor"))),
            new CompleteTaskOperation("6", local));
        Assert.NotNull(result.Plan);
        Assert.Contains(result.Plan.Preview, effect => effect.Kind == "completion" && effect.Target.LocalId == "new");
    }
    [Fact]
    public void CompletionDateUsesInjectedClock()
    {
        var result = new ProposalSemanticPlanner(new FixedTime(), new Markdown()).Validate(
            new(AgentInterchangeV1.ChangeProposalFormat, 1, Identity, new(1), [new CompleteTaskOperation("1", TaskA)]), Source(), Identity, "digest");
        Assert.Contains("2030-04-05", Assert.Single(result.Plan!.Preview, e => e.Kind == "completion").After, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData("local-kind", "invalid-local-reference")]
    [InlineData("local-duplicate", "duplicate-local-id")]
    [InlineData("operation-duplicate", "duplicate-operation-id")]
    [InlineData("association-duplicate", "duplicate-association")]
    [InlineData("association-noop", "no-op")]
    [InlineData("complete-noop", "no-op")]
    public void ReferenceAndOutcomeConflictsRejectWholeProposal(string scenario, string code)
    {
        ChangeOperation[] operations = scenario switch
        {
            "local-kind" => [new CreateParticipantOperation("1", "person", "Person"), new CreateProjectOperation("2", "project", "Created", "", ProposalReference.Local("person"), null)],
            "local-duplicate" => [new CreateCategoryOperation("1", "same", "Fresh"), new CreateParticipantOperation("2", "same", "Person")],
            "operation-duplicate" => [new CreateCategoryOperation("1", "category", "Fresh"), new CreateParticipantOperation("1", "person", "Person")],
            "association-duplicate" => [new CreateParticipantOperation("1", "person", "Person"), new ReplaceTaskParticipantsOperation("2", TaskA, [ProposalReference.Local("person"), ProposalReference.Local("person")])],
            "association-noop" => [new ReplaceTaskParticipantsOperation("1", TaskA, [])],
            "complete-noop" => [new ReopenTaskOperation("1", TaskA)],
            _ => throw new InvalidOperationException(),
        };
        var result = Validate(operations); Assert.Null(result.Plan); Assert.Equal(code, result.Failures[0].Code);
    }
    [Fact]
    public void TemporaryChildRelationshipCannotEvadeParentTerminalOwnership()
    {
        var result = Validate(new MoveTaskOperation("1", TaskA, ProposalReference.Existing("q"), AttachmentCategoryChoice.AdoptProjectCategory),
            new ArchiveProjectOperation("2", Project));
        Assert.Equal("aggregate-child-conflict", result.Failures[0].Code);
    }
    [Fact]
    public void CategoryChangeIncludesArchivedAndBinnedInheritance()
    {
        var source = Source() with { TaskBin = [new(new("bin", "p", "Binned", "", null, null, 0, 0), DateTimeOffset.UnixEpoch, "Project", "Category", true, null)] };
        var result = new ProposalSemanticPlanner(new FixedTime(), new Markdown()).Validate(
            new(AgentInterchangeV1.ChangeProposalFormat, 1, Identity, new(1), [new SetProjectCategoryOperation("1", Project, ProposalReference.Existing("d"))]), source, Identity, "digest");
        Assert.Contains(result.Plan!.Preview, e => e.Kind == "archived-child-category");
        Assert.Contains(result.Plan.Preview, e => e.Kind == "binned-child-category");
    }
    [Fact]
    public void ExplanationIsRetainedMarkdownOnActualTargetAndNeverBecomesTaskContent()
    {
        var result = Validate(new CreateTaskOperation("1", "new", "Created", "Actual", null,
            new(ProposedTaskCategoryMode.Explicit, ProposalReference.Existing("c")), null, [], "Explanation"));
        Assert.Equal("Actual", Assert.IsType<CreateTaskOperation>(result.Plan!.Operations[0]).Description);
        var explanation = Assert.Single(result.Plan.Preview, e => e.Kind == "explanation");
        Assert.Equal("new", explanation.Target.LocalId); Assert.Equal("Explanation", Assert.Single(explanation.MarkdownFields));
    }

    [Theory]
    [InlineData(1999, true)]
    [InlineData(2000, false)]
    public void ExpandedEffectsHaveExactTwoThousandBoundary(int childCount, bool valid)
    {
        var source = Source();
        var tasks = Enumerable.Range(0, childCount).Select(i => new TaskRecord("t" + i, "p", "Task", "", null, null, i, i)).ToArray();
        source = source with { Workspace = source.Workspace with { Tasks = tasks } };
        var result = new ProposalSemanticPlanner(new FixedTime(), new Markdown()).Validate(
            new(AgentInterchangeV1.ChangeProposalFormat, 1, Identity, new(1), [new SetProjectCategoryOperation("1", Project, ProposalReference.Existing("d"))]), source, Identity, "digest");
        Assert.Equal(valid, result.Plan is not null);
        if (valid) Assert.Equal(2000, result.Plan!.Preview.Count);
        else Assert.Equal("preview-effect-limit", result.Failures[0].Code);
    }
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void RetainedPreviewTextHasExactFourMebibyteBoundary(int excess, bool valid)
    {
        const string before = "Incomplete; completion date: None";
        const string after = "Complete; completion date: 2030-04-05";
        var overhead = System.Text.Encoding.UTF8.GetByteCount(
            "completion" + "a" + before + after + "Category" + IdentityColourPalette.DefaultKey);
        var source = Source();
        source = source with
        {
            Workspace = source.Workspace with
            {
                Projects = [],
                Tasks = [new("a", null,
            new string('x', 4 * 1024 * 1024 - overhead + excess), "", "c", null, 0, null)]
            }
        };
        var result = new ProposalSemanticPlanner(new FixedTime(), new Markdown()).Validate(
            new(AgentInterchangeV1.ChangeProposalFormat, 1, Identity, new(1), [new CompleteTaskOperation("1", TaskA)]), source, Identity, "digest");
        Assert.Equal(valid, result.Plan is not null);
        if (!valid) Assert.Equal("preview-text-limit", result.Failures[0].Code);
    }
    [Theory]
    [InlineData(32768, true)]
    [InlineData(32769, false)]
    public void RetainedMarkdownHasExactAggregateNodeBoundary(int nodeCount, bool valid)
    {
        var source = Source(); var tasks = source.Workspace.Tasks.ToArray();
        tasks[0] = tasks[0] with { Description = new string('x', nodeCount) };
        source = source with { Workspace = source.Workspace with { Tasks = tasks } };
        var result = new ProposalSemanticPlanner(new FixedTime(), new CountMarkdown()).Validate(
            new(AgentInterchangeV1.ChangeProposalFormat, 1, Identity, new(1), [Edit("1", "Changed")]), source, Identity, "digest");
        Assert.Equal(valid, result.Plan is not null);
        if (!valid) Assert.Equal("markdown-preview-limit", result.Failures[0].Code);
    }
    [Theory]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public void ProposedMarkdownHasExactFieldNodeBoundary(int nodeCount, bool valid)
    {
        var operation = new EditTaskOperation("1", TaskA, new(default, new(true, new string('x', nodeCount)), default));
        var result = new ProposalSemanticPlanner(new FixedTime(), new CountMarkdown()).Validate(
            new(AgentInterchangeV1.ChangeProposalFormat, 1, Identity, new(1), [operation]), Source(), Identity, "digest");
        Assert.Equal(valid, result.Plan is not null);
        if (!valid) Assert.Equal("markdown-limit", result.Failures[0].Code);
    }
    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void ProposedMarkdownHasExactContainerDepthBoundary(int depth, bool valid)
    {
        var result = new ProposalSemanticPlanner(new FixedTime(), new DepthMarkdown(depth)).Validate(
            new(AgentInterchangeV1.ChangeProposalFormat, 1, Identity, new(1), [new CreateCategoryOperation("1", "new", "Fresh", "Explanation")]), Source(), Identity, "digest");
        Assert.Equal(valid, result.Plan is not null);
        if (!valid) Assert.Equal("markdown-limit", result.Failures[0].Code);
    }
    private sealed class DepthMarkdown(int depth) : IProposalMarkdownValidator
    { public ProposalMarkdownValidation Validate(string markdown, bool proposed) => new(true, 1, depth); }
    [Fact]
    public void TerminalTaskRejectsAnySubsequentTargeting()
    {
        Assert.Equal("invalid-reference", Validate(new MoveTaskToBinOperation("1", TaskA), Edit("2", "Changed")).Failures[0].Code);
        Assert.Equal("invalid-reference", Validate(new CompleteTaskOperation("1", TaskA), new ArchiveTaskOperation("2", TaskA),
            new MoveTaskToBinOperation("3", TaskA)).Failures[0].Code);
    }

    [Fact]
    public void ExistingReferenceCannotAddressEarlierCreateThroughSimulationKey()
    {
        var result = Validate(new CreateCategoryOperation("1", "new", "Fresh"),
            new CreateProjectOperation("2", "project", "Created", "", ProposalReference.Existing("\u0000local:new"), null));
        Assert.Null(result.Plan); Assert.Equal("invalid-reference", result.Failures[0].Code);
    }
    [Fact]
    public void ExistingReferenceCannotResolveAnotherRecordKind()
    {
        var result = Validate(new SetTaskCategoryOperation("1", TaskA,
            new(ProposedTaskCategoryMode.Explicit, ProposalReference.Existing("p"))));
        Assert.Null(result.Plan); Assert.Equal("invalid-reference", result.Failures[0].Code);
    }

}
