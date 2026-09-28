using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using DotOrbit.Core.Workspaces;

namespace DotOrbit.Desktop.ViewModels;

public sealed class ProjectCaptureViewModel : INotifyPropertyChanged
{
    private readonly IWorkspaceWork _work;
    private readonly Dictionary<string, TaskRowViewModel> _taskRows = new(StringComparer.Ordinal);
    private WorkspaceWorkSnapshot _snapshot = new([], [], []);
    private Action? _pendingNavigation;
    private string? _editingId;
    private bool _editingTask;
    private bool _creating;
    private bool _creatingStandaloneTask;
    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _date = string.Empty;
    private CategoryChoice? _category;
    private (string Title, string Description, string Date, string? CategoryId) _original;
    private string _message = string.Empty;
    private bool _hasInspector;
    private string _backlogQuickTitle = string.Empty;
    private CategoryChoice? _backlogQuickCategory;
    private string _reorderAnnouncement = string.Empty;

    public ProjectCaptureViewModel(IWorkspaceWork work)
    {
        _work = work;
        NewProjectCommand = new(() => Navigate(BeginProject));
        NewTaskCommand = new(() => Navigate(BeginStandaloneTask));
        SaveCommand = new(() => Save());
        CancelCommand = new(Cancel);
        SaveAndLeaveCommand = new(() => { if (Save()) Leave(); });
        DiscardAndLeaveCommand = new(() => { Cancel(); Leave(); });
        StayCommand = new(() => { _pendingNavigation = null; Notify(nameof(NeedsDecision)); });
        Reload();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ProjectRowViewModel> Projects { get; } = [];
    public ObservableCollection<TaskRowViewModel> Backlog { get; } = [];
    public ObservableCollection<CategoryChoice> Categories { get; } = [];
    public ObservableCollection<CategoryChoice> BacklogCategories { get; } = [];
    public RelayCommand NewProjectCommand { get; }
    public RelayCommand NewTaskCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SaveAndLeaveCommand { get; }
    public RelayCommand DiscardAndLeaveCommand { get; }
    public RelayCommand StayCommand { get; }
    public bool HasInspector { get => _hasInspector; private set { _hasInspector = value; Notify(); Notify(nameof(HasNoInspector)); } }
    public bool HasNoInspector => !HasInspector;
    public bool HasProjects => Projects.Count > 0;
    public bool HasNoProjects => !HasProjects;
    public bool NeedsDecision => _pendingNavigation is not null;
    public bool IsDirty => HasInspector && (_creating || _original != Fingerprint());
    public string InspectorHeading => _creating ? (_editingTask ? "New task" : "New project") : _editingTask ? "Task details" : "Project details";
    public bool ShowProjectSummary => HasInspector && !_creating && !_editingTask;
    public string ProjectSummary => ShowProjectSummary ? Projects.Single(p => p.Id == _editingId).Summary + " · No completion date" : string.Empty;
    public string InspectorMetaLabel => _editingTask ? "CATEGORY BEHAVIOUR" : "STATUS";
    public string InspectorMetaValue => _editingTask
        ? CategoryHint
        : ShowProjectSummary
            ? $"{Projects.Single(p => p.Id == _editingId).Status} · {Projects.Single(p => p.Id == _editingId).ProgressText}"
            : "Not started · 0/0 tasks";
    public string SaveLabel => _creating ? "Create" : "Save";
    public string DateLabel => _editingTask ? "Due date (YYYY-MM-DD, optional)" : "Target date (YYYY-MM-DD, optional)";
    public string CategoryHint => _editingTask
        ? IsStandaloneTaskDraft
            ? "Standalone Task category"
            : (_category?.Id is null ? "Inherited from project" : "Explicit category override")
        : "Project category";
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public string Message { get => _message; private set { _message = value; Notify(); Notify(nameof(HasMessage)); } }
    public string Title { get => _title; set { _title = value; Notify(); } }
    public string Description { get => _description; set { _description = value; Notify(); } }
    public string Date { get => _date; set { _date = value; Notify(); } }
    public string BacklogQuickTitle { get => _backlogQuickTitle; set { _backlogQuickTitle = value; Notify(); } }
    public CategoryChoice? BacklogQuickCategory { get => _backlogQuickCategory; set { _backlogQuickCategory = value; Notify(); } }
    public string ReorderAnnouncement { get => _reorderAnnouncement; private set { _reorderAnnouncement = value; Notify(); } }
    private bool IsStandaloneTaskDraft => _editingTask && (_creatingStandaloneTask
        || (_editingId is not null && _snapshot.Tasks.Single(task => task.Id == _editingId).ProjectId is null));
    public CategoryChoice? Category
    {
        get => _category;
        set
        {
            _category = value;
            Notify();
            Notify(nameof(CategoryHint));
            Notify(nameof(InspectorMetaValue));
        }
    }

    public void Navigate(Action destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (NeedsDecision) return;
        if (IsDirty)
        {
            _pendingNavigation = destination;
            Notify(nameof(NeedsDecision));
            return;
        }
        CloseInspector();
        destination();
    }

    public void SelectProject(string id) => Navigate(() => LoadProject(id));
    public void SelectTask(string id) => Navigate(() => LoadTask(id));

    public bool QuickAdd(string projectId, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        return Attempt(() => { _work.CreateTask(projectId, title); Reload(); Message = "Task created."; });
    }

    public void BeginBacklogEntrySession()
    {
        BacklogQuickTitle = string.Empty;
        BacklogQuickCategory = null;
    }

    public void EndBacklogEntrySession()
    {
        BacklogQuickTitle = string.Empty;
        BacklogQuickCategory = null;
    }

    public bool SubmitBacklogQuickAdd()
    {
        if (string.IsNullOrWhiteSpace(BacklogQuickTitle)) return false;
        if (BacklogQuickCategory?.Id is null)
        {
            Message = "Choose a category before creating a standalone Task.";
            return false;
        }

        var title = BacklogQuickTitle;
        if (!Attempt(() => { _work.CreateStandaloneTask(title, string.Empty, BacklogQuickCategory.Id, null); Reload(); Message = "Task created."; }))
            return false;
        BacklogQuickTitle = string.Empty;
        return true;
    }

    public bool DragTask(string taskId, string targetTaskId)
    {
        var targetPosition = Backlog.IndexOf(Backlog.Single(task => task.Id == targetTaskId));
        return MoveTask(taskId, targetPosition);
    }

    private void BeginProject()
    {
        _creating = true;
        _editingTask = false;
        _creatingStandaloneTask = false;
        _editingId = null;
        SetDraft(string.Empty, string.Empty, null, _snapshot.Categories.Count == 0 ? null : _snapshot.Categories[0].Id);
    }

    private void BeginStandaloneTask()
    {
        _creating = true;
        _editingTask = true;
        _creatingStandaloneTask = true;
        _editingId = null;
        SetDraft(string.Empty, string.Empty, null, null);
    }

    private void LoadProject(string id)
    {
        var project = _snapshot.Projects.Single(p => p.Id == id);
        _editingId = id;
        _editingTask = false;
        _creating = false;
        _creatingStandaloneTask = false;
        SetDraft(project.Title, project.Description, project.TargetDate, project.CategoryId);
    }

    private void LoadTask(string id)
    {
        var task = _snapshot.Tasks.Single(t => t.Id == id);
        _editingId = id;
        _editingTask = true;
        _creating = false;
        _creatingStandaloneTask = false;
        SetDraft(task.Title, task.Description, task.DueDate, task.ExplicitCategoryId);
    }

    private void SetDraft(string title, string description, DateOnly? date, string? categoryId)
    {
        Categories.Clear();
        if (_editingTask && !IsStandaloneTaskDraft)
        {
            var task = _snapshot.Tasks.Single(t => t.Id == _editingId);
            var project = _snapshot.Projects.Single(p => p.Id == task.ProjectId);
            Categories.Add(new(null, $"Inherit — {CategoryName(project.CategoryId)}"));
        }
        foreach (var category in _snapshot.Categories) Categories.Add(new(category.Id, category.Name));
        Title = title;
        Description = description;
        Date = date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        Category = Categories.FirstOrDefault(c => c.Id == categoryId);
        _original = Fingerprint();
        HasInspector = true;
        Message = string.Empty;
        Notify(nameof(InspectorHeading)); Notify(nameof(SaveLabel)); Notify(nameof(DateLabel));
        Notify(nameof(ShowProjectSummary)); Notify(nameof(ProjectSummary));
        Notify(nameof(InspectorMetaLabel)); Notify(nameof(InspectorMetaValue));
    }

    public bool Save()
    {
        if (!HasInspector) return true;
        if (string.IsNullOrWhiteSpace(Title)) { Message = "Enter a title."; return false; }
        DateOnly? date = null;
        if (!string.IsNullOrWhiteSpace(Date))
        {
            if (!DateOnly.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            { Message = "Enter a valid date as YYYY-MM-DD, or leave it empty."; return false; }
            date = parsed;
        }
        if (Category is null || ((!_editingTask || IsStandaloneTaskDraft) && Category.Id is null)) { Message = "Choose a category."; return false; }
        return Attempt(() =>
        {
            if (_creating && _editingTask) _editingId = _work.CreateStandaloneTask(Title, Description, Category.Id!, date).Id;
            else if (_creating) _editingId = _work.CreateProject(Title, Description, Category.Id!, date).Id;
            else if (_editingTask) _work.UpdateTask(_editingId!, Title, Description, Category.Id, date);
            else _work.UpdateProject(_editingId!, Title, Description, Category.Id!, date);
            _creating = false;
            _creatingStandaloneTask = false;
            Reload();
            if (_editingTask) LoadTask(_editingId!); else LoadProject(_editingId!);
            Message = "Changes saved.";
        });
    }

    private bool Attempt(Action action)
    {
        try { action(); return true; }
        catch (WorkspaceWorkException) { Message = "Could not save workspace changes. Your draft is retained. Try again."; return false; }
        catch (ArgumentException) { Message = "The item or category is no longer available. Check your draft and try again."; return false; }
    }

    public void Cancel()
    {
        if (_creating || _editingId is null) CloseInspector();
        else if (_editingTask) LoadTask(_editingId);
        else LoadProject(_editingId);
    }
    private void CloseInspector() { HasInspector = false; _creating = false; _creatingStandaloneTask = false; Message = string.Empty; }
    private void Leave()
    {
        var destination = _pendingNavigation;
        _pendingNavigation = null;
        Notify(nameof(NeedsDecision));
        CloseInspector();
        destination?.Invoke();
    }
    private (string Title, string Description, string Date, string? CategoryId) Fingerprint() => (Title, Description, Date, Category?.Id);
    private string CategoryName(string id) => _snapshot.Categories.Single(c => c.Id == id).Name;
    private void Reload()
    {
        _snapshot = _work.Read();
        RefreshBacklogCategories();
        foreach (var removedId in _taskRows.Keys.Except(_snapshot.Tasks.Select(task => task.Id), StringComparer.Ordinal).ToArray())
            _taskRows.Remove(removedId);
        var existing = Projects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        Projects.Clear();
        foreach (var project in _snapshot.Projects)
        {
            var row = existing.GetValueOrDefault(project.Id) ?? new ProjectRowViewModel(this, project.Id);
            row.Refresh(project, ProjectWorkSummary.From(_snapshot, project.Id), CategoryName(project.CategoryId), _snapshot.Tasks.Where(t => t.ProjectId == project.Id).OrderBy(t => t.ProjectPosition).Select(ToTaskRow));
            Projects.Add(row);
        }
        var desiredBacklog = _snapshot.Tasks.OrderBy(t => t.SharedPosition).Select(ToTaskRow).ToArray();
        SynchroniseBacklog(desiredBacklog);
        for (var index = 0; index < Backlog.Count; index++) Backlog[index].SetPosition(index + 1, Backlog.Count);
        Notify(nameof(ProjectSummary));
        Notify(nameof(InspectorMetaValue));
        Notify(nameof(HasProjects));
        Notify(nameof(HasNoProjects));
    }
    private TaskRowViewModel ToTaskRow(TaskRecord task)
    {
        var inherited = task.ProjectId is not null && task.ExplicitCategoryId is null;
        var categoryId = task.ExplicitCategoryId
            ?? _snapshot.Projects.Single(project => project.Id == task.ProjectId).CategoryId;
        if (!_taskRows.TryGetValue(task.Id, out var row))
        {
            row = new(this, task.Id);
            _taskRows.Add(task.Id, row);
        }
        row.Refresh(task.Title, CategoryName(categoryId), task.ProjectId is null ? "standalone" : inherited ? "inherited" : "override");
        return row;
    }

    private void RefreshBacklogCategories()
    {
        var selectedId = BacklogQuickCategory?.Id;
        BacklogCategories.Clear();
        foreach (var category in _snapshot.Categories) BacklogCategories.Add(new(category.Id, category.Name));
        BacklogQuickCategory = BacklogCategories.FirstOrDefault(category => category.Id == selectedId);
    }

    private void SynchroniseBacklog(TaskRowViewModel[] desired)
    {
        for (var index = Backlog.Count - 1; index >= 0; index--)
            if (!desired.Contains(Backlog[index])) Backlog.RemoveAt(index);
        for (var index = 0; index < desired.Length; index++)
        {
            var current = Backlog.IndexOf(desired[index]);
            if (current < 0) Backlog.Insert(index, desired[index]);
            else if (current != index) Backlog.Move(current, index);
        }
    }

    private bool MoveTask(string taskId, int targetPosition)
    {
        var currentPosition = Backlog.IndexOf(Backlog.Single(task => task.Id == taskId));
        if (currentPosition < 0 || Backlog.Count == 0) return false;
        targetPosition = Math.Clamp(targetPosition, 0, Backlog.Count - 1);
        if (!Attempt(() =>
            {
                var change = _work.MoveTaskInSharedOrder(taskId, targetPosition);
                Reload();
                var task = Backlog.Single(row => row.Id == taskId);
                ReorderAnnouncement = $"Moved {task.Title} to position {change.Position} of {change.Count} in Backlog.";
                Message = ReorderAnnouncement;
            }, "Could not reorder the Task. The Backlog order was not changed.")) return false;
        return true;
    }

    private bool Attempt(Action action, string failureMessage)
    {
        try { action(); return true; }
        catch (WorkspaceWorkException) { Message = failureMessage; return false; }
        catch (ArgumentException) { Message = failureMessage; return false; }
    }

    internal void MoveUp(string taskId) => MoveTask(taskId, Backlog.IndexOf(Backlog.Single(task => task.Id == taskId)) - 1);
    internal void MoveDown(string taskId) => MoveTask(taskId, Backlog.IndexOf(Backlog.Single(task => task.Id == taskId)) + 1);
    internal void MoveToTop(string taskId) => MoveTask(taskId, 0);
    internal void MoveToBottom(string taskId) => MoveTask(taskId, Backlog.Count - 1);
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed record CategoryChoice(string? Id, string Name);
public sealed class TaskRowViewModel : INotifyPropertyChanged
{
    private readonly ProjectCaptureViewModel _owner;
    private string _title = string.Empty;
    private string _categoryName = string.Empty;
    private string _categoryDisplay = string.Empty;
    private int _position;
    private int _count;

    public TaskRowViewModel(ProjectCaptureViewModel owner, string id)
    {
        _owner = owner;
        Id = id;
        SelectCommand = new(() => owner.SelectTask(id));
        MoveUpCommand = new(() => owner.MoveUp(id));
        MoveDownCommand = new(() => owner.MoveDown(id));
        MoveToTopCommand = new(() => owner.MoveToTop(id));
        MoveToBottomCommand = new(() => owner.MoveToBottom(id));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; }
    public string Title => _title;
    public string CategoryName => _categoryName;
    public string CategoryDisplay => _categoryDisplay;
    public string PositionText => $"{_position} of {_count}";
    public bool IsLast => _position == _count;
    public string ReorderAccessibleName => $"Reorder {Title} in Backlog";
    public string ReorderAutomationId => $"backlog-reorder-{Id}";
    public string MoveUpAccessibleName => $"Move {Title} up in Backlog";
    public string MoveDownAccessibleName => $"Move {Title} down in Backlog";
    public string MoveToTopAccessibleName => $"Move {Title} to top of Backlog";
    public string MoveToBottomAccessibleName => $"Move {Title} to bottom of Backlog";
    public RelayCommand SelectCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand MoveToTopCommand { get; }
    public RelayCommand MoveToBottomCommand { get; }

    public void Refresh(string title, string categoryName, string categoryBehaviour)
    {
        _title = title;
        _categoryName = categoryName;
        _categoryDisplay = $"{categoryName} · {categoryBehaviour}";
        Notify(nameof(Title));
        Notify(nameof(CategoryName));
        Notify(nameof(CategoryDisplay));
        Notify(nameof(ReorderAccessibleName));
        Notify(nameof(MoveUpAccessibleName));
        Notify(nameof(MoveDownAccessibleName));
        Notify(nameof(MoveToTopAccessibleName));
        Notify(nameof(MoveToBottomAccessibleName));
    }

    public void SetPosition(int position, int count)
    {
        _position = position;
        _count = count;
        Notify(nameof(PositionText));
        Notify(nameof(IsLast));
    }

    private void Notify(string propertyName) => PropertyChanged?.Invoke(this, new(propertyName));
}

public sealed class ProjectRowViewModel : INotifyPropertyChanged
{
    private readonly ProjectCaptureViewModel _owner;
    private bool _isExpanded = true;
    private string _quickTitle = string.Empty;
    public ProjectRowViewModel(ProjectCaptureViewModel owner, string id)
    {
        _owner = owner;
        Id = id;
        SelectCommand = new(() => owner.SelectProject(id));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; }
    public string Title { get; private set; } = string.Empty;
    public string Summary { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public string ProgressText { get; private set; } = string.Empty;
    public string CategoryName { get; private set; } = string.Empty;
    public string TargetText { get; private set; } = string.Empty;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Notify(nameof(IsExpanded));
            Notify(nameof(ExpansionAccessibleName));
        }
    }
    public string ExpansionAccessibleName => $"{(IsExpanded ? "Collapse" : "Expand")} {Title}";
    public string QuickTitle { get => _quickTitle; set { _quickTitle = value; PropertyChanged?.Invoke(this, new(nameof(QuickTitle))); } }
    public RelayCommand SelectCommand { get; }
    public ObservableCollection<TaskRowViewModel> Tasks { get; } = [];
    public bool Submit() { if (!_owner.QuickAdd(Id, QuickTitle)) return false; QuickTitle = string.Empty; return true; }
    public void Refresh(ProjectRecord project, ProjectWorkSummary summary, string category, IEnumerable<TaskRowViewModel> tasks)
    {
        Title = project.Title;
        Status = summary.Status;
        ProgressText = $"{summary.CompletedCount}/{summary.TaskCount} tasks";
        CategoryName = category;
        TargetText = project.TargetDate is { } date ? $"Target {date:yyyy-MM-dd}" : string.Empty;
        Tasks.Clear();
        foreach (var task in tasks) Tasks.Add(task);
        Summary = $"{summary.Status} · {summary.CompletedCount} of {summary.TaskCount} Tasks · {category}"
            + (string.IsNullOrEmpty(TargetText) ? string.Empty : $" · {TargetText}");
        PropertyChanged?.Invoke(this, new(nameof(Title)));
        PropertyChanged?.Invoke(this, new(nameof(Summary)));
        PropertyChanged?.Invoke(this, new(nameof(Status)));
        PropertyChanged?.Invoke(this, new(nameof(ProgressText)));
        PropertyChanged?.Invoke(this, new(nameof(CategoryName)));
        PropertyChanged?.Invoke(this, new(nameof(TargetText)));
        Notify(nameof(ExpansionAccessibleName));
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
}
