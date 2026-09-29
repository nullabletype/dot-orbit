using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class ProjectCaptureWindowTests
{
    [AvaloniaFact]
    public void ProjectsAndInspectorRetainWorkbenchHierarchyAndAccessibleActions()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", new DateOnly(2026, 10, 12));
        work.CreateTask(project.Id, "Plant bulbs");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(n => n.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var newProject = NamedButton(window, "New project");
        Assert.Contains("primary-action", newProject.Classes);
        var projectButton = NamedButton(window, "Garden");
        Assert.Contains("project-title", projectButton.Classes);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Not started");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "0/1 tasks");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "12 Oct 2026");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Home · inherited");

        var quickField = QuickField(window);
        Activate(window, NamedButton(window, "Collapse Garden"));
        Assert.False(quickField.IsEffectivelyVisible);
        Activate(window, NamedButton(window, "Expand Garden"));
        Assert.True(quickField.IsEffectivelyVisible);

        Activate(window, projectButton);
        var draftTitle = window.FindControl<TextBox>("DraftTitle")!;
        Assert.Contains("inspector-title", draftTitle.Classes);
        Assert.False(draftTitle.IsFocused);
        Assert.True(draftTitle.Focus());
        Assert.Equal(new Thickness(0, 9, 0, 0), draftTitle.Margin);
        var titleBorder = Assert.Single(draftTitle.GetVisualDescendants().OfType<Border>(), border => border.Name == "PART_BorderElement");
        Assert.Equal(new Thickness(2), titleBorder.BorderThickness);
        Assert.Contains("primary-action", NamedButton(window, "Save").Classes);
        Assert.Contains("view-action", NamedButton(window, "Cancel editing").Classes);
        window.Close();
    }

    [AvaloniaFact]
    public void ProjectButtonsCreateCancelEditAndResolveFailedNavigationThroughBindings()
    {
        using var session = new RecoveryViewModelTests.StubWorkspaceSession(new RecoveryViewModelTests.StubWorkspaceRecovery());
        var work = Assert.IsType<MemoryWorkspaceWork>(session.Work);
        var window = new MainWindow(session);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        Activate(window, NamedButton(window, "Open Projects"));
        Activate(window, NamedButton(window, "New project"));
        var title = window.FindControl<TextBox>("DraftTitle")!;
        Assert.True(title.IsFocused);
        window.KeyTextInput("Cancelled project");
        Assert.Empty(work.Read().Projects);
        Assert.Empty(shell.Work!.Projects);
        Activate(window, NamedButton(window, "Cancel editing"));
        Assert.Empty(work.Read().Projects);
        Assert.False(shell.Work.HasInspector);
        Assert.True(window.FindControl<Button>("NewProjectButton")!.IsFocused);

        Activate(window, NamedButton(window, "New project"));
        window.KeyTextInput("Garden");
        Activate(window, NamedButton(window, "Create"));
        Assert.Equal("Garden", Assert.Single(work.Read().Projects).Title);
        Assert.Equal("Garden", Assert.Single(shell.Work.Projects).Title);
        Assert.NotNull(NamedButton(window, "Garden"));
        Assert.Equal("1", shell.PrimaryNavigation.Single(n => n.Title == "Projects").CountText);

        Activate(window, NamedButton(window, "Garden"));
        Assert.False(title.IsFocused);
        Assert.True(title.Focus());
        title.SelectAll();
        window.KeyTextInput("Renamed garden");
        Assert.Equal("Garden", Assert.Single(work.Read().Projects).Title);
        Activate(window, NamedButton(window, "Save"));
        Assert.Equal("Renamed garden", Assert.Single(work.Read().Projects).Title);
        Assert.Equal("Renamed garden", Assert.Single(shell.Work.Projects).Title);

        title.Focus();
        title.SelectAll();
        window.KeyTextInput("Unsaved edit");
        Activate(window, NamedButton(window, "Open Backlog"));
        Assert.True(shell.Work.NeedsDecision);
        work.FailWrites = true;
        Activate(window, NamedButton(window, "Save changes and leave"));
        Assert.True(shell.Work.NeedsDecision);
        Assert.True(window.FindControl<Button>("GuardSave")!.IsFocused);
        Assert.Equal("Projects", shell.ViewTitle);
        Assert.Equal("Unsaved edit", title.Text);
        Assert.Equal("Renamed garden", Assert.Single(work.Read().Projects).Title);
        Activate(window, NamedButton(window, "Stay and keep editing"));
        Assert.False(shell.Work.NeedsDecision);
        Assert.True(title.IsFocused);
        Assert.Equal("Unsaved edit", title.Text);
        Activate(window, NamedButton(window, "Cancel editing"));
        Assert.True(title.IsFocused);
        Assert.Equal("Renamed garden", title.Text);
        Assert.Equal("Renamed garden", Assert.Single(work.Read().Projects).Title);
        Assert.False(shell.Work.IsDirty);
        window.Close();
    }

    [AvaloniaFact]
    public void DirtyNavigationRetainsCheckedViewUntilDecision()
    {
        using var session = new RecoveryViewModelTests.StubWorkspaceSession(new RecoveryViewModelTests.StubWorkspaceRecovery());
        var window = new MainWindow(session);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.Work!.NewProjectCommand.Execute(null);
        shell.Work.Title = "Unsaved";
        var radios = window.GetVisualDescendants().OfType<RadioButton>().ToArray();
        var today = radios.Single(r => AutomationProperties.GetAutomationId(r) == "navigation-today");
        var projects = radios.Single(r => AutomationProperties.GetAutomationId(r) == "navigation-projects");
        projects.Focus();
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(shell.Work.NeedsDecision);
        Assert.True(today.IsChecked);
        Assert.False(projects.IsChecked);
        shell.Work.StayCommand.Execute(null);
        Assert.True(today.IsChecked);
        projects.Focus();
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        shell.Work.DiscardAndLeaveCommand.Execute(null);
        Assert.True(projects.IsChecked);
        Assert.False(today.IsChecked);
        window.Close();
    }

    [AvaloniaFact]
    public void RapidCaptureKeysCreateOnceRetainFocusAndAllowEmptyTabExit()
    {
        var work = new MemoryWorkspaceWork();
        work.CreateProject("Garden", "", "home", null);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(n => n.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        var field = QuickField(window);
        Assert.Equal("Quick add task", AutomationProperties.GetName(field));
        Assert.True(field.Focus());
        window.KeyTextInput("First task");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        field = QuickField(window);
        Assert.True(field.IsFocused);
        Assert.Empty(field.Text ?? string.Empty);
        Assert.Equal("First task", Assert.Single(work.Read().Tasks).Title);
        window.KeyTextInput("Second task");
        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        field = QuickField(window);
        Assert.True(field.IsFocused);
        Assert.Equal(2, work.Read().Tasks.Count);
        window.KeyTextInput("Not submitted");
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Empty(field.Text ?? string.Empty);
        Assert.Equal(2, work.Read().Tasks.Count);
        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        Assert.False(field.IsFocused);
        Assert.Equal(2, work.Read().Tasks.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void ClosingDirtyCreationRequiresDecisionAndStayPreservesDraft()
    {
        using var session = new RecoveryViewModelTests.StubWorkspaceSession(new RecoveryViewModelTests.StubWorkspaceRecovery());
        var window = new MainWindow(session);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.Work!.NewProjectCommand.Execute(null);
        shell.Work.Title = "Unsaved";
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);
        Assert.True(shell.Work.NeedsDecision);
        Assert.True(window.FindControl<Button>("GuardSave")!.IsFocused);
        shell.Work.StayCommand.Execute(null);
        Assert.Equal("Unsaved", shell.Work.Title);
        Assert.True(window.IsVisible);
        window.Close();
        shell.Work.DiscardAndLeaveCommand.Execute(null);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void FailedQuickAddKeepsEnteredTitleAndFocus()
    {
        var work = new MemoryWorkspaceWork();
        work.CreateProject("Garden", "", "home", null);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(n => n.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        var field = QuickField(window);
        field.Focus();
        work.FailWrites = true;
        window.KeyTextInput("Keep me");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(work.Read().Tasks);
        Assert.True(shell.Work!.HasMessage);
        Assert.Equal("Keep me", field.Text);
        Assert.True(field.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void BacklogRapidEntryRequiresCategoryRetainsItAndImplementsTheKeyboardFlow()
    {
        var work = new MemoryWorkspaceWork();
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        var category = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(),
            combo => AutomationProperties.GetName(combo) == "Standalone task category");
        category.SelectedItem = shell.Work!.BacklogCategories.Single(choice => choice.Id == "home");
        var field = window.FindControl<TextBox>("BacklogQuickTitle")!;

        Assert.True(field.Focus());
        window.KeyTextInput("First standalone");
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(field.IsFocused);
        Assert.Empty(field.Text ?? string.Empty);
        Assert.Equal("home", shell.Work.BacklogQuickCategory?.Id);

        window.KeyTextInput("Second standalone");
        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(field.IsFocused);
        Assert.Equal(2, work.Read().Tasks.Count);

        window.KeyTextInput("Not submitted");
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Empty(field.Text ?? string.Empty);
        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        Assert.False(field.IsFocused);
        Assert.Equal(2, work.Read().Tasks.Count);
        Assert.All(work.Read().Tasks, task => Assert.Null(task.ProjectId));
        window.Close();
    }

    [AvaloniaFact]
    public void BacklogAccessibleMovePreservesFocusAnnouncesPositionAndPointerDragUsesTheSameOrder()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var movedTask = work.CreateTask(project.Id, "Same");
        var middleTask = work.CreateTask(project.Id, "Two");
        var otherSameTitle = work.CreateTask(project.Id, "Same");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var reorderMovedTask = ButtonByAutomationId(window, $"backlog-reorder-{movedTask.Id}");
        Activate(window, reorderMovedTask);
        var menu = Assert.IsType<MenuFlyout>(reorderMovedTask.Flyout);
        Assert.True(menu.IsOpen);
        var menuItems = menu.Items.OfType<MenuItem>().ToArray();
        Assert.Equal(
            ["Move Same to top of Backlog", "Move Same up in Backlog", "Move Same down in Backlog", "Move Same to bottom of Backlog"],
            menuItems.Select(AutomationProperties.GetName));
        var moveToTop = menuItems.Single(item => item.Header?.ToString() == "Move to top");
        moveToTop.Command!.Execute(moveToTop.CommandParameter);
        menu.Hide();
        Dispatcher.UIThread.RunJobs();
        Assert.True(ButtonByAutomationId(window, $"backlog-reorder-{movedTask.Id}").IsFocused);
        Assert.Equal([movedTask.Id, otherSameTitle.Id, middleTask.Id], shell.Work!.Backlog.Select(task => task.Id));
        Assert.Equal("Moved Same to position 1 of 3 in Backlog.", shell.Work.ReorderAnnouncement);

        var dragOne = ButtonByAutomationId(window, $"backlog-reorder-{movedTask.Id}");
        var target = NamedButton(window, "Two");
        var start = CentreInWindow(dragOne, window);
        var end = CentreInWindow(target, window);
        window.MouseDown(start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        var targetRow = target.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("backlog-row"));
        Assert.Contains("drag-target", targetRow.Classes);
        AssertInsertionLine(targetRow);
        window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        targetRow = NamedButton(window, "Two").GetVisualAncestors().OfType<Border>()
            .First(border => border.Classes.Contains("backlog-row"));
        AssertNoInsertionLine(targetRow, new Thickness(0, 0, 0, 1), Color.Parse("#1D222C"));
        Assert.Equal([otherSameTitle.Id, middleTask.Id, movedTask.Id], shell.Work.Backlog.Select(task => task.Id));
        Assert.Equal(shell.Work.Backlog.Select(task => task.Id), work.Read().Tasks.Select(task => task.Id));
        window.Close();
    }

    [AvaloniaFact]
    public void BacklogRowSurfaceSelectsTask()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        work.CreateTask(project.Id, "Plant bulbs");
        work.CreateTask(project.Id, "Order compost");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var rows = window.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("backlog-row"))
            .ToArray();
        var row = rows.Single(border => border.DataContext is TaskRowViewModel { Title: "Plant bulbs" });
        var otherRow = rows.Single(border => border.DataContext is TaskRowViewModel { Title: "Order compost" });
        var point = RowSurfacePoint(row, window);
        var otherPoint = RowSurfacePoint(otherRow, window);
        window.MouseDown(otherPoint, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Assert.False(shell.Work!.HasInspector);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(shell.Work.HasInspector);
        Assert.Equal("Plant bulbs", shell.Work.Title);
        window.Close();
    }

    [AvaloniaFact]
    public void BacklogHoverHighlightsTheWholeSelectableRowSurface()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTask(project.Id, "Plant bulbs");
        work.UpdateTask(task.Id, task.Title, "", null, new DateOnly(2026, 10, 2));
        var shell = new ShellViewModel(work, new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));
        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var row = Assert.Single(window.GetVisualDescendants().OfType<Border>(),
            border => border.Classes.Contains("backlog-row"));
        var title = NamedButton(window, "Plant bulbs");
        var handle = ButtonByAutomationId(window, $"backlog-reorder-{task.Id}");
        var toggle = ToggleByAutomationId(window, $"task-completion-{task.Id}");
        var metadata = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Home · inherited");
        var date = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "2 Oct 2026");
        AssertInteractiveRowHover(window, row, title, handle, toggle, metadata, date);
        window.Close();
    }

    [AvaloniaFact]
    public void ExpandedProjectTaskHoverHighlightsTheWholeRowAndKeepsTitleTransparent()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTask(project.Id, "Plant bulbs");
        work.UpdateTask(task.Id, task.Title, "", null, new DateOnly(2026, 10, 2));
        work.CreateTask(project.Id, "Order compost");
        var shell = new ShellViewModel(work, new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var rows = window.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("project-task-row"))
            .ToArray();
        var row = rows.Single(border => border.DataContext is TaskRowViewModel { Title: "Plant bulbs" });
        var last = rows.Single(border => border.DataContext is TaskRowViewModel { Title: "Order compost" });
        Assert.Equal(new Thickness(0, 0, 0, 1), row.BorderThickness);
        Assert.Equal(new Thickness(0), last.BorderThickness);
        var title = NamedButton(window, "Plant bulbs");
        var handle = ButtonByAutomationId(window, $"project-task-reorder-{task.Id}");
        var toggle = ToggleByAutomationId(window, $"task-completion-{task.Id}");
        var metadata = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Home · inherited");
        var date = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "2 Oct 2026");
        AssertInteractiveRowHover(window, row, title, handle, toggle, metadata, date);
        window.Close();
    }

    [AvaloniaFact]
    public void ProjectHeaderHoverFillsOnlyTheHeaderAndKeepsItsTitleTransparent()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", new DateOnly(2026, 10, 12));
        work.CreateTask(project.Id, "Plant bulbs");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var card = Assert.Single(window.GetVisualDescendants().OfType<Border>(),
            border => border.Classes.Contains("project-row"));
        var header = Assert.Single(card.GetVisualDescendants().OfType<Border>(),
            border => border.Classes.Contains("project-header-row"));
        var child = Assert.Single(card.GetVisualDescendants().OfType<Border>(),
            border => border.Classes.Contains("project-task-row"));
        Assert.Equal(card.Bounds.Width - card.BorderThickness.Left - card.BorderThickness.Right, header.Bounds.Width);
        var handle = ButtonByAutomationId(window, $"project-reorder-{project.Id}");
        var disclosure = Assert.Single(header.GetVisualDescendants().OfType<ToggleButton>(),
            button => button.Classes.Contains("disclosure"));
        var target = Assert.Single(header.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "12 Oct 2026");
        Assert.Equal(new CornerRadius(9, 9, 0, 0), header.CornerRadius);
        AssertInteractiveRowHover(window, header, NamedButton(window, "Garden"), handle, disclosure, target);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(child.Background).Color);
        shell.Work!.Projects.Single().IsExpanded = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new CornerRadius(9), header.CornerRadius);
        window.MouseMove(RowSurfacePoint(header, window), RawInputModifiers.None);
        Assert.Equal(Color.Parse("#151923"), Assert.IsAssignableFrom<ISolidColorBrush>(header.Background).Color);
        window.Close();
    }

    [AvaloniaFact]
    public void CompletedHoverFillsTheWholeRowAndKeepsItsTitleTransparent()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Filed receipt", "", "home", null);
        work.CompleteTask(task.Id);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Completed").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var row = Assert.Single(window.GetVisualDescendants().OfType<Border>(),
            border => border.Classes.Contains("completed-row"));
        var toggle = ToggleByAutomationId(window, $"task-completion-{task.Id}");
        var date = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Completed 29 Sep 2026");
        AssertInteractiveRowHover(window, row, NamedButton(window, "Filed receipt"), toggle, date);
        window.Close();
    }

    [AvaloniaFact]
    public void CompletedSeparatorsAppearOnlyBetweenRowsInEachGroupAfterRegroupAndReload()
    {
        var work = new MemoryWorkspaceWork();
        AddCompleted("Today newer", new DateOnly(2026, 9, 29), 14);
        AddCompleted("Today older", new DateOnly(2026, 9, 29), 9);
        AddCompleted("Yesterday newer", new DateOnly(2026, 9, 28), 16);
        AddCompleted("Yesterday older", new DateOnly(2026, 9, 28), 8);
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero));
        var shell = new ShellViewModel(work, time);
        shell.PrimaryNavigation.Single(item => item.Title == "Completed").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        Assert.Equal(["Today", "Yesterday"], shell.Work!.CompletedGroups.Select(group => group.Heading));
        AssertCompletedSeparators(window, expectedGroups: 2, expectedRowsPerGroup: 2);
        AssertCompletedPanelGeometry(window, expectedInterGroupSpacing: 12);
        time.Set(new DateTimeOffset(2026, 10, 2, 18, 0, 0, TimeSpan.Zero));
        Assert.True(shell.Work.RefreshDatePresentation());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Week of 28 Sep 2026"], shell.Work.CompletedGroups.Select(group => group.Heading));
        AssertCompletedSeparators(window, expectedGroups: 1, expectedRowsPerGroup: 4);
        AssertCompletedPanelGeometry(window, expectedInterGroupSpacing: null);
        window.Close();

        var reloadedShell = new ShellViewModel(work, time);
        reloadedShell.PrimaryNavigation.Single(item => item.Title == "Completed").SelectCommand.Execute(null);
        var reloadedWindow = new MainWindow { DataContext = reloadedShell };
        reloadedWindow.Show();
        Assert.Equal(["Week of 28 Sep 2026"], reloadedShell.Work!.CompletedGroups.Select(group => group.Heading));
        AssertCompletedSeparators(reloadedWindow, expectedGroups: 1, expectedRowsPerGroup: 4);
        AssertCompletedPanelGeometry(reloadedWindow, expectedInterGroupSpacing: null);
        reloadedWindow.Close();

        void AddCompleted(string title, DateOnly date, int hour)
        {
            var task = work.CreateStandaloneTask(title, "", "home", null);
            work.SetCompletion(task.Id, new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.Zero), date);
        }
    }

    [AvaloniaFact]
    public void BacklogListPanelUsesEqualOuterPadding()
    {
        var work = new MemoryWorkspaceWork();
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var panel = Assert.IsType<Border>(window.FindControl<Border>("BacklogListPanel"));

        Assert.Equal(new Thickness(10), panel.Padding);
        window.Close();
    }

    [AvaloniaFact]
    public void BacklogRowsExposeEffectiveCategoryAndRelationshipAsAccessibleText()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        work.CreateTask(project.Id, "Inherited task");
        var overridden = work.CreateTask(project.Id, "Overridden task");
        work.UpdateTask(overridden.Id, overridden.Title, "", "work", null);
        work.CreateStandaloneTask("Standalone task", "", "work", null);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        AssertCategoryMetadata("Inherited task", "Home · inherited");
        AssertCategoryMetadata("Overridden task", "Work · override");
        AssertCategoryMetadata("Standalone task", "Work · standalone");
        window.Close();

        void AssertCategoryMetadata(string title, string expected)
        {
            var row = Assert.Single(window.GetVisualDescendants().OfType<Border>(),
                border => border.Classes.Contains("backlog-row") && border.DataContext is TaskRowViewModel task && task.Title == title);
            var category = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == expected);
            Assert.True(category.IsEffectivelyVisible);
            var taskButton = Assert.Single(row.GetVisualDescendants().OfType<Button>(),
                button => button.Classes.Contains("backlog-task-title"));
            var peer = ControlAutomationPeer.CreatePeerForElement(taskButton);
            Assert.Equal(expected, peer.GetItemStatus());
        }
    }

    [AvaloniaFact]
    public void LastBacklogRowHasNoTrailingDivider()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        work.CreateTask(project.Id, "Plant bulbs");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var row = Assert.Single(window.GetVisualDescendants().OfType<Border>(),
            border => border.Classes.Contains("backlog-row"));
        Assert.Equal(0, row.BorderThickness.Bottom);
        window.Close();
    }

    [AvaloniaFact]
    public void AllReorderHandleScopesUseTheSameCentredSixDotGrid()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTask(project.Id, "Plant bulbs");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        AssertReorderHandleGeometry(ButtonByAutomationId(window, $"project-reorder-{project.Id}"), window);
        AssertReorderHandleGeometry(ButtonByAutomationId(window, $"project-task-reorder-{task.Id}"), window);
        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        AssertReorderHandleGeometry(ButtonByAutomationId(window, $"backlog-reorder-{task.Id}"), window);
        window.Close();
    }

    [AvaloniaFact]
    public void TaskInspectorMetadataLabelFitsItsColumn()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        work.CreateTask(project.Id, "Plant bulbs");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Activate(window, NamedButton(window, "Plant bulbs"));

        var label = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "CATEGORY BEHAVIOUR");
        Assert.True(label.Bounds.Width >= label.TextLayout.Width,
            $"Metadata label width {label.Bounds.Width} clips rendered text width {label.TextLayout.Width}.");
        window.Close();
    }

    [AvaloniaFact]
    public void SharedCompletionToggleIsKeyboardOperableExposesStateAndShowsCapturedDate()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTask(project.Id, "Plant bulbs");
        var shell = new ShellViewModel(work, new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var toggle = ToggleByAutomationId(window, $"task-completion-{task.Id}");
        Assert.Equal("Complete Plant bulbs", AutomationProperties.GetName(toggle));
        Assert.False(toggle.IsChecked);
        Assert.True(toggle.Focus());
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        toggle = ToggleByAutomationId(window, $"task-completion-{task.Id}");
        Assert.True(toggle.IsChecked);
        Assert.Equal("Reopen Plant bulbs", AutomationProperties.GetName(toggle));
        Assert.True(toggle.IsFocused);
        Activate(window, NamedButton(window, "Plant bulbs"));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Completed 29 Sep 2026");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Today");
        toggle.Focus();
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(work.Read().Tasks.Single().IsComplete);
        Assert.True(ToggleByAutomationId(window, $"task-completion-{task.Id}").IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void CompletionToggleUsesCentredRoundedSquareInsideItsHitTargetOnHoverAndChecked()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTask(project.Id, "Plant bulbs");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var toggle = ToggleByAutomationId(window, $"task-completion-{task.Id}");
        AssertCompletionToggleVisual(toggle, window, Colors.Transparent, Color.Parse("#9099A9"), tickVisible: false);
        window.MouseMove(CentreInWindow(toggle, window), RawInputModifiers.None);
        Assert.True(toggle.IsPointerOver);
        AssertCompletionToggleVisual(toggle, window, Color.Parse("#151923"), Color.Parse("#F2F4F7"), tickVisible: false);

        window.MouseMove(new Point(2, 2), RawInputModifiers.None);
        Assert.True(toggle.Focus());
        AssertCompletionFocusShadow(toggle, Color.Parse("#FF4FA3"));
        window.MouseMove(CentreInWindow(toggle, window), RawInputModifiers.None);
        AssertCompletionToggleVisual(toggle, window, Color.Parse("#151923"), Color.Parse("#F2F4F7"), tickVisible: false);
        AssertCompletionFocusShadow(toggle, Color.Parse("#FF4FA3"));

        window.MouseMove(new Point(2, 2), RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        toggle = ToggleByAutomationId(window, $"task-completion-{task.Id}");
        Assert.True(toggle.IsChecked);
        AssertCompletionToggleVisual(toggle, window, Color.Parse("#FF4FA3"), Color.Parse("#FF4FA3"), tickVisible: true);
        AssertCompletionFocusShadow(toggle, Color.Parse("#F2F4F7"));
        window.MouseMove(CentreInWindow(toggle, window), RawInputModifiers.None);
        AssertCompletionToggleVisual(toggle, window, Color.Parse("#C93678"), Color.Parse("#C93678"), tickVisible: true);
        AssertCompletionFocusShadow(toggle, Color.Parse("#F2F4F7"));
        window.Close();
    }

    [AvaloniaFact]
    public void ExpandedDisclosureKeepsReadableCheckedHoverAndFocusTreatment()
    {
        var work = new MemoryWorkspaceWork();
        work.CreateProject("Garden", "", "home", null);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var disclosure = Assert.Single(window.GetVisualDescendants().OfType<ToggleButton>(),
            button => button.Classes.Contains("disclosure"));
        Assert.True(disclosure.IsChecked);
        window.MouseMove(CentreInWindow(disclosure, window), RawInputModifiers.None);
        Assert.Equal(Color.Parse("#151923"), Assert.IsAssignableFrom<ISolidColorBrush>(disclosure.Background).Color);
        Assert.Equal(Color.Parse("#F2F4F7"), Assert.IsAssignableFrom<ISolidColorBrush>(disclosure.Foreground).Color);
        Assert.True(disclosure.Focus());
        Assert.Equal(Color.Parse("#FF4FA3"), Assert.IsAssignableFrom<ISolidColorBrush>(disclosure.BorderBrush).Color);
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Assert.False(disclosure.IsChecked);
        window.Close();
    }

    [AvaloniaFact]
    public void ReopeningAttachedTaskFromCompletedFocusesTheNextVisibleCompletedToggle()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var first = work.CreateTask(project.Id, "First");
        var second = work.CreateTask(project.Id, "Second");
        work.CompleteTask(first.Id);
        work.CompleteTask(second.Id);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Completed").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var reopening = ToggleByAutomationId(window, $"task-completion-{second.Id}");
        Assert.True(reopening.Focus());
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        var remaining = ToggleByAutomationId(window, $"task-completion-{first.Id}");
        Assert.True(remaining.IsEffectivelyVisible);
        Assert.True(remaining.IsFocused);
        Assert.False(work.Read().Tasks.Single(task => task.Id == second.Id).IsComplete);
        window.Close();
    }

    [AvaloniaFact]
    public void CompletedProjectionShowsPersistedDateAndReopenMovesFocusToTheNavigationItem()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Filed receipt", "", "home", null);
        work.CompleteTask(task.Id);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Completed").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Completed 29 Sep 2026");
        var toggle = ToggleByAutomationId(window, $"task-completion-{task.Id}");
        Assert.Equal("Reopen Filed receipt", AutomationProperties.GetName(toggle));
        Assert.True(toggle.Focus());
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(work.Read().Tasks.Single().IsComplete);
        Assert.Contains(shell.Work!.Backlog, row => row.Id == task.Id);
        Assert.True(window.GetVisualDescendants().OfType<RadioButton>()
            .Single(button => AutomationProperties.GetAutomationId(button) == "navigation-completed").IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void ProjectStatusRetainsTextAndUsesNeutralAmberAndGreenTreatments()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var first = work.CreateTask(project.Id, "First");
        var second = work.CreateTask(project.Id, "Second");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        AssertStatusColour(window, "Not started", "not-started", Color.Parse("#9099A9"));
        ToggleByAutomationId(window, $"task-completion-{first.Id}").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        AssertStatusColour(window, "In progress", "in-progress", Color.Parse("#FFCF70"));
        ToggleByAutomationId(window, $"task-completion-{second.Id}").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        AssertStatusColour(window, "Complete", "complete", Color.Parse("#7CE7B2"));
        window.Close();
    }

    [AvaloniaFact]
    public void OpenWindowRefreshesRelativeDatesAndOverdueStateAfterZoneChange()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", new DateOnly(2026, 9, 29));
        var task = work.CreateTask(project.Id, "Water");
        work.UpdateTask(task.Id, task.Title, "", null, new DateOnly(2026, 9, 30));
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 23, 30, 0, TimeSpan.Zero));
        var shell = new ShellViewModel(work, time);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.DataContext is TaskRowViewModel && text.Text == "Tomorrow");
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("UTC+02-window", TimeSpan.FromHours(2), "UTC+02", "UTC+02");
        time.Set(time.GetUtcNow(), plusTwo);
        Assert.True(shell.Work!.RefreshDatePresentation());
        Dispatcher.UIThread.RunJobs();

        Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.DataContext is TaskRowViewModel && text.Text == "Today");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "⚠ Overdue" && text.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void DateEditorRetainsManualEntryAndNativeCalendarKeyboardOpening()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTask(project.Id, "Plant bulbs");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Activate(window, NamedButton(window, "Plant bulbs"));

        var picker = Assert.Single(window.GetVisualDescendants().OfType<CalendarDatePicker>());
        Assert.Contains("Due date", AutomationProperties.GetName(picker), StringComparison.Ordinal);
        picker.Text = "2026-10-12";
        Activate(window, NamedButton(window, "Save"));
        Assert.Equal(new DateOnly(2026, 10, 12), work.Read().Tasks.Single().DueDate);

        picker.SelectedDate = new DateTime(2026, 10, 13);
        Assert.Equal("2026-10-13", shell.Work!.Date);
        Activate(window, NamedButton(window, "Save"));
        Assert.Equal(new DateOnly(2026, 10, 13), work.Read().Tasks.Single().DueDate);

        var pickerTextBox = Assert.Single(picker.GetVisualDescendants().OfType<TextBox>());
        Assert.True(pickerTextBox.Focus());
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt);
        window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.Alt);
        Dispatcher.UIThread.RunJobs();
        Assert.True(picker.IsDropDownOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void CompletingDirtyBacklogRowRequiresDecisionThenFocusesRemainingCompletionControl()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var remaining = work.CreateTask(project.Id, "Remaining");
        var completing = work.CreateTask(project.Id, "Completing");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Activate(window, NamedButton(window, "Completing"));
        var title = window.FindControl<TextBox>("DraftTitle")!;
        title.Text = "Unsaved completing";

        var completion = ToggleByAutomationId(window, $"task-completion-{completing.Id}");
        Assert.True(completion.Focus());
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(shell.Work!.NeedsDecision);
        Assert.True(window.FindControl<Button>("GuardSave")!.IsFocused);
        Activate(window, NamedButton(window, "Discard changes and leave"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(work.Read().Tasks.Single(task => task.Id == completing.Id).IsComplete);
        Assert.True(ToggleByAutomationId(window, $"task-completion-{remaining.Id}").IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void ProjectAndAttachedTaskReorderMenusAreScopedKeyboardActionsAndRestoreFocus()
    {
        var work = new MemoryWorkspaceWork();
        var firstProject = work.CreateProject("First", "", "home", null);
        var secondProject = work.CreateProject("Second", "", "home", null);
        var firstTask = work.CreateTask(firstProject.Id, "First task");
        var secondTask = work.CreateTask(firstProject.Id, "Second task");
        var otherTask = work.CreateTask(secondProject.Id, "Other task");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var projectHandle = ButtonByAutomationId(window, $"project-reorder-{secondProject.Id}");
        Activate(window, projectHandle);
        var projectMenu = Assert.IsType<MenuFlyout>(projectHandle.Flyout);
        var projectTop = projectMenu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "Move to top");
        Assert.Equal("Move Second to top of Projects", AutomationProperties.GetName(projectTop));
        projectTop.Command!.Execute(null);
        projectMenu.Hide();
        Dispatcher.UIThread.RunJobs();
        Assert.True(ButtonByAutomationId(window, $"project-reorder-{secondProject.Id}").IsFocused);
        Assert.Equal([secondProject.Id, firstProject.Id], work.Read().Projects.Select(project => project.Id));

        var taskHandle = ButtonByAutomationId(window, $"project-task-reorder-{secondTask.Id}");
        Activate(window, taskHandle);
        var taskMenu = Assert.IsType<MenuFlyout>(taskHandle.Flyout);
        var taskTop = taskMenu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "Move to top");
        Assert.Equal("Move Second task to top of project", AutomationProperties.GetName(taskTop));
        taskTop.Command!.Execute(null);
        taskMenu.Hide();
        Dispatcher.UIThread.RunJobs();
        Assert.True(ButtonByAutomationId(window, $"project-task-reorder-{secondTask.Id}").IsFocused);
        Assert.Equal([secondTask.Id, firstTask.Id], work.Read().Tasks.Where(task => task.ProjectId == firstProject.Id).OrderBy(task => task.ProjectPosition).Select(task => task.Id));
        Assert.Contains("position 1 of 2 in project First", shell.Work!.ReorderAnnouncement, StringComparison.Ordinal);

        projectHandle = ButtonByAutomationId(window, $"project-reorder-{secondProject.Id}");
        var projectTarget = ButtonByAutomationId(window, $"project-reorder-{firstProject.Id}");
        var projectTargetRow = projectTarget.GetVisualAncestors().OfType<Border>()
            .First(border => border.Classes.Contains("project-row"));
        DragToTarget(window, projectHandle, projectTarget);
        AssertInsertionLine(projectTargetRow);
        window.MouseUp(CentreInWindow(projectTarget, window), MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        projectTargetRow = ButtonByAutomationId(window, $"project-reorder-{firstProject.Id}")
            .GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("project-row"));
        AssertNoInsertionLine(projectTargetRow, new Thickness(1), Color.Parse("#272D39"));
        Assert.Equal([firstProject.Id, secondProject.Id], work.Read().Projects.Select(project => project.Id));
        Assert.True(ButtonByAutomationId(window, $"project-reorder-{secondProject.Id}").IsFocused);

        taskHandle = ButtonByAutomationId(window, $"project-task-reorder-{secondTask.Id}");
        var taskTarget = ButtonByAutomationId(window, $"project-task-reorder-{firstTask.Id}");
        var taskTargetRow = taskTarget.GetVisualAncestors().OfType<Border>()
            .First(border => border.Classes.Contains("project-task-row"));
        DragToTarget(window, taskHandle, taskTarget);
        AssertInsertionLine(taskTargetRow);
        window.MouseUp(CentreInWindow(taskTarget, window), MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        taskTargetRow = ButtonByAutomationId(window, $"project-task-reorder-{firstTask.Id}")
            .GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("project-task-row"));
        AssertNoInsertionLine(taskTargetRow, new Thickness(0, 0, 0, 1), Color.Parse("#1D222C"));
        Assert.Equal([firstTask.Id, secondTask.Id], work.Read().Tasks.Where(task => task.ProjectId == firstProject.Id)
            .OrderBy(task => task.ProjectPosition).Select(task => task.Id));
        Assert.True(ButtonByAutomationId(window, $"project-task-reorder-{secondTask.Id}").IsFocused);

        taskHandle = ButtonByAutomationId(window, $"project-task-reorder-{secondTask.Id}");
        var crossProjectTarget = ButtonByAutomationId(window, $"project-task-reorder-{otherTask.Id}");
        var before = work.Read().Tasks.Where(task => task.ProjectId == firstProject.Id)
            .OrderBy(task => task.ProjectPosition).Select(task => task.Id).ToArray();
        DragToTarget(window, taskHandle, crossProjectTarget);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Border>(),
            border => border.Classes.Contains("drag-target"));
        window.MouseUp(CentreInWindow(crossProjectTarget, window), MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before, work.Read().Tasks.Where(task => task.ProjectId == firstProject.Id)
            .OrderBy(task => task.ProjectPosition).Select(task => task.Id));
        window.Close();
    }

    [AvaloniaFact]
    public void NewPointerPressCancelsStaleProjectDragAndRemovesItsTargetHighlight()
    {
        var work = new MemoryWorkspaceWork();
        var first = work.CreateProject("First", "", "home", null);
        var second = work.CreateProject("Second", "", "home", null);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var source = ButtonByAutomationId(window, $"project-reorder-{first.Id}");
        var target = ButtonByAutomationId(window, $"project-reorder-{second.Id}");
        var targetRow = target.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("project-row"));
        var start = CentreInWindow(source, window);
        var end = CentreInWindow(target, window);
        window.MouseDown(start, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        Assert.Contains("drag-target", targetRow.Classes);
        AssertInsertionLine(targetRow);

        var cancellationTarget = NamedButton(window, "New project");
        window.MouseDown(CentreInWindow(cancellationTarget, window), MouseButton.Left, RawInputModifiers.LeftMouseButton);
        AssertNoInsertionLine(targetRow, new Thickness(1), Color.Parse("#272D39"));
        window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([first.Id, second.Id], work.Read().Projects.Select(project => project.Id));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Border>(), border => border.Classes.Contains("drag-target"));
        window.Close();
    }

    private static void AssertStatusColour(Window window, string status, string expectedClass, Color expectedColour)
    {
        var text = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), item => item.Text == status);
        Assert.Contains("project-status", text.Classes);
        Assert.Contains(expectedClass, text.Classes);
        Assert.Equal(expectedColour, Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);
    }

    private static void AssertInteractiveRowHover(Window window, Border row, Button title, params Control[] otherChildren)
    {
        Assert.Contains("interactive-row", row.Classes);
        Assert.Contains("row-title", title.Classes);
        window.MouseMove(RowSurfacePoint(row, window), RawInputModifiers.None);
        Assert.Equal(Color.Parse("#151923"), Assert.IsAssignableFrom<ISolidColorBrush>(row.Background).Color);

        window.MouseMove(CentreInWindow(title, window), RawInputModifiers.None);
        Assert.True(title.IsPointerOver);
        Assert.Equal(Color.Parse("#151923"), Assert.IsAssignableFrom<ISolidColorBrush>(row.Background).Color);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(title.Background).Color);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(title.BorderBrush).Color);
        var presenter = Assert.Single(title.GetVisualDescendants().OfType<ContentPresenter>());
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(presenter.BorderBrush).Color);

        foreach (var child in otherChildren)
        {
            window.MouseMove(CentreInWindow(child, window), RawInputModifiers.None);
            Assert.Equal(Color.Parse("#151923"), Assert.IsAssignableFrom<ISolidColorBrush>(row.Background).Color);
            if (child is Button handle && handle.Classes.Contains("drag-handle"))
            {
                Assert.Equal(Color.Parse("#151923"), Assert.IsAssignableFrom<ISolidColorBrush>(handle.Background).Color);
                Assert.Equal(Color.Parse("#414958"), Assert.IsAssignableFrom<ISolidColorBrush>(handle.BorderBrush).Color);
                Assert.Equal(Color.Parse("#F2F4F7"), Assert.IsAssignableFrom<ISolidColorBrush>(handle.Foreground).Color);
            }
            else if (child is ToggleButton completion && completion.Classes.Contains("completion-toggle"))
            {
                var expectedFill = completion.IsChecked == true ? Color.Parse("#C93678") : Color.Parse("#151923");
                var expectedBorder = completion.IsChecked == true ? Color.Parse("#C93678") : Color.Parse("#F2F4F7");
                AssertCompletionToggleVisual(completion, window, expectedFill, expectedBorder, completion.IsChecked == true);
            }
            else if (child is TextBlock metadata)
            {
                Assert.Null(metadata.Background);
            }
        }

        Assert.True(title.Focus(NavigationMethod.Tab));
        window.MouseMove(CentreInWindow(title, window), RawInputModifiers.None);
        Assert.Equal(Color.Parse("#FF4FA3"), Assert.IsAssignableFrom<ISolidColorBrush>(title.BorderBrush).Color);
        window.MouseMove(new Point(2, 2), RawInputModifiers.None);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(row.Background).Color);
        Assert.Equal(Color.Parse("#FF4FA3"), Assert.IsAssignableFrom<ISolidColorBrush>(title.BorderBrush).Color);
    }

    private static void AssertCompletedSeparators(Window window, int expectedGroups, int expectedRowsPerGroup)
    {
        var rows = window.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("completed-row"))
            .ToArray();
        Assert.Equal(expectedGroups * expectedRowsPerGroup, rows.Length);
        Assert.Equal(expectedGroups, rows.Count(row => Assert.IsType<CompletedTaskRowViewModel>(row.DataContext).IsLast));
        Assert.Equal(expectedGroups * (expectedRowsPerGroup - 1), rows.Count(row => !Assert.IsType<CompletedTaskRowViewModel>(row.DataContext).IsLast));
        Assert.All(rows, row =>
        {
            var presentation = Assert.IsType<CompletedTaskRowViewModel>(row.DataContext);
            Assert.Contains("separated-row", row.Classes);
            Assert.Equal(presentation.IsLast ? new Thickness(0) : new Thickness(0, 0, 0, 1), row.BorderThickness);
            Assert.Equal(Color.Parse("#1D222C"), Assert.IsAssignableFrom<ISolidColorBrush>(row.BorderBrush).Color);
        });
    }

    private static void AssertCompletedPanelGeometry(Window window, double? expectedInterGroupSpacing)
    {
        var panel = Assert.IsType<Border>(window.FindControl<Border>("CompletedListPanel"));
        Assert.Equal(new Thickness(10), panel.Padding);
        var rows = window.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("completed-row"))
            .ToArray();
        var finalRow = rows[^1];
        var panelOrigin = OriginInWindow(panel, window);
        var finalRowOrigin = OriginInWindow(finalRow, window);
        var leftInset = finalRowOrigin.X - panelOrigin.X - panel.BorderThickness.Left;
        var panelInnerBottom = panelOrigin.Y + panel.Bounds.Height - panel.BorderThickness.Bottom;
        var bottomInset = panelInnerBottom - finalRowOrigin.Y - finalRow.Bounds.Height;
        Assert.InRange(Math.Abs(leftInset - panel.Padding.Left), 0, 0.01);
        Assert.InRange(Math.Abs(bottomInset - panel.Padding.Bottom), 0, 0.01);

        var groups = window.GetVisualDescendants().OfType<StackPanel>()
            .Where(stack => stack.DataContext is CompletedTaskGroupViewModel
                && stack.Children.OfType<TextBlock>().Any(text => text.Classes.Contains("eyebrow")))
            .OrderBy(stack => OriginInWindow(stack, window).Y)
            .ToArray();
        Assert.All(groups, group => Assert.Equal(new Thickness(0), group.Margin));
        if (expectedInterGroupSpacing is { } spacing)
        {
            Assert.True(groups.Length > 1);
            for (var index = 1; index < groups.Length; index++)
            {
                var previousOrigin = OriginInWindow(groups[index - 1], window);
                var currentOrigin = OriginInWindow(groups[index], window);
                var gap = currentOrigin.Y - previousOrigin.Y - groups[index - 1].Bounds.Height;
                Assert.InRange(Math.Abs(gap - spacing), 0, 0.01);
            }
        }
        else
        {
            Assert.Single(groups);
        }
    }

    private static void AssertCompletionToggleVisual(
        ToggleButton toggle,
        Window window,
        Color expectedBackground,
        Color expectedBorder,
        bool tickVisible)
    {
        Assert.Equal(30, toggle.Bounds.Width);
        Assert.Equal(30, toggle.Bounds.Height);
        Assert.Equal(HorizontalAlignment.Center, toggle.HorizontalContentAlignment);
        Assert.Equal(VerticalAlignment.Center, toggle.VerticalContentAlignment);
        Assert.Empty(toggle.GetVisualDescendants().OfType<ContentPresenter>());
        var box = Assert.Single(toggle.GetVisualDescendants().OfType<Border>());
        Assert.Equal("CompletionBox", box.Name);
        Assert.Equal(22, box.Bounds.Width);
        Assert.Equal(22, box.Bounds.Height);
        Assert.Equal(new CornerRadius(6), box.CornerRadius);
        Assert.Equal(expectedBackground, Assert.IsAssignableFrom<ISolidColorBrush>(box.Background).Color);
        Assert.Equal(expectedBorder, Assert.IsAssignableFrom<ISolidColorBrush>(box.BorderBrush).Color);
        var tick = Assert.Single(toggle.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>());
        Assert.Equal("CompletionTick", tick.Name);
        Assert.Equal(tickVisible, tick.IsVisible);
        Assert.Null(tick.Fill);
        if (tickVisible)
        {
            Assert.Equal(Color.Parse("#090B10"), Assert.IsAssignableFrom<ISolidColorBrush>(tick.Stroke).Color);
            var boxCentre = CentreInWindow(box, window);
            var glyphCentre = CentreInWindow(tick, window);
            Assert.InRange(Math.Abs(boxCentre.X - glyphCentre.X), 0, 0.5);
            Assert.InRange(Math.Abs(boxCentre.Y - glyphCentre.Y), 0, 0.5);
        }
    }

    private static void AssertCompletionFocusShadow(ToggleButton toggle, Color expectedColour)
    {
        var box = Assert.Single(toggle.GetVisualDescendants().OfType<Border>());
        Assert.Equal(1, box.BoxShadow.Count);
        var shadow = box.BoxShadow[0];
        Assert.Equal(0d, shadow.OffsetX);
        Assert.Equal(0d, shadow.OffsetY);
        Assert.Equal(0d, shadow.Blur);
        Assert.Equal(2d, shadow.Spread);
        Assert.Equal(expectedColour, shadow.Color);
        Assert.False(shadow.IsInset);
    }

    private static void AssertInsertionLine(Border targetRow)
    {
        Assert.Contains("drag-target", targetRow.Classes);
        Assert.Equal(new Thickness(0, 0, 0, 2), targetRow.BorderThickness);
        Assert.Equal(Color.Parse("#FF4FA3"), Assert.IsAssignableFrom<ISolidColorBrush>(targetRow.BorderBrush).Color);
    }

    private static void AssertNoInsertionLine(Border targetRow, Thickness expectedThickness, Color expectedColour)
    {
        Assert.DoesNotContain("drag-target", targetRow.Classes);
        Assert.Equal(expectedThickness, targetRow.BorderThickness);
        Assert.Equal(expectedColour, Assert.IsAssignableFrom<ISolidColorBrush>(targetRow.BorderBrush).Color);
    }

    private static void AssertReorderHandleGeometry(Button handle, Window window)
    {
        Assert.True(handle.Bounds.Width >= 30);
        Assert.Equal(30, handle.Bounds.Height);
        var dots = Assert.IsType<Grid>(handle.Content);
        Assert.Equal(8, dots.Bounds.Width);
        Assert.Equal(12, dots.Bounds.Height);
        Assert.Equal(2, dots.ColumnDefinitions.Count);
        Assert.Equal(3, dots.RowDefinitions.Count);
        var handleCentre = CentreInWindow(handle, window);
        var dotsCentre = CentreInWindow(dots, window);
        Assert.InRange(Math.Abs(handleCentre.X - dotsCentre.X), 0, 0.5);
        Assert.InRange(Math.Abs(handleCentre.Y - dotsCentre.Y), 0, 0.5);
        var ellipses = dots.Children.OfType<Ellipse>().ToArray();
        Assert.Equal(6, ellipses.Length);
        Assert.All(ellipses, dot =>
        {
            Assert.Equal(2, dot.Bounds.Width);
            Assert.Equal(2, dot.Bounds.Height);
            Assert.Equal(
                Assert.IsAssignableFrom<ISolidColorBrush>(handle.Foreground).Color,
                Assert.IsAssignableFrom<ISolidColorBrush>(dot.Fill).Color);
        });
    }

    private static void Drag(Window window, Control source, Control target)
    {
        DragToTarget(window, source, target);
        window.MouseUp(CentreInWindow(target, window), MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void DragToTarget(Window window, Control source, Control target)
    {
        window.MouseDown(CentreInWindow(source, window), MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseMove(CentreInWindow(target, window), RawInputModifiers.LeftMouseButton);
    }

    private static Button NamedButton(Window window, string name) => Assert.Single(
        window.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == name);

    private static Button ButtonByAutomationId(Window window, string id) => Assert.Single(
        window.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == id);

    private static ToggleButton ToggleByAutomationId(Window window, string id) => Assert.Single(
        window.GetVisualDescendants().OfType<ToggleButton>(), button => AutomationProperties.GetAutomationId(button) == id);

    private static void Activate(Window window, Button button)
    {
        Assert.True(button.Focus());
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static TextBox QuickField(Window window) => Assert.Single(window.GetVisualDescendants().OfType<TextBox>(), b => b.Classes.Contains("quick-add"));

    private static Point CentreInWindow(Control control, Window window)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        Assert.True(point.HasValue);
        return point.Value;
    }

    private static Point OriginInWindow(Control control, Window window)
    {
        var point = control.TranslatePoint(default, window);
        Assert.True(point.HasValue);
        return point.Value;
    }

    private static Point RowSurfacePoint(Border row, Window window)
    {
        var point = row.TranslatePoint(new Point(row.Bounds.Width - 2, row.Bounds.Height / 2), window);
        Assert.True(point.HasValue);
        return point.Value;
    }
}
