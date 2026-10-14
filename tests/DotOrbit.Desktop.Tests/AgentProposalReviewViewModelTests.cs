using System.Text;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using DotOrbit.Interchange.Json;
using DotOrbit.Storage.Sqlite;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class AgentProposalReviewViewModelTests
{
    [Fact]
    public void EveryVersionOneOperationHasAnExplicitOrderedReviewLabelAndReference()
    {
        var existingProject = ProposalReference.Existing("project");
        var existingTask = ProposalReference.Existing("task");
        var existingCategory = ProposalReference.Existing("category");
        var operations = new (ChangeOperation Operation, string Kind, string Detail)[]
        {
            (new CreateCategoryOperation("1", "category", "Category"), "Create Category", "New Category · local:category"),
            (new CreateParticipantOperation("2", "participant", "Participant"), "Create Participant", "New Participant · local:participant"),
            (new CreateProjectOperation("3", "project", "Project", "", existingCategory, null), "Create Project", "New Project · local:project"),
            (new EditProjectOperation("4", existingProject, new(default, default, default)), "Edit Project", "existing:project"),
            (new SetProjectCategoryOperation("5", existingProject, existingCategory), "Set Project Category", "existing:project"),
            (new CreateTaskOperation("6", "task", "Task", "", null, new(ProposedTaskCategoryMode.Explicit, existingCategory), null, []), "Create Task", "New Task · local:task"),
            (new EditTaskOperation("7", existingTask, new(default, default, default)), "Edit Task", "existing:task"),
            (new SetTaskCategoryOperation("8", existingTask, new(ProposedTaskCategoryMode.Explicit, existingCategory)), "Set Task Category", "existing:task"),
            (new ReplaceTaskParticipantsOperation("9", existingTask, []), "Replace Task Participants", "existing:task"),
            (new AttachTaskOperation("10", existingTask, existingProject, null), "Attach Task", "existing:task → existing:project"),
            (new DetachTaskOperation("11", existingTask), "Detach Task", "existing:task"),
            (new MoveTaskOperation("12", existingTask, existingProject, null), "Move Task", "existing:task → existing:project"),
            (new CompleteTaskOperation("13", existingTask), "Complete Task", "existing:task"),
            (new ReopenTaskOperation("14", existingTask), "Reopen Task", "existing:task"),
            (new ReorderProjectTaskOperation("15", existingTask, new(ProjectTaskPlacementKind.First)), "Reorder Project Task", "existing:task"),
            (new ArchiveProjectOperation("16", existingProject), "Archive Project", "existing:project"),
            (new ArchiveTaskOperation("17", existingTask), "Archive Task", "existing:task"),
            (new MoveProjectToBinOperation("18", existingProject), "Move Project to Bin", "existing:project"),
            (new MoveTaskToBinOperation("19", existingTask), "Move Task to Bin", "existing:task"),
        };

        var rows = operations
            .Select((item, index) => AgentProposalReviewViewModel.ToOperationRow(item.Operation, index))
            .ToArray();

        Assert.Equal(Enumerable.Range(1, operations.Length), rows.Select(row => row.Number));
        Assert.Equal(operations.Select(item => item.Kind), rows.Select(row => row.Kind));
        Assert.Equal(operations.Select(item => item.Detail), rows.Select(row => row.Detail));
        Assert.Equal(operations.Select(item => item.Operation.OperationId), rows.Select(row => row.OperationId));
    }

    [Theory]
    [InlineData("create-category", "Create Category")]
    [InlineData("create-participant", "Create Participant")]
    [InlineData("create-project", "Create Project")]
    [InlineData("create-task", "Create Task")]
    [InlineData("project-content", "Project content")]
    [InlineData("task-content", "Task content")]
    [InlineData("project-category", "Project Category")]
    [InlineData("inherited-task-category", "Inherited Task Category")]
    [InlineData("archived-child-category", "Archived child Category")]
    [InlineData("binned-child-category", "Binned child Category")]
    [InlineData("task-category", "Task Category")]
    [InlineData("participants", "Task Participants")]
    [InlineData("relationship", "Project relationship")]
    [InlineData("completion", "Completion state and date")]
    [InlineData("today-removal", "Today removal")]
    [InlineData("project-archive", "Project archive")]
    [InlineData("project-bin", "Project moved to Bin")]
    [InlineData("task-archive", "Task archive")]
    [InlineData("task-bin", "Task moved to Bin")]
    [InlineData("aggregate-child-bin", "Project child moved to Bin")]
    [InlineData("parent-archive-visibility", "Archived Project child visibility")]
    [InlineData("already-binned-child", "Already-binned child")]
    [InlineData("project-status", "Derived Project status")]
    [InlineData("final-project-order", "Project Task order")]
    [InlineData("final-project-order-row", "Project Task order row")]
    [InlineData("final-shared-order", "Shared Task order")]
    [InlineData("final-shared-order-row", "Shared Task order row")]
    [InlineData("explanation", "Inert explanation")]
    public void EveryAuthoritativePreviewEffectHasAnExplicitUserFacingLabel(
        string kind,
        string expected) =>
        Assert.Equal(expected, AgentProposalReviewViewModel.EffectName(kind));

    [AvaloniaFact]
    public void ReviewWindowExposesCompleteNamedListsAndWholeProposalConfirmation()
    {
        var viewModel = new AgentProposalReviewViewModel(CreatePlan(), new UnavailableApplication());
        var window = new AgentProposalReviewWindow(viewModel, new ReadyHost());
        window.Show();

        var operations = Assert.IsType<ListBox>(window.FindControl<ListBox>("ProposalOperationsList"));
        var effects = Assert.IsType<ListBox>(window.FindControl<ListBox>("ProposalEffectsList"));
        Assert.Equal("Proposal operations in application order", AutomationProperties.GetName(operations));
        Assert.Equal("Complete direct and derived proposal effects", AutomationProperties.GetName(effects));
        Assert.Equal(viewModel.OperationCount, operations.ItemCount);
        Assert.Equal(viewModel.EffectCount, effects.ItemCount);
        Assert.True(window.MinWidth > 0);
        Assert.True(window.MinHeight > 0);

        viewModel.RequestApply();

        Assert.True(viewModel.NeedsConfirmation);
        Assert.False(window.FindControl<StackPanel>("ProposalReviewHeader")!.IsEnabled);
        Assert.False(window.FindControl<TabControl>("ProposalReviewTabs")!.IsEnabled);
        Assert.False(window.FindControl<Grid>("ProposalReviewActions")!.IsEnabled);
        var confirm = Assert.IsType<Button>(window.FindControl<Button>("ConfirmProposalApplyButton"));
        Assert.Contains("single-use whole proposal", AutomationProperties.GetName(confirm), StringComparison.Ordinal);
        window.Close();
    }

    [Fact]
    public void ValidPlanProjectsOrderedOperationsLocalReferencesCompleteEffectsAndSanitisedMarkdown()
    {
        var plan = CreatePlan();
        var viewModel = new AgentProposalReviewViewModel(plan, new UnavailableApplication());

        Assert.Equal(plan.SourceSnapshotId, viewModel.SourceSnapshotId);
        Assert.Equal("1", viewModel.SourceWorkspaceRevision);
        var operation = Assert.Single(viewModel.Operations);
        Assert.Equal(1, operation.Number);
        Assert.Equal("Create Task", operation.Kind);
        Assert.Contains("local:new-task", operation.Detail, StringComparison.Ordinal);
        Assert.Equal(plan.Preview.Count, viewModel.EffectCount);
        Assert.Equal(plan.Preview.Select(effect => effect.Before), viewModel.Effects.Select(effect => effect.Before));
        Assert.Equal(plan.Preview.Select(effect => effect.After), viewModel.Effects.Select(effect => effect.After));
        Assert.Contains(viewModel.Effects, effect => !effect.HasOrderProject);
        Assert.Contains(viewModel.Effects, effect => effect.Kind == "Create Task" && effect.Target.Contains("local:new-task", StringComparison.Ordinal));
        var explanation = Assert.Single(viewModel.Effects, effect => effect.IsExplanation);
        Assert.Equal("Inert explanation — not applied", Assert.Single(explanation.Markdown).Label);
        Assert.DoesNotContain("<script", Assert.Single(explanation.Markdown).Document.ToHtml(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Excluded powers", viewModel.ExcludedPowers, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WholeProposalConfirmationCallsApproveOnceAndUnavailableApprovalCannotReplay()
    {
        var application = new UnavailableApplication();
        var viewModel = new AgentProposalReviewViewModel(CreatePlan(), application);

        Assert.True(viewModel.CanRequestApply);
        viewModel.RequestApply();
        Assert.True(viewModel.NeedsConfirmation);
        Assert.Equal(0, application.ApproveCount);

        await viewModel.ConfirmApplyAsync(new ReadyHost());
        await viewModel.ConfirmApplyAsync(new ReadyHost());

        Assert.Equal(1, application.ApproveCount);
        Assert.Equal(0, application.ApplyCount);
        Assert.False(viewModel.CanRequestApply);
        Assert.Contains("Nothing was applied", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmedPlanAppliesThroughRealEncryptedStoreAndCannotReplay()
    {
        using var directory = new TemporaryDirectory();
        var passphrase = WorkspacePassphrase.Create(
            "synthetic proposal password",
            "synthetic proposal password").Passphrase!;
        using var session = Assert.IsAssignableFrom<IWorkspaceSession>(
            new EncryptedWorkspaceStore().Create(
                Path.Combine(directory.Path, "workspace.db"),
                passphrase,
                CategoryName.Create("Home").CategoryName!).Session);
        Assert.Equal(
            RecoveryDirectoryConfigurationStatus.Configured,
            session.Recovery.ConfigureAutomaticRecoveryDirectory(
                Path.Combine(directory.Path, "recovery")).Status);
        var viewModel = new AgentProposalReviewViewModel(
            CreatePlan(session.Proposals),
            session.ProposalApplication);

        viewModel.RequestApply();
        await viewModel.ConfirmApplyAsync(new ReadyHost());
        await viewModel.ConfirmApplyAsync(new ReadyHost());

        Assert.Contains("applied atomically", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(viewModel.CanRequestApply);
        Assert.False(viewModel.CanRetryMaintenance);
        Assert.Single(session.Work.Read().Tasks, task => task.Title == "Synthetic task");
        Assert.Single(Directory.GetFiles(
            Path.Combine(directory.Path, "recovery"),
            "dot-orbit-pre-import-*.dotorbit-recovery"));
    }

    [Fact]
    public void DiscardInvalidatesReadyOrConfirmingPlanAndAnnouncesReviewRequirement()
    {
        var application = new UnavailableApplication();
        var viewModel = new AgentProposalReviewViewModel(CreatePlan(), application);
        viewModel.RequestApply();

        viewModel.Discard();

        Assert.False(viewModel.NeedsConfirmation);
        Assert.False(viewModel.CanRequestApply);
        Assert.False(viewModel.CanConfirmApply);
        Assert.Contains("invalidated", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(0, application.ApproveCount);
    }

    private static ProposalExecutionPlan CreatePlan()
    {
        var source = new Source(new(new(1), new(
            [new("c", "Synthetic", 0)], [], [], []), [], []));
        return CreatePlan(source);
    }

    private static ProposalExecutionPlan CreatePlan(IProposalWorkspaceSource source)
    {
        var state = source.Read();
        var content = PlanningSnapshotProjection.Create(state.Revision, state.Workspace);
        using var snapshot = JsonDocument.Parse(PlanningSnapshotJsonGenerator.Generate(content).Payload!.Value);
        var categoryId = Assert.Single(state.Workspace.Categories).Id;
        var input = JsonSerializer.Serialize(new
        {
            format = "dot-orbit-change-proposal",
            schemaVersion = 1,
            sourceSnapshotId = snapshot.RootElement.GetProperty("snapshotId").GetString(),
            sourceWorkspaceRevision = "1",
            operations = new[]
            {
                new
                {
                    operation = "createTask",
                    operationId = "create-synthetic-task",
                    localId = "new-task",
                    title = "Synthetic task",
                    description = "**Safe** <script>ignored</script>",
                    project = (object?)null,
                    category = new { mode = "explicit", category = new { existingId = categoryId } },
                    dueDate = (string?)null,
                    participants = Array.Empty<object>(),
                    explanation = "Review note <script>ignored</script>",
                },
            },
        });
        return Assert.IsType<ProposalExecutionPlan>(
            new ProposalReviewBoundary(source, TimeProvider.System)
                .Review(Encoding.UTF8.GetBytes(input)).Plan);
    }

    private sealed class Source(ProposalWorkspaceState state) : IProposalWorkspaceSource
    {
        public ProposalWorkspaceState State { get; } = state;
        public ProposalWorkspaceState Read() => State;
    }

    private sealed class UnavailableApplication : IProposalApplication
    {
        public int ApproveCount { get; private set; }
        public int ApplyCount { get; private set; }
        public ProposalApproval? Approve(ProposalExecutionPlan plan) { ApproveCount++; return null; }
        public ProposalApplyResult Apply(ProposalApproval approval, IProposalApplicationHost host, CancellationToken cancellationToken = default)
        { ApplyCount++; throw new InvalidOperationException(); }
    }

    private sealed class ReadyHost : IProposalApplicationHost
    {
        public bool FlushPendingInspectorWork() => true;
        public bool RefreshProjections() => true;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "orbit-desktop-proposal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
