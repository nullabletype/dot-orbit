using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class ProjectCaptureViewModelTests
{
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
    public void DraftComparisonCannotCollideAcrossFieldBoundaries()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("a\u001fb", "c", "home", null);
        var model = new ProjectCaptureViewModel(work);
        model.SelectProject(project.Id);
        model.Title = "a";
        model.Description = "b\u001fc";
        Assert.True(model.IsDirty);
        model.Navigate(() => { });
        Assert.True(model.NeedsDecision);
    }

    [Fact]
    public void CreationIsTransientAndCancelLeavesNoProject()
    {
        var work = new MemoryWorkspaceWork();
        var model = new ProjectCaptureViewModel(work);
        model.NewProjectCommand.Execute(null);
        model.Title = "Draft";
        Assert.Empty(work.Read().Projects);
        Assert.Empty(model.Projects);
        model.CancelCommand.Execute(null);
        Assert.False(model.HasInspector);
        Assert.Empty(work.Read().Projects);
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
    public void QuickAddPreservesDirtyDraftAndCreatesInheritedTasksInIndependentOrders()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var model = new ProjectCaptureViewModel(work);
        model.SelectProject(project.Id);
        model.Title = "Unsaved garden";
        Assert.True(model.QuickAdd(project.Id, " First "));
        Assert.True(model.QuickAdd(project.Id, "Second"));
        Assert.True(model.IsDirty);
        Assert.Equal("Unsaved garden", model.Title);
        Assert.Equal("Garden", Assert.Single(work.Read().Projects).Title);
        Assert.Equal(["First", "Second"], Assert.Single(model.Projects).Tasks.Select(t => t.Title));
        Assert.Equal(["Second", "First"], model.Backlog.Select(t => t.Title));
        Assert.All(work.Read().Tasks, t => { Assert.Null(t.ExplicitCategoryId); Assert.Null(t.DueDate); Assert.Empty(t.Description); });
        Assert.False(model.QuickAdd(project.Id, " \t "));
        Assert.Equal(2, work.Read().Tasks.Count);
    }

    [Fact]
    public void EditingUsesDraftUntilSaveAndCancelDiscardsAllFields()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "Original", "home", null);
        var model = new ProjectCaptureViewModel(work);
        model.SelectProject(project.Id);
        model.Title = "New garden";
        model.Description = "**Plan**";
        model.Date = "2026-10-12";
        model.Category = model.Categories.Single(c => c.Id == "work");
        Assert.Equal(project, Assert.Single(work.Read().Projects));
        model.Cancel();
        model.SelectProject(project.Id);
        Assert.Equal("Original", model.Description);
        Assert.Empty(model.Date);
        model.Title = "Saved garden";
        model.Date = "2026-02-30";
        Assert.False(model.Save());
        Assert.Equal(project, Assert.Single(work.Read().Projects));
        model.Date = "2026-10-12";
        model.Description = "**Plan**";
        model.Category = model.Categories.Single(c => c.Id == "work");
        Assert.True(model.Save());
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

    [Theory]
    [InlineData("save", "Edited", true)]
    [InlineData("discard", "Original", true)]
    [InlineData("stay", "Original", false)]
    public void NavigationRequiresExplicitDraftResolution(string choice, string savedTitle, bool leaves)
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Original", "", "home", null);
        var shell = new ShellViewModel(work);
        shell.Work!.SelectProject(project.Id);
        shell.Work.Title = "Edited";
        shell.PrimaryNavigation.Single(n => n.Title == "Projects").SelectCommand.Execute(null);
        Assert.Equal("Today", shell.ViewTitle);
        Assert.True(shell.Work.NeedsDecision);
        var command = choice switch { "save" => shell.Work.SaveAndLeaveCommand, "discard" => shell.Work.DiscardAndLeaveCommand, _ => shell.Work.StayCommand };
        command.Execute(null);
        Assert.False(shell.Work.NeedsDecision);
        Assert.Equal(leaves ? "Projects" : "Today", shell.ViewTitle);
        Assert.Equal(savedTitle, Assert.Single(work.Read().Projects).Title);
        if (!leaves) { Assert.True(shell.Work.IsDirty); Assert.Equal("Edited", shell.Work.Title); }
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
        model.Navigate(() => left = true);
        work.FailWrites = true;
        model.SaveAndLeaveCommand.Execute(null);
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
    public void ReopeningDirtyTaskFromCompletedRequiresDraftResolution()
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
        Assert.True(model.NeedsDecision);
        Assert.True(work.Read().Tasks.Single().IsComplete);
        Assert.Equal("Unsaved receipt", model.Title);
        model.StayCommand.Execute(null);
        Assert.True(model.IsDirty);

        model.Completed.Single().ToggleCompletionCommand.Execute(null);
        model.DiscardAndLeaveCommand.Execute(null);
        Assert.False(work.Read().Tasks.Single().IsComplete);
        Assert.Equal("Filed receipt", work.Read().Tasks.Single().Title);
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
    public void CompletingDirtyBacklogTaskRequiresResolutionAndMovesFocusToNextRow()
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
        Assert.True(model.NeedsDecision);
        Assert.False(work.Read().Tasks.Single(task => task.Id == second.Id).IsComplete);
        model.StayCommand.Execute(null);
        Assert.True(model.IsDirty);

        model.Backlog.Single(row => row.Id == second.Id).ToggleCompletionCommand.Execute(null);
        model.DiscardAndLeaveCommand.Execute(null);
        Assert.True(work.Read().Tasks.Single(task => task.Id == second.Id).IsComplete);
        Assert.Equal("First", work.Read().Tasks.Single(task => task.Id == first.Id).Title);
        Assert.Equal(model.Backlog.Single().CompletionAutomationId, model.CompletionFocusAutomationId);
    }

    [Fact]
    public void CompletionAndIndependentReordersPreserveDirtyInspectorDraft()
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

        Assert.True(model.IsDirty);
        Assert.Equal("Unsaved title", model.Title);
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
    private readonly List<ProjectRecord> _projects = [];
    private readonly List<TaskRecord> _tasks = [];
    public bool FailWrites { get; set; }
    public WorkspaceWorkSnapshot Read() => new([new("home", "Home", 0), new("work", "Work", 1)], _projects.OrderBy(project => project.Position).ToArray(), _tasks.OrderBy(t => t.SharedPosition).ToArray());
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
    public TaskRecord CreateStandaloneTask(string title, string description, string categoryId, DateOnly? dueDate)
    {
        Check();
        if (string.IsNullOrWhiteSpace(categoryId)) throw new ArgumentException("Category required.", nameof(categoryId));
        ShiftForNewTask();
        var task = new TaskRecord($"task-{_tasks.Count}", null, title.Trim(), description, categoryId, dueDate, 0, null);
        _tasks.Add(task); return task;
    }
    public ProjectRecord UpdateProject(string id, string title, string description, string categoryId, DateOnly? targetDate)
    {
        Check();
        int index = _projects.FindIndex(p => p.Id == id);
        return _projects[index] = _projects[index] with { Title = title.Trim(), Description = description, CategoryId = categoryId, TargetDate = targetDate };
    }
    public TaskRecord UpdateTask(string id, string title, string description, string? explicitCategoryId, DateOnly? dueDate)
    {
        Check();
        int index = _tasks.FindIndex(t => t.Id == id);
        if (_tasks[index].ProjectId is null && explicitCategoryId is null) throw new ArgumentException("Category required.", nameof(explicitCategoryId));
        return _tasks[index] = _tasks[index] with { Title = title.Trim(), Description = description, ExplicitCategoryId = explicitCategoryId, DueDate = dueDate };
    }
    public TaskRecord CompleteTask(string id)
    {
        Check();
        int index = _tasks.FindIndex(task => task.Id == id);
        return _tasks[index] = _tasks[index] with
        {
            CompletedAt = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero),
            CompletionDate = new DateOnly(2026, 9, 29),
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
    private void ShiftForNewTask()
    {
        for (var index = 0; index < _tasks.Count; index++) _tasks[index] = _tasks[index] with { SharedPosition = _tasks[index].SharedPosition + 1 };
    }
    private void Check() { if (FailWrites) throw new WorkspaceWorkException(); }
}
