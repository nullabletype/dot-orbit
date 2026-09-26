using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void NavigationControlsExposeNamesFocusAndSelectedState()
    {
        var window = new MainWindow();
        window.Show();

        var navigation = window.GetVisualDescendants().OfType<RadioButton>().ToArray();

        Assert.Equal(8, navigation.Length);
        Assert.Equal(
            [
                "Open Today",
                "Open Upcoming",
                "Open Backlog",
                "Open Projects",
                "Open Categories",
                "Open Completed",
                "Open Archive",
                "Open Bin",
            ],
            navigation.Select(AutomationProperties.GetName));
        Assert.Equal(
            [
                "navigation-today",
                "navigation-upcoming",
                "navigation-backlog",
                "navigation-projects",
                "navigation-categories",
                "navigation-completed",
                "navigation-archive",
                "navigation-bin",
            ],
            navigation.Select(AutomationProperties.GetAutomationId));
        Assert.All(navigation, control =>
        {
            Assert.True(control.Focusable);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)));
        });

        var today = Assert.Single(
            navigation,
            control => AutomationProperties.GetAutomationId(control) == "navigation-today");
        Assert.True(today.IsChecked);
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        Assert.Equal(shell.ViewTitle, window.FindControl<TextBlock>("CurrentViewTitleText")?.Text);
        Assert.Equal(shell.EmptyStateHeading, window.FindControl<TextBlock>("EmptyStateHeadingText")?.Text);
        Assert.Equal(shell.EmptyStateBody, window.FindControl<TextBlock>("EmptyStateBodyText")?.Text);
    }

    [AvaloniaFact]
    public void KeyboardActivationNavigatesAndMovesFocus()
    {
        var shell = new ShellViewModel();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        var archive = Assert.Single(
            window.GetVisualDescendants().OfType<RadioButton>(),
            control => AutomationProperties.GetAutomationId(control) == "navigation-archive");

        Assert.True(archive.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);

        Assert.True(archive.IsFocused);
        Assert.True(archive.IsChecked);
        Assert.Equal("Archive", shell.ViewTitle);
        Assert.Equal("Archive", window.FindControl<TextBlock>("CurrentViewTitleText")?.Text);
        Assert.Equal(shell.EmptyStateHeading, window.FindControl<TextBlock>("EmptyStateHeadingText")?.Text);
    }
}
