using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class ProjectCaptureViewModelTests
{
    [Fact]
    public void TextAutosaveUsesSixHundredMillisecondsAndPersistsOnlyTheLatestRevision()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Original", "", "home", null);
        var model = new ProjectCaptureViewModel(work);
        var requests = 0;
        model.AutosaveRequested += (_, _) => requests++;
        model.SelectProject(project.Id);

        model.Title = "First";
        model.Title = "Second";
        model.Description = "Latest description";

        Assert.Equal(TimeSpan.FromMilliseconds(600), ProjectCaptureViewModel.AutosaveDelay);
        Assert.Equal(3, requests);
        Assert.Equal("Saving soon…", model.AutosaveStatus);
        Assert.Equal("Original", Assert.Single(work.Read().Projects).Title);
        Assert.True(model.RunScheduledAutosave());
        var saved = Assert.Single(work.Read().Projects);
        Assert.Equal("Second", saved.Title);
        Assert.Equal("Latest description", saved.Description);
        Assert.Equal("Saved", model.AutosaveStatus);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void FirstValidAutosaveCreatesWorkAndNewParticipantAtomically()
    {
        var work = new MemoryWorkspaceWork();
        var model = new ProjectCaptureViewModel(work);
        model.NewProjectCommand.Execute(null);
        Assert.True(model.RunScheduledAutosave());
        Assert.Empty(work.Read().Projects);
        model.Title = "Garden";
        Assert.True(model.RunScheduledAutosave());
        Assert.Equal("Garden", Assert.Single(work.Read().Projects).Title);
        Assert.False(model.ShowExplicitInspectorActions);

        model.NewTaskCommand.Execute(null);
        model.Title = "Call";
        model.ParticipantToAdd = Assert.IsType<ParticipantChoice>(model.AvailableParticipants.Last());
        Assert.True(model.ShowNewParticipantEntry);
        model.NewParticipantLabel = "SD";
        model.AddNewParticipantCommand.Execute(null);
        Assert.Empty(work.Read().Tasks);
        model.Category = model.Categories.Single(item => item.Id == "home");

        var snapshot = work.Read();
        var task = Assert.Single(snapshot.Tasks);
        var participant = Assert.Single(snapshot.Participants);
        Assert.Equal("SD", participant.Label);
        Assert.Equal([participant.Id], task.Participants);
    }

    [Fact]
    public void CategoryEditingRetainsExplicitSaveAndCancel()
    {
        var work = new MemoryWorkspaceWork();
        var model = new ProjectCaptureViewModel(work);
        model.SelectCategory("home");
        model.Title = "House";

        Assert.True(model.ShowExplicitInspectorActions);
        Assert.False(model.ShowAutosaveStatus);
        Assert.Equal("Home", work.Read().Categories.Single(item => item.Id == "home").Name);
        model.Cancel();
        Assert.Equal("Home", model.Title);
    }

    [Fact]
    public void ProjectCategoryChangesFlowToInheritedTasksAndLeaveOverridesStable()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        work.CreateTask(project.Id, "Inherited");
        var overridden = work.CreateTask(project.Id, "Override");
        work.UpdateTask(overridden.Id, overridden.Title, "", "home", null);
        var shell = new ShellViewModel(work);
        var model = shell.Work!;
        model.SelectProject(project.Id);
        model.Category = model.Categories.Single(c => c.Id == "work");
        Assert.True(model.Save());
        Assert.Equal("Work", model.Backlog.Single(t => t.Title == "Inherited").CategoryName);
        Assert.Equal("Home", model.Backlog.Single(t => t.Title == "Override").CategoryName);
        Assert.Equal("1", shell.PrimaryNavigation.Single(n => n.Title == "Projects").CountText);
        Assert.Equal("2", shell.PrimaryNavigation.Single(n => n.Title == "Backlog").CountText);
        model.QuickAdd(project.Id, "Third");
        Assert.Equal("3", shell.PrimaryNavigation.Single(n => n.Title == "Backlog").CountText);
    }

    [Fact]
    public void NavigationFlushesTextWhoseFieldBoundariesCouldOtherwiseCollide()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("a\u001fb", "c", "home", null);
        var model = new ProjectCaptureViewModel(work);
        model.SelectProject(project.Id);
        model.Title = "a";
        model.Description = "b\u001fc";
        Assert.True(model.IsDirty);
        var left = false;
        model.Navigate(() => left = true);
        Assert.True(left);
        Assert.False(model.NeedsDecision);
        Assert.Equal("a", Assert.Single(work.Read().Projects).Title);
        Assert.Equal("b\u001fc", Assert.Single(work.Read().Projects).Description);
    }

    [Fact]
    public void LoadingDraftIgnoresReentrantWritesFromThePreviouslyBoundTitleEditor()
    {
        var work = new MemoryWorkspaceWork();
        var previous = work.CreateProject("Previous", "", "home", null);
        var next = work.CreateProject("Next", "", "work", null);
        var model = new ProjectCaptureViewModel(work);
        model.SelectProject(previous.Id);
        var simulatedStaleWrite = false;
        model.PropertyChanged += (_, change) =>
        {
            if (!simulatedStaleWrite && change.PropertyName == nameof(ProjectCaptureViewModel.Title) && model.Title == "Next")
            {
                simulatedStaleWrite = true;
                model.Title = "Previous";
            }
        };

        model.SelectProject(next.Id);

        Assert.True(simulatedStaleWrite);
        Assert.Equal("Next", model.Title);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void CreationIsTransientAndCancelLeavesNoProject()
    {
        var work = new MemoryWorkspaceWork();
        var model = new ProjectCaptureViewModel(work);
        model.NewProjectCommand.Execute(null);
        Assert.False(model.IsDirty);
        model.Title = "Draft";
        Assert.True(model.IsDirty);
        model.Title = string.Empty;
        Assert.False(model.IsDirty);
        model.Title = "Draft";
        Assert.Empty(work.Read().Projects);
        Assert.Empty(model.Projects);
        model.CancelCommand.Execute(null);
        Assert.False(model.HasInspector);
        Assert.Empty(work.Read().Projects);

        model.NewTaskCommand.Execute(null);
        Assert.False(model.IsDirty);
        model.CancelCommand.Execute(null);
        model.NewCategoryCommand.Execute(null);
        Assert.False(model.IsDirty);
        model.CancelCommand.Execute(null);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("task")]
    [InlineData("category")]
    public void CreateDraftsGuardNavigationOnlyAfterARealEdit(string kind)
    {
        var model = new ProjectCaptureViewModel(new MemoryWorkspaceWork());
        var command = kind switch
        {
            "project" => model.NewProjectCommand,
            "task" => model.NewTaskCommand,
            _ => model.NewCategoryCommand,
        };

        command.Execute(null);
        Assert.False(model.IsDirty);
        var leftBlankDraft = false;
        model.Navigate(() => leftBlankDraft = true);
        Assert.True(leftBlankDraft);
        Assert.False(model.NeedsDecision);

        command.Execute(null);
        model.Title = "Draft";
        var leftEditedDraft = false;
        model.Navigate(() => leftEditedDraft = true);
        if (kind == "project")
        {
            Assert.True(leftEditedDraft);
            Assert.False(model.NeedsDecision);
        }
        else
        {
            Assert.False(leftEditedDraft);
            Assert.True(model.NeedsDecision);
            Assert.True(model.IsDirty);
        }
    }

    [Fact]
    public void CreateRequiresTitleAndShowsEmptyDerivedSummary()
    {
        var work = new MemoryWorkspaceWork();
        var model = new ProjectCaptureViewModel(work);
        model.NewProjectCommand.Execute(null);
        model.Title = "  ";
        Assert.False(model.Save());
        Assert.Empty(model.Projects);
        model.Title = "Plan garden";
        Assert.True(model.Save());
        var project = Assert.Single(model.Projects);
        Assert.Contains("Not started · 0 of 0 Tasks", project.Summary, StringComparison.Ordinal);
        Assert.Null(Assert.Single(work.Read().Projects).TargetDate);
        Assert.Equal("Save", model.SaveLabel);
    }

    [Fact]
    public void QuickAddFlushesPendingProjectChangesAndCreatesInheritedTasksInIndependentOrders()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var model = new ProjectCaptureViewModel(work);
        model.SelectProject(project.Id);
        model.Title = "Unsaved garden";
        Assert.True(model.QuickAdd(project.Id, " First "));
        Assert.True(model.QuickAdd(project.Id, "Second"));
        Assert.False(model.IsDirty);
        Assert.Equal("Unsaved garden", model.Title);
        Assert.Equal("Unsaved garden", Assert.Single(work.Read().Projects).Title);
        Assert.Equal(["First", "Second"], Assert.Single(model.Projects).Tasks.Select(t => t.Title));
        Assert.Equal(["Second", "First"], model.Backlog.Select(t => t.Title));
        Assert.All(work.Read().Tasks, t => { Assert.Null(t.ExplicitCategoryId); Assert.Null(t.DueDate); Assert.Empty(t.Description); });
        Assert.False(model.QuickAdd(project.Id, " \t "));
        Assert.Equal(2, work.Read().Tasks.Count);
    }

    [Fact]
    public void ExistingProjectAutosavesValidChangesAndRetainsInvalidDateForCorrection()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "Original", "home", null);
        var model = new ProjectCaptureViewModel(work);
        model.SelectProject(project.Id);
        model.Title = "New garden";
        model.Description = "**Plan**";
        model.Date = "2026-10-12";
        model.Category = model.Categories.Single(c => c.Id == "work");
        var automaticallySaved = Assert.Single(work.Read().Projects);
        Assert.Equal("New garden", automaticallySaved.Title);
        Assert.Equal("**Plan**", automaticallySaved.Description);
        Assert.Equal(new DateOnly(2026, 10, 12), automaticallySaved.TargetDate);
        Assert.Equal("work", automaticallySaved.CategoryId);
        model.Title = "Saved garden";
        model.Date = "2026-02-30";
        Assert.False(model.RunScheduledAutosave());
        Assert.True(model.HasDateValidationError);
        Assert.Equal("Enter a valid date as YYYY-MM-DD, or leave it empty.", model.DateValidationMessage);
        Assert.Equal("New garden", Assert.Single(work.Read().Projects).Title);
        model.Date = "2026-10-12";
        Assert.False(model.HasDateValidationError);
        model.Description = "**Plan**";
        model.Category = model.Categories.Single(c => c.Id == "work");
        Assert.True(model.RunScheduledAutosave());
        var saved = Assert.Single(work.Read().Projects);
        Assert.Equal("Saved garden", saved.Title);
        Assert.Equal("**Plan**", saved.Description);
        Assert.Equal(new DateOnly(2026, 10, 12), saved.TargetDate);
        Assert.Equal("work", saved.CategoryId);
    }

    [Fact]
    public void TaskCategoryCanBeOverriddenThenReturnedToInheritance()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTask(project.Id, "Dig");
        var model = new ProjectCaptureViewModel(work);
        model.SelectTask(task.Id);
        Assert.Contains("Inherited", model.CategoryHint, StringComparison.Ordinal);
        model.Category = model.Categories.Single(c => c.Id == "work");
        model.Description = "Dig carefully";
        model.Date = "2026-11-01";
        Assert.True(model.Save());
        Assert.Equal("work", Assert.Single(work.Read().Tasks).ExplicitCategoryId);
        Assert.Equal(new DateOnly(2026, 11, 1), Assert.Single(work.Read().Tasks).DueDate);
        Assert.Equal("Dig carefully", Assert.Single(work.Read().Tasks).Description);
        model.Category = model.Categories.Single(c => c.Id is null);
        Assert.True(model.Save());
        Assert.Null(Assert.Single(work.Read().Tasks).ExplicitCategoryId);
    }

    [Fact]
    public void ParticipantAssociationsAndNewLabelsAutosaveAtomically()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTask(project.Id, "Dig");
        var existing = work.CreateParticipant("SD");
        var model = new ProjectCaptureViewModel(work);
        model.SelectTask(task.Id);

        model.ParticipantToAdd = model.AvailableParticipants.Single(item => item.Id == existing.Id);
        model.AddParticipantCommand.Execute(null);
        model.ParticipantToAdd = ParticipantChoice.New;
        model.NewParticipantLabel = "AB";
        model.AddNewParticipantCommand.Execute(null);

        Assert.False(model.IsDirty);
        Assert.Equal(["SD", "AB"], model.SelectedParticipants.Select(item => item.Label));
        var saved = work.Read();
        Assert.Equal(2, saved.Participants.Count);
        Assert.Equal(saved.Participants.Select(item => item.Id), saved.Tasks.Single().Participants);
    }

    [Fact]
    public void NewParticipantReusesEquivalentExistingIdentityAndRejectsAlreadyAssociatedMatch()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Call", "", "home", null);
        work.CreateParticipant("SD");
        var model = new ProjectCaptureViewModel(work);
        model.SelectTask(task.Id);
        model.NewParticipantLabel = "  ";
        model.AddNewParticipantCommand.Execute(null);
        Assert.Equal("Enter a Participant label.", model.NewParticipantValidationMessage);
        model.NewParticipantLabel = "sd";
        model.AddNewParticipantCommand.Execute(null);
        var selected = Assert.Single(model.SelectedParticipants);
        Assert.Equal("SD", selected.Label);
        Assert.NotNull(selected.Id);
        Assert.Single(work.Read().Participants);

        model.ParticipantToAdd = ParticipantChoice.New;
        model.NewParticipantLabel = " ＳＤ ";
        model.AddNewParticipantCommand.Execute(null);
        Assert.Equal("This Participant is already on this Task.", model.NewParticipantValidationMessage);
        Assert.Equal(" ＳＤ ", model.NewParticipantLabel);
        Assert.Equal("new-participant-label", model.ParticipantFocusAutomationId);
        Assert.Single(model.SelectedParticipants);
    }

    [Fact]
    public void StandaloneTaskDraftRequiresExplicitCategoryAndNeverAppearsUnderAProject()
    {
        var work = new MemoryWorkspaceWork();
        work.CreateProject("Garden", "", "home", null);
        var model = new ProjectCaptureViewModel(work);

        model.NewTaskCommand.Execute(null);
        model.Title = "Buy compost";
        model.Description = "Peat free";
        model.Date = "2026-10-03";
        Assert.False(model.Save());
        Assert.Empty(work.Read().Tasks);

        model.Category = model.Categories.Single(category => category.Id == "work");
        Assert.True(model.Save());

        var task = Assert.Single(work.Read().Tasks);
        Assert.Null(task.ProjectId);
        Assert.Null(task.ProjectPosition);
        Assert.Equal("work", task.ExplicitCategoryId);
        Assert.Equal("Peat free", task.Description);
        Assert.Equal(new DateOnly(2026, 10, 3), task.DueDate);
        Assert.Empty(Assert.Single(model.Projects).Tasks);
        Assert.Equal("Work · standalone", Assert.Single(model.Backlog).CategoryDisplay);
    }

    [Fact]
    public void BacklogQuickAddRetainsCategoryOnlyUntilTheEntrySessionEnds()
    {
        var work = new MemoryWorkspaceWork();
        var shell = new ShellViewModel(work);
        var backlog = shell.PrimaryNavigation.Single(item => item.Title == "Backlog");
        backlog.SelectCommand.Execute(null);
        var model = shell.Work!;
        model.BacklogQuickTitle = "Needs category";
        Assert.False(model.SubmitBacklogQuickAdd());
        Assert.Equal("Needs category", model.BacklogQuickTitle);
        Assert.Empty(work.Read().Tasks);
        model.BacklogQuickCategory = model.BacklogCategories.Single(category => category.Id == "home");
        model.BacklogQuickTitle = "First";
        Assert.True(model.SubmitBacklogQuickAdd());
        model.BacklogQuickTitle = "Second";
        Assert.True(model.SubmitBacklogQuickAdd());
        Assert.Equal("home", model.BacklogQuickCategory?.Id);
        Assert.All(work.Read().Tasks, task => Assert.Equal("home", task.ExplicitCategoryId));

        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        backlog.SelectCommand.Execute(null);
        Assert.Null(model.BacklogQuickCategory);
        Assert.Empty(model.BacklogQuickTitle);
    }

    [Fact]
    public void CategoryProjectionPreservesGlobalOrdersAndManagementUsesAccessibleCategoryScope()
    {
        var work = new MemoryWorkspaceWork();
        var homeFirst = work.CreateProject("Home first", "", "home", null);
        var workProject = work.CreateProject("Work project", "", "work", null);
        var homeSecond = work.CreateProject("Home second", "", "home", null);
        work.CreateStandaloneTask("Home older", "", "home", null);
        work.CreateStandaloneTask("Work task", "", "work", null);
        work.CreateStandaloneTask("Home newest", "", "home", null);
        var model = new ProjectCaptureViewModel(work);

        Assert.Equal(["Home", "Work"], model.CategoryGroups.Select(category => category.Name));
        var home = model.CategoryGroups.Single(category => category.Id == "home");
        Assert.Equal([homeFirst.Id, homeSecond.Id], home.Projects.Select(row => row.Project.Id));
        Assert.Equal(["Home newest", "Home older"], home.StandaloneTasks.Select(row => row.Task.Title));
        Assert.DoesNotContain(workProject.Id, home.Projects.Select(row => row.Project.Id));

        model.CategoryGroups.Single(category => category.Id == "work").MoveToTopCommand.Execute(null);
        Assert.Equal(["Work", "Home"], model.CategoryGroups.Select(category => category.Name));
        Assert.Equal("Moved Work to position 1 of 2 in Categories.", model.ReorderAnnouncement);
        Assert.Equal("category-reorder-work", model.ReorderFocusAutomationId);

        model.NewCategoryCommand.Execute(null);
        model.Title = " ";
        Assert.False(model.Save());
        Assert.True(model.HasCategoryNameValidationError);
        Assert.Equal("Enter a category name.", model.CategoryNameValidationMessage);
        model.Title = "Personal";
        Assert.False(model.HasCategoryNameValidationError);
        Assert.True(model.Save());
        Assert.Equal("Personal", model.CategoryGroups[^1].Name);
        model.Title = "HOME";
        Assert.False(model.Save());
        Assert.True(model.HasCategoryNameValidationError);
        Assert.Equal("Personal", model.CategoryGroups[^1].Name);
        model.Title = "Someday";
        Assert.False(model.HasCategoryNameValidationError);
        Assert.True(model.Save());
        Assert.Equal("Someday", model.CategoryGroups[^1].Name);

        model.CategoryGroups.Single(category => category.Id == "home").SelectCommand.Execute(null);
        model.DeleteCategoryCommand.Execute(null);
        Assert.True(model.NeedsCategoryReplacement);
        model.CategoryReplacement = model.CategoryReplacementChoices.Single(category => category.Id == "work");
        model.ConfirmDeleteCategoryCommand.Execute(null);
        Assert.False(model.NeedsCategoryReplacement);
        Assert.DoesNotContain(model.CategoryGroups, category => category.Id == "home");
        Assert.All(work.Read().Projects.Where(project => project.Id != workProject.Id), project => Assert.Equal("work", project.CategoryId));
        Assert.All(work.Read().Tasks.Where(task => task.ProjectId is null && task.Title.StartsWith("Home", StringComparison.Ordinal)), task => Assert.Equal("work", task.ExplicitCategoryId));
    }

    [Fact]
    public void CategoriesKeepCompletedActiveStandaloneTasks()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Completed but active", "", "work", null);
        work.CompleteTask(task.Id);

        var model = new ProjectCaptureViewModel(work);

        var categoryTask = Assert.Single(model.CategoryGroups.Single(category => category.Id == "work").StandaloneTasks);
        Assert.Equal(task.Id, categoryTask.Task.Id);
        Assert.True(categoryTask.Task.IsComplete);
    }

    [Theory]
    [InlineData("save", "Renamed", true)]
    [InlineData("discard", "Work", true)]
    [InlineData("stay", "Work", false)]
    public void CategoryDeletionRequiresExplicitDraftResolution(string choice, string persistedName, bool requestsDeletion)
    {
        var work = new MemoryWorkspaceWork();
        work.CreateProject("Referenced project", "", "work", null);
        var model = new ProjectCaptureViewModel(work);
        model.SelectCategory("work");
        model.Title = "Renamed";

        model.DeleteCategoryCommand.Execute(null);

        Assert.True(model.NeedsDecision);
        Assert.False(model.NeedsCategoryReplacement);
        var command = choice switch
        {
            "save" => model.SaveAndLeaveCommand,
            "discard" => model.DiscardAndLeaveCommand,
            _ => model.StayCommand,
        };
        command.Execute(null);

        Assert.False(model.NeedsDecision);
        Assert.Equal(requestsDeletion, model.NeedsCategoryReplacement);
        Assert.Equal(persistedName, work.Read().Categories.Single(category => category.Id == "work").Name);
        Assert.True(model.HasInspector);
        if (!requestsDeletion)
        {
            Assert.True(model.IsDirty);
            Assert.Equal("Renamed", model.Title);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CategoryStorageFailureRetainsTheDraftWithoutShowingANameValidationError(bool creating)
    {
        var work = new MemoryWorkspaceWork();
        var model = new ProjectCaptureViewModel(work);
        if (creating) model.NewCategoryCommand.Execute(null);
        else model.SelectCategory("work");
        model.Title = creating ? "Personal" : "Renamed";
        work.FailWrites = true;

        Assert.False(model.Save());

        Assert.False(model.HasCategoryNameValidationError);
        Assert.Equal("Could not save workspace changes. Your draft is retained. Try again.", model.Message);
        Assert.True(model.IsDirty);
        Assert.Equal(creating ? "Personal" : "Renamed", model.Title);
    }

    [Fact]
    public void TaskContextActionsDetachMatchAutomaticallyAndRequireAnExplicitMismatchChoice()
    {
        var work = new MemoryWorkspaceWork();
        var homeProject = work.CreateProject("Home project", "", "home", null);
        var workProject = work.CreateProject("Work project", "", "work", null);
        var task = work.CreateStandaloneTask("Move me", "", "home", null);
        var model = new ProjectCaptureViewModel(work);

        model.SelectTask(task.Id);
        model.TaskContextTarget = model.TaskContextChoices.Single(choice => choice.ProjectId == homeProject.Id);
        model.ChangeTaskContextCommand.Execute(null);
        var attached = work.Read().Tasks.Single(item => item.Id == task.Id);
        Assert.Equal(homeProject.Id, attached.ProjectId);
        Assert.Null(attached.ExplicitCategoryId);
        Assert.False(model.NeedsAttachmentChoice);

        model.TaskContextTarget = model.TaskContextChoices.Single(choice => choice.ProjectId is null);
        model.ChangeTaskContextCommand.Execute(null);
        var detached = work.Read().Tasks.Single(item => item.Id == task.Id);
        Assert.Null(detached.ProjectId);
        Assert.Equal("home", detached.ExplicitCategoryId);

        model.TaskContextTarget = model.TaskContextChoices.Single(choice => choice.ProjectId == workProject.Id);
        model.ChangeTaskContextCommand.Execute(null);
        Assert.True(model.NeedsAttachmentChoice);
        Assert.True(model.HasBlockingDialog);
        Assert.Null(work.Read().Tasks.Single(item => item.Id == task.Id).ProjectId);
        Assert.Contains("Keep Home", model.PreserveCategoryLabel, StringComparison.Ordinal);
        Assert.Contains("Adopt Work", model.AdoptCategoryLabel, StringComparison.Ordinal);
        work.FailWrites = true;
        model.PreserveTaskCategoryCommand.Execute(null);
        Assert.True(model.NeedsAttachmentChoice);
        Assert.Null(work.Read().Tasks.Single(item => item.Id == task.Id).ProjectId);
        work.FailWrites = false;
        model.PreserveTaskCategoryCommand.Execute(null);

        var preserved = work.Read().Tasks.Single(item => item.Id == task.Id);
        Assert.Equal(workProject.Id, preserved.ProjectId);
        Assert.Equal("home", preserved.ExplicitCategoryId);

        model.TaskContextTarget = model.TaskContextChoices.Single(choice => choice.ProjectId is null);
        model.ChangeTaskContextCommand.Execute(null);
        model.TaskContextTarget = model.TaskContextChoices.Single(choice => choice.ProjectId == workProject.Id);
        model.ChangeTaskContextCommand.Execute(null);
        Assert.True(model.NeedsAttachmentChoice);
        model.AdoptProjectCategoryCommand.Execute(null);
        var adopted = work.Read().Tasks.Single(item => item.Id == task.Id);
        Assert.Equal(workProject.Id, adopted.ProjectId);
        Assert.Null(adopted.ExplicitCategoryId);
    }

    [Fact]
    public void TaskContextActionFlushesPendingTaskFieldsWithoutClosingTheInspector()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Work project", "", "work", null);
        var task = work.CreateStandaloneTask("Move me", "", "home", null);
        var model = new ProjectCaptureViewModel(work);

        model.SelectTask(task.Id);
        model.Title = "Unsaved title";
        model.TaskContextTarget = model.TaskContextChoices.Single(choice => choice.ProjectId == project.Id);
        model.ChangeTaskContextCommand.Execute(null);
        Assert.False(model.NeedsDecision);
        Assert.True(model.HasInspector);
        Assert.True(model.NeedsAttachmentChoice);
        Assert.Equal("Unsaved title", model.Title);
        Assert.Equal("Unsaved title", work.Read().Tasks.Single(item => item.Id == task.Id).Title);
        Assert.Null(work.Read().Tasks.Single(item => item.Id == task.Id).ProjectId);

        model.CancelAttachmentCommand.Execute(null);
        Assert.False(model.NeedsAttachmentChoice);
        Assert.True(model.HasInspector);
    }

    [Fact]
    public void AllAccessibleMoveCommandsUseTheSharedOrderAndAnnouncePositionAndScope()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        foreach (var title in new[] { "One", "Two", "Three", "Four" }) work.CreateTask(project.Id, title);
        var model = new ProjectCaptureViewModel(work);
        var one = model.Backlog.Single(task => task.Title == "One");

        one.MoveUpCommand.Execute(null);
        Assert.Equal(["Four", "Three", "One", "Two"], model.Backlog.Select(task => task.Title));
        one.MoveToTopCommand.Execute(null);
        Assert.Equal(["One", "Four", "Three", "Two"], model.Backlog.Select(task => task.Title));
        one.MoveDownCommand.Execute(null);
        Assert.Equal(["Four", "One", "Three", "Two"], model.Backlog.Select(task => task.Title));
        one.MoveToBottomCommand.Execute(null);
        Assert.Equal(["Four", "Three", "Two", "One"], model.Backlog.Select(task => task.Title));
        Assert.Equal("Moved One to position 4 of 4 in Backlog.", model.ReorderAnnouncement);
        Assert.Equal(model.Backlog.Select(task => task.Id), work.Read().Tasks.Select(task => task.Id));
    }

    [Fact]
    public void NavigationFlushesValidProjectChangesWithoutPrompting()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Original", "", "home", null);
        var shell = new ShellViewModel(work);
        shell.Work!.SelectProject(project.Id);
        shell.Work.Title = "Edited";
        shell.PrimaryNavigation.Single(n => n.Title == "Projects").SelectCommand.Execute(null);
        Assert.Equal("Projects", shell.ViewTitle);
        Assert.False(shell.Work.NeedsDecision);
        Assert.Equal("Edited", Assert.Single(work.Read().Projects).Title);
    }

    [Fact]
    public void FailedSaveRetainsDraftPendingDestinationAndAllowsRetry()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Original", "", "home", null);
        var model = new ProjectCaptureViewModel(work);
        model.SelectProject(project.Id);
        model.Title = "Edited";
        bool left = false;
        work.FailWrites = true;
        model.Navigate(() => left = true);
        Assert.False(left);
        Assert.True(model.NeedsDecision);
        Assert.True(model.IsDirty);
        Assert.Equal("Edited", model.Title);
        work.FailWrites = false;
        model.SaveAndLeaveCommand.Execute(null);
        Assert.True(left);
        Assert.Equal("Edited", Assert.Single(work.Read().Projects).Title);
    }

    [Fact]
    public void CompletionUpdatesDerivedProjectStateAndReopenRestoresBacklogWithoutChangingCapturedDate()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", new DateOnly(2026, 9, 28));
        var first = work.CreateTask(project.Id, "First");
        var second = work.CreateTask(project.Id, "Second");
        var model = new ProjectCaptureViewModel(work, new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));

        model.Backlog.Single(row => row.Id == first.Id).ToggleCompletionCommand.Execute(null);
        Assert.Single(model.Backlog);
        Assert.Equal("In progress", Assert.Single(model.Projects).Status);
        Assert.Equal("1/2 tasks", Assert.Single(model.Projects).ProgressText);

        model.Projects.Single().Tasks.Single(row => row.Id == second.Id).ToggleCompletionCommand.Execute(null);
        var complete = Assert.Single(model.Projects);
        Assert.Equal("Complete", complete.Status);
        Assert.Equal("Completed 29 Sep 2026", complete.CompletionDateText);
        Assert.False(complete.IsOverdue);
        Assert.Empty(model.Backlog);

        complete.Tasks.Single(row => row.Id == first.Id).ToggleCompletionCommand.Execute(null);
        Assert.Equal("In progress", Assert.Single(model.Projects).Status);
        Assert.Empty(Assert.Single(model.Projects).CompletionDateText);
        Assert.Contains(model.Backlog, row => row.Id == first.Id);
    }

    [Fact]
    public void CompletedProjectionSurvivesReloadShowsCapturedDateAndReopensStandaloneTask()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Filed receipt", "", "home", null);
        work.CompleteTask(task.Id);

        var model = new ProjectCaptureViewModel(work);
        var completed = Assert.Single(model.Completed);
        Assert.Equal(task.Id, completed.Id);
        Assert.Equal("Completed 29 Sep 2026", completed.CompletionDateText);
        Assert.Empty(model.Backlog);

        var reloaded = new ProjectCaptureViewModel(work);
        reloaded.SetCompletedActive(true);
        Assert.Equal(task.Id, Assert.Single(reloaded.Completed).Id);
        reloaded.Completed.Single().ToggleCompletionCommand.Execute(null);
        Assert.Empty(reloaded.Completed);
        Assert.Equal(task.Id, Assert.Single(reloaded.Backlog).Id);
        Assert.Equal("navigation-completed", reloaded.CompletionFocusAutomationId);
    }

    [Fact]
    public void CompletedGroupsUseThreeDailyBucketsThenCalendarWeeks()
    {
        var work = new MemoryWorkspaceWork();
        TaskRecord Add(string title, DateOnly date, int hour)
        {
            var task = work.CreateStandaloneTask(title, "", "home", null);
            return work.SetCompletion(task.Id, new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.Zero), date);
        }

        var today = Add("Today", new DateOnly(2026, 9, 29), 12);
        var yesterday = Add("Yesterday", new DateOnly(2026, 9, 28), 12);
        var twoDaysAgo = Add("Two days ago", new DateOnly(2026, 9, 27), 12);
        var weekNewer = Add("Week newer", new DateOnly(2026, 9, 26), 15);
        var weekOlder = Add("Week older", new DateOnly(2026, 9, 24), 9);
        var priorWeek = Add("Prior week", new DateOnly(2026, 9, 20), 12);
        var model = new ProjectCaptureViewModel(
            work,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero)));

        Assert.Equal(
            ["Today", "Yesterday", "Sunday, 27 September 2026", "Week of 21 Sep 2026", "Week of 14 Sep 2026"],
            model.CompletedGroups.Select(group => group.Heading));
        Assert.Equal([today.Id], model.CompletedGroups[0].Tasks.Select(task => task.Id));
        Assert.Equal([yesterday.Id], model.CompletedGroups[1].Tasks.Select(task => task.Id));
        Assert.Equal([twoDaysAgo.Id], model.CompletedGroups[2].Tasks.Select(task => task.Id));
        Assert.Equal([weekNewer.Id, weekOlder.Id], model.CompletedGroups[3].Tasks.Select(task => task.Id));
        Assert.Equal([priorWeek.Id], model.CompletedGroups[4].Tasks.Select(task => task.Id));
    }

    [Fact]
    public void CompletedGroupsUseCapturedCalendarOrderWhenUtcInstantsAreInverted()
    {
        var work = new MemoryWorkspaceWork();
        var todayOlder = work.CreateStandaloneTask("Today older", "", "home", null);
        var todayNewer = work.CreateStandaloneTask("Today newer", "", "home", null);
        var yesterdayLaterInstant = work.CreateStandaloneTask("Yesterday later instant", "", "home", null);
        work.SetCompletion(todayOlder.Id, new DateTimeOffset(2026, 9, 29, 8, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 29));
        work.SetCompletion(todayNewer.Id, new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 29));
        work.SetCompletion(yesterdayLaterInstant.Id, new DateTimeOffset(2026, 9, 29, 20, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 28));

        var model = new ProjectCaptureViewModel(
            work,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 22, 0, 0, TimeSpan.Zero)));

        Assert.Equal(["Today", "Yesterday"], model.CompletedGroups.Select(group => group.Heading));
        Assert.Equal([todayNewer.Id, todayOlder.Id], model.CompletedGroups[0].Tasks.Select(task => task.Id));
        Assert.Equal([yesterdayLaterInstant.Id], model.CompletedGroups[1].Tasks.Select(task => task.Id));
    }

    [Fact]
    public void ReopeningTaskFlushesPendingFieldsAndMovesItImmediately()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Filed receipt", "", "home", null);
        work.CompleteTask(task.Id);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Completed").SelectCommand.Execute(null);
        var model = shell.Work!;
        model.SelectTask(task.Id);
        model.Title = "Unsaved receipt";

        model.Completed.Single().ToggleCompletionCommand.Execute(null);
        Assert.False(model.NeedsDecision);
        Assert.False(work.Read().Tasks.Single().IsComplete);
        Assert.Equal("Unsaved receipt", work.Read().Tasks.Single().Title);
        Assert.Empty(model.Completed);
    }

    [Fact]
    public void BacklogReorderUsesVisiblePositionsWhenCompletedTasksAreInterleaved()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var a = work.CreateTask(project.Id, "A");
        var b = work.CreateTask(project.Id, "B");
        var c = work.CreateTask(project.Id, "C");
        var d = work.CreateTask(project.Id, "D");
        var e = work.CreateTask(project.Id, "E");
        var f = work.CreateTask(project.Id, "F");
        work.CompleteTask(e.Id);
        work.CompleteTask(c.Id);
        var model = new ProjectCaptureViewModel(work);

        model.Backlog.Single(task => task.Id == f.Id).MoveDownCommand.Execute(null);
        Assert.Equal([d.Id, e.Id, f.Id, c.Id, b.Id, a.Id], work.Read().Tasks.Select(task => task.Id));
        model.Backlog.Single(task => task.Id == d.Id).MoveToBottomCommand.Execute(null);
        Assert.Equal([f.Id, e.Id, b.Id, c.Id, a.Id, d.Id], work.Read().Tasks.Select(task => task.Id));
        model.Backlog.Single(task => task.Id == d.Id).MoveToTopCommand.Execute(null);
        Assert.Equal([d.Id, e.Id, f.Id, c.Id, b.Id, a.Id], work.Read().Tasks.Select(task => task.Id));
        Assert.Equal("Moved D to position 1 of 4 in Backlog.", model.ReorderAnnouncement);
    }

    [Fact]
    public void CompletingBacklogTaskFlushesPendingFieldsAndMovesFocusToNextRow()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var first = work.CreateTask(project.Id, "First");
        var second = work.CreateTask(project.Id, "Second");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        var model = shell.Work!;
        model.SelectTask(second.Id);
        model.Title = "Edited second";

        model.Backlog.Single(row => row.Id == second.Id).ToggleCompletionCommand.Execute(null);
        Assert.False(model.NeedsDecision);
        Assert.True(work.Read().Tasks.Single(task => task.Id == second.Id).IsComplete);
        Assert.Equal("Edited second", work.Read().Tasks.Single(task => task.Id == second.Id).Title);
        Assert.Equal("First", work.Read().Tasks.Single(task => task.Id == first.Id).Title);
        Assert.Equal(model.Backlog.Single().CompletionAutomationId, model.CompletionFocusAutomationId);
    }

    [Fact]
    public void CompletionAndIndependentReordersFlushPendingInspectorChanges()
    {
        var work = new MemoryWorkspaceWork();
        var firstProject = work.CreateProject("First", "", "home", null);
        var secondProject = work.CreateProject("Second", "", "home", null);
        var first = work.CreateTask(firstProject.Id, "First task");
        var second = work.CreateTask(firstProject.Id, "Second task");
        var model = new ProjectCaptureViewModel(work);
        model.SelectTask(first.Id);
        model.Title = "Unsaved title";

        model.Projects.Single(project => project.Id == secondProject.Id).MoveToTopCommand.Execute(null);
        model.Projects.Single(project => project.Id == firstProject.Id).Tasks.Single(task => task.Id == second.Id).MoveProjectTaskToTopCommand.Execute(null);
        model.Projects.Single(project => project.Id == firstProject.Id).Tasks.Single(task => task.Id == first.Id).ToggleCompletionCommand.Execute(null);

        Assert.False(model.IsDirty);
        Assert.Equal("Unsaved title", model.Title);
        Assert.Equal("Unsaved title", work.Read().Tasks.Single(task => task.Id == first.Id).Title);
        Assert.Equal([secondProject.Id, firstProject.Id], work.Read().Projects.Select(project => project.Id));
        Assert.Equal([second.Id, first.Id], work.Read().Tasks.Where(task => task.ProjectId == firstProject.Id).OrderBy(task => task.ProjectPosition).Select(task => task.Id));
        Assert.True(work.Read().Tasks.Single(task => task.Id == first.Id).IsComplete);
        Assert.Equal($"task-completion-{first.Id}", model.CompletionFocusAutomationId);
    }

    [Fact]
    public void RowsPresentRelativeFullAndUndatedDatesAndEmptyPastProjectAsOverdue()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", new DateOnly(2026, 9, 28));
        var today = work.CreateStandaloneTask("Today task", "", "home", new DateOnly(2026, 9, 29));
        var undated = work.CreateStandaloneTask("Undated task", "", "home", null);
        var tomorrow = work.CreateTask(project.Id, "Tomorrow task");
        work.UpdateTask(tomorrow.Id, tomorrow.Title, "", null, new DateOnly(2026, 9, 30));
        var model = new ProjectCaptureViewModel(work, new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));

        Assert.Equal("Today", model.Backlog.Single(row => row.Id == today.Id).DateText);
        Assert.Equal("Due Tuesday, 29 September 2026", model.Backlog.Single(row => row.Id == today.Id).DateAccessibleText);
        Assert.Equal("Tomorrow", model.Backlog.Single(row => row.Id == tomorrow.Id).DateText);
        Assert.True(Assert.Single(model.Projects).IsOverdue);

        var empty = work.CreateProject("Empty", "", "home", new DateOnly(2026, 9, 28));
        model = new ProjectCaptureViewModel(work, new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));
        Assert.True(model.Projects.Single(row => row.Id == empty.Id).IsOverdue);
        Assert.Equal("No date", model.Backlog.Single(row => row.Id == undated.Id).DateText);
    }

    [Fact]
    public void DatePresentationRefreshesAfterLocalMidnightAndTimeZoneChange()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", new DateOnly(2026, 9, 29));
        var task = work.CreateTask(project.Id, "Water");
        work.UpdateTask(task.Id, task.Title, "", null, new DateOnly(2026, 9, 30));
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 23, 30, 0, TimeSpan.Zero));
        var model = new ProjectCaptureViewModel(work, time);

        Assert.Equal("Tomorrow", model.Backlog.Single().DateText);
        Assert.False(model.Projects.Single().IsOverdue);
        Assert.False(model.RefreshDatePresentation());

        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("UTC+02", TimeSpan.FromHours(2), "UTC+02", "UTC+02");
        time.Set(time.GetUtcNow(), plusTwo);
        Assert.True(model.RefreshDatePresentation());
        Assert.Equal("Today", model.Backlog.Single().DateText);
        Assert.True(model.Projects.Single().IsOverdue);

        time.Set(new DateTimeOffset(2026, 9, 30, 22, 30, 0, TimeSpan.Zero));
        Assert.True(model.RefreshDatePresentation());
        Assert.Equal("30 Sep 2026", model.Backlog.Single().DateText);
    }

    [Fact]
    public void UpcomingGroupsOverdueFirstThenDatesWithSharedOrderTieBreaks()
    {
        var work = new MemoryWorkspaceWork();
        var today = new DateOnly(2026, 10, 4);
        var sameDateLater = work.CreateStandaloneTask("Same date later", "", "home", today.AddDays(2));
        var outside = work.CreateStandaloneTask("Outside", "", "home", today.AddDays(8));
        var sameDateEarlier = work.CreateStandaloneTask("Same date earlier", "", "home", today.AddDays(2));
        var tomorrow = work.CreateStandaloneTask("Tomorrow", "", "home", today.AddDays(1));
        var todayTask = work.CreateStandaloneTask("Today", "", "home", today);
        var recentOverdue = work.CreateStandaloneTask("Recent overdue", "", "home", today.AddDays(-1));
        var oldestOverdue = work.CreateStandaloneTask("Oldest overdue", "", "home", today.AddDays(-5));
        var undated = work.CreateStandaloneTask("Undated", "", "home", null);
        var completed = work.CreateStandaloneTask("Completed", "", "home", today);
        work.SetCompletion(completed.Id, new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero), today);
        var project = work.CreateProject("Target only", "", "home", today.AddDays(1));
        work.MoveTaskInSharedOrder(sameDateLater.Id, 0);
        var model = new ProjectCaptureViewModel(work,
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero)));

        Assert.Equal(["Overdue", "Today", "Tomorrow", "Tuesday, 6 October 2026"],
            model.UpcomingGroups.Select(group => group.Heading));
        Assert.Equal([oldestOverdue.Id, recentOverdue.Id], model.UpcomingGroups[0].Rows.Select(row => row.Task.Id));
        Assert.Equal([sameDateLater.Id, sameDateEarlier.Id], model.UpcomingGroups[^1].Rows.Select(row => row.Task.Id));
        Assert.Equal(6, model.UpcomingCount);
        var excludedIds = new[] { outside.Id, undated.Id, completed.Id };
        Assert.DoesNotContain(model.UpcomingGroups.SelectMany(group => group.Rows), row => excludedIds.Contains(row.Task.Id));
        Assert.DoesNotContain(model.UpcomingGroups.SelectMany(group => group.Rows), row => row.Task.Title == project.Title);
    }

    [Fact]
    public void UpcomingActionsAndClockRefreshPreserveSharedOrderAndRegroupImmediately()
    {
        var work = new MemoryWorkspaceWork();
        var due = work.CreateStandaloneTask("Due", "", "home", new DateOnly(2026, 10, 5));
        var other = work.CreateStandaloneTask("Other", "", "home", new DateOnly(2026, 10, 6));
        var enteringBoundary = work.CreateStandaloneTask("Entering boundary", "", "home", new DateOnly(2026, 10, 12));
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 10, 4, 23, 30, 0, TimeSpan.Zero));
        var model = new ProjectCaptureViewModel(work, time);
        var originalOrder = work.Read().Tasks.Select(task => (task.Id, task.SharedPosition)).ToArray();

        model.UpcomingGroups.SelectMany(group => group.Rows).Single(row => row.Task.Id == due.Id)
            .Task.ToggleTodayCommand.Execute(null);
        Assert.Equal(TodayLane.Planned, work.Read().Tasks.Single(task => task.Id == due.Id).TodayLane);
        Assert.Equal(originalOrder, work.Read().Tasks.Select(task => (task.Id, task.SharedPosition)));

        model.SelectTask(due.Id);
        model.Date = "2026-10-20";
        Assert.True(model.RunScheduledAutosave());
        Assert.DoesNotContain(model.UpcomingGroups.SelectMany(group => group.Rows), row => row.Task.Id == due.Id);
        Assert.Equal(originalOrder, work.Read().Tasks.Select(task => (task.Id, task.SharedPosition)));

        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("UTC+02-upcoming", TimeSpan.FromHours(2), "UTC+02", "UTC+02");
        time.Set(time.GetUtcNow(), plusTwo);
        Assert.True(model.RefreshDatePresentation());
        Assert.Equal(["Tomorrow", "Monday, 12 October 2026"], model.UpcomingGroups.Select(group => group.Heading));
        Assert.Equal([other.Id, enteringBoundary.Id],
            model.UpcomingGroups.SelectMany(group => group.Rows).Select(row => row.Task.Id));
        Assert.Equal(2, model.UpcomingCount);
    }

    [Fact]
    public void TodayStarDefaultsToPlannedLaneMovementPreservesOrderAndClearRemovesMembership()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var first = work.CreateTask(project.Id, "First");
        var second = work.CreateTask(project.Id, "Second");
        var shell = new ShellViewModel(work);
        var model = shell.Work!;
        var originalOrder = work.Read().Tasks.Select(task => task.Id).ToArray();

        model.Backlog.Single(task => task.Id == first.Id).ToggleTodayCommand.Execute(null);
        Assert.Equal(TodayLane.Planned, work.Read().Tasks.Single(task => task.Id == first.Id).TodayLane);
        Assert.True(model.Backlog.Single(task => task.Id == first.Id).IsToday);
        Assert.Equal($"Remove First from Today", model.Backlog.Single(task => task.Id == first.Id).TodayAccessibleName);
        Assert.Single(model.TodayPlanned);
        Assert.Equal("1", shell.PrimaryNavigation.Single(item => item.Title == "Today").CountText);

        model.TodayPlanned.Single().MoveToOtherLaneCommand.Execute(null);
        Assert.Empty(model.TodayPlanned);
        Assert.Equal(first.Id, Assert.Single(model.TodayInProgress).Task.Id);
        Assert.Equal(originalOrder, work.Read().Tasks.Select(task => task.Id));

        model.Backlog.Single(task => task.Id == second.Id).ToggleTodayCommand.Execute(null);
        model.ClearTodayCommand.Execute(null);
        Assert.True(model.HasNoTodayTasks);
        Assert.All(work.Read().Tasks, task => Assert.Null(task.TodayLane));
        Assert.Equal("0", shell.PrimaryNavigation.Single(item => item.Title == "Today").CountText);
        Assert.Equal("Cleared 2 Tasks from Today.", model.TodayAnnouncement);
    }

    [Fact]
    public void TodayTaskDraftDefaultsToPlannedAndCanSelectAnOptionalProjectBeforeCreation()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var model = new ProjectCaptureViewModel(work);

        model.NewTodayTaskCommand.Execute(null);

        Assert.True(model.ShowTaskContext);
        Assert.False(model.ShowTaskContextAction);
        Assert.Null(model.TaskContextTarget?.ProjectId);
        model.TaskContextTarget = model.TaskContextChoices.Single(choice => choice.ProjectId == project.Id);
        Assert.Null(model.Category?.Id);
        model.Title = "Plant bulbs";
        model.Description = "Near the fence";
        Assert.True(model.Save());

        var task = work.Read().Tasks.Single();
        Assert.Equal(project.Id, task.ProjectId);
        Assert.Null(task.ExplicitCategoryId);
        Assert.Equal("Near the fence", task.Description);
        Assert.Equal(TodayLane.Planned, task.TodayLane);
    }

    [Fact]
    public void TodayImmediateActionsWaitForTheCategoryDraftDecision()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var first = work.CreateTask(project.Id, "First");
        var second = work.CreateTask(project.Id, "Second");
        work.SetTaskTodayLane(first.Id, TodayLane.Planned);
        work.SetTaskTodayLane(second.Id, TodayLane.Planned);
        var model = new ProjectCaptureViewModel(work);
        var initialOrder = model.TodayPlanned.Select(row => row.Task.Id).ToArray();
        model.SelectCategory("home");
        model.Title = "Renamed home";

        model.TodayPlanned.Single(row => row.Task.Id == initialOrder[1]).MoveToTopCommand.Execute(null);

        Assert.True(model.NeedsDecision);
        Assert.Equal(initialOrder, model.TodayPlanned.Select(row => row.Task.Id));
        model.DiscardAndLeaveCommand.Execute(null);
        Assert.Equal(initialOrder.Reverse(), model.TodayPlanned.Select(row => row.Task.Id));
    }

    [Fact]
    public void TodayLaneReorderChangesOnlyVisibleRelativeSharedPositions()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var hidden = work.CreateTask(project.Id, "Hidden");
        var older = work.CreateTask(project.Id, "Older");
        var otherLane = work.CreateTask(project.Id, "Other lane");
        var newer = work.CreateTask(project.Id, "Newer");
        work.MoveTaskInSharedOrder(hidden.Id, 1);
        work.SetTaskTodayLane(older.Id, TodayLane.Planned);
        work.SetTaskTodayLane(otherLane.Id, TodayLane.InProgress);
        work.SetTaskTodayLane(newer.Id, TodayLane.Planned);
        var model = new ProjectCaptureViewModel(work);

        model.TodayPlanned.Single(row => row.Task.Id == older.Id).MoveToTopCommand.Execute(null);

        Assert.Equal([older.Id, hidden.Id, otherLane.Id, newer.Id], work.Read().Tasks.Select(task => task.Id));
        Assert.Equal([older.Id, newer.Id], model.TodayPlanned.Select(row => row.Task.Id));
        Assert.Equal("Moved Older to position 1 of 2 in Planned.", model.TodayAnnouncement);
    }

    [Fact]
    public void CompletedTodayUsesCapturedDateNewestFirstAndReopenReturnsOnlyToBacklog()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var previouslyToday = work.CreateTask(project.Id, "Previously Today");
        var neverToday = work.CreateTask(project.Id, "Never Today");
        work.SetTaskTodayLane(previouslyToday.Id, TodayLane.Planned);
        work.SetCompletion(previouslyToday.Id, new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 29));
        work.SetTaskTodayLane(previouslyToday.Id, null);
        work.SetCompletion(neverToday.Id, new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 29));
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 14, 0, 0, TimeSpan.Zero));
        var model = new ProjectCaptureViewModel(work, time);

        Assert.Equal([neverToday.Id, previouslyToday.Id], model.CompletedToday.Select(row => row.Task.Id));
        model.CompletedToday[0].Task.ToggleCompletionCommand.Execute(null);
        Assert.Contains(model.Backlog, task => task.Id == neverToday.Id);
        Assert.Null(work.Read().Tasks.Single(task => task.Id == neverToday.Id).TodayLane);

        time.Set(new DateTimeOffset(2026, 9, 30, 0, 1, 0, TimeSpan.Zero));
        Assert.True(model.RefreshDatePresentation());
        Assert.Empty(model.CompletedToday);
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo? localTimeZone = null) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;
    private TimeZoneInfo _localTimeZone = localTimeZone ?? TimeZoneInfo.Utc;
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public override TimeZoneInfo LocalTimeZone => _localTimeZone;
    public void Set(DateTimeOffset value, TimeZoneInfo? zone = null)
    {
        _utcNow = value;
        if (zone is not null) _localTimeZone = zone;
    }
}

