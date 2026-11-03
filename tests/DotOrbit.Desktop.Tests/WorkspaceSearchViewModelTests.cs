using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class WorkspaceSearchViewModelTests
{
    [Fact]
    public async Task SearchRowsUseCommittedProjectionsAndMoveWhenCompletionChanges()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Orchard", "", "home", null);
        var task = work.CreateTask(project.Id, "Orchard ledger");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Search").SelectCommand.Execute(null);
        var model = shell.Work!;
        var search = model.Search;
        search.Text = "orch";
        Assert.Same(model.Projects.Single(), Assert.Single(search.Projects));
        var row = Assert.Single(search.Backlog);
        Assert.Same(model.Backlog.Single(), row);
        Assert.Equal("2 results.", search.Status);
        row.ToggleCompletionCommand.Execute(null);
        await model.WaitForWorkspaceActionsAsync();
        Assert.Empty(search.Backlog);
        var completed = Assert.Single(search.Completed);
        Assert.Equal(task.Id, completed.Task.Id);
        completed.Task.SelectCommand.Execute(null);
        Assert.True(shell.ShowCompleted);
        Assert.True(model.HasInspector);
        Assert.Equal(task.Title, model.Title);
        Assert.Equal(row.SelectionAutomationId, shell.MainViewTargetAutomationId);
    }

    [Fact]
    public void InvalidDraftRequiresDecisionBeforeSearchActivation()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Orchard", "", "home", null);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Search").SelectCommand.Execute(null);
        var model = shell.Work!;
        model.Search.Text = "orch";
        model.NewProjectCommand.Execute(null);
        model.Description = "keep this draft";
        Assert.Single(model.Search.Projects).SelectCommand.Execute(null);
        Assert.True(model.NeedsDecision);
        Assert.True(shell.ShowSearch);
        model.StayCommand.Execute(null);
        Assert.True(shell.ShowSearch);
        Assert.Equal("keep this draft", model.Description);
        Assert.Single(model.Search.Projects).SelectCommand.Execute(null);
        model.DiscardAndLeaveCommand.Execute(null);
        Assert.True(shell.ShowProjects);
        Assert.Equal(project.Title, model.Title);
        Assert.Equal($"project-selection-{project.Id}", shell.MainViewTargetAutomationId);
    }

    [Fact]
    public void AggregateChildrenOpenAtReadOnlyCanonicalTargets()
    {
        var work = new MemoryWorkspaceWork();
        var archived = work.CreateProject("Archive owner", "", "home", null);
        var archivedChild = work.CreateTask(archived.Id, "History child");
        work.ArchiveProject(archived.Id);
        var binned = work.CreateProject("Bin owner", "", "home", null);
        var binnedChild = work.CreateTask(binned.Id, "Bin child");
        work.MoveProjectToBin(binned.Id);
        var shell = new ShellViewModel(work);
        var navigation = shell.PrimaryNavigation.Single(item => item.Title == "Search");
        navigation.SelectCommand.Execute(null);
        var model = shell.Work!;
        model.Search.Text = "child";
        var archiveRow = Assert.Single(model.Search.Archive);
        Assert.False(archiveRow.CanRestoreTask);
        archiveRow.Task!.SelectCommand.Execute(null);
        Assert.True(shell.ShowArchive);
        Assert.False(model.HasInspector);
        Assert.Contains(model.ArchiveGroups.SelectMany(group => group.Rows), row => row.Task?.Id == archivedChild.Id);
        Assert.Equal($"task-selection-{archivedChild.Id}", shell.MainViewTargetAutomationId);

        navigation.SelectCommand.Execute(null);
        Assert.DoesNotContain(model.ArchiveGroups.SelectMany(group => group.Rows), row => row.Task?.Id == archivedChild.Id);
        var binRow = Assert.Single(model.Search.Bin);
        Assert.False(binRow.CanRestore);
        binRow.SelectCommand.Execute(null);
        Assert.True(shell.ShowBin);
        Assert.False(model.HasInspector);
        Assert.Equal(binnedChild.Id, Assert.Single(model.RevealedBinTask).Task!.Id);
        Assert.Equal(binRow.SelectionAutomationId, shell.MainViewTargetAutomationId);
        Assert.Single(model.Bin);
        navigation.SelectCommand.Execute(null);
        Assert.Empty(model.RevealedBinTask);
    }

    [Fact]
    public void IndividuallyArchivedTaskKeepsItsDateInspectorAndRestoreUnderAnArchivedParent()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Parent", "", "home", null);
        var task = work.CreateTask(project.Id, "Child");
        work.CompleteTask(task.Id);
        var individual = work.SetArchive(task.Id, new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero), new(2026, 9, 27));
        var parent = work.ArchiveProject(project.Id);
        var shell = new ShellViewModel(work);
        var searchNav = shell.PrimaryNavigation.Single(item => item.Title == "Search");
        var archiveNav = shell.PrimaryNavigation.Single(item => item.Title == "Archive");
        archiveNav.SelectCommand.Execute(null);
        var model = shell.Work!;
        model.ArchiveSearchText = "child";
        Assert.Single(model.ArchiveSearchResults).OpenCommand.Execute(null);
        Assert.Equal(task.Title, model.Title);
        Assert.True(model.HasInspector);

        searchNav.SelectCommand.Execute(null);
        model.Search.Text = "child";
        var match = Assert.Single(model.Search.Archive);
        Assert.Equal(individual.ArchiveDate, match.ArchiveDate);
        Assert.True(match.CanRestoreTask);
        match.Task!.SelectCommand.Execute(null);
        Assert.True(shell.ShowArchive);
        Assert.True(model.HasInspector);
        Assert.Equal(task.Title, model.Title);
        model.ArchiveSearchText = "child";
        Assert.Single(model.ArchiveSearchResults).OpenCommand.Execute(null);
        Assert.True(model.HasInspector);

        searchNav.SelectCommand.Execute(null);
        Assert.Single(model.Search.Archive).Task!.RestoreCommand.Execute(null);
        var remaining = Assert.Single(model.Search.Archive);
        Assert.False(remaining.CanRestoreTask);
        Assert.Equal(parent.ArchiveDate, remaining.ArchiveDate);
        Assert.False(work.Read().Tasks.Single().IsArchived);
        Assert.Empty(model.Completed);
    }

    [Fact]
    public async Task HeldReadDiscardsOlderResultsAndCoalescesInputToTheLatestQuery()
    {
        var work = new MemoryWorkspaceWork();
        work.CreateProject("First record", "", "home", null);
        var latest = work.CreateProject("Last record", "", "home", null);
        var executor = new CapturedActionExecutor(work);
        var model = new ProjectCaptureViewModel(work, null, new ControlledInspectorSaveWriter(work), executor);
        var search = model.Search;
        search.SetActive(true);
        search.Text = "First";
        await executor.ResultCaptured.WaitAsync(TestContext.Current.CancellationToken);
        search.Text = "Intermediate";
        search.Text = "Last";
        Assert.True(search.IsLoading);
        Assert.Equal("Searching the workspace…", search.Status);
        Assert.Empty(search.Projects);
        Assert.Single(executor.Requests);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        search.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(search.IsLoading) && !search.IsLoading) settled.TrySetResult();
        };
        executor.ReleaseResult();
        await settled.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(search.IsLoading);
        Assert.Equal("1 result.", search.Status);
        Assert.Equal(["First", "Last"], executor.Requests.Select(request => request.SearchQuery));
        Assert.Equal(latest.Id, Assert.Single(search.Projects).Id);
    }

    [Fact]
    public async Task ReopeningAResultReturnsItToBacklogWithoutAddingTodayMembership()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Project", "", "home", null);
        var task = work.CreateTask(project.Id, "Task");
        work.CompleteTask(task.Id);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Search").SelectCommand.Execute(null);
        var model = shell.Work!;
        model.Search.Text = "Task";
        Assert.Single(model.Search.Completed).Task.ToggleCompletionCommand.Execute(null);
        await model.WaitForWorkspaceActionsAsync();
        Assert.Empty(model.Search.Completed);
        Assert.Single(model.Search.Backlog).SelectCommand.Execute(null);
        Assert.True(shell.ShowBacklog);
        Assert.Equal($"task-selection-{task.Id}", shell.MainViewTargetAutomationId);
        Assert.Equal(task.Title, model.Title);
        Assert.Null(work.Read().Tasks.Single().TodayLane);
    }

    [Fact]
    public void ArchiveAndRestoreActionsRetainIdentityAsTheRowMovesBetweenSearchGroups()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Orchard", "", "home", null);
        var task = work.CreateTask(project.Id, "Orchard task");
        work.CompleteTask(task.Id);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Search").SelectCommand.Execute(null);
        var model = shell.Work!;
        model.Search.Text = "orch";
        Assert.Single(model.Search.Completed).Task.ArchiveCommand.Execute(null);
        model.ConfirmArchiveTaskCommand.Execute(null);
        var archivedTask = Assert.Single(model.Search.Archive).Task!;
        Assert.Equal(archivedTask.RestoreAutomationId, model.ArchiveFocusAutomationId);
        archivedTask.RestoreCommand.Execute(null);
        Assert.Empty(model.Search.Archive);
        Assert.Equal(Assert.Single(model.Search.Completed).Task.SelectionAutomationId, model.ArchiveFocusAutomationId);
        Assert.Single(model.Search.Projects).ArchiveCommand.Execute(null);
        var archivedProject = model.Search.Archive.Single(row => row.Project?.Id == project.Id).Project!;
        Assert.Equal(archivedProject.RestoreAutomationId, model.ArchiveFocusAutomationId);
        archivedProject.RestoreCommand.Execute(null);
        Assert.Equal(Assert.Single(model.Search.Projects).SelectionAutomationId, model.ArchiveFocusAutomationId);
        Assert.True(shell.ShowSearch);
    }

    [Fact]
    public void SearchStatesClearOldRowsAndUseFixedFailureText()
    {
        var work = new MemoryWorkspaceWork();
        work.CreateProject("Orchard", "", "home", null);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Search").SelectCommand.Execute(null);
        var search = shell.Work!.Search;
        Assert.False(search.ClearCommand.CanExecute(null));
        search.Text = "orch";
        Assert.Equal("1 result.", search.Status);
        work.FailWorkspaceSearch = true;
        search.Text = "sensitive query";
        Assert.Equal("Workspace search is unavailable. Try again.", search.Status);
        Assert.Empty(search.Projects);
        work.FailWorkspaceSearch = false;
        search.Text = "absent";
        Assert.Equal("No matching Projects or Tasks.", search.Status);
        search.ClearCommand.Execute(null);
        Assert.Equal(string.Empty, search.Text);
        Assert.False(search.ClearCommand.CanExecute(null));
        Assert.Equal("Enter a word or prefix to find Projects and Tasks.", search.Status);
    }
}
