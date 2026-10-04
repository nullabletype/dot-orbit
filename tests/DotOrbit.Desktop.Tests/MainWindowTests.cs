using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void WorkspaceUnavailableShowsReplacementAndClosesTheShell()
    {
        var replacementShown = 0;
        var window = new MainWindow();
        window.Show();

        window.HandleWorkspaceUnavailable(() => replacementShown++);

        Assert.Equal(1, replacementShown);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public void NavigationControlsExposeNamesFocusAndSelectedState()
    {
        var window = new MainWindow();
        window.Show();

        var navigation = window.GetVisualDescendants().OfType<RadioButton>().ToArray();

        Assert.Equal(9, navigation.Length);
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
                "Open Settings",
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
                "navigation-settings",
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
            "Select a project, task, or category\nto see its details",
            window.FindControl<TextBlock>("InspectorEmptyText")?.Text);
        Assert.NotNull(window.FindControl<Border>("TopBar"));
        Assert.NotNull(window.FindControl<Border>("InspectorRegion"));
        Assert.All(window.GetVisualDescendants().OfType<RadioButton>(), button =>
            Assert.Single(button.GetVisualDescendants().OfType<PathIcon>()));
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
        Assert.Equal(30, brandMark?.Width);
        Assert.Equal(18, window.FindControl<TextBlock>("BrandNameText")?.FontSize);
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

    [AvaloniaFact]
    public void UnlockedShellExposesRecoveryOnlyFromSettings()
    {
        using var session = new RecoveryViewModelTests.StubWorkspaceSession(
            new RecoveryViewModelTests.StubWorkspaceRecovery());
        var window = new MainWindow(session);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.SettingsNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(window.FindControl<Button>("OpenRecoveryButton"));
        var recovery = Assert.IsType<Button>(window.FindControl<Button>("SettingsRecoveryButton"));

        Assert.True(recovery.IsVisible);
        Assert.True(recovery.Focusable);
        Assert.Equal("Open recovery settings", AutomationProperties.GetName(recovery));
        Assert.True(recovery.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        var recoveryWindow = Assert.IsType<RecoveryWindow>(Assert.Single(window.OwnedWindows));
        recoveryWindow.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.True(recovery.IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public void SettingsOpensPassphraseRotationAndReturnsFocusAfterCancellation()
    {
        using var session = new PassphraseRotationViewModelTests.StubWorkspaceSession();
        var window = new MainWindow(session);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.SettingsNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var launch = Assert.IsType<Button>(window.FindControl<Button>("SettingsPassphraseButton"));

        Assert.True(launch.IsVisible);
        Assert.Equal("Change workspace passphrase", AutomationProperties.GetName(launch));
        Assert.True(launch.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        var rotation = Assert.IsType<PassphraseRotationWindow>(Assert.Single(window.OwnedWindows));
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(window.OwnedWindows);
        var cancel = Assert.IsType<Button>(rotation.FindControl<Button>("CancelPassphraseButton"));
        Assert.True(cancel.Focus());
        rotation.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(window.OwnedWindows);
        Assert.True(launch.IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public void SuccessfulPassphraseRotationReturnsToSettingsWithAccessibleConfirmation()
    {
        var replacement = new PassphraseRotationViewModelTests.StubWorkspaceSession();
        using var session = new PassphraseRotationViewModelTests.StubWorkspaceSession
        {
            RotationResult = PassphraseRotationResult.Rotated(
                replacement,
                "/safe/pre-rotation.dotorbit-recovery"),
        };
        var window = new MainWindow(session);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.SettingsNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var launch = Assert.IsType<Button>(window.FindControl<Button>("SettingsPassphraseButton"));
        launch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        var rotation = Assert.IsType<PassphraseRotationWindow>(Assert.Single(window.OwnedWindows));
        var rotationViewModel = Assert.IsType<PassphraseRotationViewModel>(rotation.DataContext);
        rotationViewModel.CurrentPassphrase = "correct horse battery";
        rotationViewModel.NewPassphrase = "new portable passphrase";
        rotationViewModel.Confirmation = "new portable passphrase";

        rotationViewModel.SubmitCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(window.OwnedWindows);
        var updatedShell = Assert.IsType<ShellViewModel>(window.DataContext);
        Assert.True(updatedShell.ShowSettings);
        var confirmation = Assert.IsType<Border>(
            window.FindControl<Border>("PassphraseChangedConfirmation"));
        Assert.True(confirmation.IsVisible);
        Assert.Equal(
            "Passphrase changed confirmation",
            AutomationProperties.GetName(confirmation));
        Assert.Equal(
            AutomationLiveSetting.Polite,
            AutomationProperties.GetLiveSetting(confirmation));
        Assert.Equal(
            "Passphrase changed. Use the new passphrase the next time you unlock this workspace.",
            updatedShell.Settings?.SecurityConfirmation);
    }
}
