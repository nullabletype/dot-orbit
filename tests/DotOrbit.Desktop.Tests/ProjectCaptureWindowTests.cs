using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class ProjectCaptureWindowTests
{
    [AvaloniaFact]
    public void RepresentativeProductionRowsRenderWithMappedIconsInDarkAndLightThemes()
    {
        var application = Assert.IsType<App>(Application.Current);
        var originalTheme = application.RequestedThemeVariant;
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var attached = work.CreateTask(project.Id, "Plant bulbs");
        work.SetTaskTodayLane(attached.Id, TodayLane.Planned);
        work.CreateStandaloneTask("Buy compost", "", "work", null);
        var archived = work.CreateStandaloneTask("Filed receipt", "", "home", null);
        work.CompleteTask(archived.Id);
        work.ArchiveTask(archived.Id);
        var binned = work.CreateStandaloneTask("Removed note", "", "work", null);
        work.MoveTaskToBin(binned.Id);
        var shell = new ShellViewModel(work);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        try
        {
            window.Show();
            foreach (var (theme, quiet) in new[]
            {
                (ThemeVariant.Dark, Color.Parse("#858E9D")),
                (ThemeVariant.Light, Color.Parse("#746870")),
            })
            {
                application.RequestedThemeVariant = theme;
                Dispatcher.UIThread.RunJobs();

                SelectView(shell, "Today");
                AssertRenderedWorkTypes(window, theme, quiet, WorkType.Task);
                SelectView(shell, "Projects");
                AssertRenderedWorkTypes(window, theme, quiet, WorkType.Project, WorkType.Task);
                SelectView(shell, "Categories");
                AssertRenderedWorkTypes(window, theme, quiet, WorkType.Category, WorkType.Project, WorkType.Task);
                SelectView(shell, "Archive");
                AssertRenderedWorkTypes(window, theme, quiet, WorkType.Task);
                window.FindControl<TextBox>("ArchiveSearchBox")!.Text = "receipt";
                Dispatcher.UIThread.RunJobs();
                AssertRenderedWorkTypes(window, theme, quiet, WorkType.Task);
                SelectView(shell, "Bin");
                AssertRenderedWorkTypes(window, theme, quiet, WorkType.Task);
                shell.Work!.SelectProject(project.Id);
                Dispatcher.UIThread.RunJobs();
                AssertWorkTypeIcon(
                    window.FindControl<TextBox>("DraftTitle")!.GetVisualAncestors().OfType<Grid>()
                        .First(grid => grid.GetVisualDescendants().OfType<WorkTypeIcon>().Any()),
                    WorkType.Project);
            }
        }
        finally
        {
            window.Close();
            application.RequestedThemeVariant = originalTheme;
        }
    }

    [AvaloniaFact]
    public void SettingsThemePickerIsNamedKeyboardReachableAndAppliesLightThemeImmediately()
    {
        var application = Assert.IsType<App>(Application.Current);
        var originalTheme = application.RequestedThemeVariant;
        application.RequestedThemeVariant = ThemeVariant.Dark;
        var themes = new TransientApplicationThemeService(ApplicationTheme.Dark, application);
        var shell = new ShellViewModel(new MemoryWorkspaceWork(), applicationThemeService: themes);
        var window = new MainWindow { DataContext = shell };
        try
        {
            window.Show();
            shell.SettingsNavigation.SelectCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var picker = window.FindControl<ComboBox>("SettingsThemePicker")!;
            Assert.True(picker.IsEffectivelyVisible);
            Assert.Equal("Colour theme", AutomationProperties.GetName(picker));
            Assert.Equal("settings-theme", AutomationProperties.GetAutomationId(picker));
            Assert.Equal("Dark theme selected", AutomationProperties.GetItemStatus(picker));
            Assert.True(picker.Focus(NavigationMethod.Tab));
            Assert.Equal(["Dark", "Light"], picker.Items.Cast<ApplicationThemeOption>().Select(option => option.Name));
            var themeRow = picker.GetVisualAncestors().OfType<Border>()
                .First(border => border.Classes.Contains("interactive-row"));
            Assert.Contains("separated-row", themeRow.Classes);
            Assert.Contains("last", themeRow.Classes);

            picker.SelectedItem = picker.Items.Cast<ApplicationThemeOption>()
                .Single(option => option.Value == ApplicationTheme.Light);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(ApplicationTheme.Light, themes.CurrentTheme);
            Assert.Equal(ThemeVariant.Light, application.RequestedThemeVariant);
            Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
            Assert.Equal("Light theme selected", AutomationProperties.GetItemStatus(picker));
            Assert.Equal("Light theme selected.", shell.Settings!.Announcement);
        }
        finally
        {
            window.Close();
            application.RequestedThemeVariant = originalTheme;
        }
    }

    [AvaloniaFact]
    public void MoveAndRestoreTaskBinActionsAreKeyboardOperableNamedAndReturnLogicalFocus()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Removed note", "", "home", null);
        var shell = new ShellViewModel(work);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        shell.Work!.SelectTask(task.Id);
        Dispatcher.UIThread.RunJobs();
        var move = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button =>
            AutomationProperties.GetAutomationId(button) == "task-move-to-bin");

        Assert.True(move.IsEffectivelyVisible);
        Assert.Equal("Move Task to Bin", AutomationProperties.GetName(move));
        Assert.Contains("restore", AutomationProperties.GetHelpText(move), StringComparison.OrdinalIgnoreCase);
        Assert.True(move.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.Work.HasInspector);
        var binNavigation = Assert.Single(window.GetVisualDescendants().OfType<RadioButton>(), button =>
            AutomationProperties.GetAutomationId(button) == "navigation-bin");
        Assert.True(binNavigation.IsKeyboardFocusWithin);
        shell.BinNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var restore = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button =>
            AutomationProperties.GetAutomationId(button) == $"bin-restore-task-{task.Id}");
        Assert.Equal("Restore Removed note from Bin", AutomationProperties.GetName(restore));
        Assert.Contains("nearest surviving former position", AutomationProperties.GetHelpText(restore), StringComparison.Ordinal);
        Assert.True(restore.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(binNavigation.IsKeyboardFocusWithin);
        Assert.Equal("Task restored from Bin.", shell.Work.Message);
        window.Close();
    }

    [AvaloniaFact]
    public void BinnedParentTaskRendersContextAndDisabledRestoreGuidance()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTask(project.Id, "Dig");
        work.MoveTaskToBin(task.Id);
        work.MarkProjectBinned(project.Id);
        var shell = new ShellViewModel(work);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        shell.BinNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var restore = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button =>
            AutomationProperties.GetAutomationId(button) == $"bin-restore-task-{task.Id}");
        var binRow = restore.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("bin-row"));
        AssertWorkTypeIcon(binRow, WorkType.Task);
        Assert.False(restore.IsEnabled);
        Assert.Contains("parent Project", AutomationProperties.GetHelpText(restore), StringComparison.Ordinal);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.IsEffectivelyVisible
            && text.Text == "Restore the parent Project from Bin before restoring this Task.");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.IsEffectivelyVisible && text.Text == "Garden");
        window.Close();
    }

    [AvaloniaFact]
    public void ProjectAggregateBinActionsAreKeyboardOperableNamedAndRestorable()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        work.CreateTask(project.Id, "Dig");
        var shell = new ShellViewModel(work);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        var binNavigation = Assert.Single(window.GetVisualDescendants().OfType<RadioButton>(), button =>
            AutomationProperties.GetAutomationId(button) == "navigation-bin");
        shell.Work!.SelectProject(project.Id);
        Dispatcher.UIThread.RunJobs();
        var move = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button =>
            AutomationProperties.GetAutomationId(button) == "project-move-to-bin");

        Assert.True(move.IsEffectivelyVisible);
        Assert.Equal("Move Project and its Tasks to Bin", AutomationProperties.GetName(move));
        Assert.Contains("recoverable aggregate", AutomationProperties.GetHelpText(move), StringComparison.Ordinal);
        Assert.True(move.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        shell.BinNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var restore = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button =>
            AutomationProperties.GetAutomationId(button) == $"bin-restore-project-{project.Id}");
        Assert.Equal("Restore Garden Project and its Tasks from Bin", AutomationProperties.GetName(restore));
        Assert.Contains("nearest surviving former positions", AutomationProperties.GetHelpText(restore), StringComparison.Ordinal);
        Assert.True(restore.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Project and its Tasks restored from Bin.", shell.Work.Message);
        Assert.True(binNavigation.IsKeyboardFocusWithin);
        window.Close();
    }

    [AvaloniaFact]
    public void EmptyBinConfirmationAnnouncesCaveatAndReturnsFocusAfterCancelOrSuccess()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        work.CreateTask(project.Id, "Dig");
        work.MoveProjectToBin(project.Id);
        var shell = new ShellViewModel(work);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        var binNavigation = Assert.Single(window.GetVisualDescendants().OfType<RadioButton>(), button =>
            AutomationProperties.GetAutomationId(button) == "navigation-bin");
        shell.BinNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var empty = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button =>
            AutomationProperties.GetAutomationId(button) == "empty-bin");

        Assert.True(empty.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        var confirm = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button =>
            AutomationProperties.GetAutomationId(button) == "empty-bin-confirm");
        var cancel = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button =>
            AutomationProperties.GetAutomationId(button) == "empty-bin-cancel");
        Assert.True(confirm.IsKeyboardFocusWithin);
        Assert.Contains("validated encrypted recovery point", AutomationProperties.GetName(confirm), StringComparison.Ordinal);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.IsEffectivelyVisible && text.Text?.Contains("1 Project and 1 Task", StringComparison.Ordinal) == true
            && text.Text.Contains("not forensic erasure", StringComparison.Ordinal));
        Assert.True(cancel.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(empty.IsKeyboardFocusWithin);
        Assert.Single(shell.Work!.Bin);

        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        confirm = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button =>
            AutomationProperties.GetAutomationId(button) == "empty-bin-confirm");
        Assert.True(confirm.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(binNavigation.IsKeyboardFocusWithin);
        Assert.False(empty.IsEffectivelyVisible);
        Assert.Empty(shell.Work.Bin);
        window.Close();
    }

    [AvaloniaFact]
    public void EmptyBinFailureIsAnnouncedPreservesRowsAndReturnsFocus()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Keep", "", "home", null);
        work.MoveTaskToBin(task.Id);
        var shell = new ShellViewModel(work);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        shell.BinNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var empty = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button =>
            AutomationProperties.GetAutomationId(button) == "empty-bin");
        Assert.True(empty.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        work.FailWrites = true;
        var confirm = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button =>
            AutomationProperties.GetAutomationId(button) == "empty-bin-confirm");
        Assert.True(confirm.Focus());

        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        var status = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("TopBarStatusMessage"));
        Assert.Equal("Could not empty Bin. Nothing was deleted.", status.Text);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
        Assert.True(status.IsEffectivelyVisible);
        Assert.True(empty.IsKeyboardFocusWithin);
        Assert.Equal(task.Id, Assert.IsType<TaskRecord>(Assert.Single(shell.Work!.Bin).Task).Id);
        window.Close();
    }

    [AvaloniaFact]
    public void CanvasViewsReserveConsistentBottomScrollClearance()
    {
        var shell = new ShellViewModel(new MemoryWorkspaceWork());
        var window = new MainWindow { DataContext = shell, Width = 1200, Height = 760 };
        window.Show();

        var bottomInsets = new List<(string View, double Bottom)>();
        foreach (var navigation in shell.PrimaryNavigation.Append(shell.BinNavigation))
        {
            navigation.SelectCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();

            var content = Assert.Single(window.GetVisualDescendants().OfType<ScrollViewer>(), scroll =>
                scroll.IsEffectivelyVisible
                && scroll.Content is StackPanel panel
                && panel.Margin.Right == 32).Content;
            bottomInsets.Add((navigation.Title, Assert.IsType<StackPanel>(content).Margin.Bottom));
        }

        shell.SettingsNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var settingsContent = Assert.Single(window.GetVisualDescendants().OfType<ScrollViewer>(), scroll =>
            scroll.IsEffectivelyVisible
            && scroll.Content is StackPanel panel
            && panel.Margin.Right == 32).Content;
        bottomInsets.Add(("Settings", Assert.IsType<StackPanel>(settingsContent).Margin.Bottom));

        Assert.Equal(["Today", "Upcoming", "Backlog", "Projects", "Categories", "Completed", "Archive", "Bin", "Settings"],
            bottomInsets.Select(inset => inset.View));
        Assert.All(bottomInsets, inset => Assert.Equal(16, inset.Bottom));
        window.Close();
    }

    [AvaloniaFact]
    public void PrimaryWorkViewsHaveTheSameRenderedBottomClearance()
    {
        var work = new MemoryWorkspaceWork();
        var today = new DateOnly(2026, 10, 4);
        for (var index = 0; index < 12; index++)
        {
            var completed = work.CreateStandaloneTask($"Completed {index}", "", "home", null);
            work.SetCompletion(completed.Id,
                new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero), today);
            var archived = work.CreateStandaloneTask($"Archived {index}", "", "home", null);
            work.SetCompletion(archived.Id,
                new DateTimeOffset(today.AddDays(-1).ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero), today.AddDays(-1));
            work.SetArchive(archived.Id,
                new DateTimeOffset(today.ToDateTime(new TimeOnly(13, 0)), TimeSpan.Zero), today);
            work.CreateStandaloneTask($"Upcoming {index}", "", "home", today);
            work.CreateProject($"Project {index}", "", "home", null);
        }
        for (var index = 0; index < 9; index++) work.CreateCategory($"Category {index}");

        var shell = new ShellViewModel(work,
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 4, 18, 0, 0, TimeSpan.Zero)));
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();

        var clearances = new[]
        {
            Measure("Today", () => window.FindControl<Border>("CompletedTodayPanel")!),
            Measure("Upcoming", () => window.GetVisualDescendants().OfType<Border>()
                .Last(border => border.Classes.Contains("list-panel")
                    && border.DataContext is UpcomingTaskGroupViewModel)),
            Measure("Backlog", () => window.FindControl<Border>("BacklogListPanel")!),
            Measure("Projects", () => window.GetVisualDescendants().OfType<Border>()
                .Last(border => border.Classes.Contains("project-row"))),
            Measure("Categories", () => window.GetVisualDescendants().OfType<Border>()
                .Last(border => border.Classes.Contains("category-row"))),
            Measure("Completed", () => window.FindControl<Border>("CompletedListPanel")!),
            Measure("Archive", () => window.GetVisualDescendants().OfType<Border>()
                .Last(border => border.Classes.Contains("list-panel")
                    && border.DataContext is ArchivedWorkGroupViewModel)),
        };

        Assert.All(clearances, item => Assert.InRange(item.Clearance, 15.99, 16.01));
        Assert.InRange(clearances.Max(item => item.Clearance) - clearances.Min(item => item.Clearance), 0, 0.01);
        window.Close();

        (string View, double Clearance) Measure(string view, Func<Border> panel)
        {
            shell.PrimaryNavigation.Single(item => item.Title == view).SelectCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            return (view, BottomClearance(window, panel()));
        }

        static double BottomClearance(Window window, Border panel)
        {
            var viewer = Assert.Single(window.GetVisualDescendants().OfType<ScrollViewer>(), scroll =>
                scroll.IsEffectivelyVisible
                && scroll.Content is StackPanel content
                && content.Margin.Right == 32);
            viewer.Offset = new Vector(0, viewer.Extent.Height);
            Dispatcher.UIThread.RunJobs();
            var viewportOrigin = viewer.TranslatePoint(default, window)!.Value;
            var panelOrigin = panel.TranslatePoint(default, window)!.Value;
            return viewportOrigin.Y + viewer.Bounds.Height - panelOrigin.Y - panel.Bounds.Height;
        }
    }

    [AvaloniaFact]
    public void UpcomingShowsBadgeDateGroupsSharedRowActionsAndNoReorderAffordance()
    {
        var work = new MemoryWorkspaceWork();
        var today = new DateOnly(2026, 10, 4);
        var overdue = work.CreateStandaloneTask("Overdue task", "", "home", today.AddDays(-1));
        var dueToday = work.CreateStandaloneTask("Due today", "", "work", today);
        var shell = new ShellViewModel(work,
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero)));
        var upcomingNavigation = shell.PrimaryNavigation.Single(item => item.Title == "Upcoming");
        upcomingNavigation.SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell, Width = 1200, Height = 760 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("2", upcomingNavigation.CountText);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Overdue");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Today");
        var upcomingGroups = window.FindControl<ItemsControl>("UpcomingGroups")!;
        var groupStacks = upcomingGroups.GetVisualDescendants().OfType<StackPanel>()
            .Where(panel => panel.DataContext is UpcomingTaskGroupViewModel
                && panel.Children.OfType<TextBlock>().Any(text => text.Classes.Contains("eyebrow"))).ToArray();
        Assert.Equal(2, groupStacks.Length);
        Assert.All(groupStacks, panel => Assert.Equal(default, panel.Margin));
        Assert.Contains(upcomingGroups.GetVisualDescendants().OfType<StackPanel>(), panel => panel.Spacing == 8);
        var navigation = ToggleByAutomationId(window, "navigation-upcoming");
        Assert.Equal("2 items", AutomationProperties.GetItemStatus(navigation));
        var overdueRow = RowForTask(window, "upcoming-row", overdue.Id);
        var todayRow = RowForTask(window, "upcoming-row", dueToday.Id);
        AssertIncompleteTaskRowContract(window, overdueRow, overdue.Id, "Overdue task", "Standalone · Home", "3 Oct 2026");
        AssertIncompleteTaskRowContract(window, todayRow, dueToday.Id, "Due today", "Standalone · Work", "Today");
        Assert.DoesNotContain(overdueRow.GetVisualDescendants().OfType<Button>(),
            button => button.Classes.Contains("drag-handle"));
        Assert.DoesNotContain("reorder-target", overdueRow.Classes);

        ClickTaskRowSurface(window, "upcoming-row", "Overdue task");
        Assert.True(shell.Work!.HasInspector);
        Assert.Equal("Overdue task", shell.Work.Title);
        window.Close();
    }

    [AvaloniaFact]
    public void UpcomingCompletionAndAddToTodayAreKeyboardOperableAndPreserveLogicalFocusAndOrder()
    {
        var work = new MemoryWorkspaceWork();
        var today = new DateOnly(2026, 10, 4);
        var first = work.CreateStandaloneTask("First", "", "home", today);
        var second = work.CreateStandaloneTask("Second", "", "home", today.AddDays(1));
        var originalOrder = work.Read().Tasks.Select(task => (task.Id, task.SharedPosition)).ToArray();
        var shell = new ShellViewModel(work,
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero)));
        shell.PrimaryNavigation.Single(item => item.Title == "Upcoming").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell, Width = 1200, Height = 760 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var completion = ToggleByAutomationId(window, $"task-completion-{first.Id}");
        Assert.Equal("Complete First", AutomationProperties.GetName(completion));
        Assert.True(completion.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(work.Read().Tasks.Single(task => task.Id == first.Id).IsComplete);
        Assert.True(ToggleByAutomationId(window, $"task-completion-{second.Id}").IsFocused);
        Assert.Equal("1", shell.PrimaryNavigation.Single(item => item.Title == "Upcoming").CountText);
        Assert.Equal("1 item", AutomationProperties.GetItemStatus(ToggleByAutomationId(window, "navigation-upcoming")));

        var todayToggle = ToggleByAutomationId(window, $"task-today-{second.Id}");
        Assert.Equal("Add Second to Today", AutomationProperties.GetName(todayToggle));
        Assert.True(todayToggle.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(TodayLane.Planned, work.Read().Tasks.Single(task => task.Id == second.Id).TodayLane);
        Assert.True(ToggleByAutomationId(window, $"task-today-{second.Id}").IsFocused);
        Assert.Equal(originalOrder, work.Read().Tasks.Select(task => (task.Id, task.SharedPosition)));

        completion = ToggleByAutomationId(window, $"task-completion-{second.Id}");
        Assert.True(completion.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(ToggleByAutomationId(window, "navigation-upcoming").IsFocused);
        Assert.Equal("0", shell.PrimaryNavigation.Single(item => item.Title == "Upcoming").CountText);
        Assert.Equal("0 items", AutomationProperties.GetItemStatus(ToggleByAutomationId(window, "navigation-upcoming")));
        window.Close();
    }

    [AvaloniaFact]
    public void UpcomingDueDateEditImmediatelyMovesTheTaskOutOfTheProjection()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Move me", "", "home", new DateOnly(2026, 10, 5));
        var originalPosition = task.SharedPosition;
        var shell = new ShellViewModel(work,
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero)));
        shell.PrimaryNavigation.Single(item => item.Title == "Upcoming").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell, Width = 1200, Height = 760 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        ClickTaskRowSurface(window, "upcoming-row", "Move me");
        shell.Work!.Date = "2026-10-06";
        Assert.True(shell.Work.RunScheduledAutosave());
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Tuesday, 6 October 2026");
        Assert.Single(window.GetVisualDescendants().OfType<Border>(), row => row.Classes.Contains("upcoming-row"));

        shell.Work.Date = "2026-10-12";
        Assert.True(shell.Work.RunScheduledAutosave());
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Border>(), row => row.Classes.Contains("upcoming-row"));
        Assert.True(shell.Work.HasNoUpcoming);
        Assert.Equal("0", shell.PrimaryNavigation.Single(item => item.Title == "Upcoming").CountText);
        var saved = work.Read().Tasks.Single(item => item.Id == task.Id);
        Assert.Equal(new DateOnly(2026, 10, 12), saved.DueDate);
        Assert.Equal(originalPosition, saved.SharedPosition);
        window.Close();
    }

    [AvaloniaFact]
    public void TodayStarLaneMovementAndClearAreKeyboardOperableAndAccessible()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("File receipt", "", "home", null);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell, Width = 1200, Height = 760 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var star = Assert.IsType<ToggleButton>(ButtonByAutomationId(window, $"task-today-{task.Id}"));
        Assert.False(star.IsChecked);
        Assert.Equal("Add File receipt to Today", AutomationProperties.GetName(star));
        Assert.True(star.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(star.IsChecked);
        Assert.Equal("Remove File receipt from Today", AutomationProperties.GetName(star));
        Assert.Equal(TodayLane.Planned, work.Read().Tasks.Single().TodayLane);

        shell.PrimaryNavigation.Single(item => item.Title == "Today").SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        AssertWorkTypeIcon(
            Assert.Single(window.GetVisualDescendants().OfType<Border>(), row => row.Classes.Contains("today-planned-row")),
            WorkType.Task);
        var start = NamedButton(window, "Move File receipt to In progress");
        Assert.True(start.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(TodayLane.InProgress, work.Read().Tasks.Single().TodayLane);
        Assert.Single(window.GetVisualDescendants().OfType<Border>(), row => row.Classes.Contains("today-in-progress-row"));
        Assert.True(window.GetVisualDescendants().OfType<ToggleButton>().Single(control =>
            AutomationProperties.GetAutomationId(control) == $"task-today-{task.Id}" && control.IsEffectivelyVisible).IsFocused);
        Assert.Equal("Moved File receipt to In progress.", shell.Work!.TodayAnnouncement);

        var clear = window.FindControl<Button>("TodayClearButton")!;
        Assert.Equal("Clear incomplete Tasks from Today", AutomationProperties.GetName(clear));
        Assert.True(clear.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(work.Read().Tasks.Single().TodayLane);
        Assert.True(shell.Work!.HasNoTodayTasks);
        Assert.True(clear.IsKeyboardFocusWithin);
        window.Close();
    }

    [AvaloniaFact]
    public void TodayCreationAndReorderAlternativesExposeContextHelpFocusAnnouncementAndDrag()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var first = work.CreateTask(project.Id, "First");
        var second = work.CreateTask(project.Id, "Second");
        work.SetTaskTodayLane(first.Id, TodayLane.Planned);
        work.SetTaskTodayLane(second.Id, TodayLane.Planned);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Today").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell, Width = 1200, Height = 760 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var create = window.FindControl<Button>("NewTodayTaskButton")!;
        Assert.Equal("New task in Today", AutomationProperties.GetName(create));
        Assert.True(create.IsVisible);

        var handle = ButtonByAutomationId(window, $"today-reorder-{second.Id}");
        Assert.Equal("Drag within Planned, or activate for keyboard reorder actions.", AutomationProperties.GetHelpText(handle));
        Activate(window, handle);
        var menu = Assert.IsType<MenuFlyout>(handle.Flyout);
        var items = menu.Items.OfType<MenuItem>().ToArray();
        Assert.Equal(4, items.Length);
        var moveToTop = items.Single(item => item.Header?.ToString() == "Move to top");
        Assert.Equal("Move Second to top of Planned", AutomationProperties.GetName(moveToTop));
        moveToTop.Command!.Execute(null);
        menu.Hide();
        Dispatcher.UIThread.RunJobs();
        Assert.True(ButtonByAutomationId(window, $"today-reorder-{second.Id}").IsFocused);
        Assert.Equal([second.Id, first.Id], shell.Work!.TodayPlanned.Select(row => row.Task.Id));
        Assert.Equal("Moved Second to position 1 of 2 in Planned.", shell.Work.TodayAnnouncement);

        handle = ButtonByAutomationId(window, $"today-reorder-{second.Id}");
        var target = ButtonByAutomationId(window, $"today-reorder-{first.Id}");
        DragToTarget(window, handle, target);
        window.MouseUp(CentreInWindow(target, window), MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([first.Id, second.Id], shell.Work.TodayPlanned.Select(row => row.Task.Id));
        Assert.True(ButtonByAutomationId(window, $"today-reorder-{second.Id}").IsFocused);
        Assert.Equal("Moved Second to position 2 of 2 in Planned.", shell.Work.TodayAnnouncement);
        window.Close();
    }

    [AvaloniaFact]
    public void CompletedTodayMarksOnlyItsFinalRowAsLast()
    {
        var work = new MemoryWorkspaceWork();
        var first = work.CreateStandaloneTask("First", "", "home", null);
        var second = work.CreateStandaloneTask("Second", "", "home", null);
        work.SetCompletion(first.Id, new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 29));
        work.SetCompletion(second.Id, new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 29));
        var shell = new ShellViewModel(work, new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));
        shell.PrimaryNavigation.Single(item => item.Title == "Today").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var rows = window.GetVisualDescendants().OfType<Border>()
            .Where(row => row.Classes.Contains("completed-today-row")).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Single(rows, row => row.Classes.Contains("last"));
        Assert.Contains("last", rows[^1].Classes);
        window.Close();
    }

    [AvaloniaFact]
    public void IncompleteTaskRowsKeepCompletionLeadingDateRightAndTodayTrailingAcrossViews()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var attached = work.CreateTask(project.Id, "Plant bulbs");
        var standalone = work.CreateStandaloneTask("File receipt", "", "home", new DateOnly(2026, 9, 30));
        work.UpdateTask(attached.Id, attached.Title, attached.Description, attached.ExplicitCategoryId,
            new DateOnly(2026, 9, 30));
        work.SetTaskTodayLane(attached.Id, TodayLane.Planned);
        work.SetTaskTodayLane(standalone.Id, TodayLane.Planned);
        var shell = new ShellViewModel(work,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));
        var window = new MainWindow { DataContext = shell, Width = 1200, Height = 760 };
        window.Show();

        AssertIncompleteTaskRowContract(window,
            RowForTask(window, "today-planned-row", attached.Id), attached.Id, "Plant bulbs", "Garden", "Tomorrow");

        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        AssertIncompleteTaskRowContract(window,
            RowForTask(window, "backlog-row", attached.Id), attached.Id, "Plant bulbs", "Garden", "Tomorrow");

        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        AssertIncompleteTaskRowContract(window,
            RowForTask(window, "project-task-row", attached.Id), attached.Id, "Plant bulbs", "Garden", "Tomorrow");

        shell.PrimaryNavigation.Single(item => item.Title == "Categories").SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        AssertIncompleteTaskRowContract(window,
            RowForTask(window, "category-task-row", standalone.Id), standalone.Id, "File receipt", "Standalone", "Tomorrow");
        window.Close();
    }

    [AvaloniaFact]
    public void CompletedCategoryTaskShowsCompletionDateInTheSharedRightMetadataColumn()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("File receipt", "", "home", new DateOnly(2026, 9, 30));
        work.CompleteTask(task.Id);
        var shell = new ShellViewModel(work,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));
        shell.PrimaryNavigation.Single(item => item.Title == "Categories").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var row = RowForTask(window, "category-task-row", task.Id);
        var title = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "File receipt");
        var completionDate = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.Classes.Contains("task-completion-date") && text.Text == "Completed 29 Sep 2026");
        Assert.True(CentreInWindow(completionDate, window).X > CentreInWindow(title, window).X);
        Assert.DoesNotContain(row.GetVisualDescendants().OfType<ToggleButton>(), toggle =>
            AutomationProperties.GetAutomationId(toggle) == $"task-today-{task.Id}" && toggle.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void TextAutosaveDebounceRestartsAndRejectsAStaleScheduledRevision()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Original", "", "home", null);
        var baselineWrites = work.WriteCount;
        var scheduler = new ManualInspectorAutosaveScheduler();
        var shell = new ShellViewModel(work);
        var window = new MainWindow(null, null, scheduler) { DataContext = shell, Width = 1440, Height = 900 };
        window.Show();
        shell.Work!.SelectProject(project.Id);

        shell.Work.Title = "First";
        var stale = scheduler.Pending.Single();
        shell.Work.Title = "Second";

        Assert.Equal(2, scheduler.ScheduleCount);
        Assert.Equal(ProjectCaptureViewModel.AutosaveDelay, scheduler.Delay);
        Assert.Equal("Original", Assert.Single(work.Read().Projects).Title);
        Assert.Equal(baselineWrites, work.WriteCount);
        stale.Callback(stale.Revision);
        Assert.Equal("Original", Assert.Single(work.Read().Projects).Title);
        Assert.Equal(baselineWrites, work.WriteCount);

        scheduler.FireCurrent();
        Assert.Equal("Second", Assert.Single(work.Read().Projects).Title);
        Assert.Equal(baselineWrites + 1, work.WriteCount);
        scheduler.FireCurrent();
        Assert.Equal(baselineWrites + 1, work.WriteCount);

        var status = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
            item => AutomationProperties.GetName(item) == "Autosave status");
        Assert.Empty(status.GetVisualAncestors().OfType<ScrollViewer>());
        window.Close();
    }

    [AvaloniaFact]
    public void TaskParticipantControlsExposeGuidanceValidationAndKeyboardSelectionRemoval()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Call", "", "home", null);
        var participant = work.CreateParticipant("SD");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 1100 };
        window.Show();
        shell.Work!.SelectTask(task.Id);
        Dispatcher.UIThread.RunJobs();

        var guidance = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
            text => AutomationProperties.GetName(text) == "Participant guidance");
        Assert.Contains("initials or a nickname", guidance.Text, StringComparison.Ordinal);
        Assert.Contains("contact details", guidance.Text, StringComparison.Ordinal);
        var picker = window.FindControl<ComboBox>("ParticipantPicker")!;
        Assert.Equal("Choose or create Participant", AutomationProperties.GetName(picker));
        picker.SelectedItem = shell.Work.AvailableParticipants.Single(item => item.Id == participant.Id);
        var add = NamedButton(window, "Add existing Participant to Task");
        add.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Assert.True(add.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("SD", Assert.Single(shell.Work.SelectedParticipants).Label);
        Assert.True(picker.IsKeyboardFocusWithin);
        Assert.Equal([participant.Id], work.Read().Tasks.Single().Participants);
        Assert.Equal("Added SD to the Task draft.", shell.Work.ParticipantAnnouncement);

        var remove = NamedButton(window, "Remove SD from Task");
        remove.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Assert.True(remove.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(shell.Work.SelectedParticipants);
        Assert.True(picker.IsKeyboardFocusWithin);
        Assert.Equal("Removed SD from the Task draft.", shell.Work.ParticipantAnnouncement);

        picker.SelectedItem = shell.Work.AvailableParticipants.Single(item => item.IsNew);
        Dispatcher.UIThread.RunJobs();
        var create = NamedButton(window, "Add new Participant to Task");
        create.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Assert.True(create.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(shell.Work.HasNewParticipantValidationError);
        Assert.Equal("Enter a Participant label.", shell.Work.NewParticipantValidationMessage);
        var field = window.FindControl<TextBox>("NewParticipantLabel")!;
        Assert.Equal("New Participant label", AutomationProperties.GetName(field));
        Assert.Equal(shell.Work.NewParticipantValidationMessage, AutomationProperties.GetHelpText(field));
        field.Text = " sd ";
        Assert.True(field.Focus());
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(participant.Id, Assert.Single(shell.Work.SelectedParticipants).Id);
        Assert.Single(work.Read().Participants);

        picker.SelectedItem = shell.Work.AvailableParticipants.Single(item => item.IsNew);
        Dispatcher.UIThread.RunJobs();
        field.Text = "ＳＤ";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("This Participant is already on this Task.", shell.Work.NewParticipantValidationMessage);
        Assert.True(field.IsKeyboardFocusWithin);
        Assert.Equal("ＳＤ", field.Text);
        shell.Work.CancelNewParticipantCommand.Execute(null);
        picker.SelectedItem = shell.Work.AvailableParticipants.Single(item => item.IsNew);
        Dispatcher.UIThread.RunJobs();
        field.Text = "Cancelled";
        Assert.True(field.Focus());
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(shell.Work.ShowNewParticipantEntry);
        Assert.Null(shell.Work.ParticipantToAdd);
        Assert.True(picker.IsKeyboardFocusWithin);

        picker.SelectedItem = shell.Work.AvailableParticipants.Single(item => item.IsNew);
        Dispatcher.UIThread.RunJobs();
        Activate(window, NamedButton(window, "Cancel new Participant"));
        Assert.False(shell.Work.ShowNewParticipantEntry);

        picker.SelectedItem = shell.Work.AvailableParticipants.Single(item => item.IsNew);
        Dispatcher.UIThread.RunJobs();
        field.Text = "AB";
        Assert.True(field.Focus());
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.False(shell.Work.ShowNewParticipantEntry);
        Assert.Contains(work.Read().Participants, item => item.Label == "AB");
        Assert.Contains(work.Read().Tasks.Single().Participants,
            id => work.Read().Participants.Single(item => item.Id == id).Label == "AB");
        Assert.True(picker.IsKeyboardFocusWithin);
        window.Close();
    }

    [AvaloniaFact]
    public void SettingsUsesListRowsForParticipantManagementAndRecovery()
    {
        var work = new MemoryWorkspaceWork();
        var participant = work.CreateParticipant("SD");
        var free = work.CreateParticipant("Free");
        work.CreateStandaloneTask("Call", "", "home", null,
            new([participant.Id], []));
        var shell = new ShellViewModel(work);
        shell.SettingsNavigation.SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 1100 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var settingsRegion = window.FindControl<ScrollViewer>("SettingsRegion")!;
        Assert.True(settingsRegion.IsVisible);
        Assert.Equal(new Thickness(32, 30, 32, 0), settingsRegion.Margin);
        Assert.Equal(window.FindControl<Grid>("CurrentViewRegion")!.Margin, settingsRegion.Margin);
        Assert.False(window.FindControl<Border>("InspectorRegion")!.IsVisible);
        Assert.Null(window.FindControl<Button>("OpenRecoveryButton"));
        var participantList = window.FindControl<Border>("SettingsParticipantsList")!;
        Assert.True(participantList.IsVisible);
        Assert.Contains("list-panel", participantList.Classes);
        var participantRows = participantList.GetVisualDescendants().OfType<Border>()
            .Where(border => border.DataContext is ParticipantSettingViewModel
                && border.Classes.Contains("interactive-row"))
            .ToArray();
        Assert.Equal(2, participantRows.Length);
        Assert.All(participantRows, row =>
        {
            Assert.Contains("interactive-row", row.Classes);
            Assert.Contains("separated-row", row.Classes);
        });
        Assert.DoesNotContain("last", participantRows[0].Classes);
        Assert.Contains("last", participantRows[1].Classes);
        var rename = ButtonByAutomationId(window, $"settings-participant-rename-{participant.Id}");
        Assert.Contains("view-action", rename.Classes);
        Assert.True(rename.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        var label = Assert.Single(window.GetVisualDescendants().OfType<TextBox>(),
            field => AutomationProperties.GetAutomationId(field) == $"settings-participant-label-{participant.Id}");
        Assert.Equal("Participant label", AutomationProperties.GetName(label));
        label.Text = string.Empty;
        Assert.True(label.Focus());
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        var manager = shell.Settings!.Participants.Single(item => item.Id == participant.Id);
        Assert.True(manager.HasValidationError);
        Assert.Equal("Enter a Participant label.", AutomationProperties.GetHelpText(label));
        Assert.Equal("SD", work.Read().Participants.Single(item => item.Id == participant.Id).Label);

        label.Text = "Ste";
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("Renamed Participant to Ste", shell.Settings.Announcement, StringComparison.Ordinal);
        Assert.Equal("Ste", work.Read().Participants.Single(item => item.Id == participant.Id).Label);
        Assert.True(ButtonByAutomationId(window, $"settings-participant-rename-{participant.Id}").IsKeyboardFocusWithin);

        var delete = ButtonByAutomationId(window, $"settings-participant-delete-{participant.Id}");
        Assert.True(delete.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        var referencedManager = shell.Settings.Participants.Single(item => item.Id == participant.Id);
        Assert.Contains("every Task", referencedManager.ValidationMessage, StringComparison.Ordinal);
        Assert.Equal("Ste", work.Read().Participants.Single(item => item.Id == participant.Id).Label);
        Assert.Equal(2, work.Read().Participants.Count);

        var freeManager = shell.Settings.Participants.Single(item => item.Id == free.Id);
        freeManager.DeleteCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(work.Read().Participants);
        Assert.NotNull(window.FindControl<Button>("SettingsRecoveryButton"));
        window.Close();
    }

    [AvaloniaFact]
    public void EmptySettingsParticipantsUseTheSharedEmptyStateAnatomy()
    {
        var shell = new ShellViewModel(new MemoryWorkspaceWork());
        shell.SettingsNavigation.SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var empty = window.FindControl<Border>("SettingsParticipantsEmpty")!;
        Assert.True(empty.IsVisible);
        Assert.Contains("empty-state", empty.Classes);
        Assert.False(window.FindControl<Border>("SettingsParticipantsList")!.IsVisible);
        Assert.Contains(empty.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "No Participants yet");
        Assert.Contains(empty.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "Create one from a Task's Participant picker.");
        window.Close();
    }

    [AvaloniaFact]
    public void SettingsParticipantExportAndRecoveryControlsFollowLogicalKeyboardTabOrder()
    {
        using var session = new RecoveryViewModelTests.StubWorkspaceSession(
            new RecoveryViewModelTests.StubWorkspaceRecovery());
        var participant = session.Work.CreateParticipant("SD");
        var window = new MainWindow(session) { Width = 1440, Height = 900 };
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.SettingsNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var rename = ButtonByAutomationId(window, $"settings-participant-rename-{participant.Id}");
        Assert.True(rename.Focus(NavigationMethod.Tab));
        Tab(window);
        Assert.True(ButtonByAutomationId(window, $"settings-participant-delete-{participant.Id}").IsKeyboardFocusWithin);

        Assert.True(rename.Focus(NavigationMethod.Tab));
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        var field = Assert.Single(window.GetVisualDescendants().OfType<TextBox>(),
            control => AutomationProperties.GetAutomationId(control) == $"settings-participant-label-{participant.Id}");
        Assert.True(field.IsKeyboardFocusWithin);
        Tab(window);
        Assert.True(NamedButton(window, "Save Participant label").IsKeyboardFocusWithin);
        Tab(window);
        var cancel = NamedButton(window, "Cancel Participant rename");
        Assert.True(cancel.IsKeyboardFocusWithin);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(rename.IsKeyboardFocusWithin);
        Tab(window);
        Assert.True(ButtonByAutomationId(window, $"settings-participant-delete-{participant.Id}").IsKeyboardFocusWithin);
        Tab(window);
        Assert.True(window.FindControl<Button>("SettingsPlaintextExportButton")!.IsKeyboardFocusWithin);
        Tab(window);
        Assert.True(window.FindControl<Button>("SettingsRecoveryButton")!.IsKeyboardFocusWithin);
        window.Close();
    }

    [AvaloniaFact]
    public void ParticipantControlsFollowLogicalKeyboardTabOrder()
    {
        var work = new MemoryWorkspaceWork();
        var participant = work.CreateParticipant("SD");
        var task = work.CreateStandaloneTask("Call", "", "home", null,
            new([participant.Id], []));
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 1100 };
        window.Show();
        shell.Work!.SelectTask(task.Id);
        Dispatcher.UIThread.RunJobs();

        var picker = window.FindControl<ComboBox>("ParticipantPicker")!;
        picker.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        var category = window.GetVisualDescendants().OfType<ComboBox>().Single(control =>
            AutomationProperties.GetName(control) == "Category");
        Assert.True(category.Focus(NavigationMethod.Tab));
        Tab(window);
        Assert.True(NamedButton(window, "Remove SD from Task").IsKeyboardFocusWithin);
        Tab(window);
        Assert.True(picker.IsKeyboardFocusWithin);
        Assert.True(shell.Work.AvailableParticipants.Last().IsNew);
        picker.SelectedItem = shell.Work.AvailableParticipants.Last();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.FindControl<TextBox>("NewParticipantLabel")!.IsKeyboardFocusWithin);
        Tab(window);
        Assert.True(NamedButton(window, "Add new Participant to Task").IsKeyboardFocusWithin);
        Tab(window);
        Assert.True(NamedButton(window, "Cancel new Participant").IsKeyboardFocusWithin);
        Assert.Null(window.FindControl<Expander>("ParticipantManagementExpander"));
        window.Close();
    }

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
        AssertWorkTypeIcon(projectButton, WorkType.Project);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Not started");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "0/1 tasks");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "12 Oct 2026");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Garden");

        var quickField = QuickField(window);
        Activate(window, NamedButton(window, "Collapse Garden"));
        Assert.False(quickField.IsEffectivelyVisible);
        Activate(window, NamedButton(window, "Expand Garden"));
        Assert.True(quickField.IsEffectivelyVisible);

        Activate(window, projectButton);
        var draftTitle = window.FindControl<TextBox>("DraftTitle")!;
        AssertWorkTypeIcon(
            draftTitle.GetVisualAncestors().OfType<Grid>()
                .First(grid => grid.GetVisualDescendants().OfType<WorkTypeIcon>().Any()),
            WorkType.Project);
        Assert.Contains("inspector-title", draftTitle.Classes);
        Assert.False(draftTitle.IsFocused);
        Assert.True(draftTitle.Focus());
        Assert.Equal(new Thickness(0), draftTitle.Margin);
        var titleBorder = Assert.Single(draftTitle.GetVisualDescendants().OfType<Border>(), border => border.Name == "PART_BorderElement");
        Assert.Equal(new Thickness(2), titleBorder.BorderThickness);
        Assert.Contains("primary-action", NamedButton(window, "Save").Classes);
        Assert.Contains("view-action", NamedButton(window, "Cancel editing").Classes);
        window.Close();
    }

    [AvaloniaFact]
    public void ProjectInspectorAutosavesCreationEditingAndFailedNavigationThroughBindings()
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
        Assert.Empty(work.Read().Projects);
        Assert.Empty(shell.Work!.Projects);
        Activate(window, NamedButton(window, "Open Backlog"));
        Assert.Empty(work.Read().Projects);
        Assert.False(shell.Work.HasInspector);

        Activate(window, NamedButton(window, "Open Projects"));
        Activate(window, NamedButton(window, "New project"));
        window.KeyTextInput("Garden");
        Assert.True(shell.Work.RunScheduledAutosave());
        Dispatcher.UIThread.RunJobs();
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
        Assert.True(shell.Work.RunScheduledAutosave());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Renamed garden", Assert.Single(work.Read().Projects).Title);
        Assert.Equal("Renamed garden", Assert.Single(shell.Work.Projects).Title);

        title.Focus();
        title.SelectAll();
        window.KeyTextInput("Unsaved edit");
        work.FailWrites = true;
        Activate(window, NamedButton(window, "Open Backlog"));
        Assert.True(shell.Work.NeedsDecision);
        Activate(window, NamedButton(window, "Retry and leave"));
        Assert.True(shell.Work.NeedsDecision);
        Assert.True(shell.Work.HasAutosaveError);
        var retry = NamedButton(window, "Retry automatic save");
        Assert.True(retry.IsVisible);
        Assert.Equal("Retry automatic save", AutomationProperties.GetName(retry));
        Assert.True(window.FindControl<Button>("GuardSave")!.IsFocused);
        Assert.Equal("Projects", shell.ViewTitle);
        Assert.Equal("Unsaved edit", title.Text);
        Assert.Equal("Renamed garden", Assert.Single(work.Read().Projects).Title);
        Activate(window, NamedButton(window, "Stay and keep editing"));
        Assert.False(shell.Work.NeedsDecision);
        Assert.True(title.IsFocused);
        Assert.Equal("Unsaved edit", title.Text);
        work.FailWrites = false;
        Activate(window, NamedButton(window, "Open Backlog"));
        Assert.Equal("Backlog", shell.ViewTitle);
        Assert.Equal("Unsaved edit", Assert.Single(work.Read().Projects).Title);
        window.Close();
    }

    [AvaloniaFact]
    public void FailedAutosaveNavigationRetainsCheckedViewUntilDecision()
    {
        using var session = new RecoveryViewModelTests.StubWorkspaceSession(new RecoveryViewModelTests.StubWorkspaceRecovery());
        var window = new MainWindow(session);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.Work!.NewProjectCommand.Execute(null);
        shell.Work.Title = "Unsaved";
        Assert.IsType<MemoryWorkspaceWork>(session.Work).FailWrites = true;
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
    public void DirtyDecisionDoesNotHighlightDisabledRowTitles()
    {
        var work = new MemoryWorkspaceWork();
        var first = work.CreateStandaloneTask("First task", "", "home", null);
        work.CreateStandaloneTask("Second task", "", "home", null);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        shell.Work!.SelectTask(first.Id);
        shell.Work.Title = "Unsaved first task";
        work.FailWrites = true;

        Activate(window, NamedButton(window, "Second task"));

        Assert.True(shell.Work.NeedsDecision);
        var disabledRowTitle = NamedButton(window, "Second task");
        Assert.False(disabledRowTitle.IsEffectivelyEnabled);
        Assert.Equal(
            Colors.Transparent,
            Assert.IsAssignableFrom<ISolidColorBrush>(disabledRowTitle.Background).Color);
        var presenter = Assert.Single(
            disabledRowTitle.GetVisualChildren().OfType<ContentPresenter>(),
            candidate => candidate.Name == "PART_ContentPresenter");
        Assert.Equal(
            Colors.Transparent,
            Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background).Color);
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
        Assert.IsType<MemoryWorkspaceWork>(session.Work).FailWrites = true;
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
    public void ExpandedProjectTaskRowSurfaceSelectsTaskWithoutCompletionToggleActivation()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var controlTask = work.CreateTask(project.Id, "Plant bulbs");
        var selectedTask = work.CreateTask(project.Id, "Order compost");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        Click(window, ToggleByAutomationId(window, $"task-completion-{controlTask.Id}"));
        Assert.False(shell.Work!.HasInspector);
        Assert.True(work.Read().Tasks.Single(task => task.Id == controlTask.Id).IsComplete);

        var reorder = ButtonByAutomationId(window, $"project-task-reorder-{selectedTask.Id}");
        Click(window, reorder);
        Assert.True(Assert.IsType<MenuFlyout>(reorder.Flyout).IsOpen);
        Assert.False(shell.Work.HasInspector);
        reorder.Flyout.Hide();

        ClickTaskRowSurface(window, "project-task-row", "Order compost");

        Assert.True(shell.Work.HasInspector);
        Assert.Equal("Order compost", shell.Work.Title);
        Assert.False(work.Read().Tasks.Single(task => task.Id == selectedTask.Id).IsComplete);
        window.Close();
    }

    [AvaloniaFact]
    public void CategoryTaskRowSurfaceSelectsTaskWithoutCompletionToggleActivation()
    {
        var work = new MemoryWorkspaceWork();
        var controlTask = work.CreateStandaloneTask("Plant bulbs", "", "home", null);
        var selectedTask = work.CreateStandaloneTask("Order compost", "", "home", null);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Categories").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        Click(window, ToggleByAutomationId(window, $"task-completion-{controlTask.Id}"));
        Assert.False(shell.Work!.HasInspector);
        Assert.True(work.Read().Tasks.Single(task => task.Id == controlTask.Id).IsComplete);

        ClickTaskRowSurface(window, "category-task-row", "Order compost");

        Assert.True(shell.Work.HasInspector);
        Assert.Equal("Order compost", shell.Work.Title);
        Assert.False(work.Read().Tasks.Single(task => task.Id == selectedTask.Id).IsComplete);
        window.Close();
    }

    [AvaloniaFact]
    public void CompletedTaskRowSurfaceTapSelectsTaskWithoutReopenToggleActivation()
    {
        var work = new MemoryWorkspaceWork();
        var controlTask = work.CreateStandaloneTask("Plant bulbs", "", "home", null);
        var selectedTask = work.CreateStandaloneTask("Order compost", "", "home", null);
        work.CompleteTask(controlTask.Id);
        work.CompleteTask(selectedTask.Id);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Completed").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        Click(window, ToggleByAutomationId(window, $"task-completion-{controlTask.Id}"));
        Assert.False(shell.Work!.HasInspector);
        Assert.False(work.Read().Tasks.Single(task => task.Id == controlTask.Id).IsComplete);

        TapTaskRowSurface(window, "completed-row", "Order compost");

        Assert.True(shell.Work.HasInspector);
        Assert.Equal("Order compost", shell.Work.Title);
        Assert.True(work.Read().Tasks.Single(task => task.Id == selectedTask.Id).IsComplete);
        window.Close();
    }

    [AvaloniaFact]
    public void ArchivingCompletedTaskRequiresAccessibleConfirmationAndMovesFocusDeterministically()
    {
        var work = new MemoryWorkspaceWork();
        var first = work.CreateStandaloneTask("First receipt", "", "home", null);
        var second = work.CreateStandaloneTask("Second receipt", "", "home", null);
        work.CompleteTask(first.Id);
        work.CompleteTask(second.Id);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Completed").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var archive = ButtonByAutomationId(window, $"task-archive-{second.Id}");
        Assert.Equal("Archive Second receipt", AutomationProperties.GetName(archive));
        Activate(window, archive);

        Assert.True(shell.Work!.NeedsArchiveConfirmation);
        var confirm = ButtonByAutomationId(window, "archive-confirm");
        Assert.True(confirm.IsFocused);
        Assert.Equal("Archive task", AutomationProperties.GetName(confirm));
        Assert.Equal("Cancel task archival", AutomationProperties.GetName(ButtonByAutomationId(window, "archive-cancel")));
        Assert.Contains("available to restore from Archive", shell.Work.ArchiveConfirmationBody, StringComparison.Ordinal);
        Assert.Contains(window.GetVisualDescendants().OfType<StackPanel>(), panel =>
            panel.IsEffectivelyVisible && AutomationProperties.GetLiveSetting(panel) == AutomationLiveSetting.Assertive);
        Assert.False(work.Read().Tasks.Single(task => task.Id == second.Id).IsArchived);

        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.Work.NeedsArchiveConfirmation);
        Assert.True(ButtonByAutomationId(window, $"task-archive-{second.Id}").IsFocused);

        Activate(window, ButtonByAutomationId(window, $"task-archive-{second.Id}"));
        Activate(window, ButtonByAutomationId(window, "archive-confirm"));

        Assert.True(work.Read().Tasks.Single(task => task.Id == second.Id).IsArchived);
        Assert.Equal("Task archived.", shell.Work.Message);
        Assert.True(ButtonByAutomationId(window, $"task-archive-{first.Id}").IsFocused);
        Assert.Equal("1", shell.PrimaryNavigation.Single(item => item.Title == "Archive").CountText);
        window.Close();
    }

    [AvaloniaFact]
    public void ArchivedProjectTaskDisablesCompletionAndOffersAccessibleRestore()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTask(project.Id, "Plant bulbs");
        work.CompleteTask(task.Id);
        work.ArchiveTask(task.Id);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var completion = ToggleByAutomationId(window, $"task-completion-{task.Id}");
        Assert.False(completion.IsEnabled);
        Assert.Equal("Plant bulbs is archived. Restore it before reopening.", AutomationProperties.GetName(completion));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "Archived");
        var restore = ButtonByAutomationId(window, $"task-restore-{task.Id}");
        Assert.Equal("Restore Plant bulbs to Completed", AutomationProperties.GetName(restore));

        Activate(window, restore);

        Assert.False(work.Read().Tasks.Single().IsArchived);
        Assert.True(work.Read().Tasks.Single().IsComplete);
        Assert.Empty(shell.Work!.TodayPlanned);
        Assert.Empty(shell.Work.TodayInProgress);
        Assert.Equal(task.Id, Assert.Single(shell.Work.Completed).Id);
        Assert.True(ToggleByAutomationId(window, $"task-completion-{task.Id}").IsEnabled);
        Assert.True(ToggleByAutomationId(window, $"task-completion-{task.Id}").IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void ArchiveViewRestoresStandaloneTaskAndMovesFocusToNavigationWhenEmpty()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Filed receipt", "", "home", null);
        work.CompleteTask(task.Id);
        work.ArchiveTask(task.Id);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Archive").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var restore = ButtonByAutomationId(window, $"task-restore-{task.Id}");
        Assert.True(restore.IsEffectivelyVisible);
        Assert.Equal("Restore Filed receipt to Completed", AutomationProperties.GetName(restore));
        Assert.Contains(window.GetVisualDescendants().OfType<Border>(), row => row.Classes.Contains("archive-row"));

        Activate(window, restore);

        Assert.False(work.Read().Tasks.Single().IsArchived);
        Assert.True(work.Read().Tasks.Single().IsComplete);
        Assert.Empty(shell.Work!.Archived);
        Assert.Equal(task.Id, Assert.Single(shell.Work.Completed).Id);
        Assert.Empty(shell.Work.CompletedToday);
        Assert.True(window.GetVisualDescendants().OfType<RadioButton>()
            .Single(button => AutomationProperties.GetAutomationId(button) == "navigation-archive").IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void ArchiveSearchShowsContextAndKeyboardActivationOpensTheArchivedTask()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTaskDraft(project.Id, "Plant bulbs", "Blue **tulips** near the gate", null, null);
        work.SetCompletion(task.Id, new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 27));
        work.ArchiveTask(task.Id);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Archive").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        var search = Assert.Single(window.GetVisualDescendants().OfType<TextBox>(),
            textBox => AutomationProperties.GetAutomationId(textBox) == "archive-search");
        Assert.Equal("Search archived Projects and Tasks", AutomationProperties.GetName(search));

        search.Text = "blue";
        Dispatcher.UIThread.RunJobs();

        var result = ButtonByAutomationId(window, $"archive-search-task-{task.Id}");
        AssertWorkTypeIcon(result, WorkType.Task);
        Assert.Contains("task-title", result.Classes);
        Assert.Contains("row-title", result.Classes);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(result.Background).Color);
        Assert.True(ButtonByAutomationId(window, $"archive-search-restore-task-{task.Id}").IsEffectivelyVisible);
        var accessibleName = AutomationProperties.GetName(result);
        Assert.Contains("Task Plant bulbs", accessibleName, StringComparison.Ordinal);
        Assert.Contains("Garden", accessibleName, StringComparison.Ordinal);
        Assert.Contains("Completed 27 Sep 2026", accessibleName, StringComparison.Ordinal);
        Assert.Contains("Blue tulips near the gate", accessibleName, StringComparison.Ordinal);
        Assert.DoesNotContain("**", accessibleName, StringComparison.Ordinal);

        Activate(window, result);

        Assert.True(shell.Work!.HasInspector);
        Assert.Equal("Plant bulbs", shell.Work.Title);

        search.Text = "missing";
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.IsEffectivelyVisible && text.Text == "No archived work found");

        var clear = ButtonByAutomationId(window, "archive-search-clear");
        Assert.True(clear.IsEffectivelyVisible);
        Assert.Equal("Clear archive search", AutomationProperties.GetName(clear));
        Assert.Equal(VerticalAlignment.Center, clear.VerticalContentAlignment);
        Activate(window, clear);

        Assert.Equal(string.Empty, search.Text);
        Assert.True(shell.Work.ShowArchiveTimeline);
        Assert.True(search.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void ArchiveSearchResultUsesTheSharedRowRecipeAndItsWholeSurfaceOpensTheMatch()
    {
        var work = new MemoryWorkspaceWork();
        work.CurrentDate = new(2026, 9, 28);
        var earlierTask = work.CreateStandaloneTask("File appliance receipt", "Filed with the manual", "home", null);
        work.SetCompletion(earlierTask.Id, new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 4));
        work.SetArchive(earlierTask.Id, new DateTimeOffset(2026, 9, 28, 13, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 28));
        work.CurrentDate = new(2026, 9, 29);
        var task = work.CreateStandaloneTask("File appliance warranty", "Filed with the receipt", "home", null);
        work.SetCompletion(task.Id, new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 5));
        work.ArchiveTask(task.Id);
        var shell = new ShellViewModel(work,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));
        shell.PrimaryNavigation.Single(item => item.Title == "Archive").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();
        window.FindControl<TextBox>("ArchiveSearchBox")!.Text = "file";
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["Today", "Yesterday"], shell.Work!.ArchiveSearchGroups.Select(group => group.Heading));
        var searchRows = window.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("archive-search-row"))
            .ToArray();
        Assert.Equal(2, searchRows.Length);
        Assert.All(searchRows, row =>
        {
            Assert.Contains("archive-row", row.Classes);
            Assert.Contains("last", row.Classes);
        });

        var earlierRestore = ButtonByAutomationId(window, $"archive-search-restore-task-{earlierTask.Id}");
        Assert.Equal("Restore File appliance receipt to Completed", AutomationProperties.GetName(earlierRestore));
        Activate(window, earlierRestore);

        Assert.False(work.Read().Tasks.Single(item => item.Id == earlierTask.Id).IsArchived);
        Assert.False(shell.Work.HasInspector);
        Assert.Single(shell.Work.ArchiveSearchResults);
        Assert.True(ButtonByAutomationId(window, $"archive-search-restore-task-{task.Id}").IsFocused);

        var result = ButtonByAutomationId(window, $"archive-search-task-{task.Id}");
        var row = result.GetVisualAncestors().OfType<Border>()
            .First(border => border.Classes.Contains("archive-search-row"));
        Assert.Contains("interactive-row", row.Classes);
        Assert.Contains("selectable-row", row.Classes);
        Assert.Contains("task-title", result.Classes);
        Assert.Contains("row-title", result.Classes);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(result.Background).Color);
        Assert.InRange(row.Bounds.Height - result.Bounds.Height, 0, 0.5);

        var state = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Archived Task");
        window.MouseMove(CentreInWindow(state, window), RawInputModifiers.None);
        Assert.Equal(Color.Parse("#151923"), Assert.IsAssignableFrom<ISolidColorBrush>(row.Background).Color);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(result.Background).Color);
        ClickSurface(window, CentreInWindow(state, window));

        Assert.True(shell.Work!.HasInspector);
        Assert.Equal("File appliance warranty", shell.Work.Title);
        window.Close();
    }

    [AvaloniaFact]
    public void RestoringLastArchiveSearchResultReturnsFocusToSearch()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("File appliance receipt", "Filed with the manual", "home", null);
        work.SetCompletion(task.Id, new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 5));
        work.ArchiveTask(task.Id);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Archive").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        var search = window.FindControl<TextBox>("ArchiveSearchBox")!;
        search.Text = "receipt";
        Dispatcher.UIThread.RunJobs();

        Activate(window, ButtonByAutomationId(window, $"archive-search-restore-task-{task.Id}"));

        Assert.False(work.Read().Tasks.Single().IsArchived);
        Assert.True(shell.Work!.ShowArchiveSearchEmpty);
        Assert.True(search.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void RestoringLastArchivedTaskMovesFocusToRemainingArchivedProject()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        work.ArchiveProject(project.Id);
        var task = work.CreateStandaloneTask("Filed receipt", "", "home", null);
        work.CompleteTask(task.Id);
        work.ArchiveTask(task.Id);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Archive").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        Activate(window, ButtonByAutomationId(window, $"task-restore-{task.Id}"));

        Assert.Empty(shell.Work!.Archived);
        Assert.Single(shell.Work.ArchivedProjects);
        Assert.True(ButtonByAutomationId(window, $"project-restore-{project.Id}").IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void ProjectArchiveAndRestoreUseAccessibleActionsAndMoveFocusDeterministically()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTask(project.Id, "Plant bulbs");
        work.SetTaskTodayLane(task.Id, TodayLane.Planned);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        shell.Work!.Projects.Single().Tasks.Single().SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(shell.Work.HasInspector);

        var archive = ButtonByAutomationId(window, $"project-archive-{project.Id}");
        Assert.Equal("Archive Project Garden", AutomationProperties.GetName(archive));
        Activate(window, archive);

        Assert.True(work.Read().Projects.Single().IsArchived);
        Assert.False(shell.Work.HasInspector);
        Assert.Null(work.Read().Tasks.Single().TodayLane);
        Assert.Equal("1", shell.PrimaryNavigation.Single(item => item.Title == "Archive").CountText);
        Assert.True(window.GetVisualDescendants().OfType<RadioButton>()
            .Single(button => AutomationProperties.GetAutomationId(button) == "navigation-projects").IsFocused);

        shell.PrimaryNavigation.Single(item => item.Title == "Archive").SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var restore = ButtonByAutomationId(window, $"project-restore-{project.Id}");
        Assert.Equal("Restore Project Garden to Projects", AutomationProperties.GetName(restore));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Archived Project");
        Activate(window, restore);

        Assert.False(work.Read().Projects.Single().IsArchived);
        Assert.Equal(task.Id, Assert.Single(shell.Work!.Backlog).Id);
        Assert.Empty(shell.Work.ArchivedProjects);
        Assert.True(window.GetVisualDescendants().OfType<RadioButton>()
            .Single(button => AutomationProperties.GetAutomationId(button) == "navigation-archive").IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void BulkArchiveThresholdCountConfirmationAndCancellationAreAccessible()
    {
        var work = new MemoryWorkspaceWork();
        var old = work.CreateStandaloneTask("Old receipt", "", "home", null);
        var recent = work.CreateStandaloneTask("Recent receipt", "", "home", null);
        work.SetCompletion(old.Id, new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 25));
        work.SetCompletion(recent.Id, new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 29));
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Completed").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        var threshold = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(),
            combo => AutomationProperties.GetAutomationId(combo) == "bulk-archive-threshold");

        threshold.SelectedItem = 3;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(3, shell.Work!.BulkArchiveCompletedAgeDays);
        Assert.Equal(1, shell.Work.BulkArchiveAffectedCount);
        Assert.Equal("Completed age threshold in calendar days", AutomationProperties.GetName(threshold));
        var request = ButtonByAutomationId(window, "bulk-archive-request");
        Assert.Equal("Archive older completed Tasks", AutomationProperties.GetName(request));
        Activate(window, request);
        Assert.True(shell.Work.NeedsBulkTaskArchiveConfirmation);
        Assert.True(ButtonByAutomationId(window, "bulk-archive-confirm").IsFocused);
        Assert.Equal("Confirm bulk Task archive", AutomationProperties.GetName(ButtonByAutomationId(window, "bulk-archive-confirm")));
        Assert.Equal("Cancel bulk Task archive", AutomationProperties.GetName(ButtonByAutomationId(window, "bulk-archive-cancel")));

        Activate(window, ButtonByAutomationId(window, "bulk-archive-cancel"));
        Assert.False(shell.Work.NeedsBulkTaskArchiveConfirmation);
        Assert.True(ButtonByAutomationId(window, "bulk-archive-request").IsFocused);
        Assert.False(work.Read().Tasks.Single(task => task.Id == old.Id).IsArchived);

        Activate(window, ButtonByAutomationId(window, "bulk-archive-request"));
        Activate(window, ButtonByAutomationId(window, "bulk-archive-confirm"));

        Assert.True(work.Read().Tasks.Single(task => task.Id == old.Id).IsArchived);
        Assert.False(work.Read().Tasks.Single(task => task.Id == recent.Id).IsArchived);
        Assert.False(shell.Work.NeedsBulkTaskArchiveConfirmation);
        Assert.Equal("1 completed Task archived.", shell.Work.Message);
        Assert.True(ButtonByAutomationId(window, $"task-archive-{recent.Id}").IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void ArchiveViewGroupsRowsByCapturedArchiveDate()
    {
        var work = new MemoryWorkspaceWork();
        var today = work.CreateStandaloneTask("Today archive", "", "home", null);
        var yesterday = work.CreateStandaloneTask("Yesterday archive", "", "home", null);
        work.SetCompletion(today.Id, new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 20));
        work.SetCompletion(yesterday.Id, new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 20));
        work.SetArchive(today.Id, new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 29));
        work.SetArchive(yesterday.Id, new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 28));
        var shell = new ShellViewModel(
            work,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero)));
        shell.PrimaryNavigation.Single(item => item.Title == "Archive").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        Assert.Equal(["Today", "Yesterday"], shell.Work!.ArchiveGroups.Select(group => group.Heading));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "Today");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "Yesterday");
        var rows = window.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("archive-row"))
            .ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.True(Assert.IsType<ArchivedWorkRowViewModel>(row.DataContext).IsLast));
        window.Close();
    }

    [AvaloniaFact]
    public void ProjectHeaderSurfaceSelectsProject()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        work.CreateTask(project.Id, "Plant bulbs");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();

        var header = Assert.Single(window.GetVisualDescendants().OfType<Border>(),
            border => border.Classes.Contains("project-header-row"));
        ClickSurface(window, TrailingSurfacePoint(header, window));

        Assert.True(shell.Work!.HasInspector);
        Assert.Equal("Garden", shell.Work.Title);
        window.Close();
    }

    [AvaloniaFact]
    public void CategoryProjectRowTopAndBottomSurfacesSelectProjects()
    {
        var work = new MemoryWorkspaceWork();
        work.CreateProject("Garden", "", "home", null);
        work.CreateProject("Kitchen", "", "home", null);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Categories").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();

        ClickRowVerticalEdge(window, "category-project-row", "Garden", top: true);
        Assert.True(shell.Work!.HasInspector);
        Assert.Equal("Garden", shell.Work.Title);

        ClickRowVerticalEdge(window, "category-project-row", "Kitchen", top: false);
        Assert.Equal("Kitchen", shell.Work.Title);
        window.Close();
    }

    [AvaloniaFact]
    public void CategoryHeaderSurfaceSelectsCategory()
    {
        var work = new MemoryWorkspaceWork();
        work.CreateStandaloneTask("Plant bulbs", "", "work", null);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Categories").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();

        var title = ButtonByAutomationId(window, "category-selection-work");
        var header = title.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("category-header-row"));
        var handle = ButtonByAutomationId(window, "category-reorder-work");
        var disclosure = NamedButton(window, "Collapse Work category");
        var summary = Assert.Single(header.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "0 Projects · 1 standalone Task");
        AssertInteractiveRowHover(window, header, title, handle, disclosure, summary);
        ClickSurface(window, TrailingSurfacePoint(header, window));

        Assert.True(shell.Work!.HasInspector);
        Assert.Equal("Work", shell.Work.Title);
        window.Close();
    }

    [AvaloniaFact]
    public void CategoryDisclosureCollapsesAndExpandsItsWorkRows()
    {
        var work = new MemoryWorkspaceWork();
        work.CreateProject("Garden", "", "work", null);
        var task = work.CreateStandaloneTask("Plant bulbs", "", "work", null);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Categories").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();

        var projectTitle = NamedButton(window, "Garden");
        Activate(window, NamedButton(window, "Collapse Work category"));
        Assert.False(projectTitle.IsEffectivelyVisible);
        Assert.False(shell.Work!.HasInspector);

        shell.Work.Backlog.Single(row => row.Id == task.Id).ToggleCompletionCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var workCategory = shell.Work.CategoryGroups.Single(category => category.Id == "work");
        Assert.False(workCategory.IsExpanded);
        Assert.Equal("Expand Work category", workCategory.ExpansionAccessibleName);

        Activate(window, NamedButton(window, "Expand Work category"));
        projectTitle = NamedButton(window, "Garden");
        Assert.True(projectTitle.IsEffectivelyVisible);
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
        var metadata = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Garden");
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
        var metadata = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Garden");
        var date = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "2 Oct 2026");
        AssertInteractiveRowHover(window, row, title, handle, toggle, metadata, date);
        window.Close();
    }

    [AvaloniaFact]
    public void ProjectHeaderHoverFillsOnlyTheHeaderAndKeepsItsTitleTransparent()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", new DateOnly(2026, 10, 12));
        var task = work.CreateTask(project.Id, "Plant bulbs");
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
        var projectTitle = Assert.Single(header.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Garden");
        var taskTitle = Assert.Single(child.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Plant bulbs");
        var projectTitleOrigin = projectTitle.TranslatePoint(default, window)!.Value;
        var taskTitleOrigin = taskTitle.TranslatePoint(default, window)!.Value;
        Assert.InRange(taskTitleOrigin.X - projectTitleOrigin.X, 7, 9);
        var projectDots = Assert.Single(handle.GetVisualDescendants().OfType<Grid>(),
            grid => grid.Bounds.Width == 8 && grid.Bounds.Height == 12);
        var taskHandle = ButtonByAutomationId(window, $"project-task-reorder-{task.Id}");
        var taskDots = Assert.Single(taskHandle.GetVisualDescendants().OfType<Grid>(),
            grid => grid.Bounds.Width == 8 && grid.Bounds.Height == 12);
        Assert.InRange(CentreInWindow(taskDots, window).X - CentreInWindow(projectDots, window).X, 3, 5);
        var handleOrigin = OriginInWindow(handle, window);
        var disclosureOrigin = OriginInWindow(disclosure, window);
        Assert.InRange(disclosureOrigin.X - handleOrigin.X - handle.Bounds.Width, 3, 5);
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
    public void ExpandedProjectTaskUsesBacklogControlAndTextAlignmentWithProjectDateTypography()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTask(project.Id, "Plant bulbs");
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
        var projectDate = Assert.Single(header.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "No date");
        var taskDate = Assert.Single(child.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "No date");
        Assert.Equal(12, projectDate.FontSize);
        Assert.Equal(projectDate.FontSize, taskDate.FontSize);

        var taskTitle = Assert.Single(child.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Plant bulbs");
        var taskMetadata = Assert.Single(child.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Garden");
        var emptyCompletionDate = Assert.Single(child.GetVisualDescendants().OfType<TextBlock>(),
            text => string.IsNullOrEmpty(text.Text) && text.Classes.Contains("task-completion-date"));
        Assert.False(emptyCompletionDate.IsVisible);
        Assert.Equal(12, emptyCompletionDate.FontSize);
        Assert.Equal(0, emptyCompletionDate.Bounds.Height);
        var handle = ButtonByAutomationId(window, $"project-task-reorder-{task.Id}");
        var dots = Assert.Single(handle.GetVisualDescendants().OfType<Grid>(),
            grid => grid.Bounds.Width == 8 && grid.Bounds.Height == 12);
        var toggle = ToggleByAutomationId(window, $"task-completion-{task.Id}");
        var completionBox = Assert.Single(toggle.GetVisualDescendants().OfType<Border>(),
            border => border.Name == "CompletionBox");
        var projectOrigin = OriginInWindow(child, window);
        var projectTitleOrigin = OriginInWindow(taskTitle, window) - projectOrigin;
        var projectMetadataOrigin = OriginInWindow(taskMetadata, window) - projectOrigin;
        var projectDotsCentre = CentreInWindow(dots, window) - projectOrigin;
        var projectCompletionCentre = CentreInWindow(completionBox, window) - projectOrigin;

        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var backlogRow = Assert.Single(window.GetVisualDescendants().OfType<Border>(),
            border => border.Classes.Contains("backlog-row"));
        var backlogTitle = Assert.Single(backlogRow.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Plant bulbs");
        var backlogMetadata = Assert.Single(backlogRow.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Garden");
        var backlogHandle = ButtonByAutomationId(window, $"backlog-reorder-{task.Id}");
        var backlogDots = Assert.Single(backlogHandle.GetVisualDescendants().OfType<Grid>(),
            grid => grid.Bounds.Width == 8 && grid.Bounds.Height == 12);
        var backlogToggle = Assert.Single(backlogRow.GetVisualDescendants().OfType<ToggleButton>(),
            button => AutomationProperties.GetAutomationId(button) == $"task-completion-{task.Id}");
        var backlogCompletionBox = Assert.Single(backlogToggle.GetVisualDescendants().OfType<Border>(),
            border => border.Name == "CompletionBox");
        var backlogOrigin = OriginInWindow(backlogRow, window);
        var backlogTitleOrigin = OriginInWindow(backlogTitle, window) - backlogOrigin;
        var backlogMetadataOrigin = OriginInWindow(backlogMetadata, window) - backlogOrigin;
        var backlogDotsCentre = CentreInWindow(backlogDots, window) - backlogOrigin;
        var backlogCompletionCentre = CentreInWindow(backlogCompletionBox, window) - backlogOrigin;

        Assert.Equal(12, taskMetadata.FontSize);
        Assert.Equal(backlogMetadata.FontSize, taskMetadata.FontSize);
        Assert.InRange(Math.Abs(projectDotsCentre.X - backlogDotsCentre.X), 0, 1);
        Assert.InRange(Math.Abs(projectCompletionCentre.X - backlogCompletionCentre.X), 0, 1);
        Assert.InRange(Math.Abs(projectTitleOrigin.X - backlogTitleOrigin.X), 0, 1);
        Assert.InRange(Math.Abs(projectMetadataOrigin.X - backlogMetadataOrigin.X), 0, 1);
        Assert.InRange(Math.Abs((projectDotsCentre.Y - projectTitleOrigin.Y) - (backlogDotsCentre.Y - backlogTitleOrigin.Y)), 0, 1);
        Assert.InRange(Math.Abs((projectCompletionCentre.Y - projectTitleOrigin.Y) - (backlogCompletionCentre.Y - backlogTitleOrigin.Y)), 0, 1);
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

        AssertRelationshipMetadata("Inherited task", "Garden", "Home", false, "Inherited Category Home");
        AssertRelationshipMetadata("Overridden task", "Garden", "Work", true, "Category override Work");
        AssertRelationshipMetadata("Standalone task", "Standalone", "Work", false, "Standalone. Category Work");
        window.Close();

        void AssertRelationshipMetadata(
            string title,
            string visibleRelationship,
            string categoryName,
            bool hasOverride,
            string accessibleRelationship)
        {
            var row = Assert.Single(window.GetVisualDescendants().OfType<Border>(),
                border => border.Classes.Contains("backlog-row") && border.DataContext is TaskRowViewModel task && task.Title == title);
            var relationship = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == visibleRelationship);
            Assert.True(relationship.IsEffectivelyVisible);
            var pill = Assert.Single(row.GetVisualDescendants().OfType<CategoryPill>());
            Assert.Equal(categoryName, pill.CategoryName);
            Assert.Equal(hasOverride, pill.HasOverride);
            Assert.True(pill.IsEffectivelyVisible);
            var taskButton = Assert.Single(row.GetVisualDescendants().OfType<Button>(),
                button => button.Classes.Contains("backlog-task-title"));
            var peer = ControlAutomationPeer.CreatePeerForElement(taskButton);
            Assert.Contains($"Task {title}", peer.GetName(), StringComparison.Ordinal);
            Assert.Contains(accessibleRelationship, peer.GetName(), StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public void CategoryPillsAppearOnlyOnTheFourScopedTaskSurfaces()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var attached = work.CreateTaskDraft(project.Id, "Attached", "", null,
            new DateOnly(2026, 10, 7));
        work.SetTaskTodayLane(attached.Id, TodayLane.Planned);
        var standalone = work.CreateStandaloneTask("Standalone", "", "work", null);
        var completed = work.CreateTask(project.Id, "Completed");
        work.CompleteTask(completed.Id);
        var archived = work.CreateStandaloneTask("Archived", "", "home", null);
        work.CompleteTask(archived.Id);
        work.ArchiveTask(archived.Id);
        var binned = work.CreateStandaloneTask("Binned", "", "work", null);
        work.MoveTaskToBin(binned.Id);
        var shell = new ShellViewModel(work,
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero)));
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();

        AssertScopedSurface("Projects", attached.Id, "Home");
        AssertScopedSurface("Backlog", standalone.Id, "Work");
        AssertScopedSurface("Categories", standalone.Id, "Work");
        AssertScopedSurface("Completed", completed.Id, "Home");

        SelectView(shell, "Today");
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<CategoryPill>(), pill => pill.IsEffectivelyVisible);
        SelectView(shell, "Upcoming");
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<CategoryPill>(), pill => pill.IsEffectivelyVisible);
        SelectView(shell, "Archive");
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<CategoryPill>(), pill => pill.IsEffectivelyVisible);
        SelectView(shell, "Bin");
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<CategoryPill>(), pill => pill.IsEffectivelyVisible);
        window.Close();

        void AssertScopedSurface(string view, string taskId, string categoryName)
        {
            SelectView(shell, view);
            Dispatcher.UIThread.RunJobs();
            var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), candidate =>
                candidate.IsEffectivelyVisible
                && candidate.Classes.Contains("row-title")
                && (candidate.DataContext is TaskRowViewModel task && task.Id == taskId
                    || candidate.DataContext is CategoryTaskRowViewModel categoryTask && categoryTask.Task.Id == taskId
                    || candidate.DataContext is CompletedTaskRowViewModel completedTask && completedTask.Task.Id == taskId));
            var pill = Assert.Single(button.GetVisualDescendants().OfType<CategoryPill>(), candidate => candidate.IsEffectivelyVisible);
            Assert.Equal(categoryName, pill.CategoryName);
            Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(pill));
        }
    }

    [AvaloniaFact]
    public void CategoryInspectorOffersEightKeyboardReachableColoursAndLivePreview()
    {
        var work = new MemoryWorkspaceWork();
        var shell = new ShellViewModel(work);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();
        SelectView(shell, "Categories");
        Dispatcher.UIThread.RunJobs();
        Activate(window, NamedButton(window, "New category"));
        Dispatcher.UIThread.RunJobs();

        var picker = Assert.IsType<ComboBox>(window.GetVisualDescendants().Single(control =>
            control is ComboBox combo && AutomationProperties.GetAutomationId(combo) == "category-colour"));
        Assert.Equal(8, picker.ItemCount);
        Assert.True(picker.Focusable);
        Assert.True(picker.Focus(NavigationMethod.Tab));
        Assert.True(picker.IsKeyboardFocusWithin);
        Assert.Equal("Indigo", AutomationProperties.GetItemStatus(picker));
        picker.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
        var options = Enumerable.Range(0, picker.ItemCount)
            .Select(index => Assert.IsType<ComboBoxItem>(picker.ContainerFromIndex(index)))
            .ToArray();
        Assert.Equal(shell.Work!.CategoryColourChoices.Select(choice => choice.Name),
            options.Select(AutomationProperties.GetName));
        Assert.All(options, option =>
        {
            var peer = ControlAutomationPeer.CreatePeerForElement(option);
            Assert.Equal(AutomationControlType.ComboBoxItem, peer.GetAutomationControlType());
            Assert.NotNull(peer.GetProvider<ISelectionItemProvider>());
        });
        Assert.True(ControlAutomationPeer.CreatePeerForElement(options[2])
            .GetProvider<ISelectionItemProvider>()!.IsSelected);
        picker.IsDropDownOpen = false;
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("ocean", shell.Work.CategoryColourKey);
        Assert.Equal("Ocean", AutomationProperties.GetItemStatus(picker));
        var preview = Assert.Single(window.GetVisualDescendants().OfType<CategoryIdentityMarker>(), marker =>
            marker.IsEffectivelyVisible && marker.CategoryName == "Category name" && marker.ColourKey == "ocean");
        Assert.False(preview.Focusable);
        Assert.False(preview.IsHitTestVisible);

        shell.Work.Title = "Errands";
        shell.Work.SelectedCategoryColour = shell.Work.CategoryColourChoices.Single(choice => choice.Key == "tangerine");
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<CategoryIdentityMarker>(), marker =>
            marker.IsEffectivelyVisible && marker.CategoryName == "Errands" && marker.ColourKey == "tangerine");
        Assert.True(shell.Work.IsDirty);
        window.Close();
    }

    [AvaloniaFact]
    public void CompactInlineIdentityKeepsLongProjectAndOverrideContextInsideTheTitleColumn()
    {
        var work = new MemoryWorkspaceWork();
        var overrideCategory = work.CreateCategory("A deliberately long category name for constrained rows");
        var project = work.CreateProject("A deliberately long project relationship that must truncate", "", "home", null);
        var task = work.CreateTask(project.Id, "A compact titled task");
        work.UpdateTask(task.Id, task.Title, "", overrideCategory.Id, new DateOnly(2026, 10, 6));
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();

        var row = Assert.Single(window.GetVisualDescendants().OfType<Border>(), border =>
            border.Classes.Contains("backlog-row") && border.DataContext is TaskRowViewModel item && item.Id == task.Id);
        var button = Assert.Single(row.GetVisualDescendants().OfType<Button>(), item => item.Classes.Contains("row-title"));
        var icon = Assert.Single(button.GetVisualDescendants().OfType<WorkTypeIcon>());
        var title = Assert.Single(button.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.Classes.Contains("work-identity-title"));
        var relationship = Assert.Single(button.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.Classes.Contains("work-identity-context"));
        var pill = Assert.Single(button.GetVisualDescendants().OfType<CategoryPill>());
        var overrideIcon = Assert.Single(pill.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(),
            icon => icon.Classes.Contains("category-pill-override-icon") && icon.IsEffectivelyVisible);
        var categoryText = Assert.Single(pill.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == overrideCategory.Name);
        var date = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Classes.Contains("task-date"));

        Assert.Equal(14, icon.Bounds.Width);
        Assert.InRange(OriginInWindow(title, window).X - (OriginInWindow(icon, window).X + icon.Bounds.Width), 5, 7);
        Assert.InRange(CentreInWindow(icon, window).Y - CentreInWindow(title, window).Y, 0.75, 1.25);
        Assert.Equal(TextTrimming.CharacterEllipsis, relationship.TextTrimming);
        Assert.Equal(TextTrimming.CharacterEllipsis, categoryText.TextTrimming);
        Assert.True(pill.HasOverride);
        Assert.True(overrideIcon.Bounds.Width > 0);
        Assert.InRange(pill.Bounds.Width, 25, 240);
        Assert.True(
            OriginInWindow(button, window).X + button.Bounds.Width <= OriginInWindow(date, window).X,
            "The identity column must end before the right-aligned date column.");
        window.Close();
    }

    [AvaloniaFact]
    public void ProjectAndTaskIdentityIconsAreOpticallyAlignedWithTheirTitles()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTask(project.Id, "Plant bulbs");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        shell.Work!.Projects.Single().IsExpanded = true;
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();

        var projectButton = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button =>
            button.Classes.Contains("project-title") && button.DataContext is ProjectRowViewModel row && row.Id == project.Id);
        var taskButton = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button =>
            button.Classes.Contains("project-task-title") && button.DataContext is TaskRowViewModel row && row.Id == task.Id);

        AssertOpticallyAlignedTitleIcon(projectButton, window);
        AssertOpticallyAlignedTitleIcon(taskButton, window);
        window.Close();
    }

    [AvaloniaFact]
    public void CompactArchivedProjectKeepsLongRelationshipMetadataBeforeItsStateAndAction()
    {
        var work = new MemoryWorkspaceWork();
        var category = work.CreateCategory("A deliberately long archived category name that must truncate");
        var project = work.CreateProject("A compact archived project", "", category.Id, null);
        work.ArchiveProject(project.Id);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Archive").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell, Width = 760, Height = 600 };
        window.Show();

        var row = Assert.Single(window.GetVisualDescendants().OfType<Border>(), border =>
            border.Classes.Contains("archive-row")
            && border.DataContext is ArchivedWorkRowViewModel item
            && item.IsProject);
        var identity = Assert.Single(row.GetVisualDescendants().OfType<StackPanel>(), panel =>
            panel.DataContext is ArchivedWorkRowViewModel item && item.IsProject);
        var relationship = Assert.Single(identity.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.Classes.Contains("work-identity-context"));
        var metadata = Assert.Single(identity.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.Text == ((ArchivedWorkRowViewModel)row.DataContext!).Project!.MetadataText);
        var state = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Archived Project");
        var restore = ButtonByAutomationId(window, $"project-restore-{project.Id}");

        AssertWorkTypeIcon(identity, WorkType.Project);
        Assert.Equal(TextTrimming.CharacterEllipsis, relationship.TextTrimming);
        Assert.Equal(TextTrimming.CharacterEllipsis, metadata.TextTrimming);
        Assert.True(
            OriginInWindow(identity, window).X + identity.Bounds.Width <= OriginInWindow(state, window).X,
            "Archived Project identity must end before its state column.");
        Assert.True(
            OriginInWindow(state, window).X + state.Bounds.Width <= OriginInWindow(restore, window).X,
            "Archived Project state must end before its action column.");
        window.Close();
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
        Assert.Equal(12, picker.FontSize);
        picker.Text = "2026-10-12";
        Assert.Null(work.Read().Tasks.Single().DueDate);
        Assert.True(shell.Work!.RunScheduledAutosave());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new DateOnly(2026, 10, 12), work.Read().Tasks.Single().DueDate);

        picker.IsDropDownOpen = true;
        picker.SelectedDate = new DateTime(2026, 10, 13);
        picker.IsDropDownOpen = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("2026-10-13", shell.Work!.Date);
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
    public void DateEditorKeepsFixedWidthAndRejectsInvalidDraftWithFieldFeedback()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        work.CreateTask(project.Id, "Plant bulbs");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();
        Activate(window, NamedButton(window, "Plant bulbs"));

        var picker = Assert.Single(window.GetVisualDescendants().OfType<CalendarDatePicker>());
        Assert.Equal(150, picker.Bounds.Width);
        var editor = Assert.Single(picker.GetVisualDescendants().OfType<TextBox>());
        Assert.True(editor.Focus());
        editor.Text = "f";
        Assert.Equal("f", Assert.IsType<InspectorDatePicker>(picker).RawText);
        Assert.Equal("f", shell.Work!.Date);
        Assert.Equal(150, picker.Bounds.Width);
        Assert.Equal("f", shell.Work!.Date);
        Assert.False(shell.Work.RunScheduledAutosave());
        Dispatcher.UIThread.RunJobs();

        Assert.Null(work.Read().Tasks.Single().DueDate);
        Assert.Equal("f", shell.Work!.Date);
        Assert.NotEqual("Changes saved.", shell.Work.Message);
        Assert.Contains("error", picker.Classes);
        Assert.True(picker.IsKeyboardFocusWithin);
        Assert.Equal("f", editor.Text);
        var validation = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Classes.Contains("validation-message")
                && text.Text == "Enter a valid date as YYYY-MM-DD, or leave it empty.");
        Assert.True(validation.IsEffectivelyVisible);
        Assert.Equal(validation.Text, AutomationProperties.GetHelpText(picker));

        editor.Text = "2026-10-12";
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain("error", picker.Classes);
        Assert.False(validation.IsEffectivelyVisible);

        Assert.True(shell.Work.RunScheduledAutosave());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new DateOnly(2026, 10, 12), work.Read().Tasks.Single().DueDate);

        Assert.True(editor.Focus());
        editor.Text = "f";
        Assert.False(shell.Work.RunScheduledAutosave());
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new DateOnly(2026, 10, 12), work.Read().Tasks.Single().DueDate);
        Assert.Equal("f", shell.Work.Date);
        Assert.Equal("f", editor.Text);
        Assert.True(shell.Work.HasDateValidationError);
        window.Close();
    }

    [AvaloniaFact]
    public void LeavingAnUnchangedDatedInspectorDoesNotRequestDraftResolution()
    {
        var work = new MemoryWorkspaceWork();
        work.CreateProject("Dated project", "", "home", new DateOnly(2026, 10, 12));
        work.CreateProject("Next project", "", "work", new DateOnly(2026, 11, 5));
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        Activate(window, NamedButton(window, "Dated project"));
        Dispatcher.UIThread.RunJobs();
        Assert.False(shell.Work!.IsDirty);
        var picker = Assert.Single(window.GetVisualDescendants().OfType<CalendarDatePicker>());
        Assert.True(Assert.Single(picker.GetVisualDescendants().OfType<TextBox>()).Focus());
        Activate(window, NamedButton(window, "Next project"));
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.Work.NeedsDecision);
        Assert.Equal("Next project", shell.Work.Title);
        Assert.Equal("2026-11-05", shell.Work.Date);
        Assert.All(work.Read().Projects, project => Assert.True(project.Title is "Dated project" or "Next project"));
        window.Close();
    }

    [AvaloniaFact]
    public void LeavingAnUnchangedTaskTitleDoesNotRequestDraftResolution()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        work.CreateTask(project.Id, "Plant bulbs");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        Activate(window, NamedButton(window, "Plant bulbs"));
        Dispatcher.UIThread.RunJobs();
        Assert.False(shell.Work!.IsDirty);
        var title = window.FindControl<TextBox>("DraftTitle")!;
        Assert.True(title.Focus());
        Assert.Equal("Plant bulbs", title.Text);
        Activate(window, NamedButton(window, "Garden"));
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.Work.NeedsDecision);
        Assert.Equal("Garden", shell.Work.Title);
        Assert.Equal("Plant bulbs", work.Read().Tasks.Single().Title);
        window.Close();
    }

    [AvaloniaFact]
    public void ClickingAmongUnchangedInspectorsNeverRequestsDraftResolution()
    {
        var work = new MemoryWorkspaceWork();
        var datedProject = work.CreateProject("Dated project", "Dated description", "home", new DateOnly(2026, 10, 12));
        var undatedProject = work.CreateProject("Undated project", "Undated description", "work", null);
        work.CreateTask(datedProject.Id, "Undated task");
        var datedTask = work.CreateTask(undatedProject.Id, "Dated task");
        work.UpdateTask(datedTask.Id, datedTask.Title, datedTask.Description, datedTask.ExplicitCategoryId, new DateOnly(2026, 11, 5));
        work.CreateStandaloneTask("Standalone task", "Standalone description", "work", new DateOnly(2026, 12, 1));
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var sequence = new[] { "Dated project", "Undated task", "Undated project", "Dated task" };
        for (var iteration = 0; iteration < 10; iteration++)
        {
            foreach (var title in sequence)
            {
                Activate(window, NamedButton(window, title));
                Dispatcher.UIThread.RunJobs();
                Assert.False(shell.Work!.NeedsDecision, $"Selection of {title} requested draft resolution on iteration {iteration}.");
                Assert.False(shell.Work.IsDirty, $"Selection of {title} produced an untouched dirty draft on iteration {iteration}.");

                Assert.True(window.FindControl<TextBox>("DraftTitle")!.Focus());
                var markdownPreview = window.FindControl<MarkdownPreviewSurface>("MarkdownPreviewButton")!;
                Assert.Equal(shell.Work.MarkdownPreviewAutomationName, AutomationProperties.GetName(markdownPreview));
                markdownPreview.RaiseEvent(new RoutedEventArgs(MarkdownPreviewSurface.ActivatedEvent));
                var description = window.GetVisualDescendants().OfType<TextBox>()
                    .Single(text => AutomationProperties.GetName(text) == "Description, Markdown source");
                Assert.True(description.Focus());
                var dateEditor = Assert.Single(window.FindControl<CalendarDatePicker>("DraftDate")!
                    .GetVisualDescendants().OfType<TextBox>());
                Assert.True(dateEditor.Focus());
                var category = window.GetVisualDescendants().OfType<ComboBox>()
                    .Single(combo => AutomationProperties.GetName(combo) == "Category");
                Assert.True(category.Focus());
                if (shell.Work.ShowTaskContext)
                {
                    var context = window.GetVisualDescendants().OfType<ComboBox>()
                        .Single(combo => AutomationProperties.GetName(combo) == "Task context");
                    Assert.True(context.Focus());
                }
            }
        }

        shell.PrimaryNavigation.Single(item => item.Title == "Categories").SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Activate(window, NamedButton(window, "Standalone task"));
        Dispatcher.UIThread.RunJobs();
        Assert.False(shell.Work!.IsDirty);
        Assert.True(window.FindControl<TextBox>("DraftTitle")!.Focus());
        Assert.True(window.GetVisualDescendants().OfType<ComboBox>()
            .Single(combo => AutomationProperties.GetName(combo) == "Category").Focus());
        Assert.True(window.GetVisualDescendants().OfType<ComboBox>()
            .Single(combo => AutomationProperties.GetName(combo) == "Task context").Focus());

        Activate(window, ButtonByAutomationId(window, "category-selection-work"));
        Dispatcher.UIThread.RunJobs();
        Assert.False(shell.Work.NeedsDecision);
        Assert.False(shell.Work.IsDirty);
        Assert.True(window.FindControl<TextBox>("DraftTitle")!.Focus());
        Activate(window, ButtonByAutomationId(window, "category-selection-home"));
        Dispatcher.UIThread.RunJobs();
        Assert.False(shell.Work.NeedsDecision);
        Assert.False(shell.Work.IsDirty);
        Assert.Equal("Home", shell.Work.Title);

        Assert.False(shell.Work!.NeedsDecision);
        Assert.False(shell.Work.IsDirty);
        window.Close();
    }

    [AvaloniaFact]
    public void LeavingAnUntouchedCreateDraftDoesNotRequestDraftResolution()
    {
        var work = new MemoryWorkspaceWork();
        work.CreateProject("Existing project", "", "home", null);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        Activate(window, NamedButton(window, "New project"));
        Dispatcher.UIThread.RunJobs();
        Assert.False(shell.Work!.IsDirty);
        Activate(window, NamedButton(window, "Existing project"));
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.Work.NeedsDecision);
        Assert.Equal("Existing project", shell.Work.Title);
        window.Close();
    }

    [AvaloniaFact]
    public void DiscardingInvalidDateCannotDirtyTheNextInspectorThroughAQueuedRestore()
    {
        var work = new MemoryWorkspaceWork();
        work.CreateProject("First project", "", "home", new DateOnly(2026, 10, 12));
        work.CreateProject("Second project", "", "work", new DateOnly(2026, 11, 5));
        work.CreateProject("Third project", "", "home", null);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        Activate(window, NamedButton(window, "First project"));
        Dispatcher.UIThread.RunJobs();
        var date = window.FindControl<CalendarDatePicker>("DraftDate")!;
        var editor = Assert.Single(date.GetVisualDescendants().OfType<TextBox>());
        Assert.True(editor.Focus());
        editor.Text = "f";
        Activate(window, NamedButton(window, "Second project"));
        Assert.True(shell.Work!.NeedsDecision);
        shell.Work.DiscardAndLeaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Second project", shell.Work.Title);
        Assert.Equal("2026-11-05", shell.Work.Date);
        Assert.False(shell.Work.IsDirty);
        Activate(window, NamedButton(window, "Third project"));
        Dispatcher.UIThread.RunJobs();
        Assert.False(shell.Work.NeedsDecision);
        window.Close();
    }

    [AvaloniaFact]
    public void CompletingBacklogRowFlushesPendingFieldsThenFocusesRemainingCompletionControl()
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
        Assert.False(shell.Work!.NeedsDecision);
        Assert.True(work.Read().Tasks.Single(task => task.Id == completing.Id).IsComplete);
        Assert.Equal("Unsaved completing", work.Read().Tasks.Single(task => task.Id == completing.Id).Title);
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

    [AvaloniaFact]
    public void CaptureLossAndWindowDeactivationClearTheInsertionRule()
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
        DragToTarget(window, source, target);
        AssertInsertionLine(targetRow);

        var pointer = new Pointer(1, PointerType.Mouse, isPrimary: true);
        window.RaiseEvent(new PointerCaptureLostEventArgs(window, pointer)
        {
            RoutedEvent = InputElement.PointerCaptureLostEvent,
        });
        Dispatcher.UIThread.RunJobs();
        AssertNoInsertionLine(targetRow, new Thickness(1), Color.Parse("#272D39"));

        DragToTarget(window, source, target);
        AssertInsertionLine(targetRow);
        window.Hide();
        Dispatcher.UIThread.RunJobs();
        AssertNoInsertionLine(targetRow, new Thickness(1), Color.Parse("#272D39"));
        Assert.Equal([first.Id, second.Id], work.Read().Projects.Select(project => project.Id));
        window.Close();
    }

    [AvaloniaFact]
    public void CategoriesUseSharedListRowsAndContextualInspectorEditing()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("A deliberately long project title", "", "work", null);
        work.CreateTask(project.Id, "Attached task");
        var shortProject = work.CreateProject("Potato Project", "", "work", null);
        for (var index = 0; index < 8; index++) work.CreateTask(shortProject.Id, $"Potato task {index}");
        work.CreateStandaloneTask("A deliberately long standalone task title", "", "work", new DateOnly(2026, 10, 6));
        var shell = new ShellViewModel(
            work,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));
        shell.PrimaryNavigation.Single(item => item.Title == "Categories").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.Text == "Select a project, task, or category\nto see its details");

        var workCategory = window.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("category-row")
                && Assert.IsType<CategoryGroupViewModel>(border.DataContext).Id == "work");
        Assert.Empty(workCategory.GetVisualDescendants().OfType<TextBox>());
        Assert.DoesNotContain(workCategory.GetVisualDescendants().OfType<Button>(), button =>
        {
            var name = AutomationProperties.GetName(button) ?? string.Empty;
            return name.StartsWith("Rename ", StringComparison.Ordinal)
                || name.StartsWith("Delete ", StringComparison.Ordinal);
        });

        var projectButton = NamedButton(window, "A deliberately long project title");
        var taskButton = NamedButton(window, "A deliberately long standalone task title");
        var categoryButton = ButtonByAutomationId(window, "category-selection-work");
        var categoryHandle = ButtonByAutomationId(window, "category-reorder-work");
        var categoryDisclosure = NamedButton(window, "Collapse Work category");
        Assert.Single(categoryHandle.GetVisualDescendants().OfType<Grid>(), grid =>
            grid.Bounds.Width == 8 && grid.Bounds.Height == 12);
        var categoryHandleOrigin = OriginInWindow(categoryHandle, window);
        var categoryDisclosureOrigin = OriginInWindow(categoryDisclosure, window);
        Assert.InRange(categoryDisclosureOrigin.X - categoryHandleOrigin.X - categoryHandle.Bounds.Width, 3, 5);
        Assert.Contains("project-title", projectButton.Classes);
        Assert.Contains("task-title", taskButton.Classes);
        AssertWorkTypeIcon(categoryButton, WorkType.Category);
        AssertWorkTypeIcon(projectButton, WorkType.Project);
        AssertWorkTypeIcon(taskButton, WorkType.Task);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(projectButton.Background).Color);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(taskButton.Background).Color);
        AssertTitleMetadataGap(projectButton.GetVisualAncestors().OfType<Border>()
            .First(border => border.Classes.Contains("category-project-row")), "A deliberately long project title", "0/1 tasks", window);
        var shortProjectButton = NamedButton(window, "Potato Project");
        var longCount = Assert.Single(projectButton.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "0/1 tasks");
        var shortCount = Assert.Single(shortProjectButton.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "0/8 tasks");
        var longCountRight = OriginInWindow(longCount, window).X + longCount.Bounds.Width;
        var shortCountRight = OriginInWindow(shortCount, window).X + shortCount.Bounds.Width;
        Assert.InRange(Math.Abs(longCountRight - shortCountRight), 0, 0.5);
        Assert.Equal(12, longCount.FontSize);
        Assert.Equal(TextAlignment.Right, longCount.TextAlignment);
        Assert.Contains("Project A deliberately long project title", ControlAutomationPeer.CreatePeerForElement(projectButton).GetName(), StringComparison.Ordinal);
        Assert.Contains("Not started", ControlAutomationPeer.CreatePeerForElement(projectButton).GetName(), StringComparison.Ordinal);
        AssertTitleMetadataGap(taskButton.GetVisualAncestors().OfType<Border>()
            .First(border => border.Classes.Contains("category-task-row")), "A deliberately long standalone task title", "6 Oct 2026", window);

        Activate(window, NamedButton(window, "New category"));
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "New category");
        Assert.True(window.FindControl<TextBox>("DraftTitle")!.IsFocused);
        window.KeyTextInput("Uncommitted category");
        Assert.DoesNotContain(work.Read().Categories, category => category.Name == "Uncommitted category");
        Activate(window, NamedButton(window, "Cancel editing"));
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(work.Read().Categories, category => category.Name == "Uncommitted category");
        Assert.True(NamedButton(window, "New category").IsFocused);

        Activate(window, ButtonByAutomationId(window, "category-selection-work"));
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Category details");
        Assert.Equal("Work", window.FindControl<TextBox>("DraftTitle")!.Text);
        Assert.NotNull(NamedButton(window, "Delete category"));
        window.Close();
    }

    [AvaloniaFact]
    public void CanvasAndInspectorScrollbarsReserveAGutterAtMinimumWindowSize()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Inspect me", new string('x', 300), "home", new DateOnly(2026, 10, 6));
        for (var index = 0; index < 9; index++) work.CreateCategory($"Category {index}");
        for (var index = 0; index < 8; index++)
        {
            work.CreateProject($"Project {index}", "", "work", null);
            work.CreateStandaloneTask($"Backlog task {index}", "", "work", null);
        }
        foreach (var completed in work.Read().Tasks.Where(item => item.Title.StartsWith("Backlog", StringComparison.Ordinal)).Take(5))
            work.CompleteTask(completed.Id);
        var shell = new ShellViewModel(work);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();

        var currentView = Assert.IsType<Grid>(window.FindControl<Grid>("CurrentViewRegion"));
        foreach (var viewName in new[] { "Projects", "Backlog", "Categories", "Completed" })
        {
            shell.PrimaryNavigation.Single(item => item.Title == viewName).SelectCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var canvasScroll = Assert.Single(currentView.GetVisualChildren().OfType<ScrollViewer>(), viewer => viewer.IsVisible);
            Assert.Equal(32, Assert.IsAssignableFrom<Control>(canvasScroll.Content).Margin.Right);
            if (viewName == "Categories")
            {
                var categoryRow = canvasScroll.GetVisualDescendants().OfType<Border>()
                    .First(border => border.Classes.Contains("category-row"));
                AssertRightScrollbarGutter(canvasScroll, categoryRow, window, 12);
            }
        }

        shell.Work!.SelectTask(task.Id);
        Dispatcher.UIThread.RunJobs();
        var inspector = Assert.IsType<Border>(window.FindControl<Border>("InspectorRegion"));
        var inspectorScroll = Assert.Single(inspector.GetVisualChildren().OfType<Grid>()
            .SelectMany(grid => grid.GetVisualChildren().OfType<ScrollViewer>()), viewer => viewer.IsVisible);
        Assert.Equal(32, Assert.IsAssignableFrom<Control>(inspectorScroll.Content).Margin.Right);
        AssertRightScrollbarGutter(inspectorScroll, window.FindControl<TextBox>("DraftTitle")!, window, 12);
        window.Close();
    }

    [AvaloniaFact]
    public void InspectorTitleTextStaysInsideItsFocusBorderAcrossWorkContexts()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Aligned project", "", "home", null);
        var task = work.CreateStandaloneTask("Aligned task", "", "work", null);
        var shell = new ShellViewModel(work);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();

        foreach (var select in new Action[]
                 {
                     () => shell.Work!.SelectProject(project.Id),
                     () => shell.Work!.SelectTask(task.Id),
                 })
        {
            select();
            Dispatcher.UIThread.RunJobs();
            var titleText = Assert.Single(window.FindControl<TextBox>("DraftTitle")!
                .GetVisualDescendants().OfType<TextPresenter>());
            var titleBox = window.FindControl<TextBox>("DraftTitle")!;
            var titleOrigin = titleText.TranslatePoint(default, window)!.Value;
            var titleBoxOrigin = titleBox.TranslatePoint(default, window)!.Value;
            Assert.InRange(titleOrigin.X - titleBoxOrigin.X, 4, 6);
        }

        window.Close();
    }

    [AvaloniaFact]
    public void InspectorActionsClearTheViewportBottomAtMaximumScroll()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Inspect me", new string('x', 600), "home", null);
        var shell = new ShellViewModel(work);
        shell.Work!.SelectTask(task.Id);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var inspector = window.FindControl<Border>("InspectorRegion")!;
        var inspectorScroll = Assert.Single(inspector.GetVisualChildren().OfType<Grid>()
            .SelectMany(grid => grid.GetVisualChildren().OfType<ScrollViewer>()), viewer => viewer.IsVisible);
        inspectorScroll.Offset = new Vector(0, inspectorScroll.Extent.Height);
        Dispatcher.UIThread.RunJobs();
        var actions = window.FindControl<Grid>("InspectorActions")!;
        var viewportOrigin = inspectorScroll.TranslatePoint(default, window)!.Value;
        var actionsOrigin = actions.TranslatePoint(default, window)!.Value;
        var bottomClearance = viewportOrigin.Y + inspectorScroll.Bounds.Height - actionsOrigin.Y - actions.Bounds.Height;
        Assert.InRange(bottomClearance, 12, double.PositiveInfinity);
        window.Close();
    }

    [AvaloniaFact]
    public void CategoriesViewGroupsExistingOrdersAndOffersAccessibleCategoryReorder()
    {
        var work = new MemoryWorkspaceWork();
        var homeFirst = work.CreateProject("Home first", "", "home", null);
        var workProject = work.CreateProject("Work project", "", "work", null);
        var homeSecond = work.CreateProject("Home second", "", "home", null);
        work.CreateStandaloneTask("Home older", "", "home", null);
        work.CreateStandaloneTask("Work task", "", "work", null);
        work.CreateStandaloneTask("Home newest", "", "home", null);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Categories").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        var categoryRows = window.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("category-row"))
            .ToArray();
        Assert.Equal(["Home", "Work"], categoryRows.Select(row => Assert.IsType<CategoryGroupViewModel>(row.DataContext).Name));
        var home = categoryRows.Single(row => Assert.IsType<CategoryGroupViewModel>(row.DataContext).Id == "home");
        Assert.Equal([homeFirst.Id, homeSecond.Id], home.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("category-project-row"))
            .Select(row => Assert.IsType<CategoryProjectRowViewModel>(row.DataContext).Project.Id));
        Assert.Equal(["Home newest", "Home older"], home.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("category-task-row"))
            .Select(row => Assert.IsType<CategoryTaskRowViewModel>(row.DataContext).Task.Title));
        Assert.DoesNotContain(workProject.Id, home.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("category-project-row"))
            .Select(row => Assert.IsType<CategoryProjectRowViewModel>(row.DataContext).Project.Id));
        var taskButton = NamedButton(window, "Home newest");
        Assert.Contains("Standalone. Category Home", ControlAutomationPeer.CreatePeerForElement(taskButton).GetName(), StringComparison.Ordinal);

        var workHandle = ButtonByAutomationId(window, "category-reorder-work");
        Activate(window, workHandle);
        var menu = Assert.IsType<MenuFlyout>(workHandle.Flyout);
        var items = menu.Items.OfType<MenuItem>().ToArray();
        Assert.Equal(
            ["Move Work to top of Categories", "Move Work up in Categories", "Move Work down in Categories", "Move Work to bottom of Categories"],
            items.Select(AutomationProperties.GetName));
        items.Single(item => item.Header?.ToString() == "Move to top").Command!.Execute(null);
        menu.Hide();
        Dispatcher.UIThread.RunJobs();
        Assert.True(ButtonByAutomationId(window, "category-reorder-work").IsFocused);
        Assert.Equal(["Work", "Home"], shell.Work!.CategoryGroups.Select(category => category.Name));
        Assert.Equal("Moved Work to position 1 of 2 in Categories.", shell.Work.ReorderAnnouncement);

        workHandle = ButtonByAutomationId(window, "category-reorder-work");
        var homeHandle = ButtonByAutomationId(window, "category-reorder-home");
        var homeRow = homeHandle.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("category-row"));
        DragToTarget(window, workHandle, homeHandle);
        AssertInsertionLine(homeRow);
        window.MouseUp(CentreInWindow(homeHandle, window), MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(["Home", "Work"], shell.Work.CategoryGroups.Select(category => category.Name));
        Assert.True(ButtonByAutomationId(window, "category-reorder-work").IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void ReferencedCategoryDeletionRequiresAReplacementAndMovesFocusIntoTheDecision()
    {
        var work = new MemoryWorkspaceWork();
        work.CreateProject("Garden", "", "home", null);
        work.CreateStandaloneTask("Home task", "", "home", null);
        var spare = work.CreateCategory("Spare");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Categories").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();

        Activate(window, ButtonByAutomationId(window, $"category-selection-{spare.Id}"));
        Activate(window, NamedButton(window, "Delete category"));
        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(work.Read().Categories, category => category.Name == "Spare");
        Assert.True(ButtonByAutomationId(window, "category-selection-work").IsFocused);

        Activate(window, ButtonByAutomationId(window, "category-selection-home"));
        Activate(window, NamedButton(window, "Delete category"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(shell.Work!.NeedsCategoryReplacement);
        var replacement = Assert.IsType<ComboBox>(window.FindControl<ComboBox>("CategoryReplacement"));
        Assert.True(replacement.IsFocused);
        Assert.Equal("Replacement category", AutomationProperties.GetName(replacement));
        Assert.Equal("work", shell.Work.CategoryReplacement?.Id);

        Activate(window, NamedButton(window, "Cancel category deletion"));
        Dispatcher.UIThread.RunJobs();
        Assert.False(shell.Work.NeedsCategoryReplacement);
        Assert.True(ButtonByAutomationId(window, "category-delete").IsFocused);

        Activate(window, NamedButton(window, "Delete category"));
        Dispatcher.UIThread.RunJobs();
        Activate(window, NamedButton(window, "Replace references and delete category"));
        Dispatcher.UIThread.RunJobs();
        Assert.False(shell.Work.NeedsCategoryReplacement);
        Assert.True(ButtonByAutomationId(window, "category-selection-work").IsFocused);
        Assert.DoesNotContain(work.Read().Categories, category => category.Id == "home");
        Assert.All(work.Read().Projects, project => Assert.Equal("work", project.CategoryId));
        Assert.Equal("work", Assert.Single(work.Read().Tasks).ExplicitCategoryId);
        window.Close();
    }

    [AvaloniaFact]
    public void TaskContextMismatchOffersPreserveOrAdoptBeforeMovingAndDetachMaterialisesCategory()
    {
        var work = new MemoryWorkspaceWork();
        var homeProject = work.CreateProject("Home project", "", "home", null);
        var workProject = work.CreateProject("Work project", "", "work", null);
        var task = work.CreateStandaloneTask("Move me", "", "home", null);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        var window = new MainWindow { DataContext = shell };
        window.Show();
        Activate(window, NamedButton(window, "Move me"));

        shell.Work!.TaskContextTarget = shell.Work.TaskContextChoices.Single(choice => choice.ProjectId == workProject.Id);
        Activate(window, NamedButton(window, "Attach to project"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(shell.Work.NeedsAttachmentChoice);
        var preserve = Assert.IsType<Button>(window.FindControl<Button>("PreserveTaskCategory"));
        Assert.True(preserve.IsFocused);
        Assert.Equal("Keep Home as override", AutomationProperties.GetName(preserve));
        Assert.Equal("Adopt Work", AutomationProperties.GetName(NamedButton(window, "Adopt Work")));
        Assert.Null(work.Read().Tasks.Single(item => item.Id == task.Id).ProjectId);

        Activate(window, NamedButton(window, "Cancel task move"));
        Dispatcher.UIThread.RunJobs();
        Assert.False(shell.Work.NeedsAttachmentChoice);
        Assert.True(shell.Work.HasInspector);
        Assert.True(Assert.IsType<Button>(window.FindControl<Button>("ChangeTaskContextButton")).IsFocused);
        Assert.Null(work.Read().Tasks.Single(item => item.Id == task.Id).ProjectId);

        Activate(window, NamedButton(window, "Attach to project"));
        Dispatcher.UIThread.RunJobs();
        Activate(window, NamedButton(window, "Adopt Work"));
        Dispatcher.UIThread.RunJobs();
        var adopted = work.Read().Tasks.Single(item => item.Id == task.Id);
        Assert.Equal(workProject.Id, adopted.ProjectId);
        Assert.Null(adopted.ExplicitCategoryId);

        shell.Work.TaskContextTarget = shell.Work.TaskContextChoices.Single(choice => choice.ProjectId is null);
        Activate(window, NamedButton(window, "Detach to standalone"));
        var detached = work.Read().Tasks.Single(item => item.Id == task.Id);
        Assert.Null(detached.ProjectId);
        Assert.Equal("work", detached.ExplicitCategoryId);

        shell.Work.TaskContextTarget = shell.Work.TaskContextChoices.Single(choice => choice.ProjectId == homeProject.Id);
        Activate(window, NamedButton(window, "Attach to project"));
        Assert.True(shell.Work.NeedsAttachmentChoice);
        Activate(window, NamedButton(window, "Cancel task move"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(shell.Work.HasInspector);
        Assert.True(Assert.IsType<Button>(window.FindControl<Button>("ChangeTaskContextButton")).IsFocused);
        window.Close();
    }

    private static void AssertStatusColour(Window window, string status, string expectedClass, Color expectedColour)
    {
        var text = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), item => item.Text == status);
        Assert.Contains("status-marker", text.Classes);
        Assert.Contains(expectedClass, text.Classes);
        Assert.Equal(expectedColour, Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);
    }

    private static void AssertTitleMetadataGap(Control row, string title, string metadata, Window window)
    {
        var titleText = row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == title);
        var metadataText = row.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == metadata);
        var titleOrigin = titleText.TranslatePoint(default, window)!.Value;
        var metadataOrigin = metadataText.TranslatePoint(default, window)!.Value;
        var gap = metadataOrigin.X - titleOrigin.X - titleText.Bounds.Width;
        Assert.True(gap >= 12, $"The title-to-metadata gap was {gap:0.##} pixels.");
    }

    private static void AssertRightScrollbarGutter(ScrollViewer viewer, Control content, Window window, double minimum)
    {
        var scrollbar = Assert.Single(viewer.GetVisualDescendants().OfType<ScrollBar>(), bar =>
            bar.Orientation == Orientation.Vertical && bar.IsEffectivelyVisible);
        var contentOrigin = content.TranslatePoint(default, window)!.Value;
        var scrollbarOrigin = scrollbar.TranslatePoint(default, window)!.Value;
        var gap = scrollbarOrigin.X - contentOrigin.X - content.Bounds.Width;
        Assert.True(gap >= minimum, $"The content-to-scrollbar gap was {gap:0.##} pixels.");
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
        var presenter = Assert.Single(title.GetVisualChildren().OfType<ContentPresenter>());
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
        var dots = Assert.Single(
            handle.GetVisualDescendants().OfType<Grid>(),
            grid => grid.Bounds.Width == 8 && grid.Bounds.Height == 12);
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

    private static void ClickTaskRowSurface(Window window, string rowClass, string taskTitle)
    {
        var title = NamedButton(window, taskTitle);
        var row = title.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains(rowClass));
        var point = RowSurfacePoint(row, window);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void TapTaskRowSurface(Window window, string rowClass, string taskTitle)
    {
        var title = NamedButton(window, taskTitle);
        var row = title.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains(rowClass));
        var translated = row.TranslatePoint(new Point(row.Bounds.Width / 2, 2), window);
        Assert.True(translated.HasValue);
        var point = translated.Value;
        var touch = window.TouchBegin(point, RawInputModifiers.None);
        window.TouchEnd(touch, point, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void ClickRowVerticalEdge(Window window, string rowClass, string title, bool top)
    {
        var titleButton = NamedButton(window, title);
        var row = titleButton.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains(rowClass));
        var y = top ? 2 : row.Bounds.Height - 2;
        var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, y), window);
        Assert.True(point.HasValue);
        ClickSurface(window, point.Value);
    }

    private static void ClickSurface(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Window window, Control control)
    {
        var point = CentreInWindow(control, window);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.LeftMouseButton);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static Button NamedButton(Window window, string name)
    {
        var buttons = window.GetVisualDescendants().OfType<Button>().ToArray();
        var exact = buttons.Where(button => AutomationProperties.GetName(button) == name).ToArray();
        if (exact.Length == 1)
        {
            return exact[0];
        }

        return Assert.Single(buttons, button =>
            button.Classes.Contains("row-title")
            && button.GetVisualDescendants().OfType<TextBlock>().Any(text =>
                text.Classes.Contains("work-identity-title") && text.Text == name));
    }

    private static Button ButtonByAutomationId(Window window, string id) => Assert.Single(
        window.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetAutomationId(button) == id);

    private static ToggleButton ToggleByAutomationId(Window window, string id) => Assert.Single(
        window.GetVisualDescendants().OfType<ToggleButton>(), button => AutomationProperties.GetAutomationId(button) == id);

    private static void AssertWorkTypeIcon(Control root, WorkType expected)
    {
        var icon = Assert.Single(root.GetVisualDescendants().OfType<WorkTypeIcon>(), item => item.IsVisible);
        Assert.Equal(expected, icon.WorkType);
        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(icon));
        Assert.Null(AutomationProperties.GetName(icon));
    }

    private static void AssertRenderedWorkTypes(
        Window window,
        ThemeVariant theme,
        Color quiet,
        params WorkType[] expected)
    {
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(theme, window.ActualThemeVariant);
        var icons = window.GetVisualDescendants().OfType<WorkTypeIcon>()
            .Where(icon => icon.IsEffectivelyVisible)
            .ToArray();
        foreach (var workType in expected)
        {
            var matches = icons.Where(candidate => candidate.WorkType == workType).ToArray();
            Assert.NotEmpty(matches);
            Assert.All(matches, icon =>
            {
                var glyph = Assert.Single(icon.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>(), path => path.IsVisible);
                Assert.Equal(quiet, Assert.IsAssignableFrom<ISolidColorBrush>(glyph.Stroke ?? glyph.Fill).Color);
            });
        }

        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height),
            new Vector(96, 96));
        bitmap.Render(window);
    }

    private static void SelectView(ShellViewModel shell, string title)
    {
        var navigation = title == "Bin"
            ? shell.BinNavigation
            : shell.PrimaryNavigation.Single(item => item.Title == title);
        navigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Activate(Window window, Button button)
    {
        Assert.True(button.Focus());
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Tab(Window window)
    {
        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static TextBox QuickField(Window window) => Assert.Single(window.GetVisualDescendants().OfType<TextBox>(), b => b.Classes.Contains("quick-add"));

    private static Border RowForTask(Window window, string rowClass, string taskId) => Assert.Single(
        window.GetVisualDescendants().OfType<Border>(),
        row => row.Classes.Contains(rowClass) && row.GetVisualDescendants().OfType<ToggleButton>().Any(toggle =>
            AutomationProperties.GetAutomationId(toggle) == $"task-completion-{taskId}"));

    private static void AssertIncompleteTaskRowContract(
        Window window,
        Border row,
        string taskId,
        string titleText,
        string relationshipDisplay,
        string dateText)
    {
        var completion = Assert.Single(row.GetVisualDescendants().OfType<ToggleButton>(), toggle =>
            AutomationProperties.GetAutomationId(toggle) == $"task-completion-{taskId}");
        var title = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == titleText);
        var titleButton = Assert.Single(row.GetVisualDescendants().OfType<Button>(), button =>
            button.Classes.Contains("row-title"));
        Assert.Contains($"Task {titleText}", AutomationProperties.GetName(titleButton), StringComparison.Ordinal);
        Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == relationshipDisplay);
        var date = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.Classes.Contains("task-date") && text.Text == dateText);
        var today = Assert.Single(row.GetVisualDescendants().OfType<ToggleButton>(), toggle =>
            AutomationProperties.GetAutomationId(toggle) == $"task-today-{taskId}");
        var completionX = CentreInWindow(completion, window).X;
        var titleX = CentreInWindow(title, window).X;
        var dateX = CentreInWindow(date, window).X;
        var todayX = CentreInWindow(today, window).X;
        Assert.True(completionX < titleX, $"Completion {completionX} should lead title {titleX}.");
        Assert.True(titleX < dateX, $"Title {titleX} should lead date {dateX}.");
        Assert.True(dateX < todayX, $"Date {dateX} should lead trailing Today action {todayX}.");
        Assert.Equal(12, date.FontSize);
    }

    private static Point CentreInWindow(Control control, Window window)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        Assert.True(point.HasValue);
        return point.Value;
    }

    private static void AssertOpticallyAlignedTitleIcon(Button identity, Window window)
    {
        var icon = Assert.Single(identity.GetVisualDescendants().OfType<WorkTypeIcon>());
        var title = Assert.Single(identity.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.Classes.Contains("work-identity-title"));
        var iconY = CentreInWindow(icon, window).Y;
        var titleY = CentreInWindow(title, window).Y;
        Assert.InRange(
            iconY - titleY,
            0.75,
            1.25);
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

    private static Point TrailingSurfacePoint(Control surface, Window window)
    {
        var point = surface.TranslatePoint(new Point(surface.Bounds.Width - 2, surface.Bounds.Height / 2), window);
        Assert.True(point.HasValue);
        return point.Value;
    }

    private sealed class ManualInspectorAutosaveScheduler : IInspectorAutosaveScheduler
    {
        private ScheduledAutosave? _current;
        public List<ScheduledAutosave> Pending { get; } = [];
        public int ScheduleCount { get; private set; }
        public TimeSpan Delay { get; private set; }

        public void Schedule(TimeSpan delay, long revision, Action<long> callback)
        {
            Delay = delay;
            ScheduleCount++;
            _current = new(revision, callback);
            Pending.Add(_current);
        }

        public void Cancel() => _current = null;
        public void Dispose() => Cancel();
        public void FireCurrent() => _current?.Callback(_current.Revision);
    }

    private sealed record ScheduledAutosave(long Revision, Action<long> Callback);

}
