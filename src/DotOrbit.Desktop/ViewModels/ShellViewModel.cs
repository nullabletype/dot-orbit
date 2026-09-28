using System.ComponentModel;
using DotOrbit.Core.Workspaces;
using System.Runtime.CompilerServices;

namespace DotOrbit.Desktop.ViewModels;

public sealed class ShellViewModel : INotifyPropertyChanged
{
    private NavigationItemViewModel _selectedItem = null!;

    public ShellViewModel(IWorkspaceWork? work = null)
    {
        Work = work is null ? null : new ProjectCaptureViewModel(work);
        PrimaryNavigation =
        [
            CreateNavigationItem(
                "Today",
                "M6.76,4.84L5.35,3.43L3.93,4.84L5.34,6.25M1,13H4V11H1M11,1V4H13V1M18.66,6.25L20.07,4.84L18.65,3.43L17.24,4.84M20,11V13H23V11M11,20V23H13V20M3.93,19.16L5.35,20.58L6.76,19.16L5.34,17.75M12,6A6,6 0 1,0 12,18A6,6 0 1,0 12,6",
                "Your chosen tasks, in the same manual order as the backlog.",
                "Nothing planned for today",
                "Choose work from Backlog when you are ready to focus."),
            CreateNavigationItem(
                "Upcoming",
                "M12,2A10,10 0 1,0 12,22A10,10 0 1,0 12,2M13,7H11V13L16.2,16.2L17.3,14.5L13,12V7",
                "Overdue tasks and tasks due through the same weekday next week, grouped by date.",
                "No upcoming tasks",
                "Overdue tasks and tasks due through the same weekday next week will appear here."),
            CreateNavigationItem(
                "Backlog",
                "M3,5H6V8H3V5M9,5H21V8H9V5M3,11H6V14H3V11M9,11H21V14H9V11M3,17H6V20H3V17M9,17H21V20H9V17",
                "Every incomplete task in one shared manual order.",
                "Your backlog is clear",
                "New standalone tasks and incomplete project work will collect here."),
            CreateNavigationItem(
                "Projects",
                "M10,4H2C0.9,4 0,4.9 0,6V18C0,19.1 0.9,20 2,20H22C23.1,20 24,19.1 24,18V8C24,6.9 23.1,6 22,6H12L10,4",
                "Purposeful work, ordered the way you want to approach it.",
                "No projects yet",
                "Create a project when a piece of work needs more than one task."),
            CreateNavigationItem(
                "Categories",
                "M3,3H10V10H3V3M14,3H21V10H14V3M3,14H10V21H3V14M14,14H21V21H14V14",
                "Projects and standalone tasks grouped by category.",
                "No categorised work yet",
                "Projects and standalone tasks will be grouped by category here."),
            CreateNavigationItem(
                "Completed",
                "M9,16.17L4.83,12L3.41,13.41L9,19L21,7L19.59,5.59L9,16.17",
                "Completed work grouped by its captured completion date.",
                "No completed tasks yet",
                "Recently completed work will be grouped by completion date here."),
            CreateNavigationItem(
                "Archive",
                "M3,4H21V8H3V4M5,10H19V21H5V10M9,12V14H15V12H9",
                "Searchable history that stays out of active work.",
                "Your archive is empty",
                "Archived projects and tasks will remain searchable here."),
        ];

        BinNavigation = CreateNavigationItem(
            "Bin",
            "M6,19C6,20.1 6.9,21 8,21H16C17.1,21 18,20.1 18,19V7H6V19M8,9H10V18H8V9M14,9H16V18H14V9M15.5,4L14.5,3H9.5L8.5,4H5V6H19V4H15.5",
            "Removed work that can still be restored.",
            "Bin is empty",
            "Removed projects and tasks will wait here until restored or permanently emptied.",
            countText: null);

        Select(PrimaryNavigation[0]);
        if (Work is not null)
        {
            Work.Projects.CollectionChanged += (_, _) => UpdateCounts();
            Work.Backlog.CollectionChanged += (_, _) => UpdateCounts();
            UpdateCounts();
        }
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
            OnPropertyChanged(nameof(ShowProjects));
            OnPropertyChanged(nameof(ShowBacklog));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(ViewSubtitle));
            OnPropertyChanged(nameof(EmptyStateHeading));
            OnPropertyChanged(nameof(EmptyStateBody));
        }
    }

    public ProjectCaptureViewModel? Work { get; }

    public bool ShowProjects => Work is not null && ViewTitle == "Projects";
    public bool ShowBacklog => Work is not null && ViewTitle == "Backlog";
    public bool ShowEmpty => !ShowProjects && !ShowBacklog;

    public string ViewTitle => SelectedItem.Title;

    public string ViewSubtitle => SelectedItem.ViewSubtitle;

    public string EmptyStateHeading => SelectedItem.EmptyStateHeading;

    public string EmptyStateBody => SelectedItem.EmptyStateBody;

    private NavigationItemViewModel CreateNavigationItem(
        string title,
        string iconData,
        string viewSubtitle,
        string emptyStateHeading,
        string emptyStateBody,
        string? countText = "0") =>
        new(
            title,
            iconData,
            countText,
            $"navigation-{title.ToLowerInvariant()}",
            viewSubtitle,
            emptyStateHeading,
            emptyStateBody,
            Select);

    private void Select(NavigationItemViewModel item)
    {
        if (Work is null) SelectCore(item);
        else Work.Navigate(() => SelectCore(item));
    }

    private void SelectCore(NavigationItemViewModel item)
    {
        var wasBacklog = _selectedItem is not null && _selectedItem.Title == "Backlog";
        if (_selectedItem is not null)
        {
            _selectedItem.IsSelected = false;
        }

        item.IsSelected = true;
        SelectedItem = item;
        var isBacklog = item.Title == "Backlog";
        if (Work is not null && wasBacklog != isBacklog)
        {
            if (isBacklog) Work.BeginBacklogEntrySession();
            else Work.EndBacklogEntrySession();
        }
    }

    private void UpdateCounts()
    {
        PrimaryNavigation.Single(n => n.Title == "Projects").CountText = Work!.Projects.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PrimaryNavigation.Single(n => n.Title == "Backlog").CountText = Work.Backlog.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
