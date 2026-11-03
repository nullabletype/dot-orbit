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
    public void SearchReorderMenuMovesAcrossHiddenCanonicalPositionsAndFilteredDragDoesNothing(bool projects)
    {
        var work = new MemoryWorkspaceWork();
        foreach (var title in new[] { "Match one", "Hidden peer", "Match two" })
            if (projects) work.CreateProject(title, "", "home", null);
            else work.CreateStandaloneTask(title, "", "home", null);
        string[] Order() => projects
            ? work.Read().Projects.OrderBy(item => item.Position).Select(item => item.Id).ToArray()
            : work.Read().Tasks.OrderBy(item => item.SharedPosition).Select(item => item.Id).ToArray();
        var before = Order();
        var shell = new ShellViewModel(work);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        try
        {
            window.Show();
            Activate(window, Visible<RadioButton>(window, "navigation-search"));
            Visible<TextBox>(window, "workspace-search-field").Text = "match";
            Dispatcher.UIThread.RunJobs();
            var prefix = projects ? "project-reorder-" : "backlog-reorder-";
            var handle = Visible<Button>(window, prefix + before[2]);
            var first = Visible<Button>(window, prefix + before[0]);
            Assert.Contains("outside these results", AutomationProperties.GetHelpText(handle), StringComparison.Ordinal);
            var from = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
            var to = first.TranslatePoint(new Point(first.Bounds.Width / 2, first.Bounds.Height / 2), window)!.Value;
            window.MouseDown(from, MouseButton.Left, RawInputModifiers.LeftMouseButton);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Border>(), row => row.Classes.Contains("reorder-placeholder"));
            window.MouseUp(to, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(before, Order());

            Activate(window, handle);
            var menu = Assert.IsType<MenuFlyout>(handle.Flyout);
            Assert.True(menu.IsOpen);
            var move = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Move up");
            Assert.True(move.Command!.CanExecute(null));
            move.Command.Execute(null);
            menu.Hide();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([before[0], before[2], before[1]], Order());
            Assert.True(Visible<Button>(window, prefix + before[2]).IsFocused);
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
