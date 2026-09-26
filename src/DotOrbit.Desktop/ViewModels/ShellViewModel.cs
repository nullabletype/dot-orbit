using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DotOrbit.Desktop.ViewModels;

public sealed class ShellViewModel : INotifyPropertyChanged
{
    private NavigationItemViewModel _selectedItem = null!;

    public ShellViewModel()
    {
        PrimaryNavigation =
        [
            CreateNavigationItem(
                "Today",
                "Nothing planned for today",
                "Choose work from Backlog when you are ready to focus."),
            CreateNavigationItem(
                "Upcoming",
                "No upcoming tasks",
                "Tasks with due dates in the next seven days will appear here."),
            CreateNavigationItem(
                "Backlog",
                "Your backlog is clear",
                "New standalone tasks and incomplete project work will collect here."),
            CreateNavigationItem(
                "Projects",
                "No projects yet",
                "Create a project when a piece of work needs more than one task."),
            CreateNavigationItem(
                "Categories",
                "No categorised work yet",
                "Projects and standalone tasks will be grouped by category here."),
            CreateNavigationItem(
                "Completed",
                "No completed tasks yet",
                "Recently completed work will be grouped by completion date here."),
            CreateNavigationItem(
                "Archive",
                "Your archive is empty",
                "Archived projects and tasks will remain searchable here."),
        ];

        BinNavigation = CreateNavigationItem(
            "Bin",
            "Bin is empty",
            "Removed projects and tasks will wait here until restored or permanently emptied.");

        Select(PrimaryNavigation[0]);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<NavigationItemViewModel> PrimaryNavigation { get; }

    public NavigationItemViewModel BinNavigation { get; }

    public NavigationItemViewModel SelectedItem
    {
        get => _selectedItem;
        private set
        {
            if (ReferenceEquals(_selectedItem, value))
            {
                return;
            }

            _selectedItem = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ViewTitle));
            OnPropertyChanged(nameof(EmptyStateHeading));
            OnPropertyChanged(nameof(EmptyStateBody));
        }
    }

    public string ViewTitle => SelectedItem.Title;

    public string EmptyStateHeading => SelectedItem.EmptyStateHeading;

    public string EmptyStateBody => SelectedItem.EmptyStateBody;

    private NavigationItemViewModel CreateNavigationItem(
        string title,
        string emptyStateHeading,
        string emptyStateBody) =>
        new(
            title,
            $"navigation-{title.ToLowerInvariant()}",
            emptyStateHeading,
            emptyStateBody,
            Select);

    private void Select(NavigationItemViewModel item)
    {
        if (_selectedItem is not null)
        {
            _selectedItem.IsSelected = false;
        }

        item.IsSelected = true;
        SelectedItem = item;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
