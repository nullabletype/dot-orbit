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
        Assert.All(work.Read().Tasks, t => { Assert.Null(t.CategoryOverrideId); Assert.Null(t.DueDate); Assert.Empty(t.Description); });
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
        Assert.Equal("work", Assert.Single(work.Read().Tasks).CategoryOverrideId);
        Assert.Equal(new DateOnly(2026, 11, 1), Assert.Single(work.Read().Tasks).DueDate);
        Assert.Equal("Dig carefully", Assert.Single(work.Read().Tasks).Description);
        model.Category = model.Categories.Single(c => c.Id is null);
        Assert.True(model.Save());
        Assert.Null(Assert.Single(work.Read().Tasks).CategoryOverrideId);
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
        var task = new TaskRecord($"task-{_tasks.Count}", projectId, title.Trim(), "", null, null, -_tasks.Count, _tasks.Count(t => t.ProjectId == projectId));
        _tasks.Add(task); return task;
    }
    public ProjectRecord UpdateProject(string id, string title, string description, string categoryId, DateOnly? targetDate)
    {
        Check();
        int index = _projects.FindIndex(p => p.Id == id);
        return _projects[index] = _projects[index] with { Title = title.Trim(), Description = description, CategoryId = categoryId, TargetDate = targetDate };
    }
    public TaskRecord UpdateTask(string id, string title, string description, string? categoryOverrideId, DateOnly? dueDate)
    {
        Check();
        int index = _tasks.FindIndex(t => t.Id == id);
        return _tasks[index] = _tasks[index] with { Title = title.Trim(), Description = description, CategoryOverrideId = categoryOverrideId, DueDate = dueDate };
    }
    private void Check() { if (FailWrites) throw new WorkspaceWorkException(); }
}
