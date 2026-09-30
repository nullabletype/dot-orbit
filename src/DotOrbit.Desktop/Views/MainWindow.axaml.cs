using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;

namespace DotOrbit.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private enum DragScope { None, Backlog, Projects, ProjectTasks }

    private IWorkspaceSession? _session;
    private bool _closingApproved;
    private DragScope _dragScope;
    private string? _draggedId;
    private string? _draggedProjectId;
    private string? _pressedTaskRowId;
    private Border? _dragTarget;
    private ProjectCaptureViewModel? _subscribedWork;
    private readonly DispatcherTimer _dateRefreshTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    public MainWindow()
        : this(null)
    {
    }

    internal MainWindow(IWorkspaceSession? session)
    {
        _session = session;
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += OnDataContextChanged;
        AddHandler(PointerPressedEvent, OnTaskDragHandlePointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnTaskDragPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnBacklogTaskPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(
            PointerCaptureLostEvent,
            OnPointerCaptureLost,
            RoutingStrategies.Direct | RoutingStrategies.Bubble,
            handledEventsToo: true);
        _dateRefreshTimer.Tick += OnDateRefreshTick;
        Opened += OnOpened;
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        SetSessionContext();
        var recoveryButton = this.FindControl<Button>("OpenRecoveryButton");
        if (recoveryButton is not null)
        {
            recoveryButton.IsVisible = session is not null;
        }

        Closed += OnClosed;
        Closing += OnClosing;
    }

    private void OnOpenRecovery(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        Navigate(() => new RecoveryWindow(_session!.Recovery, ReplaceSession).Show(this));
    }

    private void ReplaceSession(IWorkspaceSession session)
    {
        _session?.Dispose();
        _session = session;
        SetSessionContext();
    }

    private void SetSessionContext()
    {
        DataContext = new ShellViewModel(_session?.Work);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedWork is not null) _subscribedWork.PropertyChanged -= OnWorkChanged;
        _subscribedWork = (DataContext as ShellViewModel)?.Work;
        if (_subscribedWork is not null) _subscribedWork.PropertyChanged += OnWorkChanged;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        RefreshDatePresentation();
        _dateRefreshTimer.Start();
    }

    private void OnActivated(object? sender, EventArgs e) => RefreshDatePresentation();
    private void OnDateRefreshTick(object? sender, EventArgs e) => RefreshDatePresentation();
    private void RefreshDatePresentation() => (DataContext as ShellViewModel)?.Work?.RefreshDatePresentation();

    private void OnWorkChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProjectCaptureViewModel.NeedsDecision)
            && DataContext is ShellViewModel { Work.NeedsDecision: true })
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var radio in this.GetVisualDescendants().OfType<RadioButton>())
                    if (radio.DataContext is NavigationItemViewModel navigation)
                        radio.SetCurrentValue(RadioButton.IsCheckedProperty, navigation.IsSelected);
                this.FindControl<Button>("GuardSave")?.Focus();
            });
        if (e.PropertyName == nameof(ProjectCaptureViewModel.CompletionFocusAutomationId)
            && sender is ProjectCaptureViewModel { CompletionFocusAutomationId.Length: > 0 } work)
            Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(control => control.IsEffectivelyVisible
                    && AutomationProperties.GetAutomationId(control) == work.CompletionFocusAutomationId)?.Focus(),
                DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.ReorderFocusAutomationId)
            && sender is ProjectCaptureViewModel { ReorderFocusAutomationId.Length: > 0 } reordered)
            Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == reordered.ReorderFocusAutomationId)?.Focus(),
                DispatcherPriority.ApplicationIdle);
    }

    private void Navigate(Action action)
    {
        if (DataContext is ShellViewModel { Work: { } work }) work.Navigate(action);
        else action();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingApproved || DataContext is not ShellViewModel { Work.IsDirty: true }) return;
        e.Cancel = true;
        Navigate(() => { _closingApproved = true; Close(); });
    }

    private void OnFocusInspector(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (DataContext is ShellViewModel { Work.NeedsDecision: true }) this.FindControl<Button>("GuardSave")?.Focus();
        else if (DataContext is ShellViewModel { Work.HasInspector: true }) this.FindControl<TextBox>("DraftTitle")?.Focus();
        else this.FindControl<Button>("NewProjectButton")?.Focus();
    });

    private void OnQuickAddKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ProjectRowViewModel project } field) return;
        if (e.Key == Key.Escape) { project.QuickTitle = string.Empty; e.Handled = true; return; }
        if (e.Key is not (Key.Enter or Key.Tab) || string.IsNullOrWhiteSpace(field.Text)) return;
        project.QuickTitle = field.Text;
        e.Handled = true;
        project.Submit();
        Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(box => box.Classes.Contains("quick-add") && box.DataContext is ProjectRowViewModel row && row.Id == project.Id)?.Focus());
    }

    private void OnBacklogQuickAddKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox field || DataContext is not ShellViewModel { Work: { } work }) return;
        if (e.Key == Key.Escape) { work.BacklogQuickTitle = string.Empty; e.Handled = true; return; }
        if (e.Key is not (Key.Enter or Key.Tab) || string.IsNullOrWhiteSpace(field.Text)) return;
        work.BacklogQuickTitle = field.Text;
        e.Handled = true;
        if (work.SubmitBacklogQuickAdd())
            Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("BacklogQuickTitle")?.Focus());
    }

    private void OnTaskDragHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ResetPointerGesture();
        var hit = this.InputHitTest(e.GetPosition(this)) as Control;
        Button? handle = (hit as Button)?.Classes.Contains("drag-handle") == true
            ? (Button?)hit
            : hit?.GetVisualAncestors().OfType<Button>().FirstOrDefault(button => button.Classes.Contains("drag-handle"));
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (handle?.Classes.Contains("backlog-drag-handle") == true && handle.DataContext is TaskRowViewModel backlogTask)
        {
            _dragScope = DragScope.Backlog;
            _draggedId = backlogTask.Id;
            return;
        }
        if (handle?.Classes.Contains("project-drag-handle") == true && handle.DataContext is ProjectRowViewModel project)
        {
            _dragScope = DragScope.Projects;
            _draggedId = project.Id;
            return;
        }
        if (handle?.Classes.Contains("project-task-drag-handle") == true
            && handle.DataContext is TaskRowViewModel { ProjectId: { } projectId } projectTask)
        {
            _dragScope = DragScope.ProjectTasks;
            _draggedId = projectTask.Id;
            _draggedProjectId = projectId;
            return;
        }
        if (hit is Button || hit?.GetVisualAncestors().OfType<Button>().Any() == true) return;
        var row = (hit as Border)?.Classes.Contains("backlog-row") == true
            ? (Border?)hit
            : hit?.GetVisualAncestors().OfType<Border>().FirstOrDefault(border => border.Classes.Contains("backlog-row"));
        if (row?.DataContext is TaskRowViewModel pressedTask) _pressedTaskRowId = pressedTask.Id;
    }

    private void OnTaskDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragScope == DragScope.None || _draggedId is null) return;
        var hit = this.InputHitTest(e.GetPosition(this)) as Control;
        var targetClass = _dragScope switch
        {
            DragScope.Backlog => "backlog-row",
            DragScope.Projects => "project-row",
            DragScope.ProjectTasks => "project-task-row",
            _ => string.Empty,
        };
        var target = FindRow(hit, targetClass);
        if (_dragScope == DragScope.ProjectTasks
            && target?.DataContext is TaskRowViewModel task
            && task.ProjectId != _draggedProjectId)
            target = null;
        if (ReferenceEquals(target, _dragTarget)) return;
        _dragTarget?.Classes.Remove("drag-target");
        _dragTarget = target;
        _dragTarget?.Classes.Add("drag-target");
    }

    private void OnBacklogTaskPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not ShellViewModel { Work: { } work }
            || e.InitialPressMouseButton != MouseButton.Left)
        {
            ResetPointerGesture();
            return;
        }
        if (_dragScope == DragScope.None || _draggedId is null)
        {
            var pressedTaskRowId = _pressedTaskRowId;
            ResetPointerGesture();
            if (pressedTaskRowId is null) return;
            var hit = this.InputHitTest(e.GetPosition(this)) as Control;
            if (hit is Button || hit?.GetVisualAncestors().OfType<Button>().Any() == true) return;
            var row = (hit as Border)?.Classes.Contains("backlog-row") == true
                ? (Border?)hit
                : hit?.GetVisualAncestors().OfType<Border>().FirstOrDefault(border => border.Classes.Contains("backlog-row"));
            if (row?.DataContext is not TaskRowViewModel task || task.Id != pressedTaskRowId) return;
            task.SelectCommand.Execute(null);
            e.Handled = true;
            return;
        }
        var target = _dragTarget?.DataContext as TaskRowViewModel;
        var targetProject = _dragTarget?.DataContext as ProjectRowViewModel;
        _dragTarget?.Classes.Remove("drag-target");
        _dragTarget = null;
        var draggedId = _draggedId;
        var draggedProjectId = _draggedProjectId;
        var dragScope = _dragScope;
        ResetPointerGesture();
        if (dragScope == DragScope.Backlog && target is not null)
            work.DragTask(draggedId, target.Id);
        else if (dragScope == DragScope.Projects && targetProject is not null)
            work.DragProject(draggedId, targetProject.Id);
        else if (dragScope == DragScope.ProjectTasks && target is not null && draggedProjectId is not null)
            work.DragProjectTask(draggedProjectId, draggedId, target.Id);
        else return;
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => ResetPointerGesture();
    private void OnDeactivated(object? sender, EventArgs e) => ResetPointerGesture();

    private void ResetPointerGesture()
    {
        _dragTarget?.Classes.Remove("drag-target");
        _dragTarget = null;
        _dragScope = DragScope.None;
        _draggedId = null;
        _draggedProjectId = null;
        _pressedTaskRowId = null;
    }

    private static Border? FindRow(Control? hit, string rowClass) =>
        (hit as Border)?.Classes.Contains(rowClass) == true
            ? (Border?)hit
            : hit?.GetVisualAncestors().OfType<Border>().FirstOrDefault(border => border.Classes.Contains(rowClass));

    private void OnReorderMenuClosed(object? sender, EventArgs e)
    {
        if (sender is not FlyoutBase { Target: Button target }) return;
        var automationId = AutomationProperties.GetAutomationId(target);
        Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(candidate => AutomationProperties.GetAutomationId(candidate) == automationId)?.Focus(),
            DispatcherPriority.ApplicationIdle);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        Opened -= OnOpened;
        Activated -= OnActivated;
        Deactivated -= OnDeactivated;
        DataContextChanged -= OnDataContextChanged;
        ResetPointerGesture();
        _dateRefreshTimer.Stop();
        _dateRefreshTimer.Tick -= OnDateRefreshTick;
        if (_subscribedWork is not null) _subscribedWork.PropertyChanged -= OnWorkChanged;
        _session?.Dispose();
        _session = null;
    }
}
