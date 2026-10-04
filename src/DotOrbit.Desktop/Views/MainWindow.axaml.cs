using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.Clipboard;
using DotOrbit.Desktop.Markdown;
using DotOrbit.Desktop.ViewModels;

namespace DotOrbit.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private enum DragScope { None, TodayPlanned, TodayInProgress, Backlog, Projects, ProjectTasks, Categories }

    private IWorkspaceSession? _session;
    private bool _closingApproved;
    private DragScope _dragScope;
    private string? _draggedId;
    private string? _draggedProjectId;
    private Border? _pressedSelectableRow;
    private Border? _dragTarget;
    private bool _markdownPointerStartedOutside;
    private DateTime? _calendarDateAtOpen;
    private ProjectCaptureViewModel? _subscribedWork;
    private SettingsViewModel? _subscribedSettings;
    private readonly DispatcherTimer _dateRefreshTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly IInspectorAutosaveScheduler _autosaveScheduler;
    private readonly IMarkdownClipboard? _markdownClipboard;
    private readonly Action _workspaceUnavailable;

    public MainWindow()
        : this(null, null, null, null)
    {
    }

    internal MainWindow(IWorkspaceSession? session)
        : this(session, null, null, null)
    {
    }

    internal MainWindow(IWorkspaceSession? session, IMarkdownClipboard? markdownClipboard)
        : this(session, markdownClipboard, null, null)
    {
    }

    internal MainWindow(
        IWorkspaceSession? session,
        IMarkdownClipboard? markdownClipboard,
        IInspectorAutosaveScheduler? autosaveScheduler,
        Action? workspaceUnavailable = null)
    {
        _session = session;
        _markdownClipboard = markdownClipboard;
        _autosaveScheduler = autosaveScheduler ?? new DispatcherInspectorAutosaveScheduler();
        _workspaceUnavailable = () => HandleWorkspaceUnavailable(workspaceUnavailable);
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += OnDataContextChanged;
        AddHandler(PointerPressedEvent, OnWorkPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnWorkDragPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnWorkPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        this.FindControl<TextBox>("MarkdownSource")?.AddHandler(
            KeyDownEvent,
            OnMarkdownSourceKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
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
        if (this.FindControl<Button>("SettingsRecoveryButton") is { } recoveryButton)
            recoveryButton.IsVisible = session is not null;
        if (this.FindControl<Button>("SettingsPassphraseButton") is { } passphraseButton)
            passphraseButton.IsVisible = session is not null;
        Closed += OnClosed;
        Closing += OnClosing;
    }

    private void OnOpenRecovery(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var launcher = sender as Control;
        Navigate(() =>
        {
            var recovery = new RecoveryWindow(_session!.Recovery, ReplaceSession);
            recovery.Closed += (_, _) => Dispatcher.UIThread.Post(() => launcher?.Focus());
            recovery.Show(this);
        });
    }

    private void OnOpenPassphraseRotation(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var launcher = sender as Control;
        Navigate(() =>
        {
            var rotation = new PassphraseRotationWindow(
                _session!,
                ReplaceSession,
                _workspaceUnavailable);
            rotation.PassphraseChanged += OnPassphraseChanged;
            rotation.Closed += (_, _) => Dispatcher.UIThread.Post(() => launcher?.Focus());
            _ = rotation.ShowDialog(this);
        });
    }

    private void OnPassphraseChanged(object? sender, EventArgs e)
    {
        if (DataContext is not ShellViewModel shell || shell.Settings is null)
        {
            return;
        }

        shell.SettingsNavigation.SelectCommand.Execute(null);
        shell.Settings.ConfirmPassphraseChanged();
    }

    internal void HandleWorkspaceUnavailable(Action? showWorkspaceAccess = null)
    {
        if (showWorkspaceAccess is not null)
        {
            showWorkspaceAccess();
        }
        else
        {
            var access = new WorkspaceAccessWindow();
            if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = access;
            }

            access.Show();
        }

        _closingApproved = true;
        Close();
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
        _autosaveScheduler.Cancel();
        if (_subscribedWork is not null)
        {
            _subscribedWork.PropertyChanged -= OnWorkChanged;
            _subscribedWork.AutosaveRequested -= OnAutosaveRequested;
        }
        if (_subscribedSettings is not null)
            _subscribedSettings.PropertyChanged -= OnSettingsChanged;
        _subscribedWork = (DataContext as ShellViewModel)?.Work;
        _subscribedSettings = (DataContext as ShellViewModel)?.Settings;
        if (_subscribedWork is not null)
        {
            _subscribedWork.PropertyChanged += OnWorkChanged;
            _subscribedWork.AutosaveRequested += OnAutosaveRequested;
        }
        if (_subscribedSettings is not null)
            _subscribedSettings.PropertyChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.FocusAutomationId)
            && sender is SettingsViewModel { FocusAutomationId.Length: > 0 } settings)
            Dispatcher.UIThread.Post(() => FocusAutomationId(settings.FocusAutomationId), DispatcherPriority.ApplicationIdle);
    }

    private void OnAutosaveRequested(object? sender, AutosaveRequestEventArgs e)
    {
        if (sender is not ProjectCaptureViewModel work) return;
        _autosaveScheduler.Schedule(
            ProjectCaptureViewModel.AutosaveDelay,
            e.Revision,
            revision => work.RunScheduledAutosave(revision));
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        RefreshDatePresentation();
        _dateRefreshTimer.Start();
    }

    private void OnActivated(object? sender, EventArgs e) => RefreshDatePresentation();
    private void OnDateRefreshTick(object? sender, EventArgs e) => RefreshDatePresentation();
    private void RefreshDatePresentation() => (DataContext as ShellViewModel)?.Work?.RefreshDatePresentation();

    private void OnDateValidationError(object? sender, CalendarDatePickerDateValidationErrorEventArgs e)
    {
        e.ThrowException = false;
    }

    private void OnCalendarDateOpened(object? sender, EventArgs e)
    {
        if (sender is CalendarDatePicker picker) _calendarDateAtOpen = picker.SelectedDate;
    }

    private void OnCalendarDateCommitted(object? sender, EventArgs e)
    {
        if (sender is CalendarDatePicker picker
            && picker.SelectedDate != _calendarDateAtOpen
            && DataContext is ShellViewModel { Work: { } work })
            work.CommitCalendarDate(picker.SelectedDate);
        _calendarDateAtOpen = null;
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
        if (e.PropertyName == nameof(ProjectCaptureViewModel.DateValidationMessage)
            && sender is ProjectCaptureViewModel { HasDateValidationError: true })
            Dispatcher.UIThread.Post(
                () => this.FindControl<CalendarDatePicker>("DraftDate")?.Focus(),
                DispatcherPriority.ApplicationIdle);
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
        if (e.PropertyName == nameof(ProjectCaptureViewModel.TodayFocusAutomationId)
            && sender is ProjectCaptureViewModel { TodayFocusAutomationId.Length: > 0 } todayWork)
            Dispatcher.UIThread.Post(() => FocusAutomationId(todayWork.TodayFocusAutomationId), DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.DialogReturnFocusAutomationId)
            && sender is ProjectCaptureViewModel { DialogReturnFocusAutomationId.Length: > 0 } dialogWork)
            Dispatcher.UIThread.Post(() => FocusAutomationId(dialogWork.DialogReturnFocusAutomationId), DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.ParticipantFocusAutomationId)
            && sender is ProjectCaptureViewModel { ParticipantFocusAutomationId.Length: > 0 } participantWork)
            Dispatcher.UIThread.Post(() => FocusAutomationId(participantWork.ParticipantFocusAutomationId), DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.IsEditingMarkdown)
            && sender is ProjectCaptureViewModel { IsEditingMarkdown: true })
            Dispatcher.UIThread.Post(
                () => this.FindControl<TextBox>("MarkdownSource")?.Focus(),
                DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.NeedsArchiveConfirmation)
            && sender is ProjectCaptureViewModel archiveConfirmation)
            Dispatcher.UIThread.Post(() =>
            {
                if (archiveConfirmation.NeedsArchiveConfirmation)
                    this.FindControl<Button>("ConfirmArchiveTaskButton")?.Focus();
                else
                    FocusAutomationId(archiveConfirmation.DialogReturnFocusAutomationId);
            }, DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.ArchiveFocusAutomationId)
            && sender is ProjectCaptureViewModel { ArchiveFocusAutomationId.Length: > 0 } archiveWork)
            Dispatcher.UIThread.Post(() => FocusAutomationId(archiveWork.ArchiveFocusAutomationId), DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.NeedsCategoryReplacement)
            && sender is ProjectCaptureViewModel categoryWork)
            Dispatcher.UIThread.Post(() =>
            {
                if (categoryWork.NeedsCategoryReplacement) this.FindControl<ComboBox>("CategoryReplacement")?.Focus();
                else FocusAutomationId(categoryWork.DialogReturnFocusAutomationId);
            }, DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.NeedsAttachmentChoice)
            && sender is ProjectCaptureViewModel attachmentWork)
            Dispatcher.UIThread.Post(() =>
            {
                if (attachmentWork.NeedsAttachmentChoice) this.FindControl<Button>("PreserveTaskCategory")?.Focus();
                else this.FindControl<Button>("ChangeTaskContextButton")?.Focus();
            }, DispatcherPriority.ApplicationIdle);
    }

    private void FocusAutomationId(string automationId) => this.GetVisualDescendants().OfType<Control>()
        .FirstOrDefault(control => control.IsEffectivelyVisible
            && AutomationProperties.GetAutomationId(control) == automationId)?.Focus();

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
        else if (this.FindControl<Button>("NewProjectButton") is { IsEffectivelyVisible: true } projectButton) projectButton.Focus();
        else if (this.FindControl<Button>("NewTaskButton") is { IsEffectivelyVisible: true } taskButton) taskButton.Focus();
        else if (this.FindControl<Button>("NewCategoryButton") is { IsEffectivelyVisible: true } categoryButton) categoryButton.Focus();
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

    private void OnNewParticipantKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ShellViewModel { Work: { } work }) return;
        if (e.Key == Key.Escape)
        {
            work.NewParticipantLabel = string.Empty;
            work.ParticipantToAdd = null;
            e.Handled = true;
            Dispatcher.UIThread.Post(() => this.FindControl<ComboBox>("ParticipantPicker")?.Focus());
            return;
        }
        if (e.Key == Key.Enter)
        {
            work.AddNewParticipantCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnParticipantRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ParticipantSettingViewModel participant }) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            participant.SaveRenameCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            participant.CancelRenameCommand.Execute(null);
            Dispatcher.UIThread.Post(() => FocusAutomationId(participant.RenameAutomationId));
        }
    }

    private async void OnCopyRendered(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel { Work: { } work }) return;
        var clipboard = _markdownClipboard ?? (Clipboard is null ? null : new AvaloniaMarkdownClipboard(Clipboard));
        if (clipboard is null)
        {
            work.MarkdownCopyFailed();
            return;
        }

        try
        {
            await clipboard.WriteAsync(work.RenderedDescription);
            work.MarkdownCopySucceeded();
        }
        catch (Exception)
        {
            work.MarkdownCopyFailed();
        }
    }

    private void OnMarkdownPreviewActivated(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel { Work: { } work } && work.EditMarkdownCommand.CanExecute(null))
            work.EditMarkdownCommand.Execute(null);
    }

    private void OnMarkdownSourceLostFocus(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<TextBox>("MarkdownSource") is { IsKeyboardFocusWithin: false }
                && DataContext is ShellViewModel { Work: { IsEditingMarkdown: true } work })
                work.FinishMarkdownEditing();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void OnMarkdownSourceKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is TextBox editor) MarkdownSourceEditor.TryHandleKeyDown(editor, e);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape
            || DataContext is not ShellViewModel { Work: { NeedsArchiveConfirmation: true } work }) return;
        work.CancelArchiveTaskCommand.Execute(null);
        e.Handled = true;
    }

    private void OnWorkPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ResetPointerGesture();
        var hit = this.InputHitTest(e.GetPosition(this)) as Control;
        _markdownPointerStartedOutside = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && DataContext is ShellViewModel { Work.IsEditingMarkdown: true }
            && this.FindControl<TextBox>("MarkdownSource") is { } markdownSource
            && hit != markdownSource
            && hit?.GetVisualAncestors().Contains(markdownSource) != true;
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
        if (handle?.Classes.Contains("today-planned-drag-handle") == true && handle.DataContext is TodayTaskRowViewModel plannedTask)
        {
            _dragScope = DragScope.TodayPlanned;
            _draggedId = plannedTask.Task.Id;
            return;
        }
        if (handle?.Classes.Contains("today-in-progress-drag-handle") == true && handle.DataContext is TodayTaskRowViewModel activeTask)
        {
            _dragScope = DragScope.TodayInProgress;
            _draggedId = activeTask.Task.Id;
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
        if (handle?.Classes.Contains("category-drag-handle") == true && handle.DataContext is CategoryGroupViewModel category)
        {
            _dragScope = DragScope.Categories;
            _draggedId = category.Id;
            return;
        }
        if (hit is Button || hit?.GetVisualAncestors().OfType<Button>().Any() == true) return;
        _pressedSelectableRow = FindRow(hit, "selectable-row");
    }

    private void OnWorkDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragScope == DragScope.None || _draggedId is null) return;
        var hit = this.InputHitTest(e.GetPosition(this)) as Control;
        var targetClass = _dragScope switch
        {
            DragScope.Backlog => "backlog-row",
            DragScope.TodayPlanned => "today-planned-row",
            DragScope.TodayInProgress => "today-in-progress-row",
            DragScope.Projects => "project-row",
            DragScope.ProjectTasks => "project-task-row",
            DragScope.Categories => "category-row",
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

    private void OnWorkPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var finishMarkdownEditing = _markdownPointerStartedOutside
            && e.InitialPressMouseButton == MouseButton.Left;
        _markdownPointerStartedOutside = false;
        if (finishMarkdownEditing && DataContext is ShellViewModel { Work: { IsEditingMarkdown: true } markdownWork })
            markdownWork.FinishMarkdownEditing();

        if (DataContext is not ShellViewModel { Work: { } work }
            || e.InitialPressMouseButton != MouseButton.Left)
        {
            ResetPointerGesture();
            return;
        }
        if (_dragScope == DragScope.None || _draggedId is null)
        {
            var pressedSelectableRow = _pressedSelectableRow;
            ResetPointerGesture();
            if (pressedSelectableRow is null) return;
            var hit = this.InputHitTest(e.GetPosition(this)) as Control;
            if (hit is Button || hit?.GetVisualAncestors().OfType<Button>().Any() == true) return;
            var releasedSelectableRow = FindRow(hit, "selectable-row");
            if (!ReferenceEquals(releasedSelectableRow, pressedSelectableRow)
                || SelectionCommandForRow(releasedSelectableRow) is not { } selectionCommand)
                return;
            selectionCommand.Execute(null);
            e.Handled = true;
            return;
        }
        var target = _dragTarget?.DataContext as TaskRowViewModel;
        var targetToday = _dragTarget?.DataContext as TodayTaskRowViewModel;
        var targetProject = _dragTarget?.DataContext as ProjectRowViewModel;
        var targetCategory = _dragTarget?.DataContext as CategoryGroupViewModel;
        _dragTarget?.Classes.Remove("drag-target");
        _dragTarget = null;
        var draggedId = _draggedId;
        var draggedProjectId = _draggedProjectId;
        var dragScope = _dragScope;
        ResetPointerGesture();
        if (dragScope == DragScope.Backlog && target is not null)
            work.DragTask(draggedId, target.Id);
        else if ((dragScope == DragScope.TodayPlanned || dragScope == DragScope.TodayInProgress) && targetToday is not null)
            work.DragTodayTask(draggedId, targetToday.Task.Id);
        else if (dragScope == DragScope.Projects && targetProject is not null)
            work.DragProject(draggedId, targetProject.Id);
        else if (dragScope == DragScope.ProjectTasks && target is not null && draggedProjectId is not null)
            work.DragProjectTask(draggedProjectId, draggedId, target.Id);
        else if (dragScope == DragScope.Categories && targetCategory is not null)
            work.DragCategory(draggedId, targetCategory.Id);
        else return;
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _markdownPointerStartedOutside = false;
        ResetPointerGesture();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        _markdownPointerStartedOutside = false;
        ResetPointerGesture();
    }

    private void ResetPointerGesture()
    {
        _dragTarget?.Classes.Remove("drag-target");
        _dragTarget = null;
        _dragScope = DragScope.None;
        _draggedId = null;
        _draggedProjectId = null;
        _pressedSelectableRow = null;
    }

    private static Border? FindRow(Control? hit, string rowClass) =>
        (hit as Border)?.Classes.Contains(rowClass) == true
            ? (Border?)hit
            : hit?.GetVisualAncestors().OfType<Border>().FirstOrDefault(border => border.Classes.Contains(rowClass));

    private static RelayCommand? SelectionCommandForRow(Border? row) => row?.DataContext switch
    {
        TaskRowViewModel task => task.SelectCommand,
        CategoryTaskRowViewModel categoryTask => categoryTask.Task.SelectCommand,
        CompletedTaskRowViewModel completedTask => completedTask.Task.SelectCommand,
        ArchivedTaskRowViewModel archivedTask => archivedTask.Task.SelectCommand,
        UpcomingTaskRowViewModel upcomingTask => upcomingTask.Task.SelectCommand,
        TodayTaskRowViewModel todayTask => todayTask.Task.SelectCommand,
        ProjectRowViewModel project => project.SelectCommand,
        CategoryProjectRowViewModel categoryProject => categoryProject.Project.SelectCommand,
        CategoryGroupViewModel category => category.SelectCommand,
        _ => null,
    };

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
        _autosaveScheduler.Dispose();
        if (_subscribedWork is not null)
        {
            _subscribedWork.PropertyChanged -= OnWorkChanged;
            _subscribedWork.AutosaveRequested -= OnAutosaveRequested;
        }
        _session?.Dispose();
        _session = null;
    }
}