internal sealed class MemoryWorkspaceWork : IWorkspaceWork
{
    private readonly List<WorkspaceCategory> _categories = [new("home", "Home", 0), new("work", "Work", 1)];
    private readonly List<ProjectRecord> _projects = [];
    private readonly List<TaskRecord> _tasks = [];
    private readonly List<ParticipantRecord> _participants = [];
    public bool FailWrites { get; set; }
    public int WriteCount { get; private set; }
    public WorkspaceWorkSnapshot Read() => new(_categories.OrderBy(category => category.Position).ToArray(), _projects.OrderBy(project => project.Position).ToArray(), _tasks.OrderBy(t => t.SharedPosition).ToArray(), _participants.ToArray());
    public ParticipantRecord CreateParticipant(string label)
    {
        Check();
        label = ParticipantLabel.Normalize(label);
        if (_participants.Any(item => ParticipantLabel.ComparisonKey(item.Label) == ParticipantLabel.ComparisonKey(label)))
            throw new ArgumentException("Participant label unavailable.", nameof(label));
        var participant = new ParticipantRecord($"participant-{_participants.Count}", label);
        _participants.Add(participant);
        return participant;
    }
    public ParticipantRecord RenameParticipant(string id, string label)
    {
        Check();
        label = ParticipantLabel.Normalize(label);
        if (_participants.Any(item => item.Id != id
            && ParticipantLabel.ComparisonKey(item.Label) == ParticipantLabel.ComparisonKey(label)))
            throw new ArgumentException("Participant label unavailable.", nameof(label));
        var index = _participants.FindIndex(item => item.Id == id);
        return _participants[index] = _participants[index] with { Label = label };
    }
    public void DeleteParticipant(string id)
    {
        Check();
        if (_tasks.Any(task => task.Participants.Contains(id))) throw new InvalidOperationException();
        _participants.RemoveAll(item => item.Id == id);
    }
    public WorkspaceCategory CreateCategory(string name)
    {
        Check();
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name) || _categories.Any(category => string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Category name unavailable.", nameof(name));
        var category = new WorkspaceCategory($"category-{_categories.Count}", name, _categories.Count);
        _categories.Add(category);
        return category;
    }
    public WorkspaceCategory RenameCategory(string id, string name)
    {
        Check();
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name) || _categories.Any(category => category.Id != id && string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Category name unavailable.", nameof(name));
        var index = _categories.FindIndex(category => category.Id == id);
        return _categories[index] = _categories[index] with { Name = name };
    }
    public void DeleteCategory(string id, string? replacementCategoryId = null)
    {
        Check();
        if (_categories.Count == 1) throw new InvalidOperationException();
        var referenced = _projects.Any(project => project.CategoryId == id) || _tasks.Any(task => task.ExplicitCategoryId == id);
        if (referenced && (replacementCategoryId is null || replacementCategoryId == id))
            throw new ArgumentException("Replacement required.", nameof(replacementCategoryId));
        if (replacementCategoryId is not null && _categories.All(category => category.Id != replacementCategoryId))
            throw new ArgumentException("Replacement missing.", nameof(replacementCategoryId));
        for (var index = 0; index < _projects.Count; index++)
            if (_projects[index].CategoryId == id) _projects[index] = _projects[index] with { CategoryId = replacementCategoryId! };
        for (var index = 0; index < _tasks.Count; index++)
            if (_tasks[index].ExplicitCategoryId == id) _tasks[index] = _tasks[index] with { ExplicitCategoryId = replacementCategoryId };
        _categories.RemoveAll(category => category.Id == id);
        RewriteCategoryPositions();
    }
    public CategoryOrderChange MoveCategory(string id, int targetPosition)
    {
        Check();
        if ((uint)targetPosition >= (uint)_categories.Count) throw new ArgumentOutOfRangeException(nameof(targetPosition));
        var category = _categories.Single(item => item.Id == id);
        _categories.Remove(category);
        _categories.Insert(targetPosition, category);
        RewriteCategoryPositions();
        return new(id, targetPosition + 1, _categories.Count);
    }
    public ProjectRecord CreateProject(string title, string description, string categoryId, DateOnly? targetDate)
    {
        Check();
        var project = new ProjectRecord($"project-{_projects.Count}", title.Trim(), description, categoryId, targetDate, _projects.Count);
        _projects.Add(project); return project;
    }
    public TaskRecord CreateTask(string projectId, string title)
    {
        Check();
        ShiftForNewTask();
        var task = new TaskRecord($"task-{_tasks.Count}", projectId, title.Trim(), "", null, null, 0, _tasks.Count(t => t.ProjectId == projectId));
        _tasks.Add(task); return task;
    }
    public TaskRecord CreateTaskDraft(string? projectId, string title, string description, string? categoryId,
        DateOnly? dueDate, ParticipantDraftChange? participantChange = null, TodayLane? todayLane = null)
    {
        Check();
        if (projectId is null && categoryId is null) throw new ArgumentException("Category required.", nameof(categoryId));
        if (projectId is not null && _projects.All(project => project.Id != projectId)) throw new ArgumentException("Project missing.", nameof(projectId));
        var taskBackup = _tasks.ToList();
        var participantBackup = _participants.ToList();
        ShiftForNewTask();
        try
        {
            var associations = ApplyParticipantChanges(participantChange ?? new([], []));
            var task = new TaskRecord($"task-{_tasks.Count}", projectId, title.Trim(), description, categoryId, dueDate, 0,
                projectId is null ? null : _tasks.Count(item => item.ProjectId == projectId),
                ParticipantIds: associations, TodayLane: todayLane);
            _tasks.Add(task);
            return task;
        }
        catch
        {
            _tasks.Clear();
            _tasks.AddRange(taskBackup);
            _participants.Clear();
            _participants.AddRange(participantBackup);
            throw;
        }
    }
    public TaskRecord CreateStandaloneTask(string title, string description, string categoryId, DateOnly? dueDate,
        ParticipantDraftChange? participantChange = null)
    {
        Check();
        if (string.IsNullOrWhiteSpace(categoryId)) throw new ArgumentException("Category required.", nameof(categoryId));
        var taskBackup = _tasks.ToList();
        var participantBackup = _participants.ToList();
        ShiftForNewTask();
        try
        {
            var taskId = $"task-{_tasks.Count}";
            var associations = ApplyParticipantChanges(participantChange ?? new([], []));
            var task = new TaskRecord(taskId, null, title.Trim(), description, categoryId, dueDate, 0, null, ParticipantIds: associations);
            _tasks.Add(task); return task;
        }
        catch
        {
            _tasks.Clear();
            _tasks.AddRange(taskBackup);
            _participants.Clear();
            _participants.AddRange(participantBackup);
            throw;
        }
    }
    public ProjectRecord UpdateProject(string id, string title, string description, string categoryId, DateOnly? targetDate)
    {
        Check();
        int index = _projects.FindIndex(p => p.Id == id);
        return _projects[index] = _projects[index] with { Title = title.Trim(), Description = description, CategoryId = categoryId, TargetDate = targetDate };
    }
    public TaskRecord UpdateTask(string id, string title, string description, string? explicitCategoryId, DateOnly? dueDate,
        ParticipantDraftChange? participantChange = null)
    {
        Check();
        int index = _tasks.FindIndex(t => t.Id == id);
        if (_tasks[index].ProjectId is null && explicitCategoryId is null) throw new ArgumentException("Category required.", nameof(explicitCategoryId));
        var taskBackup = _tasks.ToList();
        var participantBackup = _participants.ToList();
        try
        {
            var associations = participantChange is null
                ? _tasks[index].Participants.ToList()
                : ApplyParticipantChanges(participantChange);
            return _tasks[index] = _tasks[index] with { Title = title.Trim(), Description = description, ExplicitCategoryId = explicitCategoryId, DueDate = dueDate, ParticipantIds = associations };
        }
        catch
        {
            _tasks.Clear();
            _tasks.AddRange(taskBackup);
            _participants.Clear();
            _participants.AddRange(participantBackup);
            throw;
        }
    }
    public TaskRecord CompleteTask(string id)
    {
        Check();
        int index = _tasks.FindIndex(task => task.Id == id);
        return _tasks[index] = _tasks[index] with
        {
            CompletedAt = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero),
            CompletionDate = new DateOnly(2026, 9, 29),
            TodayLane = null,
        };
    }
    public TaskRecord ReopenTask(string id)
    {
        Check();
        int index = _tasks.FindIndex(task => task.Id == id);
        return _tasks[index] = _tasks[index] with { CompletedAt = null, CompletionDate = null };
    }
    public TaskRecord SetCompletion(string id, DateTimeOffset instant, DateOnly date)
    {
        int index = _tasks.FindIndex(task => task.Id == id);
        return _tasks[index] = _tasks[index] with { CompletedAt = instant, CompletionDate = date };
    }
    public TaskRecord SetTaskTodayLane(string id, TodayLane? lane)
    {
        Check();
        var index = _tasks.FindIndex(task => task.Id == id);
        if (_tasks[index].IsComplete && lane is not null) throw new InvalidOperationException();
        return _tasks[index] = _tasks[index] with { TodayLane = lane };
    }
    public int ClearToday()
    {
        Check();
        var cleared = 0;
        for (var index = 0; index < _tasks.Count; index++)
            if (_tasks[index].TodayLane is not null)
            {
                _tasks[index] = _tasks[index] with { TodayLane = null };
                cleared++;
            }
        return cleared;
    }
    public TodayLaneOrderChange MoveTaskInTodayLane(string id, int targetPosition)
    {
        Check();
        var ordered = _tasks.OrderBy(task => task.SharedPosition).ToList();
        var task = ordered.Single(item => item.Id == id);
        if (task.IsComplete || task.TodayLane is null) throw new ArgumentException("Task is not in Today.", nameof(id));
        var visible = ordered.Where(item => !item.IsComplete && item.TodayLane == task.TodayLane).ToList();
        if ((uint)targetPosition >= (uint)visible.Count) throw new ArgumentOutOfRangeException(nameof(targetPosition));
        visible.Remove(task);
        visible.Insert(targetPosition, task);
        var visibleIds = visible.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var visibleIndex = 0;
        ordered = ordered.Select(item => visibleIds.Contains(item.Id) ? visible[visibleIndex++] : item).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var storedIndex = _tasks.FindIndex(candidate => candidate.Id == ordered[index].Id);
            _tasks[storedIndex] = _tasks[storedIndex] with { SharedPosition = index };
        }
        return new(id, task.TodayLane.Value, targetPosition + 1, visible.Count);
    }
    public SharedTaskOrderChange MoveTaskInSharedOrder(string id, int targetPosition)
    {
        Check();
        var ordered = _tasks.OrderBy(task => task.SharedPosition).ToList();
        var visible = ordered.Where(task => !task.IsComplete).ToList();
        if ((uint)targetPosition >= (uint)visible.Count) throw new ArgumentOutOfRangeException(nameof(targetPosition));
        var task = visible.Single(task => task.Id == id);
        visible.Remove(task);
        visible.Insert(targetPosition, task);
        var visibleIndex = 0;
        ordered = ordered.Select(item => item.IsComplete ? item : visible[visibleIndex++]).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var storedIndex = _tasks.FindIndex(candidate => candidate.Id == ordered[index].Id);
            _tasks[storedIndex] = _tasks[storedIndex] with { SharedPosition = index };
        }
        return new(id, targetPosition + 1, visible.Count);
    }
    public ProjectOrderChange MoveProject(string id, int targetPosition)
    {
        Check();
        if ((uint)targetPosition >= (uint)_projects.Count) throw new ArgumentOutOfRangeException(nameof(targetPosition));
        var ordered = _projects.OrderBy(project => project.Position).ToList();
        var project = ordered.Single(project => project.Id == id);
        ordered.Remove(project);
        ordered.Insert(targetPosition, project);
        for (var index = 0; index < ordered.Count; index++)
        {
            var storedIndex = _projects.FindIndex(candidate => candidate.Id == ordered[index].Id);
            _projects[storedIndex] = _projects[storedIndex] with { Position = index };
        }
        return new(id, targetPosition + 1, ordered.Count);
    }
    public ProjectTaskOrderChange MoveTaskInProject(string projectId, string taskId, int targetPosition)
    {
        Check();
        var ordered = _tasks.Where(task => task.ProjectId == projectId).OrderBy(task => task.ProjectPosition).ToList();
        if ((uint)targetPosition >= (uint)ordered.Count) throw new ArgumentOutOfRangeException(nameof(targetPosition));
        var task = ordered.Single(task => task.Id == taskId);
        ordered.Remove(task);
        ordered.Insert(targetPosition, task);
        for (var index = 0; index < ordered.Count; index++)
        {
            var storedIndex = _tasks.FindIndex(candidate => candidate.Id == ordered[index].Id);
            _tasks[storedIndex] = _tasks[storedIndex] with { ProjectPosition = index };
        }
        return new(projectId, taskId, targetPosition + 1, ordered.Count);
    }
    public TaskRecord DetachTask(string id)
    {
        Check();
        var index = _tasks.FindIndex(task => task.Id == id);
        var task = _tasks[index];
        if (task.ProjectId is null) throw new ArgumentException("Task is standalone.", nameof(id));
        var effectiveCategoryId = EffectiveCategoryId(task);
        var sourceProjectId = task.ProjectId;
        _tasks[index] = task with { ProjectId = null, ProjectPosition = null, ExplicitCategoryId = effectiveCategoryId };
        RewriteProjectPositions(sourceProjectId);
        return _tasks[index];
    }
    public TaskRecord AttachTask(string id, string projectId, TaskAttachmentCategoryChoice? categoryChoice = null)
    {
        Check();
        var index = _tasks.FindIndex(task => task.Id == id);
        var task = _tasks[index];
        var effectiveCategoryId = EffectiveCategoryId(task);
        var targetCategoryId = _projects.Single(project => project.Id == projectId).CategoryId;
        if (effectiveCategoryId != targetCategoryId && categoryChoice is null)
            throw new ArgumentException("Category choice required.", nameof(categoryChoice));
        var sourceProjectId = task.ProjectId;
        _tasks[index] = task with
        {
            ProjectId = projectId,
            ProjectPosition = _tasks.Count(candidate => candidate.ProjectId == projectId),
            ExplicitCategoryId = effectiveCategoryId == targetCategoryId || categoryChoice == TaskAttachmentCategoryChoice.AdoptProjectCategory
                ? null
                : effectiveCategoryId,
        };
        if (sourceProjectId is not null && sourceProjectId != projectId) RewriteProjectPositions(sourceProjectId);
        return _tasks[index];
    }
    private void ShiftForNewTask()
    {
        for (var index = 0; index < _tasks.Count; index++) _tasks[index] = _tasks[index] with { SharedPosition = _tasks[index].SharedPosition + 1 };
    }
    private List<string> ApplyParticipantChanges(ParticipantDraftChange change)
    {
        var associations = change.ParticipantIds.Distinct(StringComparer.Ordinal).ToList();
        foreach (var label in change.NewParticipantLabels) associations.Add(CreateParticipant(label).Id);
        return associations;
    }
    private string EffectiveCategoryId(TaskRecord task) => task.ExplicitCategoryId
        ?? _projects.Single(project => project.Id == task.ProjectId).CategoryId;
    private void RewriteProjectPositions(string projectId)
    {
        var ordered = _tasks.Where(task => task.ProjectId == projectId).OrderBy(task => task.ProjectPosition).ToArray();
        for (var position = 0; position < ordered.Length; position++)
        {
            var index = _tasks.FindIndex(task => task.Id == ordered[position].Id);
            _tasks[index] = _tasks[index] with { ProjectPosition = position };
        }
    }
    private void RewriteCategoryPositions()
    {
        for (var position = 0; position < _categories.Count; position++)
            _categories[position] = _categories[position] with { Position = position };
    }
    private void Check()
    {
        if (FailWrites) throw new WorkspaceWorkException();
        WriteCount++;
    }
}
