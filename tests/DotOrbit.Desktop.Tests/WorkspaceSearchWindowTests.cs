using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class WorkspaceSearchWindowTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SearchShowsFiveCanonicalRowsAndAnnouncesCountsInBothThemes(bool light)
    {
        var app = Assert.IsType<App>(Application.Current);
        var initialTheme = app.RequestedThemeVariant;
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Match project", "", "home", null);
        var active = work.CreateTask(project.Id, "Match active task");
        work.SetTaskTodayLane(active.Id, TodayLane.Planned);
        var done = work.CreateStandaloneTask("Match completed task", "", "home", null);
        work.CompleteTask(done.Id);
        var archived = work.CreateStandaloneTask("Match archive", "", "work", null);
        work.CompleteTask(archived.Id);
        work.ArchiveTask(archived.Id);
        var removed = work.CreateStandaloneTask("Match bin", "", "work", null);
        work.MoveTaskToBin(removed.Id);
        var shell = new ShellViewModel(work);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        try
        {
            app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
            window.Show();
            var navigation = Visible<RadioButton>(window, "navigation-search");
            Assert.Equal("Open Search", AutomationProperties.GetName(navigation));
            Activate(window, navigation);
            var search = Visible<TextBox>(window, "workspace-search-field");
            Assert.True(search.IsFocused);
            var status = Visible<TextBlock>(window, "workspace-search-status");
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
            Assert.Equal("Enter a word or prefix to find Projects and Tasks.", status.Text);

            search.Text = "mat";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("5 results.", status.Text);
            var surface = search.GetVisualAncestors().OfType<ScrollViewer>().First();
            var headings = surface.GetVisualDescendants().OfType<TextBlock>()
                .Where(item => item.IsEffectivelyVisible && item.Classes.Contains("eyebrow"));
            Assert.Equal(["Projects", "Backlog", "Completed", "Archive", "Bin"], headings.Select(item => item.Text));
            Assert.Single(surface.GetVisualDescendants().OfType<Border>(), item => item.Classes.Contains("project-header-row"));
            Assert.Single(surface.GetVisualDescendants().OfType<Border>(), item => item.Classes.Contains("backlog-row"));
            Assert.Single(surface.GetVisualDescendants().OfType<Border>(), item => item.Classes.Contains("completed-row"));
            Assert.Single(surface.GetVisualDescendants().OfType<Border>(), item => item.Classes.Contains("archive-row"));
            Assert.Single(surface.GetVisualDescendants().OfType<Border>(), item => item.Classes.Contains("bin-row"));
            Assert.True(Visible<ToggleButton>(window, $"task-today-{active.Id}").IsChecked);
            Assert.True(Visible<ToggleButton>(window, $"task-completion-{done.Id}").IsChecked);
            Assert.Equal("Restore", Visible<Button>(window, $"task-restore-{archived.Id}").Content);
            using var render = new RenderTargetBitmap(new PixelSize(1120, 600), new Vector(96, 96));
            render.Render(window);

            search.Text = "unrelated";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("No matching Projects or Tasks.", status.Text);
            Assert.Empty(headings);
            var clear = Assert.Single(surface.GetVisualDescendants().OfType<Button>(),
                button => AutomationProperties.GetName(button) == "Clear workspace search");
            Activate(window, clear);
            Assert.Equal(string.Empty, search.Text);
            Assert.True(search.IsFocused);
            Assert.Equal("Enter a word or prefix to find Projects and Tasks.", status.Text);
            Assert.Equal(TodayLane.Planned, work.Read().Tasks.Single(task => task.Id == active.Id).TodayLane);
        }
        finally { window.Close(); app.RequestedThemeVariant = initialTheme; }
    }

    [AvaloniaTheory]
    [InlineData("Projects")]
    [InlineData("Backlog")]
    [InlineData("Completed")]
    [InlineData("Archive")]
    [InlineData("Bin")]
    public void KeyboardActivationFocusesCanonicalIdentityAndRespectsBinRestoreBoundary(string destination)
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Search target project", "", "home", null);
        var task = work.CreateStandaloneTask("Search target task", "", "work", null);
        if (destination is "Completed" or "Archive") work.CompleteTask(task.Id);
        if (destination == "Archive") work.ArchiveTask(task.Id);
        if (destination == "Bin") work.MoveTaskToBin(task.Id);
        var shell = new ShellViewModel(work);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        try
        {
            window.Show();
            Activate(window, Visible<RadioButton>(window, "navigation-search"));
            Visible<TextBox>(window, "workspace-search-field").Text = "target";
            Dispatcher.UIThread.RunJobs();
            var id = destination switch
            {
                "Projects" => $"project-selection-{project.Id}",
                "Bin" => $"bin-selection-task-{task.Id}",
                _ => $"task-selection-{task.Id}",
            };
            var result = Visible<Button>(window, id);
            Assert.Contains("Search target", AutomationProperties.GetName(result), StringComparison.Ordinal);
            Activate(window, result);
            Assert.Equal(destination, shell.ViewTitle);
            var canonicalTarget = Visible<Control>(window, id);
            Assert.True(canonicalTarget.IsFocused);
            Assert.NotSame(result, canonicalTarget);
            Assert.Equal(destination != "Bin", shell.Work!.HasInspector);
            if (destination == "Bin")
                Assert.Single(work.ReadTaskBin(), item => item.Task.Id == task.Id);
            else
                Assert.Equal(destination == "Projects" ? project.Title : task.Title, shell.Work.Title);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void SearchOmitsReorderAndDisclosureSpaceAndRetainsCanonicalControls(bool projects)
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Match project", "", "home", null);
        var task = work.CreateStandaloneTask("Match task", "", "home", null);
        var shell = new ShellViewModel(work);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        try
        {
            window.Show();
            Activate(window, Visible<RadioButton>(window, "navigation-search"));
            Visible<TextBox>(window, "workspace-search-field").Text = "match";
            Dispatcher.UIThread.RunJobs();
            var id = projects ? project.Id : task.Id;
            var handleId = (projects ? "project-reorder-" : "backlog-reorder-") + id;
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button =>
                button.IsEffectivelyVisible && AutomationProperties.GetAutomationId(button) == handleId);
            var title = Visible<Button>(window, (projects ? "project-selection-" : "task-selection-") + id);
            var row = title.GetVisualAncestors().OfType<Border>().First(border =>
                border.Classes.Contains(projects ? "project-header-row" : "backlog-row"));
            var layout = Assert.Single(row.GetVisualChildren().OfType<Grid>());
            Assert.Equal(0, layout.ColumnDefinitions[0].ActualWidth);
            if (projects)
            {
                Assert.Equal(0, layout.ColumnDefinitions[1].ActualWidth);
                Assert.Equal(0, layout.ColumnDefinitions[2].ActualWidth);
                Assert.InRange(title.TranslatePoint(default, row)!.Value.X, 0, 16);
            }

            Activate(window, Visible<RadioButton>(window, projects ? "navigation-projects" : "navigation-backlog"));
            Assert.True(Visible<Button>(window, handleId).IsEnabled);
            if (projects)
            {
                var canonicalTitle = Visible<Button>(window, $"project-selection-{id}");
                var header = canonicalTitle.GetVisualAncestors().OfType<Border>()
                    .First(border => border.Classes.Contains("project-header-row"));
                Assert.Contains(header.GetVisualDescendants().OfType<ToggleButton>(), control =>
                    control.IsEffectivelyVisible && control.Classes.Contains("disclosure"));
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlockedBinRestoreShowsOnlyGuidanceInSearchAndBin(bool search)
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Match parent", "", "home", null);
        var task = work.CreateTask(project.Id, "Match child");
        work.MoveTaskToBin(task.Id);
        work.MoveProjectToBin(project.Id);
        var shell = new ShellViewModel(work);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        try
        {
            window.Show();
            Activate(window, Visible<RadioButton>(window, search ? "navigation-search" : "navigation-bin"));
            if (search) Visible<TextBox>(window, "workspace-search-field").Text = "match";
            Dispatcher.UIThread.RunJobs();
            var restore = Assert.Single(window.GetVisualDescendants().OfType<Button>(), control =>
                AutomationProperties.GetAutomationId(control) == $"bin-restore-task-{task.Id}");
            Assert.False(restore.IsEffectivelyVisible);
            Assert.False(restore.Focus());
            var row = restore.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("bin-row"));
            var guidance = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.IsEffectivelyVisible && text.Text == "Restore the parent Project from Bin before restoring this Task.");
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(guidance));
            Assert.True(Visible<Button>(window, $"bin-restore-project-{project.Id}").IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoringFromBinFocusesBlockedSuccessorsHeading(bool restoreProject)
    {
        var work = new MemoryWorkspaceWork();
        var parent = work.CreateProject("Z parent", "", "home", null);
        var blocked = work.CreateTask(parent.Id, "B blocked");
        work.MoveTaskToBin(blocked.Id);
        work.MoveProjectToBin(parent.Id);
        string id;
        if (restoreProject)
        {
            var project = work.CreateProject("A restorable", "", "home", null);
            work.MoveProjectToBin(project.Id);
            id = $"bin-restore-project-{project.Id}";
        }
        else
        {
            var task = work.CreateStandaloneTask("A restorable", "", "home", null);
            work.MoveTaskToBin(task.Id);
            id = $"bin-restore-task-{task.Id}";
        }
        var shell = new ShellViewModel(work);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        try
        {
            window.Show();
            Activate(window, Visible<RadioButton>(window, "navigation-bin"));
            Activate(window, Visible<Button>(window, id));
            var heading = Visible<ContentControl>(window, $"bin-selection-task-{blocked.Id}");
            Assert.True(heading.IsFocused);
            Assert.Equal($"bin-selection-task-{blocked.Id}", shell.Work!.BinFocusAutomationId);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CompletingFromSearchRefocusesTheSameTaskAfterDelayedRegrouping()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Find this", "", "work", null);
        var operation = new WorkspacePersistenceOperation(work);
        await using var writer = new InlineWorkspacePersistenceExecutor(operation);
        var executor = new HeldSearchExecutor(operation);
        var shell = new ShellViewModel(work, null, null, writer, executor);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        try
        {
            window.Show();
            Activate(window, Visible<RadioButton>(window, "navigation-search"));
            Visible<TextBox>(window, "workspace-search-field").Text = "find";
            Dispatcher.UIThread.RunJobs();
            var id = $"task-completion-{task.Id}";
            var incomplete = Visible<ToggleButton>(window, id);
            executor.HoldNextRead = true;
            Activate(window, incomplete);
            await shell.Work!.WaitForWorkspaceActionsAsync();
            Assert.Equal("Searching the workspace…", Visible<TextBlock>(window, "workspace-search-status").Text);
            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            shell.Work.Search.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(WorkspaceSearchViewModel.IsLoading) && !shell.Work.Search.IsLoading)
                    applied.TrySetResult();
            };
            executor.Release();
            await applied.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            var complete = Visible<ToggleButton>(window, id);
            Assert.NotSame(incomplete, complete);
            Assert.True(complete.IsChecked);
            Assert.True(complete.IsFocused);
            Assert.Contains(complete.GetVisualAncestors().OfType<Border>(), border => border.Classes.Contains("completed-row"));
            Assert.Equal("1 result.", Visible<TextBlock>(window, "workspace-search-status").Text);
        }
        finally { window.Close(); }
    }

    private sealed class HeldSearchExecutor(WorkspacePersistenceOperation operation) : IWorkspaceActionExecutor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldNextRead { get; set; }
        public void Release() => _release.TrySetResult();

        public async Task<WorkspaceActionResult> SubmitActionAsync(WorkspaceActionRequest request)
        {
            if (request.Kind == WorkspaceActionKind.SearchWorkspace && HoldNextRead)
            {
                HoldNextRead = false;
                await _release.Task.WaitAsync(TestContext.Current.CancellationToken);
            }
            return operation.ExecuteAction(request);
        }
    }

    [AvaloniaTheory]
    [InlineData(600)]
    [InlineData(480)]
    public void PrimaryNavigationAndAllUtilitiesStayReachableAtBoundedHeights(double height)
    {
        var shell = new ShellViewModel(new MemoryWorkspaceWork());
        shell.Settings!.AiFeaturesEnabled = true;
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = height };
        try
        {
            window.Show();
            window.MinHeight = height;
            window.Height = height;
            Dispatcher.UIThread.RunJobs();
            var ai = Visible<RadioButton>(window, "navigation-ai");
            var bin = Visible<RadioButton>(window, "navigation-bin");
            var settings = Visible<RadioButton>(window, "navigation-settings");
            static double Top(Control button, Window window) => button.TranslatePoint(default, window)!.Value.Y;
            static double Bottom(Control button, Window window) => button.TranslatePoint(new(0, button.Bounds.Height), window)!.Value.Y;
            Assert.True(Bottom(ai, window) <= Top(bin, window));
            Assert.True(Bottom(bin, window) <= Top(settings, window));
            Assert.True(Bottom(settings, window) <= window.Bounds.Height);
            foreach (var item in shell.PrimaryNavigation)
            {
                var button = Visible<RadioButton>(window, item.AutomationId);
                Assert.True(button.Focus(NavigationMethod.Tab));
                Dispatcher.UIThread.RunJobs();
                var viewport = button.GetVisualAncestors().OfType<ScrollViewer>().First();
                Assert.True(Bottom(button, window) <= Top(ai, window));
                Assert.True(Top(button, window) >= Top(viewport, window) - 1);
                Assert.True(Bottom(button, window) <= Bottom(viewport, window) + 1);
                if (height == 600) Assert.Equal(0, viewport.ScrollBarMaximum.Y);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ArchiveSearchRowsShowCapturedDatesAndRestoreAnIndividualTaskWithinItsArchivedProject()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Match parent", "", "home", null);
        var own = work.CreateTask(project.Id, "Match individual");
        var child = work.CreateTask(project.Id, "Match inherited");
        work.CompleteTask(own.Id);
        work.SetArchive(own.Id, new DateTimeOffset(2026, 9, 2, 13, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 2));
        work.ArchiveProject(project.Id);
        var shell = new ShellViewModel(work);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        try
        {
            window.Show();
            Activate(window, Visible<RadioButton>(window, "navigation-search"));
            Visible<TextBox>(window, "workspace-search-field").Text = "match";
            Dispatcher.UIThread.RunJobs();
            AssertDate($"archive-project-{project.Id}", "Archived 29 Sep 2026");
            AssertDate($"task-selection-{own.Id}", "Archived 2 Sep 2026");
            AssertDate($"task-selection-{child.Id}", "Project archived 29 Sep 2026");
            var parentRequired = Visible<Button>(window, $"task-restore-{child.Id}");
            Assert.False(parentRequired.IsEnabled);
            Assert.Equal("Restore Project Match parent first to access Match inherited", AutomationProperties.GetName(parentRequired));
            var restore = Visible<Button>(window, $"task-restore-{own.Id}");
            Assert.Equal("Restore Match individual within archived Project Match parent", AutomationProperties.GetName(restore));
            Activate(window, restore);
            Assert.False(work.Read().Tasks.Single(task => task.Id == own.Id).IsArchived);
            Assert.True(work.Read().Projects.Single().IsArchived);
            Assert.Empty(shell.Work!.Search.Completed);
            AssertDate($"task-selection-{own.Id}", "Project archived 29 Sep 2026");
        }
        finally { window.Close(); }

        void AssertDate(string id, string expected)
        {
            var title = Visible<Button>(window, id);
            var row = title.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("archive-row"));
            var date = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text =>
                text.IsEffectivelyVisible && text.Classes.Contains("search-archive-date"));
            Assert.Equal(expected, date.Text);
            Assert.Equal(expected, AutomationProperties.GetName(date));
        }
    }

    private static T Visible<T>(Window window, string id) where T : Control =>
        Assert.Single(window.GetVisualDescendants().OfType<T>(), item =>
            item.IsEffectivelyVisible && AutomationProperties.GetAutomationId(item) == id);

    private static void Activate(Window window, Button button)
    {
        Assert.True(button.Focus(NavigationMethod.Tab));
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        Dispatcher.UIThread.RunJobs();
    }
}
