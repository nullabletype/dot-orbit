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
}

internal sealed class MemoryWorkspaceWork : IWorkspaceWork
{
    private readonly List<ProjectRecord> _projects = [];
    private readonly List<TaskRecord> _tasks = [];
    public bool FailWrites { get; set; }
    public WorkspaceWorkSnapshot Read() => new([new("home", "Home", 0), new("work", "Work", 1)], _projects.ToArray(), _tasks.OrderBy(t => t.SharedPosition).ToArray());
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
    public SharedTaskOrderChange MoveTaskInSharedOrder(string id, int targetPosition)
    {
        Check();
        var ordered = _tasks.OrderBy(task => task.SharedPosition).ToList();
        if ((uint)targetPosition >= (uint)ordered.Count) throw new ArgumentOutOfRangeException(nameof(targetPosition));
        var task = ordered.Single(task => task.Id == id);
        ordered.Remove(task);
        ordered.Insert(targetPosition, task);
        for (var index = 0; index < ordered.Count; index++)
        {
            var storedIndex = _tasks.FindIndex(candidate => candidate.Id == ordered[index].Id);
            _tasks[storedIndex] = _tasks[storedIndex] with { SharedPosition = index };
        }
        return new(id, targetPosition + 1, ordered.Count);
    }
    private void ShiftForNewTask()
    {
        for (var index = 0; index < _tasks.Count; index++) _tasks[index] = _tasks[index] with { SharedPosition = _tasks[index].SharedPosition + 1 };
    }
    private void Check() { if (FailWrites) throw new WorkspaceWorkException(); }
}
