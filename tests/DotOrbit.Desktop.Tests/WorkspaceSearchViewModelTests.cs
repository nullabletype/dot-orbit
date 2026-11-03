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
