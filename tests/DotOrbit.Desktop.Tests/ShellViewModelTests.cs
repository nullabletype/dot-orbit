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
        Assert.DoesNotContain(shell.BinNavigation, shell.PrimaryNavigation);
        Assert.All(shell.PrimaryNavigation, item => Assert.Equal("0", item.CountText));
        Assert.Null(shell.BinNavigation.CountText);
    }

    [Fact]
    public void EveryDestinationProvidesAUsableEmptyState()
    {
        var shell = new ShellViewModel();
        var destinations = shell.PrimaryNavigation.Append(shell.BinNavigation);

        Assert.All(destinations, destination =>
        {
            Assert.False(string.IsNullOrWhiteSpace(destination.EmptyStateHeading));
            Assert.False(string.IsNullOrWhiteSpace(destination.EmptyStateBody));
            Assert.NotEqual(destination.EmptyStateHeading, destination.EmptyStateBody);
        });
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
}
