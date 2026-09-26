using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
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
        var currentViewRegion = window.FindControl<Grid>("CurrentViewRegion");
        Assert.NotNull(currentViewRegion);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(currentViewRegion));
        Assert.Equal(shell.ViewTitle, window.FindControl<TextBlock>("CurrentViewTitleText")?.Text);
        Assert.Equal(shell.ViewSubtitle, window.FindControl<TextBlock>("CurrentViewSubtitleText")?.Text);
        Assert.Equal(shell.EmptyStateHeading, window.FindControl<TextBlock>("EmptyStateHeadingText")?.Text);
        Assert.Equal(shell.EmptyStateBody, window.FindControl<TextBlock>("EmptyStateBodyText")?.Text);
    }

    [AvaloniaFact]
    public void ShellExposesTheWorkbenchVisualFoundation()
    {
        var window = new MainWindow();
        window.Show();

        Assert.Equal("dot-orbit", window.FindControl<TextBlock>("BrandNameText")?.Text);
        Assert.Equal("WORKSPACE", window.FindControl<TextBlock>("WorkspaceLabelText")?.Text);
        Assert.Equal("PERSONAL WORKSPACE", window.FindControl<TextBlock>("CurrentViewEyebrowText")?.Text);
        Assert.Equal(
            "Select a project or task\nto see its details",
            window.FindControl<TextBlock>("InspectorEmptyText")?.Text);
        Assert.NotNull(window.FindControl<Border>("TopBar"));
        Assert.NotNull(window.FindControl<Border>("InspectorRegion"));
        Assert.Equal(8, window.GetVisualDescendants().OfType<PathIcon>().Count());
        Assert.Single(
            window.GetVisualDescendants().OfType<Border>(),
            border => border.Name == "SelectionIndicator" && border.IsVisible);
    }

    [AvaloniaFact]
    public void HeaderAndNavigationUseTheReviewedVisualAssetsAndAlignment()
    {
        var window = new MainWindow();
        window.Show();

        var brandMark = window.FindControl<Image>("BrandMarkImage");
        Assert.IsType<DrawingImage>(brandMark?.Source);
        Assert.DoesNotContain(
            window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text?.Contains("Calm three-pane workspace", StringComparison.Ordinal) is true);

        var navigation = window.GetVisualDescendants().OfType<RadioButton>().ToArray();
        Assert.All(navigation, control => Assert.True(control.Bounds.Width > 180));

        var counts = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(text => text.Classes.Contains("nav-count") && text.IsVisible)
            .ToArray();
        Assert.Equal(7, counts.Length);
        Assert.All(counts, count => Assert.Equal(counts[0].Bounds.Right, count.Bounds.Right, 1));
    }

    [AvaloniaFact]
    public void KeyboardActivationNavigatesAndRetainsFocusOnTheAction()
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
        Assert.Equal(shell.ViewSubtitle, window.FindControl<TextBlock>("CurrentViewSubtitleText")?.Text);
        Assert.Equal(shell.EmptyStateHeading, window.FindControl<TextBlock>("EmptyStateHeadingText")?.Text);
    }
}
