using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using DotOrbit.Core.Workspaces;

namespace DotOrbit.Desktop.ViewModels;

public sealed class ProjectCaptureViewModel : INotifyPropertyChanged
{
    private readonly IWorkspaceWork _work;
    private WorkspaceWorkSnapshot _snapshot = new([], [], []);
    private Action? _pendingNavigation;
    private string? _editingId;
    private bool _editingTask;
    private bool _creating;
    private string _title = string.Empty;
    private string _description = string.Empty;
    private string _date = string.Empty;
    private CategoryChoice? _category;
    private (string Title, string Description, string Date, string? CategoryId) _original;
    private string _message = string.Empty;
    private bool _hasInspector;

    public ProjectCaptureViewModel(IWorkspaceWork work)
    {
        _work = work;
        NewProjectCommand = new(() => Navigate(BeginProject));
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
    public RelayCommand NewProjectCommand { get; }
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
    public string InspectorHeading => _creating ? "New project" : _editingTask ? "Task details" : "Project details";
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
    public string CategoryHint => _editingTask ? (_category?.Id is null ? "Inherited from project" : "Explicit category override") : "Project category";
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public string Message { get => _message; private set { _message = value; Notify(); Notify(nameof(HasMessage)); } }
    public string Title { get => _title; set { _title = value; Notify(); } }
    public string Description { get => _description; set { _description = value; Notify(); } }
    public string Date { get => _date; set { _date = value; Notify(); } }
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

    private void BeginProject()
    {
        _creating = true;
        _editingTask = false;
        _editingId = null;
        SetDraft(string.Empty, string.Empty, null, _snapshot.Categories.Count == 0 ? null : _snapshot.Categories[0].Id);
    }

    private void LoadProject(string id)
    {
        var project = _snapshot.Projects.Single(p => p.Id == id);
        _editingId = id;
        _editingTask = false;
        _creating = false;
        SetDraft(project.Title, project.Description, project.TargetDate, project.CategoryId);
    }

    private void LoadTask(string id)
    {
        var task = _snapshot.Tasks.Single(t => t.Id == id);
        _editingId = id;
        _editingTask = true;
        _creating = false;
        SetDraft(task.Title, task.Description, task.DueDate, task.CategoryOverrideId);
    }

    private void SetDraft(string title, string description, DateOnly? date, string? categoryId)
    {
        Categories.Clear();
        if (_editingTask)
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
        if (Category is null || (!_editingTask && Category.Id is null)) { Message = "Choose a category."; return false; }
        return Attempt(() =>
        {
            if (_creating) _editingId = _work.CreateProject(Title, Description, Category.Id!, date).Id;
            else if (_editingTask) _work.UpdateTask(_editingId!, Title, Description, Category.Id, date);
            else _work.UpdateProject(_editingId!, Title, Description, Category.Id!, date);
            _creating = false;
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
    private void CloseInspector() { HasInspector = false; _creating = false; Message = string.Empty; }
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
        var existing = Projects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        Projects.Clear();
        foreach (var project in _snapshot.Projects)
        {
            var row = existing.GetValueOrDefault(project.Id) ?? new ProjectRowViewModel(this, project.Id);
            row.Refresh(project, ProjectWorkSummary.From(_snapshot, project.Id), CategoryName(project.CategoryId), _snapshot.Tasks.Where(t => t.ProjectId == project.Id).OrderBy(t => t.ProjectPosition).Select(ToTaskRow));
            Projects.Add(row);
        }
        Backlog.Clear();
        foreach (var task in _snapshot.Tasks.OrderBy(t => t.SharedPosition)) Backlog.Add(ToTaskRow(task));
        Notify(nameof(ProjectSummary));
        Notify(nameof(InspectorMetaValue));
        Notify(nameof(HasProjects));
        Notify(nameof(HasNoProjects));
    }
    private TaskRowViewModel ToTaskRow(TaskRecord task)
    {
        var project = _snapshot.Projects.Single(p => p.Id == task.ProjectId);
        return new(task.Id, task.Title, CategoryName(task.CategoryOverrideId ?? project.CategoryId), task.CategoryOverrideId is null, new(() => SelectTask(task.Id)));
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed record CategoryChoice(string? Id, string Name);
public sealed record TaskRowViewModel(string Id, string Title, string CategoryName, bool IsInherited, RelayCommand SelectCommand)
{
    public string CategoryDisplay => IsInherited ? $"{CategoryName} · inherited" : $"{CategoryName} · override";
}

public sealed class ProjectRowViewModel : INotifyPropertyChanged
{
    private readonly ProjectCaptureViewModel _owner;
    private string _quickTitle = string.Empty;
    public ProjectRowViewModel(ProjectCaptureViewModel owner, string id)
    { _owner = owner; Id = id; SelectCommand = new(() => owner.SelectProject(id)); }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; }
    public string Title { get; private set; } = string.Empty;
    public string Summary { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public string ProgressText { get; private set; } = string.Empty;
    public string CategoryName { get; private set; } = string.Empty;
    public string TargetText { get; private set; } = string.Empty;
    public bool IsExpanded { get; set; } = true;
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
    }
}
