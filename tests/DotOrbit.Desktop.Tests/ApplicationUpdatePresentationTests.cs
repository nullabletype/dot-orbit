using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ApplicationUpdates;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class ApplicationUpdatePresentationTests
{
    [Fact]
    public void SettingsProjectsFixedStatusToggleAndReleasePage()
    {
        var service = new RecordingUpdateService(UpdateAvailableSnapshot());
        var settings = new SettingsViewModel(
            new MemoryWorkspaceWork(),
            () => { },
            null,
            null,
            null,
            null,
            service);

        Assert.Equal("1.0.0", settings.InstalledVersion);
        Assert.Equal("Stable release", settings.InstalledBuildKind);
        Assert.Equal("Version 1.1.0 is available.", settings.UpdateStatus);
        Assert.Equal("Update available", settings.AvailableReleaseHeading);
        Assert.Equal("Stable", settings.AvailableUpdateChannel);
        Assert.Equal(
            "https://github.com/nullabletype/dot-orbit/releases/tag/v1.1.0",
            settings.AvailableUpdatePage!.AbsoluteUri.TrimEnd('/'));
        Assert.True(settings.CanDismissAvailableUpdate);

        settings.AutomaticUpdateChecksEnabled = false;
        settings.DismissAvailableUpdate();

        Assert.False(service.Snapshot.AutomaticEnabled);
        Assert.False(settings.CanDismissAvailableUpdate);
        Assert.True(settings.HasAvailableUpdate);
        Assert.Contains("details remain available", settings.Announcement, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedRefreshKeepsVerifiedUpdateDetailsVisible()
    {
        var service = new RecordingUpdateService(UpdateAvailableSnapshot() with
        {
            Status = ApplicationUpdateStatus.CheckUnavailable,
        });
        var settings = new SettingsViewModel(
            new MemoryWorkspaceWork(),
            () => { },
            null,
            null,
            null,
            null,
            service);

        Assert.Equal("The update check is unavailable. Try again later.", settings.UpdateStatus);
        Assert.True(settings.HasAvailableUpdate);
        Assert.Equal("1.1.0", settings.AvailableUpdateVersion);
    }

    [Fact]
    public void SettingsExposesEveryFixedStateAndVerifiedUpToDateDetails()
    {
        Assert.Equal("Not checked yet.", SettingsFor(ApplicationUpdateStatus.NotChecked).UpdateStatus);
        Assert.Equal("Checking for updates…", SettingsFor(ApplicationUpdateStatus.Checking).UpdateStatus);
        var current = SettingsFor(ApplicationUpdateStatus.UpToDate, "1.0.0");
        Assert.Equal("dot-orbit is up to date.", current.UpdateStatus);
        Assert.True(current.HasAvailableUpdate);
        Assert.Equal("Latest verified release", current.AvailableReleaseHeading);
        Assert.Equal("Stable", current.AvailableUpdateChannel);
        Assert.Equal(
            "Version 1.1.0 is available.",
            SettingsFor(ApplicationUpdateStatus.UpdateAvailable, "1.1.0").UpdateStatus);
        Assert.Equal(
            "The update check is unavailable. Try again later.",
            SettingsFor(ApplicationUpdateStatus.CheckUnavailable).UpdateStatus);

        Assert.True(SemanticVersion.TryParse("1.1.0", out var stable));
        var unsupported = new SettingsViewModel(
            new MemoryWorkspaceWork(),
            () => { },
            null,
            null,
            null,
            null,
            new RecordingUpdateService(new(
                ApplicationUpdateStatus.UnsupportedDevelopmentBuild,
                InstalledApplicationBuild.CreateForTests(
                    "Development build",
                    null,
                    ApplicationReleaseChannel.Development),
                true,
                new(stable),
                null,
                false)));
        Assert.Equal(
            "Unsupported development build. Latest stable release: 1.1.0.",
            unsupported.UpdateStatus);
        Assert.Equal("Latest supported stable release", unsupported.AvailableReleaseHeading);
    }

    [Fact]
    public void ShellShowsCountFreeAccessibleUpdateAttentionAndClearsItAfterDismissal()
    {
        var service = new RecordingUpdateService(UpdateAvailableSnapshot());
        var shell = new ShellViewModel(
            new MemoryWorkspaceWork(),
            timeProvider: null,
            applicationThemeService: null,
            uiDispatcher: new InlineUiDispatcher(),
            applicationUpdateService: service);

        Assert.Equal("Update", shell.SettingsNavigation.AttentionText);
        Assert.Equal("Application update available", shell.SettingsNavigation.AccessibleCount);

        service.Dismiss();

        Assert.Null(shell.SettingsNavigation.AttentionText);
        Assert.Equal(string.Empty, shell.SettingsNavigation.AccessibleCount);
        shell.Dispose();
    }

    [AvaloniaFact]
    public void AutomaticCheckStartsOnlyAfterAnUnlockedMainWindowOpens()
    {
        var service = new RecordingUpdateService(UpdateAvailableSnapshot());
        var window = new MainWindow(
            new PassphraseRotationViewModelTests.StubWorkspaceSession(),
            applicationUpdateService: service);

        Assert.Equal(0, service.AutomaticCheckCount);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, service.AutomaticCheckCount);
        window.Close();
    }

    [AvaloniaFact]
    public void ClosingTheWindowCancelsAnOutstandingAutomaticCheck()
    {
        var service = new CancellationRecordingUpdateService(UpdateAvailableSnapshot());
        var window = new MainWindow(
            new PassphraseRotationViewModelTests.StubWorkspaceSession(),
            applicationUpdateService: service);

        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(service.Started);

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.True(service.Canceled);
    }

    [AvaloniaFact]
    public void ReleaseActionUsesTheFixedPageAndDismissalRestoresFocusToIt()
    {
        var service = new RecordingUpdateService(UpdateAvailableSnapshot());
        var browser = new RecordingBrowserLauncher();
        var window = new MainWindow(
            new PassphraseRotationViewModelTests.StubWorkspaceSession(),
            applicationUpdateService: service,
            externalBrowserLauncher: browser)
        {
            Width = 1120,
            Height = 600,
        };
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.SettingsNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var release = Assert.IsType<Button>(window.FindControl<Button>("SettingsViewReleaseButton"));
        var dismiss = Assert.IsType<Button>(window.FindControl<Button>("SettingsDismissUpdateButton"));
        var version = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("SettingsAvailableUpdateVersion"));
        var status = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("SettingsUpdateStatusText"));
        Assert.Equal("View release notes and downloads in browser", release.Content);
        Assert.Equal("View release notes and downloads in browser", AutomationProperties.GetName(release));
        Assert.Equal("Dismiss this version", dismiss.Content);
        Assert.Equal("Dismiss this version", AutomationProperties.GetName(dismiss));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
        Assert.Equal(TextWrapping.Wrap, version.TextWrapping);
        Assert.Equal(360, version.MaxWidth);

        release.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(
            "https://github.com/nullabletype/dot-orbit/releases/tag/v1.1.0",
            browser.OpenedUri?.AbsoluteUri);

        Assert.True(dismiss.Focus());
        dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        Assert.True(release.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void CheckNowIsFocusableNamedAndAvailableWhenAutomaticCheckingIsDisabled()
    {
        Assert.True(SemanticVersion.TryParse("1.0.0", out var installed));
        var service = new RecordingUpdateService(new(
            ApplicationUpdateStatus.NotChecked,
            InstalledApplicationBuild.CreateForTests(
                "1.0.0",
                installed,
                ApplicationReleaseChannel.Stable),
            false,
            null,
            null,
            false));
        var window = new MainWindow(
            new PassphraseRotationViewModelTests.StubWorkspaceSession(),
            applicationUpdateService: service);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.SettingsNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var check = Assert.IsType<Button>(window.FindControl<Button>("SettingsCheckForUpdatesButton"));
        var toggle = Assert.IsType<ToggleSwitch>(window.FindControl<ToggleSwitch>("SettingsAutomaticUpdatesToggle"));

        Assert.True(check.IsEnabled);
        Assert.True(check.Focus(NavigationMethod.Tab));
        Assert.Equal("Check for application updates now", AutomationProperties.GetName(check));
        Assert.Equal(
            "Automatic update checks disabled",
            AutomationProperties.GetItemStatus(toggle));
        check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, service.ManualCheckCount);
        Assert.True(check.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void UnsupportedBuildDisablesAutomaticControlButKeepsManualCheckAvailable()
    {
        var service = new RecordingUpdateService(new(
            ApplicationUpdateStatus.UnsupportedDevelopmentBuild,
            InstalledApplicationBuild.CreateForTests(
                "Development build",
                null,
                ApplicationReleaseChannel.Development),
            true,
            null,
            null,
            false));
        var window = new MainWindow(
            new PassphraseRotationViewModelTests.StubWorkspaceSession(),
            applicationUpdateService: service);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.SettingsNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var check = Assert.IsType<Button>(window.FindControl<Button>("SettingsCheckForUpdatesButton"));
        var toggle = Assert.IsType<ToggleSwitch>(window.FindControl<ToggleSwitch>("SettingsAutomaticUpdatesToggle"));

        Assert.False(toggle.IsEnabled);
        Assert.Equal(
            "Automatic checks unavailable for this development build",
            AutomationProperties.GetItemStatus(toggle));
        Assert.True(check.IsEnabled);
        check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, service.ManualCheckCount);
        window.Close();
    }

    [AvaloniaFact]
    public void BrowserLaunchFailureIsAnnouncedWithoutChangingTheDestination()
    {
        var service = new RecordingUpdateService(UpdateAvailableSnapshot());
        var browser = new RecordingBrowserLauncher(succeeds: false);
        var window = new MainWindow(
            new PassphraseRotationViewModelTests.StubWorkspaceSession(),
            applicationUpdateService: service,
            externalBrowserLauncher: browser);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.SettingsNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        window.FindControl<Button>("SettingsViewReleaseButton")!
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            "https://github.com/nullabletype/dot-orbit/releases/tag/v1.1.0",
            browser.OpenedUri?.AbsoluteUri);
        Assert.Equal("Could not open the release page in your browser.", shell.Settings!.Announcement);
        window.Close();
    }

    private static ApplicationUpdateSnapshot UpdateAvailableSnapshot()
    {
        Assert.True(SemanticVersion.TryParse("1.0.0", out var installed));
        Assert.True(SemanticVersion.TryParse("1.1.0", out var available));
        return new(
            ApplicationUpdateStatus.UpdateAvailable,
            InstalledApplicationBuild.CreateForTests(
                "1.0.0",
                installed,
                ApplicationReleaseChannel.Stable),
            true,
            new(available),
            null,
            false);
    }

    private static SettingsViewModel SettingsFor(
        ApplicationUpdateStatus status,
        string? availableVersion = null)
    {
        Assert.True(SemanticVersion.TryParse("1.0.0", out var installed));
        ApplicationRelease? release = null;
        if (availableVersion is not null)
        {
            Assert.True(SemanticVersion.TryParse(availableVersion, out var available));
            release = new(available);
        }

        return new(
            new MemoryWorkspaceWork(),
            () => { },
            null,
            null,
            null,
            null,
            new RecordingUpdateService(new(
                status,
                InstalledApplicationBuild.CreateForTests(
                    "1.0.0",
                    installed,
                    ApplicationReleaseChannel.Stable),
                true,
                release,
                null,
                false)));
    }

    private sealed class RecordingUpdateService(ApplicationUpdateSnapshot snapshot)
        : IApplicationUpdateService
    {
        public event EventHandler? StateChanged;
        public ApplicationUpdateSnapshot Snapshot { get; private set; } = snapshot;
        public int AutomaticCheckCount { get; private set; }
        public int ManualCheckCount { get; private set; }

        public bool TrySetAutomaticEnabled(bool enabled)
        {
            Snapshot = Snapshot with { AutomaticEnabled = enabled };
            StateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public Task CheckAutomaticallyAsync(CancellationToken cancellationToken)
        {
            AutomaticCheckCount++;
            return Task.CompletedTask;
        }
        public Task CheckManuallyAsync(CancellationToken cancellationToken)
        {
            ManualCheckCount++;
            return Task.CompletedTask;
        }

        public bool TryDismissAvailableVersion()
        {
            Dismiss();
            return true;
        }

        public void Dismiss()
        {
            Snapshot = Snapshot with { IsAvailableVersionDismissed = true };
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class RecordingBrowserLauncher(bool succeeds = true) : IExternalBrowserLauncher
    {
        public Uri? OpenedUri { get; private set; }

        public Task<bool> TryOpenAsync(Uri uri)
        {
            OpenedUri = uri;
            return Task.FromResult(succeeds);
        }
    }

    private sealed class CancellationRecordingUpdateService(ApplicationUpdateSnapshot snapshot)
        : IApplicationUpdateService
    {
        private readonly TaskCompletionSource _canceled = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public ApplicationUpdateSnapshot Snapshot { get; } = snapshot;
        public bool Started { get; private set; }
        public bool Canceled { get; private set; }

        public bool TrySetAutomaticEnabled(bool enabled) => true;

        public async Task CheckAutomaticallyAsync(CancellationToken cancellationToken)
        {
            Started = true;
            using var registration = cancellationToken.Register(() =>
            {
                Canceled = true;
                _canceled.TrySetResult();
            });
            await _canceled.Task;
        }

        public Task CheckManuallyAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public bool TryDismissAvailableVersion() => false;
    }
}
