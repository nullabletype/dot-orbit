using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
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
        Assert.Equal(420, window.MinHeight);
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
    public void ReviewRendersScrollableAtSupportedMinimumInLightAndDarkThemes()
    {
        var colours = new List<Color>();
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            var viewModel = new PlanningSnapshotCopyViewModel(
                PlanningSnapshotCopyViewModelTests.CreateRepresentativeSnapshot(),
                includeAgentInstructionsAndSchema: true,
                new PlanningSnapshotCopyViewModelTests.RecordingPlainTextClipboard());
            var window = new PlanningSnapshotCopyWindow(viewModel)
            {
                RequestedThemeVariant = theme,
                Width = 560,
                Height = 420,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(theme, window.ActualThemeVariant);
            Assert.Equal(560, window.Bounds.Width);
            Assert.Equal(420, window.Bounds.Height);
            var scroll = Assert.IsType<ScrollViewer>(
                window.FindControl<ScrollViewer>("PlanningSnapshotReviewScrollViewer"));
            Assert.True(scroll.Viewport.Height > 0);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            Assert.True(window.FindControl<Button>("ConfirmPlanningSnapshotCopyButton")!.IsVisible);
            using var bitmap = new RenderTargetBitmap(
                new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height),
                new Vector(96, 96));
            bitmap.Render(window);
            colours.Add(Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color);
            window.Close();
        }

        Assert.Equal(Color.Parse("#090B10"), colours[0]);
        Assert.Equal(Color.Parse("#F7F4F6"), colours[1]);
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
    public async Task AiActionsRestoreSnapshotLauncherFocusAndInstructionsNeverReadWorkspace()
    {
        var source = new RecordingPlanningSnapshotSource(
            PlanningSnapshotCopyViewModelTests.CreateRepresentativeSnapshot());
        using var session = new StubWorkspaceSession(new MemoryWorkspaceWork(), source);
        var clipboard = new PlanningSnapshotCopyViewModelTests.RecordingPlainTextClipboard();
        var window = new MainWindow(session, null, null, plainTextClipboard: clipboard);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var snapshotLauncher = Assert.IsType<Button>(
            window.FindControl<Button>("AiPlanningSnapshotButton"));
        var instructions = Assert.IsType<Button>(
            window.FindControl<Button>("AiReusableInstructionsButton"));
        var preference = Assert.IsType<ToggleSwitch>(
            window.FindControl<ToggleSwitch>("AiIncludeAgentInstructionsToggle"));

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
            shell.Ai!.InterchangeStatus);
        Assert.True(instructions.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, source.ReadCount);
        Assert.Equal(1, clipboard.WriteCount);
        Assert.Equal(
            "Reusable agent instructions and v1 schemas copied.",
            shell.Ai.InterchangeStatus);
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
            shell.Ai!.InterchangeStatus);
        Assert.DoesNotContain("private", shell.Ai.InterchangeStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, clipboard.WriteCount);
        Assert.Empty(window.OwnedWindows);
        window.Close();
    }

    [AvaloniaFact]
    public void AiInterchangeOutcomesRemainVisibleReadableAndPolitelyAnnounced()
    {
        using var session = new StubWorkspaceSession(
            new MemoryWorkspaceWork(),
            new RecordingPlanningSnapshotSource(
                PlanningSnapshotCopyViewModelTests.CreateRepresentativeSnapshot()));
        var window = new MainWindow(
            session,
            null,
            null,
            plainTextClipboard: new PlanningSnapshotCopyViewModelTests.RecordingPlainTextClipboard())
        {
            Width = 1120,
            Height = 600,
        };
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var status = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("AiInterchangeStatus"));
        var outcomes = new[]
        {
            "Reusable agent instructions and v1 schemas copied.",
            "Reusable agent instructions and schemas could not be copied. Try again.",
            "The system clipboard is unavailable. Nothing was copied.",
            "The Planning snapshot could not be prepared. Nothing was copied.",
        };

        foreach (var outcome in outcomes)
        {
            shell.Ai!.ReportInterchangeStatus(outcome);
            Dispatcher.UIThread.RunJobs();

            Assert.True(status.IsVisible);
            Assert.Equal(outcome, status.Text);
            Assert.Equal(1, status.Opacity);
            Assert.True(status.Bounds.Height > 1);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
        }

        window.Close();
    }

    [AvaloniaFact]
    public void AiDestinationOwnsInterchangeContentAndSettingsDoesNotDuplicateIt()
    {
        using var session = new StubWorkspaceSession(
            new MemoryWorkspaceWork(),
            new RecordingPlanningSnapshotSource(
                PlanningSnapshotCopyViewModelTests.CreateRepresentativeSnapshot()));
        var window = new MainWindow(
            session,
            null,
            null,
            plainTextClipboard: new PlanningSnapshotCopyViewModelTests.RecordingPlainTextClipboard())
        {
            Width = 1120,
            Height = 600,
        };
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.SettingsNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var settingsRegion = Assert.IsType<ScrollViewer>(window.FindControl<ScrollViewer>("SettingsRegion"));
        var aiRegion = Assert.IsType<ScrollViewer>(window.FindControl<ScrollViewer>("AiRegion"));
        Assert.True(settingsRegion.IsVisible);
        Assert.False(aiRegion.IsVisible);
        Assert.DoesNotContain(settingsRegion.GetVisualDescendants().OfType<Control>(), control =>
            AutomationProperties.GetAutomationId(control)?.StartsWith("ai-", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(settingsRegion.GetVisualDescendants().OfType<TextBlock>(), text =>
            text.Text is "Agent interchange" or "Copy planning context");

        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(settingsRegion.IsVisible);
        Assert.True(aiRegion.IsVisible);
        Assert.True(aiRegion.Viewport.Height > 0);
        var introduction = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("AiIntroductionText"));
        Assert.Contains("does not contact an AI provider", introduction.Text, StringComparison.Ordinal);
        Assert.Contains("Copy planning context to an AI assistant", introduction.Text, StringComparison.Ordinal);
        Assert.Contains("review before anything is applied", introduction.Text, StringComparison.Ordinal);
        Assert.Contains("explicit whole-proposal approval", introduction.Text, StringComparison.Ordinal);
        Assert.Single(aiRegion.GetVisualDescendants().OfType<BoundedProposalTextBox>());

        var preference = Assert.IsType<ToggleSwitch>(
            window.FindControl<ToggleSwitch>("AiIncludeAgentInstructionsToggle"));
        var snapshot = Assert.IsType<Button>(window.FindControl<Button>("AiPlanningSnapshotButton"));
        var instructions = Assert.IsType<Button>(window.FindControl<Button>("AiReusableInstructionsButton"));
        var status = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("AiInterchangeStatus"));
        Assert.Equal("ai-interchange-include-instructions", AutomationProperties.GetAutomationId(preference));
        Assert.Equal("ai-planning-snapshot-open", AutomationProperties.GetAutomationId(snapshot));
        Assert.Equal("ai-interchange-instructions-copy", AutomationProperties.GetAutomationId(instructions));
        Assert.Equal("Agent interchange status", AutomationProperties.GetName(status));
        Assert.True(preference.Focus(NavigationMethod.Tab));
        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(snapshot.IsKeyboardFocusWithin);
        window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.True(instructions.IsKeyboardFocusWithin);
        window.Close();
    }

    [AvaloniaFact]
    public async Task AiNavigationUsesTheExistingSaveDiscardStayGuard()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Original", "", "home", null);
        using var session = new StubWorkspaceSession(
            work,
            new RecordingPlanningSnapshotSource(
                PlanningSnapshotCopyViewModelTests.CreateRepresentativeSnapshot()));
        var window = new MainWindow(session);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.Work!.SelectTask(task.Id);
        shell.Work.Title = "Edited";
        work.FailWrites = true;

        var firstDecision = DecisionRequested(shell.Work);
        shell.AiNavigation.SelectCommand.Execute(null);
        await firstDecision;
        Dispatcher.UIThread.RunJobs();

        Assert.True(shell.Work.NeedsDecision);
        Assert.False(shell.ShowAI);
        Assert.True(window.FindControl<Button>("GuardSave")!.IsKeyboardFocusWithin);
        shell.Work.StayCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(shell.Work.NeedsDecision);
        Assert.False(shell.ShowAI);
        Assert.Equal("Edited", shell.Work.Title);
        Assert.Equal("Original", work.Read().Tasks.Single(item => item.Id == task.Id).Title);

        var secondDecision = DecisionRequested(shell.Work);
        shell.AiNavigation.SelectCommand.Execute(null);
        await secondDecision;
        Dispatcher.UIThread.RunJobs();
        Assert.True(shell.Work.NeedsDecision);
        shell.Work.DiscardAndLeaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(shell.ShowAI);
        Assert.Equal("Original", work.Read().Tasks.Single(item => item.Id == task.Id).Title);
        window.Close();
    }

    private static async Task DecisionRequested(ProjectCaptureViewModel work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler changed = (_, args) =>
        {
            if (args.PropertyName != nameof(ProjectCaptureViewModel.NeedsDecision) || !work.NeedsDecision)
            {
                return;
            }

            completion.TrySetResult();
        };
        work.PropertyChanged += changed;
        try
        {
            await completion.Task.WaitAsync(
                TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            work.PropertyChanged -= changed;
        }
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
        public DotOrbit.Core.Interchange.IProposalWorkspaceSource Proposals => throw new NotSupportedException();

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
