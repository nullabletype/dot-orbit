using DotOrbit.Desktop.ViewModels;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class ShellViewModelTests
{
    [Fact]
    public void StartsOnTodayWithBinSeparatedFromPrimaryNavigation()
    {
        var shell = new ShellViewModel();

        Assert.Equal("Today", shell.SelectedItem.Title);
        Assert.Equal(
            ["Today", "Upcoming", "Backlog", "Projects", "Categories", "Completed", "Archive"],
            shell.PrimaryNavigation.Select(item => item.Title));
        Assert.Equal("Bin", shell.BinNavigation.Title);
        Assert.Equal("Settings", shell.SettingsNavigation.Title);
        Assert.DoesNotContain(shell.BinNavigation, shell.PrimaryNavigation);
        Assert.DoesNotContain(shell.SettingsNavigation, shell.PrimaryNavigation);
        Assert.All(shell.PrimaryNavigation, item => Assert.Equal("0", item.CountText));
        Assert.Null(shell.BinNavigation.CountText);
        Assert.Null(shell.SettingsNavigation.CountText);
    }

    [Fact]
    public void EveryDestinationProvidesAUsableEmptyState()
    {
        var shell = new ShellViewModel();
        var destinations = shell.PrimaryNavigation.Append(shell.BinNavigation).Append(shell.SettingsNavigation);

        Assert.All(destinations, destination =>
        {
            Assert.False(string.IsNullOrWhiteSpace(destination.EmptyStateHeading));
            Assert.False(string.IsNullOrWhiteSpace(destination.EmptyStateBody));
            Assert.NotEqual(destination.EmptyStateHeading, destination.EmptyStateBody);
        });
    }

    [Fact]
    public void UpcomingCopyMatchesTheAcceptedDateWindow()
    {
        var shell = new ShellViewModel();
        var upcoming = Assert.Single(shell.PrimaryNavigation, item => item.Title == "Upcoming");

        Assert.Contains("overdue", upcoming.ViewSubtitle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("same weekday next week", upcoming.ViewSubtitle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("overdue", upcoming.EmptyStateBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("same weekday next week", upcoming.EmptyStateBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectingADestinationUpdatesSelectionAndVisibleCopy()
    {
        var shell = new ShellViewModel();
        var archive = Assert.Single(shell.PrimaryNavigation, item => item.Title == "Archive");

        archive.SelectCommand.Execute(null);

        Assert.Same(archive, shell.SelectedItem);
        Assert.True(archive.IsSelected);
        Assert.False(shell.PrimaryNavigation[0].IsSelected);
        Assert.Equal("Archive", shell.ViewTitle);
        Assert.Equal(archive.EmptyStateHeading, shell.EmptyStateHeading);
        Assert.Equal(archive.EmptyStateBody, shell.EmptyStateBody);
    }

    [Fact]
    public void UpcomingSelectionUsesItsRealSurfaceAndBadgeTracksQualifyingTasks()
    {
        var work = new MemoryWorkspaceWork();
        var included = work.CreateStandaloneTask("Included", "", "home", new DateOnly(2026, 10, 11));
        work.CreateStandaloneTask("Outside", "", "home", new DateOnly(2026, 10, 12));
        var shell = new ShellViewModel(work,
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero)));
        var upcoming = shell.PrimaryNavigation.Single(item => item.Title == "Upcoming");

        Assert.Equal("1", upcoming.CountText);
        Assert.Equal("1 item", upcoming.AccessibleCount);
        upcoming.SelectCommand.Execute(null);
        Assert.True(shell.ShowUpcoming);
        Assert.False(shell.ShowEmpty);

        shell.Work!.UpcomingGroups.SelectMany(group => group.Rows).Single(row => row.Task.Id == included.Id)
            .Task.ToggleCompletionCommand.Execute(null);
        Assert.Equal("0", upcoming.CountText);
        Assert.Equal("0 items", upcoming.AccessibleCount);
    }

    [Fact]
    public void ArchiveSelectionUsesItsRealSurfaceAndBadgeTracksRestoration()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Filed receipt", "", "home", null);
        work.CompleteTask(task.Id);
        work.ArchiveTask(task.Id);
        var shell = new ShellViewModel(work);
        var archive = shell.PrimaryNavigation.Single(item => item.Title == "Archive");

        Assert.Equal("1", archive.CountText);
        Assert.Equal("1 item", archive.AccessibleCount);
        Assert.Contains("restore", archive.EmptyStateBody, StringComparison.OrdinalIgnoreCase);

        archive.SelectCommand.Execute(null);

        Assert.True(shell.ShowArchive);
        Assert.False(shell.ShowEmpty);
        shell.Work!.Archived.Single().Task.RestoreCommand.Execute(null);
        Assert.Equal("0", archive.CountText);
        Assert.Equal("0 items", archive.AccessibleCount);
    }

    [Fact]
    public void BinSelectionUsesItsRealSurfaceAndBadgeAppearsOnlyWhileNonEmpty()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Removed note", "", "home", null);
        var shell = new ShellViewModel(work);

        Assert.Null(shell.BinNavigation.CountText);
        work.MoveTaskToBin(task.Id);
        shell.Work!.RefreshFromStore();

        Assert.Equal("1", shell.BinNavigation.CountText);
        Assert.Equal("1 item", shell.BinNavigation.AccessibleCount);
        shell.BinNavigation.SelectCommand.Execute(null);
        Assert.True(shell.ShowBin);
        Assert.False(shell.ShowEmpty);

        shell.Work.Bin.Single().RestoreCommand.Execute(null);
        Assert.Null(shell.BinNavigation.CountText);
        Assert.Empty(shell.BinNavigation.AccessibleCount);
        Assert.Equal("navigation-bin", shell.Work.BinFocusAutomationId);
    }

    [Fact]
    public void ProjectBinAggregateCountsAsOneSidebarItemRegardlessOfChildCount()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        work.CreateTask(project.Id, "Dig");
        work.CreateTask(project.Id, "Plant");
        var shell = new ShellViewModel(work);

        work.MoveProjectToBin(project.Id);
        shell.Work!.RefreshFromStore();

        Assert.Equal("1", shell.BinNavigation.CountText);
        Assert.Equal("1 item", shell.BinNavigation.AccessibleCount);
        Assert.Equal(2, Assert.Single(shell.Work.Bin).TaskCount);
    }
}
