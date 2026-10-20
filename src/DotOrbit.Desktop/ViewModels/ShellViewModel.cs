using System.ComponentModel;
using DotOrbit.Core.Workspaces;
using System.Runtime.CompilerServices;

namespace DotOrbit.Desktop.ViewModels;

public sealed class ShellViewModel : INotifyPropertyChanged
{
    private NavigationItemViewModel _selectedItem = null!;
    private readonly IInterchangePreferenceService _interchangePreferenceService;
    private readonly IAiFeaturePreferenceService _aiFeaturePreferenceService;

    public ShellViewModel(
        IWorkspaceWork? work = null,
        TimeProvider? timeProvider = null,
        IApplicationThemeService? applicationThemeService = null,
        IInterchangePreferenceService? interchangePreferenceService = null,
        IAiFeaturePreferenceService? aiFeaturePreferenceService = null)
        : this(
            work,
            timeProvider,
            applicationThemeService,
            null,
            null,
            null,
            interchangePreferenceService,
            aiFeaturePreferenceService)
    {
    }

    internal ShellViewModel(
        IWorkspaceWork? work,
        TimeProvider? timeProvider,
        IApplicationThemeService? applicationThemeService,
        IInspectorSaveWriter? inspectorSaveWriter = null,
        IWorkspaceActionExecutor? actionExecutor = null,
        IUiDispatcher? uiDispatcher = null,
        IInterchangePreferenceService? interchangePreferenceService = null,
        IAiFeaturePreferenceService? aiFeaturePreferenceService = null)
    {
        applicationThemeService ??= new TransientApplicationThemeService();
        _interchangePreferenceService = interchangePreferenceService
            ?? new TransientInterchangePreferenceService();
        _aiFeaturePreferenceService = aiFeaturePreferenceService
            ?? new TransientAiFeaturePreferenceService();
        Work = work is null
            ? null
            : inspectorSaveWriter is null
                ? new ProjectCaptureViewModel(work, timeProvider)
                : new ProjectCaptureViewModel(work, timeProvider, inspectorSaveWriter, actionExecutor, uiDispatcher);
        Settings = work is null
            ? null
            : new SettingsViewModel(
                work,
                () => Work!.RefreshFromStore(),
                applicationThemeService,
                _aiFeaturePreferenceService,
                OnAiFeaturesChanged);
        Ai = work is null || !AiFeaturesEnabled
            ? null
            : new AiViewModel(_interchangePreferenceService);
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
                WorkTypeIconGeometry.BacklogPath,
                "Every incomplete task in one shared manual order.",
                "Your backlog is clear",
                "New standalone tasks and incomplete project work will collect here."),
            CreateNavigationItem(
                "Projects",
                WorkTypeIconGeometry.ProjectPath,
                "Purposeful work, ordered the way you want to approach it.",
                "No projects yet",
                "Create a project when a piece of work needs more than one task."),
            CreateNavigationItem(
                "Categories",
                WorkTypeIconGeometry.CategoryPath,
                "Projects and standalone tasks grouped by category.",
                "No categorised work yet",
                "Projects and standalone tasks will be grouped by category here."),
            CreateNavigationItem(
                "Completed",
                "M9,16.17L4.83,12L3.41,13.41L9,19L21,7L19.59,5.59L9,16.17",
                "Completed tasks grouped by captured day for three days, then by calendar week.",
                "No completed tasks yet",
                "Recently completed work will be grouped by completion date here."),
            CreateNavigationItem(
                "Archive",
                "M3,4H21V8H3V4M5,10H19V21H5V10M9,12V14H15V12H9",
                "Archived projects and tasks retained outside active work.",
                "Your archive is empty",
                "Projects and tasks you archive will remain available to restore here."),
        ];

        AiNavigation = CreateNavigationItem(
            "AI",
            "M12 1.8l1.55 4.15L17.7 7.5l-4.15 1.55L12 13.2l-1.55-4.15L6.3 7.5l4.15-1.55L12 1.8zm-6.2 8.7l1.05 2.8 2.8 1.05-2.8 1.05-1.05 2.8-1.05-2.8-2.8-1.05 2.8-1.05 1.05-2.8zm12.4 4l.95 2.55L21.7 18l-2.55.95-.95 2.55-.95-2.55L14.7 18l2.55-.95.95-2.55z",
            "Copy planning context for an AI assistant without contacting a provider.",
            "AI planning exchange",
            "Copy planning context explicitly and review proposed changes before applying them.",
            countText: null);

        BinNavigation = CreateNavigationItem(
            "Bin",
            "M6,19C6,20.1 6.9,21 8,21H16C17.1,21 18,20.1 18,19V7H6V19M8,9H10V18H8V9M14,9H16V18H14V9M15.5,4L14.5,3H9.5L8.5,4H5V6H19V4H15.5",
            "Removed Tasks that can still be restored.",
            "Bin is empty",
            "Removed Tasks will remain available to restore here.",
            countText: null);

        SettingsNavigation = CreateNavigationItem(
            "Settings",
            "M19.43,12.98C19.47,12.66 19.5,12.34 19.5,12C19.5,11.66 19.47,11.34 19.43,11.02L21.54,9.37L19.54,5.91L17.05,6.91C16.54,6.5 15.98,6.17 15.35,5.92L15,3.27H11L10.65,5.92C10.02,6.17 9.46,6.5 8.95,6.91L6.46,5.91L4.46,9.37L6.57,11.02C6.53,11.34 6.5,11.67 6.5,12C6.5,12.33 6.53,12.66 6.57,12.98L4.46,14.63L6.46,18.09L8.95,17.09C9.46,17.5 10.02,17.83 10.65,18.08L11,20.73H15L15.35,18.08C15.98,17.83 16.54,17.5 17.05,17.09L19.54,18.09L21.54,14.63L19.43,12.98M13,15.5A3.5,3.5 0 1,1 13,8.5A3.5,3.5 0 1,1 13,15.5",
            "Manage appearance, reusable workspace data, security, and recovery options.",
            "Settings",
            "Manage appearance, reusable Participants, and encrypted recovery options.",
            countText: null);

        Select(PrimaryNavigation[0]);
        if (Work is not null)
        {
            Work.Projects.CollectionChanged += (_, _) => UpdateCounts();
            Work.UpcomingGroups.CollectionChanged += (_, _) => UpdateCounts();
            Work.Backlog.CollectionChanged += (_, _) => UpdateCounts();
            Work.Completed.CollectionChanged += (_, _) => UpdateCounts();
            Work.Archived.CollectionChanged += (_, _) => UpdateCounts();
            Work.ArchivedProjects.CollectionChanged += (_, _) => UpdateCounts();
            Work.Bin.CollectionChanged += (_, _) => UpdateCounts();
            Work.TodayPlanned.CollectionChanged += (_, _) => UpdateCounts();
            Work.TodayInProgress.CollectionChanged += (_, _) => UpdateCounts();
            Work.CategoryGroups.CollectionChanged += (_, _) => UpdateCounts();
            Work.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ProjectCaptureViewModel.UpcomingCount)) UpdateCounts();
            };
            UpdateCounts();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<NavigationItemViewModel> PrimaryNavigation { get; }

    public NavigationItemViewModel AiNavigation { get; }
    public NavigationItemViewModel BinNavigation { get; }
    public NavigationItemViewModel SettingsNavigation { get; }
    public IReadOnlyList<NavigationItemViewModel> UtilityNavigation => AiFeaturesEnabled
        ? [AiNavigation, BinNavigation, SettingsNavigation]
        : [BinNavigation, SettingsNavigation];

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
            OnPropertyChanged(nameof(ShowToday));
            OnPropertyChanged(nameof(ShowUpcoming));
            OnPropertyChanged(nameof(ShowBacklog));
            OnPropertyChanged(nameof(ShowCompleted));
            OnPropertyChanged(nameof(ShowArchive));
            OnPropertyChanged(nameof(ShowBin));
            OnPropertyChanged(nameof(ShowCategories));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(ShowAI));
            OnPropertyChanged(nameof(ShowSettings));
            OnPropertyChanged(nameof(ShowWorkspace));
            OnPropertyChanged(nameof(ViewSubtitle));
            OnPropertyChanged(nameof(EmptyStateHeading));
            OnPropertyChanged(nameof(EmptyStateBody));
        }
    }

    public ProjectCaptureViewModel? Work { get; }
    public SettingsViewModel? Settings { get; }
    public AiViewModel? Ai { get; private set; }

    public bool AiFeaturesEnabled => _aiFeaturePreferenceService.AiFeaturesEnabled;

    public bool ShowProjects => Work is not null && ViewTitle == "Projects";
    public bool ShowToday => Work is not null && ViewTitle == "Today";
    public bool ShowUpcoming => Work is not null && ViewTitle == "Upcoming";
    public bool ShowBacklog => Work is not null && ViewTitle == "Backlog";
    public bool ShowCompleted => Work is not null && ViewTitle == "Completed";
    public bool ShowArchive => Work is not null && ViewTitle == "Archive";
    public bool ShowBin => Work is not null && ViewTitle == "Bin";
    public bool ShowCategories => Work is not null && ViewTitle == "Categories";
    public bool ShowAI => AiFeaturesEnabled && ViewTitle == "AI";
    public bool ShowSettings => ViewTitle == "Settings";
    public bool ShowWorkspace => !ShowAI && !ShowSettings;
    public bool ShowEmpty => !ShowToday && !ShowUpcoming && !ShowProjects && !ShowBacklog && !ShowCompleted && !ShowArchive && !ShowBin && !ShowCategories && !ShowAI && !ShowSettings;

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
        if (ReferenceEquals(item, AiNavigation) && !AiFeaturesEnabled)
        {
            return;
        }

        if (Work is null) SelectCore(item);
        else Work.Navigate(() => SelectCore(item));
    }

    private void OnAiFeaturesChanged(bool enabled)
    {
        Ai = enabled && Work is not null
            ? new AiViewModel(_interchangePreferenceService)
            : null;
        OnPropertyChanged(nameof(Ai));
        OnPropertyChanged(nameof(AiFeaturesEnabled));
        OnPropertyChanged(nameof(UtilityNavigation));

        if (!enabled && ReferenceEquals(SelectedItem, AiNavigation))
        {
            SelectCore(SettingsNavigation);
            return;
        }

        OnPropertyChanged(nameof(ShowAI));
        OnPropertyChanged(nameof(ShowWorkspace));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private void SelectCore(NavigationItemViewModel item)
    {
        var wasBacklog = _selectedItem is not null && _selectedItem.Title == "Backlog";
        var wasUpcoming = _selectedItem is not null && _selectedItem.Title == "Upcoming";
        var wasCompleted = _selectedItem is not null && _selectedItem.Title == "Completed";
        var wasArchive = _selectedItem is not null && _selectedItem.Title == "Archive";
        var wasBin = _selectedItem is not null && _selectedItem.Title == "Bin";
        var wasToday = _selectedItem is not null && _selectedItem.Title == "Today";
        if (_selectedItem is not null)
        {
            _selectedItem.IsSelected = false;
        }

        item.IsSelected = true;
        SelectedItem = item;
        var isBacklog = item.Title == "Backlog";
        var isUpcoming = item.Title == "Upcoming";
        var isCompleted = item.Title == "Completed";
        var isArchive = item.Title == "Archive";
        var isBin = item.Title == "Bin";
        var isToday = item.Title == "Today";
        if (Work is not null && wasBacklog != isBacklog)
        {
            if (isBacklog) Work.BeginBacklogEntrySession();
            else Work.EndBacklogEntrySession();
        }
        Work?.SetBacklogActive(isBacklog);
        if (Work is not null && wasUpcoming != isUpcoming) Work.SetUpcomingActive(isUpcoming);
        if (Work is not null && wasCompleted != isCompleted) Work.SetCompletedActive(isCompleted);
        if (Work is not null && wasArchive != isArchive) Work.SetArchiveActive(isArchive);
        if (Work is not null && wasBin != isBin) Work.SetBinActive(isBin);
        if (Work is not null && wasToday != isToday) Work.SetTodayActive(isToday);
        if (item.Title == "Settings") Settings?.Refresh();
    }

    private void UpdateCounts()
    {
        PrimaryNavigation.Single(n => n.Title == "Today").CountText =
            (Work!.TodayPlanned.Count + Work.TodayInProgress.Count).ToString(System.Globalization.CultureInfo.InvariantCulture);
        PrimaryNavigation.Single(n => n.Title == "Upcoming").CountText = Work.UpcomingCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PrimaryNavigation.Single(n => n.Title == "Projects").CountText = Work.Projects.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PrimaryNavigation.Single(n => n.Title == "Backlog").CountText = Work.Backlog.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PrimaryNavigation.Single(n => n.Title == "Completed").CountText = Work.Completed.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        PrimaryNavigation.Single(n => n.Title == "Archive").CountText =
            (Work.Archived.Count + Work.ArchivedProjects.Count).ToString(System.Globalization.CultureInfo.InvariantCulture);
        PrimaryNavigation.Single(n => n.Title == "Categories").CountText = Work.CategoryGroups.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        BinNavigation.CountText = Work.Bin.Count == 0
            ? null
            : Work.Bin.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
