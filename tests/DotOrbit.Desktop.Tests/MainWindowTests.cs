using Avalonia;
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
        Assert.False(window.FindControl<Border>("TopBarStatus")?.IsEffectivelyVisible);
        Assert.NotNull(window.FindControl<Border>("InspectorRegion"));
        Assert.All(window.GetVisualDescendants().OfType<RadioButton>(), button =>
            Assert.Single(button.GetVisualDescendants().OfType<PathIcon>()));
        Assert.Single(
            window.GetVisualDescendants().OfType<Border>(),
            border => border.Name == "SelectionIndicator" && border.IsVisible);
    }

    [AvaloniaFact]
    public void TransientStatusUsesTheTopBarWithoutReservingContentSpaceOrMovingFocus()
    {
        var work = new MemoryWorkspaceWork();
        var longTitle = $"Review {new string('W', 160)}";
        var task = work.CreateStandaloneTask(longTitle, "", "home", null);
        var shell = new ShellViewModel(work);
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var topBar = Assert.IsType<Border>(window.FindControl<Border>("TopBar"));
        var status = Assert.IsType<Border>(window.FindControl<Border>("TopBarStatus"));
        var statusIcon = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("TopBarStatusIcon"));
        var statusMessage = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("TopBarStatusMessage"));
        var brand = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("BrandNameText"));
        var currentView = Assert.IsType<Grid>(window.FindControl<Grid>("CurrentViewRegion"));
        var settings = Assert.IsType<ScrollViewer>(window.FindControl<ScrollViewer>("SettingsRegion"));
        var inspector = Assert.IsType<Border>(window.FindControl<Border>("InspectorRegion"));
        var archive = window.GetVisualDescendants().OfType<RadioButton>().Single(control =>
            AutomationProperties.GetAutomationId(control) == "navigation-archive");
        var brandOrigin = brand.TranslatePoint(default, window);
        var contentHeightWithoutStatus = currentView.Bounds.Height;

        Assert.False(status.IsEffectivelyVisible);
        Assert.Equal("•", statusIcon.Text);
        Assert.DoesNotContain(status.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "STATUS");
        Assert.Equal(new Thickness(32, 30, 32, 0), currentView.Margin);
        Assert.Equal(currentView.Margin, settings.Margin);
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(statusMessage));
        Assert.Equal(TextWrapping.Wrap, statusMessage.TextWrapping);
        Assert.Equal(TextTrimming.CharacterEllipsis, statusMessage.TextTrimming);
        Assert.Equal(2, statusMessage.MaxLines);
        Assert.True(archive.Focus());

        shell.Work!.ToggleCompletion(task.Id);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Task completed.", statusMessage.Text);
        Assert.True(status.IsEffectivelyVisible);
        Assert.True(status.Bounds.Width < 300);
        Assert.True(status.Bounds.Height < 48);

        shell.Work.ToggleCompletion(task.Id);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Task reopened.", statusMessage.Text);

        Assert.True(archive.Focus());
        shell.Work.MarkdownCopyFailed();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Could not copy the rendered description. Try again.", statusMessage.Text);
        Assert.True(archive.IsFocused);
        Assert.Equal(contentHeightWithoutStatus, currentView.Bounds.Height);
        Assert.Equal(brandOrigin, brand.TranslatePoint(default, window));
        Assert.Contains(topBar, status.GetVisualAncestors());
        Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.IsEffectivelyVisible && text.Text == shell.Work.Message);

        shell.Work.ToggleToday(task.Id);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal($"Added {longTitle} to Today in Planned.", statusMessage.Text);
        Assert.True(status.Bounds.Width <= 620);
        Assert.True(status.Bounds.Height <= topBar.Bounds.Height);

        foreach (var width in new[] { 1120d, 1440d })
        {
            window.Width = width;
            Dispatcher.UIThread.RunJobs();
            var topBarOrigin = Assert.IsType<Point>(topBar.TranslatePoint(default, window));
            var statusOrigin = Assert.IsType<Point>(status.TranslatePoint(default, window));
            var brandPosition = Assert.IsType<Point>(brand.TranslatePoint(default, window));
            var inspectorOrigin = Assert.IsType<Point>(inspector.TranslatePoint(default, window));
            Assert.True(brandPosition.X + brand.Bounds.Width < statusOrigin.X);
            Assert.True(statusOrigin.X + status.Bounds.Width <= topBarOrigin.X + topBar.Bounds.Width);
            Assert.True(statusOrigin.Y + status.Bounds.Height <= inspectorOrigin.Y);
        }

        shell.Work.NewTaskCommand.Execute(null);
        shell.Work.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Enter a title.", statusMessage.Text);
        Assert.True(status.IsEffectivelyVisible);

        shell.Work.NewProjectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(status.IsEffectivelyVisible);
        Assert.Equal(contentHeightWithoutStatus, currentView.Bounds.Height);
        window.Close();
    }

    [AvaloniaFact]
    public void TransientStatusDismissesFiveSecondsAfterTheLatestMessage()
    {
        var scheduler = new ManualTransientMessageScheduler();
        var shell = new ShellViewModel(new MemoryWorkspaceWork());
        var window = new MainWindow(null, null, null, null, scheduler) { DataContext = shell };
        window.Show();
        var status = Assert.IsType<Border>(window.FindControl<Border>("TopBarStatus"));

        shell.Work!.MarkdownCopySucceeded();
        Dispatcher.UIThread.RunJobs();
        var stale = Assert.Single(scheduler.Pending);
        Assert.Equal(MainWindow.TransientMessageDuration, scheduler.Delay);
        Assert.Equal(TimeSpan.FromSeconds(5), scheduler.Delay);
        Assert.True(status.IsEffectivelyVisible);

        shell.Work.MarkdownCopyFailed();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, scheduler.ScheduleCount);
        Assert.Equal("Could not copy the rendered description. Try again.", shell.Work.Message);

        stale.Callback(stale.Revision);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Could not copy the rendered description. Try again.", shell.Work.Message);
        Assert.True(status.IsEffectivelyVisible);

        scheduler.FireCurrent();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(shell.Work.Message);
        Assert.False(status.IsEffectivelyVisible);

        window.Close();
        Assert.True(scheduler.IsDisposed);
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

    private sealed class ManualTransientMessageScheduler : ITransientMessageScheduler
    {
        private ScheduledMessage? _current;

        public List<ScheduledMessage> Pending { get; } = [];
        public int ScheduleCount { get; private set; }
        public TimeSpan Delay { get; private set; }
        public bool IsDisposed { get; private set; }

        public void Schedule(TimeSpan delay, long revision, Action<long> callback)
        {
            Delay = delay;
            ScheduleCount++;
            _current = new(revision, callback);
            Pending.Add(_current);
        }

        public void Cancel() => _current = null;

        public void FireCurrent()
        {
            var current = _current;
            _current = null;
            current?.Callback(current.Revision);
        }

        public void Dispose()
        {
            Cancel();
            IsDisposed = true;
        }

        public sealed record ScheduledMessage(long Revision, Action<long> Callback);
    }
}
