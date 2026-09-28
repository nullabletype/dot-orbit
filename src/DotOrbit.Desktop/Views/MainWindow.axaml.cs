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
    private IWorkspaceSession? _session;
    private bool _closingApproved;
    private string? _draggedTaskId;
    private string? _pressedTaskRowId;
    private Border? _dragTarget;

    public MainWindow()
        : this(null)
    {
    }

    internal MainWindow(IWorkspaceSession? session)
    {
        _session = session;
        AvaloniaXamlLoader.Load(this);
        AddHandler(PointerPressedEvent, OnTaskDragHandlePointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnTaskDragPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnBacklogTaskPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
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
        if (DataContext is ShellViewModel { Work: { } oldWork }) oldWork.PropertyChanged -= OnWorkChanged;
        DataContext = new ShellViewModel(_session?.Work);
        if (DataContext is ShellViewModel { Work: { } work }) work.PropertyChanged += OnWorkChanged;
    }

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
        _pressedTaskRowId = null;
        var hit = this.InputHitTest(e.GetPosition(this)) as Control;
        Button? handle = (hit as Button)?.Classes.Contains("drag-handle") == true
            ? (Button?)hit
            : hit?.GetVisualAncestors().OfType<Button>().FirstOrDefault(button => button.Classes.Contains("drag-handle"));
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (handle?.DataContext is TaskRowViewModel draggedTask)
        {
            _draggedTaskId = draggedTask.Id;
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
        if (_draggedTaskId is null) return;
        var hit = this.InputHitTest(e.GetPosition(this)) as Control;
        var target = (hit as Border)?.Classes.Contains("backlog-row") == true
            ? (Border?)hit
            : hit?.GetVisualAncestors().OfType<Border>().FirstOrDefault(border => border.Classes.Contains("backlog-row"));
        if (ReferenceEquals(target, _dragTarget)) return;
        _dragTarget?.Classes.Remove("drag-target");
        _dragTarget = target;
        _dragTarget?.Classes.Add("drag-target");
    }

    private void OnBacklogTaskPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not ShellViewModel { Work: { } work }
            || e.InitialPressMouseButton != MouseButton.Left) return;
        if (_draggedTaskId is null)
        {
            var pressedTaskRowId = _pressedTaskRowId;
            _pressedTaskRowId = null;
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
        _dragTarget?.Classes.Remove("drag-target");
        _dragTarget = null;
        if (target is null) { _draggedTaskId = null; return; }
        var draggedTaskId = _draggedTaskId;
        _draggedTaskId = null;
        work.DragTask(draggedTaskId, target.Id);
        e.Handled = true;
    }

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
        _session?.Dispose();
        _session = null;
    }
}
