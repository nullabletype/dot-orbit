using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
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
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Target 2026-10-12");
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Home · inherited");

        Activate(window, projectButton);
        Assert.Contains("inspector-title", window.FindControl<TextBox>("DraftTitle")!.Classes);
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
        Assert.True(title.IsFocused);
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

    private static Button NamedButton(Window window, string name) => Assert.Single(
        window.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == name);

    private static void Activate(Window window, Button button)
    {
        Assert.True(button.Focus());
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    private static TextBox QuickField(Window window) => Assert.Single(window.GetVisualDescendants().OfType<TextBox>(), b => b.Classes.Contains("quick-add"));
}
