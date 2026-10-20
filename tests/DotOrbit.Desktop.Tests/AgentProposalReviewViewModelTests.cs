using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    [InlineData("create-category", "Add category")]
    [InlineData("create-participant", "Add person")]
    [InlineData("create-project", "Add project")]
    [InlineData("create-task", "Add task")]
    [InlineData("project-content", "Edit project")]
    [InlineData("task-content", "Edit task")]
    [InlineData("project-category", "Change project category")]
    [InlineData("inherited-task-category", "Inherited Task Category")]
    [InlineData("archived-child-category", "Archived child Category")]
    [InlineData("binned-child-category", "Binned child Category")]
    [InlineData("task-category", "Change task category")]
    [InlineData("participants", "Change people")]
    [InlineData("relationship", "Move task")]
    [InlineData("completion", "Change completion")]
    [InlineData("today-removal", "Today removal")]
    [InlineData("project-archive", "Archive project")]
    [InlineData("project-bin", "Move project to Bin")]
    [InlineData("task-archive", "Archive task")]
    [InlineData("task-bin", "Move task to Bin")]
    [InlineData("aggregate-child-bin", "Project child moved to Bin")]
    [InlineData("parent-archive-visibility", "Archived Project child visibility")]
    [InlineData("already-binned-child", "Already-binned child")]
    [InlineData("project-status", "Derived Project status")]
    [InlineData("final-project-order", "Project Task order")]
    [InlineData("final-project-order-row", "Project Task order row")]
    [InlineData("final-shared-order", "Shared Task order")]
    [InlineData("final-shared-order-row", "Shared Task order row")]
    [InlineData("explanation", "Why this was suggested")]
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

        var effects = Assert.IsType<StackPanel>(window.FindControl<StackPanel>("ProposalEffectsList"));
        Assert.Equal(viewModel.ReviewListAccessibleName, AutomationProperties.GetName(effects));
        Assert.False(Assert.IsType<Expander>(window.FindControl<Expander>("ProposalTechnicalDetails")).IsExpanded);
        var content = Assert.IsType<ScrollViewer>(window.FindControl<ScrollViewer>("ProposalReviewContent"));
        Assert.True(content.IsFocused);
        Assert.True(window.MinWidth > 0);
        Assert.True(window.MinHeight > 0);

        viewModel.RequestApply();

        Assert.True(viewModel.NeedsConfirmation);
        Assert.False(window.FindControl<StackPanel>("ProposalReviewHeader")!.IsEnabled);
        Assert.False(window.FindControl<ScrollViewer>("ProposalReviewContent")!.IsEnabled);
        Assert.False(window.FindControl<Grid>("ProposalReviewActions")!.IsEnabled);
        var confirm = Assert.IsType<Button>(window.FindControl<Button>("ConfirmProposalApplyButton"));
        Assert.Equal(viewModel.ConfirmButtonAccessibleName, AutomationProperties.GetName(confirm));
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
        Assert.Contains(viewModel.Effects, effect => effect.Kind == "Add task" && effect.TechnicalReference.Contains("local:new-task", StringComparison.Ordinal));
        var explanation = Assert.Single(viewModel.Effects, effect => effect.IsExplanation);
        Assert.Equal("Why this was suggested — not applied", Assert.Single(explanation.Markdown).Label);
        Assert.DoesNotContain("<script", Assert.Single(explanation.Markdown).Document.ToHtml(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Excluded powers", viewModel.ExcludedPowers, StringComparison.Ordinal);
        Assert.True(viewModel.HasImpliedChanges);
        Assert.DoesNotContain(viewModel.ReviewEffects, effect => effect.Kind.Contains("order", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NewWorkReviewLeadsWithHumanCreationSummaryAndKindSpecificActions()
    {
        var viewModel = new AgentProposalReviewViewModel(CreateNewWorkPlan(), new UnavailableApplication());

        Assert.Equal("Adds new work", viewModel.ProposalKindLabel);
        Assert.Contains("1 task", viewModel.ProposalKindSummary, StringComparison.Ordinal);
        Assert.Contains("1 category", viewModel.ProposalKindSummary, StringComparison.Ordinal);
        Assert.Contains("No existing item is referenced or edited directly", viewModel.ProposalKindSummary, StringComparison.Ordinal);
        Assert.False(viewModel.HasSourceDetails);
        Assert.Equal("Add all new work…", viewModel.ApplyButtonText);
        Assert.Contains("adding all shown new work", viewModel.ApplyButtonAccessibleName, StringComparison.Ordinal);
        Assert.Contains("adding all shown new work", viewModel.ConfirmButtonAccessibleName, StringComparison.Ordinal);
        Assert.Equal("Add this new work?", viewModel.ConfirmationTitle);
        Assert.Equal("What will be added", viewModel.ReviewHeading);
        var creation = Assert.Single(viewModel.ReviewEffects, effect => effect.Kind == "Add task");
        Assert.Equal("Synthetic task", creation.TargetLabel);
        Assert.True(creation.IsCreation);
        Assert.False(creation.HasBefore);
        Assert.DoesNotContain(viewModel.ReviewEffects, effect => effect.IsExplanation);
    }

    [Fact]
    public void NewWorkReviewBuildsProjectHierarchyStandaloneTasksAndSupportingIdentitiesWithFinalColours()
    {
        var viewModel = new AgentProposalReviewViewModel(CreateRichNewWorkPlan(), new UnavailableApplication());

        var project = Assert.Single(viewModel.NewProjectGroups);
        Assert.Equal("Launch", project.TargetLabel);
        Assert.Equal("lime", project.ProjectColourKey);
        Assert.Equal("Fresh", project.CategoryName);
        Assert.Equal("violet", project.CategoryColourKey);
        var projectTask = Assert.Single(project.Tasks);
        Assert.Equal("Launch", projectTask.TargetLabel);
        Assert.Equal("Launch", projectTask.ProjectName);
        Assert.False(projectTask.HasCategoryOverride);
        Assert.Equal("Alex", projectTask.ParticipantsText);

        var standalone = Assert.Single(viewModel.NewStandaloneTasks);
        Assert.Equal("Independent follow-up", standalone.TargetLabel);
        Assert.True(standalone.IsStandalone);
        Assert.Equal("Fresh", standalone.CategoryName);
        Assert.Equal("violet", standalone.CategoryColourKey);

        var category = Assert.Single(viewModel.NewCategories);
        Assert.Equal("violet", category.TargetColourKey);
        Assert.Equal("Fresh", category.TargetLabel);
        Assert.Equal("Alex", Assert.Single(viewModel.NewParticipants).TargetLabel);
        Assert.Empty(viewModel.ChangedItems);
    }

    [Fact]
    public void TaskCreatedInExistingProjectUsesContextGroupWithoutPresentingProjectAsChanged()
    {
        var viewModel = new AgentProposalReviewViewModel(CreateExistingProjectTaskPlan(), new UnavailableApplication());

        var group = Assert.Single(viewModel.NewTasksByExistingProject);
        Assert.Equal("Existing project", group.ProjectName);
        Assert.Equal("cyan", group.ProjectColourKey);
        Assert.Equal("Client", group.CategoryName);
        Assert.Equal("gold", group.CategoryColourKey);
        Assert.Contains("Category Client", group.AccessibleName, StringComparison.Ordinal);
        var task = Assert.Single(group.Tasks);
        Assert.Equal("New task", task.TargetLabel);
        Assert.True(task.HasCategoryOverride);
        Assert.Equal("Urgent", task.CategoryName);
        Assert.Equal("Category override Urgent", task.CategoryRelationshipText);
        Assert.Empty(viewModel.NewProjectGroups);
        Assert.Empty(viewModel.NewStandaloneTasks);
        Assert.Empty(viewModel.ChangedItems);
    }

    private static ProposalExecutionPlan CreateExistingProjectTaskPlan()
    {
        const string identity = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var state = new ProposalWorkspaceState(new(1), new(
            [new("category", "Client", 0, "gold"), new("override", "Urgent", 1, "red")],
            [new("project", "Existing project", "", "category", null, 0, ColourKey: "cyan")],
            [], []), [], []);
        var plan = Assert.IsType<ProposalExecutionPlan>(new ProposalSemanticPlanner(
                TimeProvider.System,
                new AcceptingMarkdownValidator())
            .Validate(
                new ChangeProposal(
                    AgentInterchangeV1.ChangeProposalFormat,
                    1,
                    identity,
                    new(1),
                    [new CreateTaskOperation(
                        "create",
                        "task",
                        "New task",
                        "",
                        ProposalReference.Existing("project"),
                        new(ProposedTaskCategoryMode.Explicit, ProposalReference.Existing("override")),
                        null,
                        [])]),
                state,
                identity,
                "digest")
            .Plan);

        return plan;
    }

    [Fact]
    public void ChangedProjectUsesProjectIdentityInsteadOfTaskIconography()
    {
        const string identity = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var state = new ProposalWorkspaceState(new(1), new(
            [new("category", "Client", 0, "gold")],
            [new("project", "Existing project", "", "category", null, 0, ColourKey: "cyan")],
            [], []), [], []);
        var plan = Assert.IsType<ProposalExecutionPlan>(new ProposalSemanticPlanner(
                TimeProvider.System,
                new AcceptingMarkdownValidator())
            .Validate(
                new ChangeProposal(
                    AgentInterchangeV1.ChangeProposalFormat,
                    1,
                    identity,
                    new(1),
                    [new EditProjectOperation(
                        "edit",
                        ProposalReference.Existing("project"),
                        new(new(true, "Renamed project"), default, default))]),
                state,
                identity,
                "digest")
            .Plan);

        var changed = Assert.Single(new AgentProposalReviewViewModel(plan, new UnavailableApplication()).ChangedItems);

        Assert.True(changed.IsProject);
        Assert.False(changed.IsTask);
        Assert.Contains("Category Client", changed.AccessibleName, StringComparison.Ordinal);
    }

    [Fact]
    public void PostCreateChangesStayWithTheCreatedTaskInsteadOfDuplicatingItAsExistingWork()
    {
        const string identity = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var state = new ProposalWorkspaceState(new(1), new(
            [new("category", "Existing", 0, "orchid")],
            [], [], []), [], []);
        var plan = Assert.IsType<ProposalExecutionPlan>(new ProposalSemanticPlanner(
                TimeProvider.System,
                new AcceptingMarkdownValidator())
            .Validate(
                new ChangeProposal(
                    AgentInterchangeV1.ChangeProposalFormat,
                    1,
                    identity,
                    new(1),
                    [new CreateTaskOperation(
                        "create",
                        "task",
                        "Created task",
                        "",
                        null,
                        new(ProposedTaskCategoryMode.Explicit, ProposalReference.Existing("category")),
                        null,
                        []),
                     new CompleteTaskOperation("complete", ProposalReference.Local("task"))]),
                state,
                identity,
                "digest")
            .Plan);

        var viewModel = new AgentProposalReviewViewModel(plan, new UnavailableApplication());

        var task = Assert.Single(viewModel.NewStandaloneTasks);
        Assert.Equal("Created task", task.TargetLabel);
        Assert.Equal("Change completion", Assert.Single(task.Changes).Kind);
        Assert.Empty(viewModel.ChangedItems);
    }

    [Fact]
    public void CreatedTaskUsesItsProjectsFinalNameAndInheritedCategory()
    {
        const string identity = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var state = new ProposalWorkspaceState(new(1), new(
            [new("first", "First", 0, "gold"), new("final", "Final", 1, "violet")],
            [], [], []), [], []);
        var plan = Assert.IsType<ProposalExecutionPlan>(new ProposalSemanticPlanner(
                TimeProvider.System,
                new AcceptingMarkdownValidator())
            .Validate(
                new ChangeProposal(
                    AgentInterchangeV1.ChangeProposalFormat,
                    1,
                    identity,
                    new(1),
                    [new CreateProjectOperation(
                        "create-project", "project", "Initial", "", ProposalReference.Existing("first"), null),
                     new CreateTaskOperation(
                        "create-task", "task", "Task", "", ProposalReference.Local("project"),
                        new(ProposedTaskCategoryMode.Inherit), null, []),
                     new EditProjectOperation(
                        "rename-project", ProposalReference.Local("project"),
                        new(new(true, "Renamed"), default, default)),
                     new SetProjectCategoryOperation(
                        "recategorise-project", ProposalReference.Local("project"), ProposalReference.Existing("final"))]),
                state,
                identity,
                "digest")
            .Plan);

        var project = Assert.Single(new AgentProposalReviewViewModel(plan, new UnavailableApplication()).NewProjectGroups);
        var task = Assert.Single(project.Tasks);
        Assert.Equal("Renamed", project.TargetLabel);
        Assert.Equal("Final", project.CategoryName);
        Assert.Equal("Renamed", task.ProjectName);
        Assert.Equal("Final", task.CategoryName);
        Assert.False(task.HasCategoryOverride);
    }

    [Fact]
    public void MixedWorkspaceChangeSeparatesNewWorkFromExistingChanges()
    {
        const string identity = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var state = new ProposalWorkspaceState(new(1), new(
            [new("category", "Category", 0, "gold")],
            [],
            [new("existing", null, "Existing", "", "category", null, 0, null)],
            []), [], []);
        var plan = Assert.IsType<ProposalExecutionPlan>(new ProposalSemanticPlanner(
                TimeProvider.System,
                new AcceptingMarkdownValidator())
            .Validate(
                new ChangeProposal(
                    AgentInterchangeV1.ChangeProposalFormat,
                    1,
                    identity,
                    new(1),
                    [new CreateTaskOperation(
                        "create", "created", "Created", "", null,
                        new(ProposedTaskCategoryMode.Explicit, ProposalReference.Existing("category")), null, []),
                     new CompleteTaskOperation("complete", ProposalReference.Existing("existing"))]),
                state,
                identity,
                "digest")
            .Plan);

        var viewModel = new AgentProposalReviewViewModel(plan, new UnavailableApplication());

        Assert.True(viewModel.HasMixedReview);
        Assert.Equal("Adds and updates work", viewModel.ProposalKindLabel);
        Assert.Equal("What will be added and changed", viewModel.ReviewHeading);
        Assert.Contains("New work is separated", viewModel.ReviewHelpText, StringComparison.Ordinal);
        Assert.Single(viewModel.NewStandaloneTasks);
        Assert.Single(viewModel.ChangedItems);
    }

    [Fact]
    public void ReorderOnlyReviewShowsTheDirectlyMovedTaskAndNotShiftedNeighbours()
    {
        const string identity = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var state = new ProposalWorkspaceState(new(1), new(
            [new("category", "Synthetic", 0)],
            [new("project", "Project", "", "category", null, 0)],
            [new("first", "project", "First task", "", null, null, 0, 0),
             new("second", "project", "Second task", "", null, null, 1, 1)],
            []), [], []);
        var plan = Assert.IsType<ProposalExecutionPlan>(new ProposalSemanticPlanner(
                TimeProvider.System,
                new AcceptingMarkdownValidator())
            .Validate(
                new ChangeProposal(
                    AgentInterchangeV1.ChangeProposalFormat,
                    1,
                    identity,
                    new(1),
                    [new ReorderProjectTaskOperation(
                        "reorder",
                        ProposalReference.Existing("second"),
                        new(ProjectTaskPlacementKind.First))]),
                state,
                identity,
                "digest")
            .Plan);

        var viewModel = new AgentProposalReviewViewModel(plan, new UnavailableApplication());

        var row = Assert.Single(viewModel.ReviewEffects);
        Assert.Equal("Reorder task", row.Kind);
        Assert.Equal("Second task", row.TargetLabel);
        Assert.Equal("Position 2", row.Before);
        Assert.Equal("Position 1", row.After);
        Assert.DoesNotContain(viewModel.ReviewEffects, effect => effect.TargetLabel == "First task");
        Assert.Contains("Changes 1 item", viewModel.ProposalKindSummary, StringComparison.Ordinal);
        Assert.True(viewModel.HasImpliedChanges);
    }

    [Fact]
    public void MoveThenReorderShowsOneDestinationReorderAndKeepsOldProjectRemovalImplied()
    {
        const string identity = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var state = new ProposalWorkspaceState(new(1), new(
            [new("category", "Synthetic", 0)],
            [new("source", "Source project", "", "category", null, 0),
             new("destination", "Destination project", "", "category", null, 1)],
            [new("moving", "source", "Moving task", "", null, null, 0, 0),
             new("anchor", "destination", "Anchor task", "", null, null, 1, 0)],
            []), [], []);
        var plan = Assert.IsType<ProposalExecutionPlan>(new ProposalSemanticPlanner(
                TimeProvider.System,
                new AcceptingMarkdownValidator())
            .Validate(
                new ChangeProposal(
                    AgentInterchangeV1.ChangeProposalFormat,
                    1,
                    identity,
                    new(1),
                    [new MoveTaskOperation(
                        "move",
                        ProposalReference.Existing("moving"),
                        ProposalReference.Existing("destination"),
                        null),
                     new ReorderProjectTaskOperation(
                        "reorder",
                        ProposalReference.Existing("moving"),
                        new(ProjectTaskPlacementKind.Before, ProposalReference.Existing("anchor")))]),
                state,
                identity,
                "digest")
            .Plan);

        var viewModel = new AgentProposalReviewViewModel(plan, new UnavailableApplication());

        Assert.Single(viewModel.ReviewEffects, effect => effect.Kind == "Move task");
        var reorder = Assert.Single(viewModel.ReviewEffects, effect => effect.Kind == "Reorder task");
        Assert.Equal("Moving task", reorder.TargetLabel);
        Assert.Equal("Position 2", reorder.Before);
        Assert.Equal("Position 1", reorder.After);
        Assert.DoesNotContain(
            viewModel.ReviewEffects,
            effect => effect.Kind == "Reorder task" && effect.After == "Not included");
        var changed = Assert.Single(viewModel.ChangedItems);
        Assert.Equal("Moving task", changed.TargetLabel);
        Assert.Equal(["Move task", "Reorder task"], changed.Changes.Select(change => change.Kind));
    }

    [Theory]
    [InlineData("detach")]
    [InlineData("bin")]
    public void ReorderThenRemovalKeepsOneAccurateDirectReorderRow(string removal)
    {
        const string identity = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var state = new ProposalWorkspaceState(new(1), new(
            [new("category", "Synthetic", 0)],
            [new("project", "Project", "", "category", null, 0)],
            [new("first", "project", "First task", "", null, null, 0, 0),
             new("second", "project", "Second task", "", null, null, 1, 1)],
            []), [], []);
        ChangeOperation removalOperation = removal == "detach"
            ? new DetachTaskOperation("remove", ProposalReference.Existing("second"))
            : new MoveTaskToBinOperation("remove", ProposalReference.Existing("second"));
        var plan = Assert.IsType<ProposalExecutionPlan>(new ProposalSemanticPlanner(
                TimeProvider.System,
                new AcceptingMarkdownValidator())
            .Validate(
                new ChangeProposal(
                    AgentInterchangeV1.ChangeProposalFormat,
                    1,
                    identity,
                    new(1),
                    [new ReorderProjectTaskOperation(
                        "reorder",
                        ProposalReference.Existing("second"),
                        new(ProjectTaskPlacementKind.First)),
                     removalOperation]),
                state,
                identity,
                "digest")
            .Plan);

        var viewModel = new AgentProposalReviewViewModel(plan, new UnavailableApplication());

        var reorder = Assert.Single(viewModel.ReviewEffects, effect => effect.Kind == "Reorder task");
        Assert.Equal("Second task", reorder.TargetLabel);
        Assert.Equal("Position 2", reorder.Before);
        Assert.Equal("Position 1", reorder.After);
    }

    [AvaloniaFact]
    public void NewWorkWindowKeepsTechnicalDetailsCollapsedAndUsesAddLanguage()
    {
        var viewModel = new AgentProposalReviewViewModel(CreateNewWorkPlan(), new UnavailableApplication());
        var window = new AgentProposalReviewWindow(viewModel, new ReadyHost());
        window.Show();

        var details = Assert.IsType<Expander>(window.FindControl<Expander>("ProposalTechnicalDetails"));
        var apply = Assert.IsType<Button>(window.FindControl<Button>("RequestProposalApplyButton"));
        Assert.False(details.IsExpanded);
        Assert.Equal("Add all new work…", apply.Content);
        Assert.Equal(viewModel.ApplyButtonAccessibleName, AutomationProperties.GetName(apply));
        Assert.IsType<StackPanel>(window.FindControl<StackPanel>("ProposalEffectsList"));

        window.Close();
    }

    [AvaloniaFact]
    public void NewWorkWindowUsesNativeProjectCategoryAndTaskIdentityRecipes()
    {
        var viewModel = new AgentProposalReviewViewModel(CreateRichNewWorkPlan(), new UnavailableApplication());
        var window = new AgentProposalReviewWindow(viewModel, new ReadyHost());
        window.Show();

        Assert.Contains(window.GetVisualDescendants().OfType<IdentityTypeIcon>(), icon =>
            icon.WorkType == WorkType.Project && icon.ColourKey == "lime");
        Assert.Contains(window.GetVisualDescendants().OfType<CategoryIdentityMarker>(), marker =>
            marker.CategoryName == "Fresh" && marker.ColourKey == "violet");
        Assert.Contains(window.GetVisualDescendants().OfType<CategoryProjectPill>(), pill =>
            pill.CategoryName == "Fresh"
            && pill.CategoryColourKey == "violet"
            && pill.ProjectName == "Launch"
            && pill.ProjectColourKey == "lime");
        Assert.Contains(window.GetVisualDescendants().OfType<WorkTypeIcon>(), icon => icon.WorkType == WorkType.Task);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<ListBox>(), _ => true);

        window.Close();
    }

    [AvaloniaFact]
    public void ReviewVisualHierarchyAlignsProjectAndTaskTitleIcons()
    {
        var window = new AgentProposalReviewWindow(
            new AgentProposalReviewViewModel(CreateRichNewWorkPlan(), new UnavailableApplication()),
            new ReadyHost());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var rows = window.GetVisualDescendants()
            .OfType<Grid>()
            .Where(grid => grid.Classes.Contains("review-title-row"))
            .ToArray();
        Assert.True(rows.Length >= 2);
        foreach (var row in rows)
        {
            var icon = Assert.Single(row.GetVisualDescendants().OfType<Control>(),
                control => control.Classes.Contains("review-title-icon"));
            var title = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Classes.Contains("review-title-text") && text.IsEffectivelyVisible);
            Assert.True(icon.IsEffectivelyVisible);
            Assert.True(title.IsEffectivelyVisible);
            var iconOrigin = Assert.IsType<Point>(icon.TranslatePoint(default, window));
            var titleOrigin = Assert.IsType<Point>(title.TranslatePoint(default, window));
            var iconCentre = iconOrigin.Y + (icon.Bounds.Height / 2);
            var titleCentre = titleOrigin.Y + (title.Bounds.Height / 2);
            var difference = Math.Abs(iconCentre - titleCentre);
            Assert.True(
                difference <= 1,
                $"{title.Text} {icon.GetType().Name} centre difference was {difference}px; signed offset was {iconCentre - titleCentre}px.");
        }

        window.Close();
    }

    [AvaloniaFact]
    public void ExistingChangeTitlesAlignTheirVisibleTaskAndProjectIcons()
    {
        const string identity = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var state = new ProposalWorkspaceState(new(1), new(
            [new("category", "Client", 0, "gold")],
            [new("project", "Existing project", "", "category", null, 0, ColourKey: "cyan")],
            [new("task", "project", "Existing task", "", null, null, 0, 0)], []), [], []);
        var plan = Assert.IsType<ProposalExecutionPlan>(new ProposalSemanticPlanner(
                TimeProvider.System,
                new AcceptingMarkdownValidator())
            .Validate(
                new ChangeProposal(
                    AgentInterchangeV1.ChangeProposalFormat,
                    1,
                    identity,
                    new(1),
                    [new EditProjectOperation(
                         "edit-project", ProposalReference.Existing("project"),
                         new(new(true, "Renamed project"), default, default)),
                     new EditTaskOperation(
                         "edit-task", ProposalReference.Existing("task"),
                         new(new(true, "Renamed task"), default, default))]),
                state,
                identity,
                "digest")
            .Plan);
        var window = new AgentProposalReviewWindow(
            new AgentProposalReviewViewModel(plan, new UnavailableApplication()),
            new ReadyHost());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var rows = window.GetVisualDescendants().OfType<Grid>()
            .Where(grid => grid.Classes.Contains("review-title-row"))
            .ToArray();
        Assert.Equal(2, rows.Length);
        foreach (var row in rows)
        {
            var icon = Assert.Single(row.GetVisualDescendants().OfType<Control>(),
                control => control.Classes.Contains("review-title-icon") && control.IsEffectivelyVisible);
            var title = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Classes.Contains("review-title-text") && text.IsEffectivelyVisible);
            var iconOrigin = Assert.IsType<Point>(icon.TranslatePoint(default, window));
            var titleOrigin = Assert.IsType<Point>(title.TranslatePoint(default, window));
            var iconCentre = iconOrigin.Y + (icon.Bounds.Height / 2);
            var titleCentre = titleOrigin.Y + (title.Bounds.Height / 2);
            var difference = Math.Abs(iconCentre - titleCentre);
            Assert.True(
                difference <= 1,
                $"{title.Text} {icon.GetType().Name} centre difference was {difference}px; signed offset was {iconCentre - titleCentre}px.");
        }

        window.Close();
    }

    [AvaloniaFact]
    public void ReviewVisualHierarchyDoesNotIndentParticipantName()
    {
        var window = new AgentProposalReviewWindow(
            new AgentProposalReviewViewModel(CreateRichNewWorkPlan(), new UnavailableApplication()),
            new ReadyHost());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var kind = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Classes.Contains("supporting-person-kind"));
        var name = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Classes.Contains("supporting-person-name"));
        var kindOrigin = Assert.IsType<Point>(kind.TranslatePoint(default, window));
        var nameOrigin = Assert.IsType<Point>(name.TranslatePoint(default, window));
        Assert.InRange(Math.Abs(kindOrigin.X - nameOrigin.X), 0, 0.5);
        Assert.True(nameOrigin.Y > kindOrigin.Y);

        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false, 1, false)]
    [InlineData(false, 2, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, false)]
    [InlineData(false, 1, true)]
    [InlineData(false, 2, true)]
    [InlineData(true, 1, true)]
    [InlineData(true, 2, true)]
    public void ProposalIconsRenderLikeTheWorkingReference(bool lightTheme, int scale, bool existingProject)
    {
        var theme = lightTheme ? ThemeVariant.Light : ThemeVariant.Dark;
        var window = new AgentProposalReviewWindow(
            new AgentProposalReviewViewModel(
                existingProject ? CreateExistingProjectTaskPlan() : CreateRichNewWorkPlan(), new UnavailableApplication()),
            new ReadyHost()) { Height = 1100, RequestedThemeVariant = theme };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            using var rendered = RenderWindow(window, scale);
            var identities = window.GetVisualDescendants().OfType<StackPanel>()
                .Where(panel => panel.Classes.Contains("task-identity-layout")).ToArray();
            Assert.Equal(existingProject ? 1 : 2, identities.Length);
            foreach (var identity in identities)
            {
                var taskIcon = Assert.Single(identity.GetVisualDescendants().OfType<WorkTypeIcon>());
                var pill = Assert.Single(identity.GetVisualDescendants().OfType<CategoryProjectPill>());
                AssertMatchesReference(taskIcon, new WorkTypeIcon { WorkType = WorkType.Task });
                AssertMatchesReference(pill, new CategoryProjectPill
                {
                    CategoryName = pill.CategoryName,
                    CategoryColourKey = pill.CategoryColourKey,
                    ProjectName = pill.ProjectName,
                    ProjectColourKey = pill.ProjectColourKey,
                    HasOverride = pill.HasOverride,
                });
            }

            void AssertMatchesReference(Control actual, Control expected)
            {
                var reference = new Window
                {
                    Width = 500,
                    Height = 80,
                    RequestedThemeVariant = theme,
                    Background = actual.GetVisualAncestors().OfType<Border>()
                        .First(border => border.Background is not null).Background,
                    Content = new StackPanel { Margin = new Thickness(10), Children = { expected } },
                };
                expected.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
                reference.Show();
                Dispatcher.UIThread.RunJobs();
                try
                {
                    using var referencePixels = RenderWindow(reference, scale);
                    var actualOrigin = Assert.IsType<Point>(actual.TranslatePoint(default, window));
                    var expectedOrigin = Assert.IsType<Point>(expected.TranslatePoint(default, reference));
                    Assert.Equal(expected.Bounds.Size, actual.Bounds.Size);
                    var differences = 0;
                    var actualColours = new HashSet<SkiaSharp.SKColor>();
                    var expectedColours = new HashSet<SkiaSharp.SKColor>();
                    for (var y = 0; y < (int)(actual.Bounds.Height * scale); y++)
                    for (var x = 0; x < (int)(actual.Bounds.Width * scale); x++)
                    {
                        var actualPixel = rendered.GetPixel(
                            (int)(actualOrigin.X * scale) + x, (int)(actualOrigin.Y * scale) + y);
                        var expectedPixel = referencePixels.GetPixel(
                            (int)(expectedOrigin.X * scale) + x, (int)(expectedOrigin.Y * scale) + y);
                        actualColours.Add(actualPixel);
                        expectedColours.Add(expectedPixel);
                        // Skia's curved-edge antialiasing can round a channel by one at different screen origins.
                        if (Math.Abs(actualPixel.Red - expectedPixel.Red) > 1 ||
                            Math.Abs(actualPixel.Green - expectedPixel.Green) > 1 ||
                            Math.Abs(actualPixel.Blue - expectedPixel.Blue) > 1 ||
                            actualPixel.Alpha != expectedPixel.Alpha) differences++;
                    }
                    Assert.True(actualColours.Count > 1, "The proposal control must contain visible rendered ink.");
                    Assert.True(expectedColours.Count > 1, "The reference control must contain visible rendered ink.");
                    Assert.True(differences == 0,
                        $"{actual.GetType().Name}: {differences} rendered pixels differ from the working shared control.");
                }
                finally { reference.Close(); }
            }
        }
        finally { window.Close(); }
    }

    private static SkiaSharp.SKBitmap RenderWindow(Window window, int scale)
    {
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)window.Bounds.Width * scale, (int)window.Bounds.Height * scale),
            new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        return SkiaSharp.SKBitmap.Decode(stream);
    }

    [AvaloniaFact]
    public void ReviewTaskIdentityUsesTheNativeTaskIdentityRecipe()
    {
        var window = new AgentProposalReviewWindow(
            new AgentProposalReviewViewModel(CreateRichNewWorkPlan(), new UnavailableApplication()),
            new ReadyHost());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var identities = window.GetVisualDescendants().OfType<StackPanel>()
            .Where(panel => panel.Classes.Contains("task-identity-layout"))
            .ToArray();
        Assert.Equal(2, identities.Length);
        foreach (var identity in identities)
        {
            Assert.Equal(6, identity.Spacing);
            var titleRow = Assert.Single(identity.Children.OfType<Grid>());
            Assert.Equal(6, titleRow.ColumnSpacing);
            var icon = Assert.Single(titleRow.Children.OfType<WorkTypeIcon>());
            Assert.Equal(WorkType.Task, icon.WorkType);
            var title = Assert.Single(titleRow.Children.OfType<TextBlock>());
            Assert.Contains("work-identity-title", title.Classes);
            Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, title.TextTrimming);
        }

        window.Close();
    }

    [AvaloniaFact]
    public void SharedCategoryProjectPillKeepsTheCompactNativeRecipe()
    {
        var overridden = new CategoryProjectPill
        {
            CategoryName = "Urgent",
            CategoryColourKey = "gold",
            ProjectName = "Launch",
            ProjectColourKey = "cyan",
            HasOverride = true,
        };
        var inherited = new CategoryProjectPill
        {
            CategoryName = "Client",
            CategoryColourKey = "violet",
            ProjectName = "Launch",
            ProjectColourKey = "cyan",
        };
        var content = new StackPanel { Children = { overridden, inherited } };
        var window = new Window { Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(overridden.GetVisualDescendants().OfType<TextBlock>(),
            text => string.Equals(text.Text, "Category override", StringComparison.Ordinal));
        var overrideIcon = Assert.Single(overridden.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(),
            path => path.Classes.Contains("category-pill-override-icon"));
        Assert.True(overrideIcon.IsEffectivelyVisible);
        Assert.DoesNotContain(inherited.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(),
            path => path.Classes.Contains("category-pill-override-icon") && path.IsEffectivelyVisible);
        var icons = overridden.GetVisualDescendants().OfType<IdentityTypeIcon>().ToArray();
        Assert.Contains(icons, icon => icon.WorkType == WorkType.Category && icon.ColourKey == "gold");
        Assert.Contains(icons, icon => icon.WorkType == WorkType.Project && icon.ColourKey == "cyan");
        Assert.All(overridden.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.Classes.Contains("identity-pill-name")),
            text => Assert.Equal(130, text.MaxWidth));

        window.Close();
    }

    [AvaloniaFact]
    public void CategoryOverridePillKeepsLongIdentitySegmentsInsideItsCompactBounds()
    {
        var pill = new CategoryProjectPill
        {
            CategoryName = "A category name long enough to require truncation",
            CategoryColourKey = "gold",
            ProjectName = "A project name long enough to require truncation",
            ProjectColourKey = "cyan",
            HasOverride = true,
        };
        var window = new Window { Content = pill };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.InRange(pill.Bounds.Width, 1, 360);
        var pillOrigin = Assert.IsType<Point>(pill.TranslatePoint(default, window));
        var pillRight = pillOrigin.X + pill.Bounds.Width;
        foreach (var control in pill.GetVisualDescendants().OfType<Control>()
                     .Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0))
        {
            var origin = Assert.IsType<Point>(control.TranslatePoint(default, window));
            Assert.True(origin.X >= pillOrigin.X - 0.5, $"{control.GetType().Name} starts outside the pill.");
            Assert.True(origin.X + control.Bounds.Width <= pillRight + 0.5,
                $"{control.GetType().Name} ends outside the pill.");
        }
        Assert.All(pill.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.Classes.Contains("identity-pill-name")),
            text =>
            {
                Assert.Equal(130, text.MaxWidth);
                Assert.Equal(Avalonia.Media.TextTrimming.CharacterEllipsis, text.TextTrimming);
            });

        window.Close();
    }

    [AvaloniaFact]
    public void ReviewHierarchyRendersAtMinimumSizeInDarkAndLightThemes()
    {
        foreach (var (theme, name) in new[]
                 {
                     (ThemeVariant.Dark, "dark"),
                     (ThemeVariant.Light, "light"),
                 })
        {
            var viewModel = new AgentProposalReviewViewModel(CreateRichNewWorkPlan(), new UnavailableApplication());
            var window = new AgentProposalReviewWindow(viewModel, new ReadyHost())
            {
                RequestedThemeVariant = theme,
                Width = 660,
                Height = 520,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(theme, window.ActualThemeVariant);
            Assert.Equal(660, window.Bounds.Width);
            Assert.Equal(520, window.Bounds.Height);
            var scroll = Assert.IsType<ScrollViewer>(window.FindControl<ScrollViewer>("ProposalReviewContent"));
            Assert.True(scroll.Viewport.Height > 0);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            Assert.True(window.FindControl<Button>("RequestProposalApplyButton")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<Button>("CloseProposalReviewButton")!.IsEffectivelyVisible);

            using var bitmap = new RenderTargetBitmap(
                new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height),
                new Vector(96, 96));
            bitmap.Render(window);
            window.Close();
        }
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

    private static ProposalExecutionPlan CreateNewWorkPlan()
    {
        var source = new Source(new(new(1), new(
            [new("c", "Synthetic", 0)], [], [], []), [], []));
        const string input = """
            {
              "format": "dot-orbit-new-work-proposal",
              "schemaVersion": 1,
              "operations": [
                { "operation": "createCategory", "operationId": "create-category", "localId": "category", "name": "Fresh" },
                { "operation": "createTask", "operationId": "create-task", "localId": "task", "title": "Synthetic task", "description": "", "project": null, "category": { "mode": "explicit", "category": { "localId": "category" } }, "dueDate": null, "participants": [] }
              ]
            }
            """;
        return Assert.IsType<ProposalExecutionPlan>(
            new ProposalReviewBoundary(source, TimeProvider.System)
                .Review(Encoding.UTF8.GetBytes(input)).Plan);
    }

    private static ProposalExecutionPlan CreateRichNewWorkPlan()
    {
        var source = new Source(new(new(1), new(
            [new("existing-category", "Existing", 0, "orchid")], [], [], []), [], []));
        const string input = """
            {
              "format": "dot-orbit-new-work-proposal",
              "schemaVersion": 1,
              "operations": [
                { "operation": "createCategory", "operationId": "create-category", "localId": "category", "name": "Fresh" },
                { "operation": "createParticipant", "operationId": "create-person", "localId": "person", "label": "Alex" },
                { "operation": "createProject", "operationId": "create-project", "localId": "project", "title": "Launch", "description": "**Project description**", "category": { "localId": "category" }, "targetDate": "2026-11-02" },
                { "operation": "createTask", "operationId": "create-project-task", "localId": "project-task", "title": "Launch", "description": "Project task", "project": { "localId": "project" }, "category": { "mode": "inherit" }, "dueDate": "2026-10-30", "participants": [{ "localId": "person" }] },
                { "operation": "createTask", "operationId": "create-standalone-task", "localId": "standalone-task", "title": "Independent follow-up", "description": "Standalone task", "project": null, "category": { "mode": "explicit", "category": { "localId": "category" } }, "dueDate": null, "participants": [] }
              ]
            }
            """;
        return Assert.IsType<ProposalExecutionPlan>(
            new ProposalReviewBoundary(source, TimeProvider.System)
                .Review(Encoding.UTF8.GetBytes(input)).Plan);
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

    private sealed class AcceptingMarkdownValidator : IProposalMarkdownValidator
    {
        public ProposalMarkdownValidation Validate(string markdown, bool proposed) => new(true, 0, 0);
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
