using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class PlanningSnapshotCopyWindowTests
{
    [AvaloniaFact]
    public void ReviewUsesAccessibleConfirmationLogicalFocusAndMinimumDesktopSize()
    {
        var clipboard = new PlanningSnapshotCopyViewModelTests.RecordingPlainTextClipboard();
        var viewModel = new PlanningSnapshotCopyViewModel(
            PlanningSnapshotCopyViewModelTests.CreateRepresentativeSnapshot(),
            includeAgentInstructionsAndSchema: true,
            clipboard);
        var window = new PlanningSnapshotCopyWindow(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var disclosure = Assert.IsType<Border>(window.FindControl<Border>("PlanningSnapshotCatalogueDisclosure"));
        var warning = Assert.IsType<Border>(window.FindControl<Border>("PlanningSnapshotClipboardWarning"));
        var confirm = Assert.IsType<Button>(window.FindControl<Button>("ConfirmPlanningSnapshotCopyButton"));
        var cancel = Assert.IsType<Button>(window.FindControl<Button>("CancelPlanningSnapshotCopyButton"));
        var status = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("PlanningSnapshotCopyStatus"));

        Assert.Equal(560, window.MinWidth);
        Assert.Equal(640, window.MinHeight);
        Assert.Equal("Reusable catalogue disclosure", AutomationProperties.GetName(disclosure));
        Assert.Equal("Sensitive plaintext clipboard confirmation", AutomationProperties.GetName(warning));
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(warning));
        Assert.Equal(AutomationLiveSetting.Assertive, AutomationProperties.GetLiveSetting(status));
        Assert.Equal("Confirm sensitive plaintext Planning snapshot copy", AutomationProperties.GetName(confirm));
        Assert.Equal("Cancel Planning snapshot copy", AutomationProperties.GetName(cancel));
        Assert.Equal(0, confirm.TabIndex);
        Assert.Equal(1, cancel.TabIndex);
        Assert.True(cancel.IsKeyboardFocusWithin);

        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, clipboard.WriteCount);
        Assert.Equal(
            "Planning snapshot copy cancelled. Clipboard contents were not changed.",
            viewModel.StatusMessage);
        Assert.True(window.FindControl<Button>("ClosePlanningSnapshotCopyButton")!.IsKeyboardFocusWithin);
        window.Close();
    }

    [AvaloniaFact]
    public void ConfirmedCopyWritesOnceAndMovesFocusToTheCloseAction()
    {
        var clipboard = new PlanningSnapshotCopyViewModelTests.RecordingPlainTextClipboard();
        var viewModel = new PlanningSnapshotCopyViewModel(
            PlanningSnapshotCopyViewModelTests.CreateRepresentativeSnapshot(),
            includeAgentInstructionsAndSchema: true,
            clipboard);
        var window = new PlanningSnapshotCopyWindow(viewModel);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var confirm = window.FindControl<Button>("ConfirmPlanningSnapshotCopyButton")!;
        Assert.True(confirm.Focus());

        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, clipboard.WriteCount);
        Assert.False(confirm.IsEnabled);
        Assert.True(window.FindControl<Button>("ClosePlanningSnapshotCopyButton")!.IsKeyboardFocusWithin);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SnapshotLaunchFlushesValidPendingEditBeforeOneAtomicSourceRead()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Before", "", "home", null);
        var source = new RecordingPlanningSnapshotSource(
            PlanningSnapshotCopyViewModelTests.CreateRepresentativeSnapshot());
        using var session = new StubWorkspaceSession(work, source);
        var clipboard = new PlanningSnapshotCopyViewModelTests.RecordingPlainTextClipboard();
        var window = new MainWindow(session, null, null, plainTextClipboard: clipboard);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.Work!.SelectTask(task.Id);
        shell.Work.Title = "After";

        await window.OpenPlanningSnapshotAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("After", work.Read().Tasks.Single(item => item.Id == task.Id).Title);
        Assert.Equal(1, source.ReadCount);
        Assert.IsType<PlanningSnapshotCopyWindow>(Assert.Single(window.OwnedWindows));
        window.OwnedWindows.Single().Close();
        window.Close();
    }

    [AvaloniaFact]
    public async Task InvalidPendingEditBlocksSnapshotUntilTheUserExplicitlyDiscardsIt()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Before", "", "home", null);
        var source = new RecordingPlanningSnapshotSource(
            PlanningSnapshotCopyViewModelTests.CreateRepresentativeSnapshot());
        using var session = new StubWorkspaceSession(work, source);
        var window = new MainWindow(
            session,
            null,
            null,
            plainTextClipboard: new PlanningSnapshotCopyViewModelTests.RecordingPlainTextClipboard());
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.Work!.SelectTask(task.Id);
        shell.Work.Title = "";

        await window.OpenPlanningSnapshotAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.True(shell.Work.NeedsDecision);
        Assert.Equal(0, source.ReadCount);
        Assert.Empty(window.OwnedWindows);
        Assert.True(window.FindControl<Button>("GuardSave")!.IsKeyboardFocusWithin);
        shell.Work.DiscardAndLeaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, source.ReadCount);
        Assert.IsType<PlanningSnapshotCopyWindow>(Assert.Single(window.OwnedWindows));
        window.OwnedWindows.Single().Close();
        window.Close();
    }

    [AvaloniaFact]
    public async Task SettingsActionsRestoreSnapshotLauncherFocusAndInstructionsNeverReadWorkspace()
    {
        var source = new RecordingPlanningSnapshotSource(
            PlanningSnapshotCopyViewModelTests.CreateRepresentativeSnapshot());
        using var session = new StubWorkspaceSession(new MemoryWorkspaceWork(), source);
        var clipboard = new PlanningSnapshotCopyViewModelTests.RecordingPlainTextClipboard();
        var window = new MainWindow(session, null, null, plainTextClipboard: clipboard);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.SettingsNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var snapshotLauncher = Assert.IsType<Button>(
            window.FindControl<Button>("SettingsPlanningSnapshotButton"));
        var instructions = Assert.IsType<Button>(
            window.FindControl<Button>("SettingsReusableInstructionsButton"));
        var preference = Assert.IsType<ToggleSwitch>(
            window.FindControl<ToggleSwitch>("SettingsIncludeAgentInstructionsToggle"));

        Assert.Equal("Review and copy current Planning snapshot", AutomationProperties.GetName(snapshotLauncher));
        Assert.Contains("without workspace content", AutomationProperties.GetName(instructions), StringComparison.Ordinal);
        Assert.Equal(
            "Include agent instructions and schema with snapshot",
            AutomationProperties.GetName(preference));
        Assert.Equal("Agent instructions and schemas included", AutomationProperties.GetItemStatus(preference));
        Assert.True(snapshotLauncher.Focus());
        await window.OpenPlanningSnapshotAsync(snapshotLauncher);
        Dispatcher.UIThread.RunJobs();
        var review = Assert.IsType<PlanningSnapshotCopyWindow>(Assert.Single(window.OwnedWindows));
        Assert.Equal(1, source.ReadCount);

        review.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.True(snapshotLauncher.IsKeyboardFocusWithin);
        Assert.Equal(
            "Planning snapshot copy cancelled. Clipboard contents were not changed.",
            shell.Settings!.InterchangeStatus);
        Assert.True(instructions.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, source.ReadCount);
        Assert.Equal(1, clipboard.WriteCount);
        Assert.Equal(
            "Reusable agent instructions and v1 schemas copied.",
            shell.Settings.InterchangeStatus);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SnapshotPreparationFailureUsesFixedStatusAndNeverTouchesClipboard()
    {
        using var session = new StubWorkspaceSession(
            new MemoryWorkspaceWork(),
            new ThrowingPlanningSnapshotSource());
        var clipboard = new PlanningSnapshotCopyViewModelTests.RecordingPlainTextClipboard();
        var window = new MainWindow(session, null, null, plainTextClipboard: clipboard);
        window.Show();

        await window.OpenPlanningSnapshotAsync();
        Dispatcher.UIThread.RunJobs();

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        Assert.Equal(
            "The Planning snapshot could not be prepared. Nothing was copied.",
            shell.Settings!.InterchangeStatus);
        Assert.DoesNotContain("private", shell.Settings.InterchangeStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, clipboard.WriteCount);
        Assert.Empty(window.OwnedWindows);
        window.Close();
    }

    private sealed class RecordingPlanningSnapshotSource(PlanningSnapshotContent snapshot)
        : IPlanningSnapshotSource
    {
        public int ReadCount { get; private set; }

        public PlanningSnapshotContent Read()
        {
            ReadCount++;
            return snapshot;
        }
    }

    private sealed class ThrowingPlanningSnapshotSource : IPlanningSnapshotSource
    {
        public PlanningSnapshotContent Read() =>
            throw new InvalidOperationException("private synthetic snapshot content");
    }

    private sealed class StubWorkspaceSession(
        IWorkspaceWork work,
        IPlanningSnapshotSource planning) : IWorkspaceSession
    {
        public int SchemaVersion => 14;
        public string FirstCategoryName => "Home";
        public IWorkspaceRecovery Recovery { get; } = new RecoveryViewModelTests.StubWorkspaceRecovery();
        public IPlanningSnapshotSource Planning { get; } = planning;
        public IWorkspaceWork Work { get; } = work;

        public PassphraseRotationResult RotatePassphrase(
            WorkspacePassphrase currentPassphrase,
            WorkspacePassphrase newPassphrase) => PassphraseRotationResult.Failed();

        public void Dispose()
        {
        }
    }
}
