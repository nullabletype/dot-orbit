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

    private static TextBox QuickField(Window window) => Assert.Single(window.GetVisualDescendants().OfType<TextBox>(), b => b.Classes.Contains("quick-add"));
}
