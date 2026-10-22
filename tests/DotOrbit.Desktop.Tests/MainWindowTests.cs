using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.Clipboard;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using DotOrbit.Interchange.Json;
using DotOrbit.Mcp.Ipc;
using DotOrbit.Storage.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public async Task InputMutationDuringReviewDiscardsLatePlanAndNeverEnablesApply()
    {
        var source = new BlockingProposalSource();
        var session = new ProposalWorkspaceSession(source);
        var window = new MainWindow(session, aiFeaturePreferenceService: EnabledAiFeatures());
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        var editor = Assert.IsType<BoundedProposalTextBox>(
            window.FindControl<BoundedProposalTextBox>("AiProposalTextBox"));
        editor.Text = ProposalJson(source.State, "Original synthetic task");
        var launcher = Assert.IsType<Button>(window.FindControl<Button>("AiProposalReviewButton"));

        var review = window.CompleteProposalReviewAsync(launcher);
        await source.ReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        editor.Text = ProposalJson(source.State, "Changed while validation was running");
        source.ReleaseRead.Set();
        await review;

        Assert.Contains("invalidated", shell.Ai!.ProposalStatus, StringComparison.Ordinal);
        Assert.True(shell.Ai.CanReviewProposal);
        Assert.True(launcher.IsEnabled);
        Assert.True(launcher.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RejectedExplicitPastePreservesPasteButtonFocusAndExistingText()
    {
        var window = new MainWindow(
            new DisposalTrackingWorkspaceSession(),
            null,
            null,
            proposalClipboardReader: new StaticProposalClipboardReader("\ud800"),
            aiFeaturePreferenceService: EnabledAiFeatures());
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var editor = Assert.IsType<BoundedProposalTextBox>(
            window.FindControl<BoundedProposalTextBox>("AiProposalTextBox"));
        editor.Text = "preserved";
        var paste = Assert.IsType<Button>(window.FindControl<Button>("AiProposalPasteButton"));
        paste.Focus();
        Assert.True(paste.IsFocused);

        paste.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("preserved", editor.Text);
        Assert.True(paste.IsFocused);
        Assert.Equal(AiViewModel.ProposalInputRejectedStatus, shell.Ai!.ProposalStatus);
        window.Close();
    }

    [AvaloniaFact]
    public void RepeatedExplicitPasteReplacesAndRendersTheWholeProposal()
    {
        var window = new MainWindow(
            new DisposalTrackingWorkspaceSession(),
            null,
            null,
            proposalClipboardReader: new StaticProposalClipboardReader("replacement"),
            aiFeaturePreferenceService: EnabledAiFeatures());
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var editor = Assert.IsType<BoundedProposalTextBox>(
            window.FindControl<BoundedProposalTextBox>("AiProposalTextBox"));
        editor.Text = "existing";
        editor.SelectionStart = editor.Text.Length;
        editor.SelectionEnd = editor.Text.Length;
        var paste = Assert.IsType<Button>(window.FindControl<Button>("AiProposalPasteButton"));

        paste.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        paste.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("replacement", editor.Text);
        Assert.Equal(editor.Text.Length, editor.CaretIndex);
        Assert.Contains(
            editor.GetVisualDescendants().OfType<TextPresenter>(),
            presenter => presenter.IsEffectivelyVisible && presenter.Text == "replacement");
        window.Close();
    }

    [AvaloniaFact]
    public async Task LaterTypingPreventsEarlierFileLoadFromReplacingProposalText()
    {
        var intake = new BlockingProposalFileIntake();
        var window = new MainWindow(
            new DisposalTrackingWorkspaceSession(),
            null,
            null,
            proposalFilePicker: new StaticProposalFilePicker("/synthetic/proposal.json"),
            proposalFileIntake: intake,
            aiFeaturePreferenceService: EnabledAiFeatures());
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var editor = Assert.IsType<BoundedProposalTextBox>(
            window.FindControl<BoundedProposalTextBox>("AiProposalTextBox"));
        var load = Assert.IsType<Button>(window.FindControl<Button>("AiProposalLoadButton"));

        load.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await intake.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        editor.Text = "newer typed proposal";
        intake.Complete(ProposalFileIntakeResult.Loaded("older file proposal"));
        await DrainUiUntilAsync(
            () => shell.Ai!.ProposalStatus.Contains("superseded", StringComparison.Ordinal));

        Assert.Equal("newer typed proposal", editor.Text);
        Assert.True(intake.ObservedCancellation);
        Assert.Contains("not changed", shell.Ai!.ProposalStatus, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DisablingAiFeaturesCancelsPendingProposalIntakeAndDiscardsAiState()
    {
        var preferences = new TransientAiFeaturePreferenceService(aiFeaturesEnabled: true);
        var intake = new BlockingProposalFileIntake();
        var window = new MainWindow(
            new DisposalTrackingWorkspaceSession(),
            null,
            null,
            proposalFilePicker: new StaticProposalFilePicker("/synthetic/proposal.json"),
            proposalFileIntake: intake,
            aiFeaturePreferenceService: preferences);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var editor = Assert.IsType<BoundedProposalTextBox>(
            window.FindControl<BoundedProposalTextBox>("AiProposalTextBox"));
        editor.Text = "sensitive in-memory proposal";

        var operation = window.LoadProposalFromPickerAsync(editor);
        await intake.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        shell.SettingsNavigation.SelectCommand.Execute(null);
        shell.Settings!.AiFeaturesEnabled = false;
        Dispatcher.UIThread.RunJobs();
        intake.Complete(ProposalFileIntakeResult.Loaded("late proposal"));
        await operation;

        Assert.True(intake.ObservedCancellation);
        Assert.False(shell.AiFeaturesEnabled);
        Assert.Null(shell.Ai);
        Assert.True(shell.ShowSettings);
        Assert.True(string.IsNullOrEmpty(editor.Text));
        Assert.DoesNotContain(
            window.GetVisualDescendants().OfType<RadioButton>(),
            item => AutomationProperties.GetAutomationId(item) == "navigation-ai");

        shell.Settings!.AiFeaturesEnabled = true;
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(shell.Ai);
        Assert.Equal(string.Empty, shell.Ai.ProposalText);
        Assert.True(string.IsNullOrEmpty(editor.Text));
        window.Close();
    }

    [AvaloniaFact]
    public async Task DisabledAiFeaturesRejectDirectInterchangeHandlersBeforeAccessingDependencies()
    {
        var session = new DisposalTrackingWorkspaceSession();
        var project = session.Work.CreateProject("Original", "", "home", null);
        var window = new MainWindow(
            session,
            null,
            null,
            proposalClipboardReader: new ThrowingProposalClipboardReader(),
            proposalFilePicker: new ThrowingProposalFilePicker(),
            proposalFileIntake: new ThrowingProposalFileIntake(),
            aiFeaturePreferenceService: new TransientAiFeaturePreferenceService());
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.Work!.SelectProject(project.Id);
        shell.Work.Title = "Unsaved direct-handler draft";
        Assert.True(shell.Work.IsDirty);
        var editor = Assert.IsType<BoundedProposalTextBox>(
            window.FindControl<BoundedProposalTextBox>("AiProposalTextBox"));
        var dataTransferItem = new DataTransferItem();
        dataTransferItem.Set(
            DataFormat.File,
            () => throw new InvalidOperationException("The disabled AI gate inspected dropped file data."));
        var dataTransfer = new DataTransfer();
        dataTransfer.Add(dataTransferItem);

        await window.OpenPlanningSnapshotAsync();
        await window.PasteProposalAsync(editor, null);
        await window.LoadProposalFromPickerAsync(null);
        await window.CompleteProposalReviewAsync(null);
        Assert.Equal(DragDropEffects.None, window.ProposalDragEffects(dataTransfer));
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.AiFeaturesEnabled);
        Assert.Null(shell.Ai);
        Assert.False(shell.Work.NeedsDecision);
        Assert.Equal("Original", session.Work.Read().Projects.Single().Title);
        Assert.Empty(window.OwnedWindows);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DisablingAiFeaturesConsumesAndClosesAnUnapprovedProposalReview()
    {
        var state = new ProposalWorkspaceState(
            new(1),
            new([new("c", "Synthetic", 0)], [], [], []),
            [],
            []);
        var preferences = new TransientAiFeaturePreferenceService(aiFeaturesEnabled: true);
        var application = new RecordingProposalApplication();
        var window = new MainWindow(
            new ProposalWorkspaceSession(new FixedProposalSource(state), application),
            aiFeaturePreferenceService: preferences);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var editor = Assert.IsType<BoundedProposalTextBox>(
            window.FindControl<BoundedProposalTextBox>("AiProposalTextBox"));
        editor.Text = ProposalJson(state, "Synthetic task");

        await window.CompleteProposalReviewAsync(null);
        Dispatcher.UIThread.RunJobs();
        var review = Assert.Single(window.OwnedWindows.OfType<AgentProposalReviewWindow>());
        var reviewModel = Assert.IsType<AgentProposalReviewViewModel>(review.DataContext);
        Assert.Equal("Text / JSON import", reviewModel.SourceLabel);
        Assert.True(reviewModel.CanApply);

        shell.Settings!.AiFeaturesEnabled = false;
        Dispatcher.UIThread.RunJobs();

        Assert.False(reviewModel.CanApply);
        Assert.Equal(0, application.ApproveCount);
        Assert.DoesNotContain(review, window.OwnedWindows);
        Assert.Null(shell.Ai);
        window.Close();
    }

    [AvaloniaFact]
    public async Task DisablingAiFeaturesDoesNotInterruptOrReplayAnApprovedAtomicApply()
    {
        using var directory = new TemporaryDirectory();
        const string passphraseText = "synthetic AI gate apply password";
        var passphrase = WorkspacePassphrase.Create(passphraseText, passphraseText).Passphrase!;
        var innerSession = Assert.IsAssignableFrom<IWorkspaceSession>(
            new EncryptedWorkspaceStore().Create(
                Path.Combine(directory.Path, "workspace.db"),
                passphrase,
                CategoryName.Create("Home").CategoryName!).Session);
        Assert.Equal(
            RecoveryDirectoryConfigurationStatus.Configured,
            innerSession.Recovery.ConfigureAutomaticRecoveryDirectory(
                Path.Combine(directory.Path, "recovery")).Status);
        using var application = new BlockingProposalApplication(innerSession.ProposalApplication);
        var preferences = new TransientAiFeaturePreferenceService(aiFeaturesEnabled: true);
        var window = new MainWindow(
            new ProposalApplicationWorkspaceSession(innerSession, application),
            aiFeaturePreferenceService: preferences);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var state = innerSession.Proposals.Read();
        var editor = Assert.IsType<BoundedProposalTextBox>(
            window.FindControl<BoundedProposalTextBox>("AiProposalTextBox"));
        editor.Text = ProposalJson(state, "Approved synthetic task");
        await window.CompleteProposalReviewAsync(null);
        Dispatcher.UIThread.RunJobs();
        var review = Assert.Single(window.OwnedWindows.OfType<AgentProposalReviewWindow>());
        var reviewModel = Assert.IsType<AgentProposalReviewViewModel>(review.DataContext);
        var apply = reviewModel.ApplyAsync(new ReadyProposalApplicationHost());
        await application.ApplyStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            shell.Settings!.AiFeaturesEnabled = false;
            Dispatcher.UIThread.RunJobs();

            Assert.False(reviewModel.CanClose);
            Assert.Contains(review, window.OwnedWindows);
            Assert.Null(shell.Ai);
            Assert.Equal(1, application.ApproveCount);
            Assert.Equal(1, application.ApplyCount);
        }
        finally
        {
            application.ReleaseApply.Set();
        }

        await apply;
        await reviewModel.ApplyAsync(new ReadyProposalApplicationHost());

        Assert.True(reviewModel.CanClose);
        Assert.Contains("applied atomically", reviewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(1, application.ApproveCount);
        Assert.Equal(1, application.ApplyCount);
        Assert.Single(innerSession.Work.Read().Tasks, task => task.Title == "Approved synthetic task");
        review.Close();
        window.Close();
    }

    [AvaloniaFact]
    public async Task PendingInboundProposalKeepsManualImportEditableAndRemainsReviewableAfterTheGlobalAiGate()
    {
        using var directory = new TemporaryDirectory();
        const string passphraseText = "synthetic pending inbound password";
        var passphrase = WorkspacePassphrase.Create(passphraseText, passphraseText).Passphrase!;
        var session = Assert.IsAssignableFrom<IWorkspaceSession>(
            new EncryptedWorkspaceStore().Create(
                Path.Combine(directory.Path, "workspace.orb"),
                passphrase,
                CategoryName.Create("Home").CategoryName!).Session);
        var payload = Encoding.UTF8.GetBytes(ProposalJson(session.Proposals.Read(), "Inbound synthetic task"));
        var pending = new PendingAgentProposal(
            "receipt-1",
            "key-1",
            "authorization-1",
            new string('a', 64),
            "Unverified test client",
            DateTimeOffset.UnixEpoch,
            ProposalKind.NewWork,
            1,
            1,
            payload);
        Assert.Equal(
            PendingAgentProposalStoreStatus.Stored,
            session.AgentIntegration.TryStorePendingProposal(pending).Status);
        var preferences = new TransientAiFeaturePreferenceService(aiFeaturesEnabled: true);
        var window = new MainWindow(session, aiFeaturePreferenceService: preferences);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var editor = Assert.IsType<BoundedProposalTextBox>(
            window.FindControl<BoundedProposalTextBox>("AiProposalTextBox"));
        var paste = Assert.IsType<Button>(window.FindControl<Button>("AiProposalPasteButton"));
        var load = Assert.IsType<Button>(window.FindControl<Button>("AiProposalLoadButton"));
        Assert.False(editor.IsReadOnly);
        Assert.True(paste.IsEnabled);
        Assert.True(load.IsEnabled);
        Assert.True(shell.Ai!.CanReplaceProposalText);
        Assert.Empty(editor.Text ?? string.Empty);
        Assert.Equal("Review 1", shell.AiNavigation.AttentionText);
        Assert.Single(shell.Ai!.PendingProposals);

        shell.Settings!.AiFeaturesEnabled = false;
        Dispatcher.UIThread.RunJobs();
        shell.Settings.AiFeaturesEnabled = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Single(session.AgentIntegration.ReadPendingProposals());
        Assert.True(shell.Ai!.HasPendingInboundProposal);
        Assert.Empty(shell.Ai.ProposalText);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        await window.CompleteInboundProposalReviewAsync("receipt-1");
        Dispatcher.UIThread.RunJobs();
        var review = Assert.Single(window.OwnedWindows.OfType<AgentProposalReviewWindow>());
        Assert.Equal(
            "Unverified test client via local MCP",
            Assert.IsType<AgentProposalReviewViewModel>(review.DataContext).SourceLabel);
        review.Close();
        window.Close();
    }

    [AvaloniaFact]
    public async Task StayingAfterACancelledCloseKeepsThePendingProposalReviewable()
    {
        using var directory = new TemporaryDirectory();
        const string passphraseText = "synthetic cancelled close queue password";
        var passphrase = WorkspacePassphrase.Create(passphraseText, passphraseText).Passphrase!;
        var innerSession = Assert.IsAssignableFrom<IWorkspaceSession>(
            new EncryptedWorkspaceStore().Create(
                Path.Combine(directory.Path, "workspace.orb"),
                passphrase,
                CategoryName.Create("Home").CategoryName!).Session);
        var session = new DecisionWorkspaceSession(innerSession);
        var windowSizePreferences = new RecordingMainWindowSizePreferenceStore();
        var payload = Encoding.UTF8.GetBytes(ProposalJson(innerSession.Proposals.Read(), "Queued after close"));
        Assert.Equal(
            PendingAgentProposalStoreStatus.Stored,
            session.AgentIntegration.TryStorePendingProposal(new PendingAgentProposal(
                "receipt-close",
                "key-close",
                "authorization-close",
                new string('a', 64),
                "Codex",
                DateTimeOffset.UnixEpoch,
                ProposalKind.NewWork,
                1,
                1,
                payload)).Status);
        var window = new MainWindow(
            session,
            aiFeaturePreferenceService: new TransientAiFeaturePreferenceService(aiFeaturesEnabled: true),
            mainWindowSizePreferenceStore: windowSizePreferences,
            mainWindowScreenProvider: new FixedMainWindowScreenProvider(new(1600, 1000)));
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.Work!.NewProjectCommand.Execute(null);
        shell.Work.Title = "Unsaved";
        session.MemoryWork.FailWrites = true;

        window.Close();
        await DrainUiUntilAsync(() => shell.Work.NeedsDecision);
        shell.Work.StayCommand.Execute(null);

        Assert.True(window.IsVisible);
        Assert.False(shell.Work.NeedsDecision);
        Assert.Equal(0, windowSizePreferences.SaveAttempts);
        session.MemoryWork.FailWrites = false;
        shell.AiNavigation.SelectCommand.Execute(null);
        await DrainUiUntilAsync(() => shell.ShowAI);
        await window.CompleteInboundProposalReviewAsync("receipt-close");
        Dispatcher.UIThread.RunJobs();
        var review = Assert.Single(window.OwnedWindows.OfType<AgentProposalReviewWindow>());
        Assert.Equal(
            "Codex via local MCP",
            Assert.IsType<AgentProposalReviewViewModel>(review.DataContext).SourceLabel);
        review.Close();
        window.Close();
        await DrainUiUntilAsync(() => !window.IsVisible);
        Assert.Equal(1, windowSizePreferences.SaveAttempts);
    }

    [AvaloniaFact]
    public async Task InboundProposalRowUsesExistingModalWithSelfReportedSource()
    {
        using var directory = new TemporaryDirectory();
        const string passphraseText = "synthetic inbound modal password";
        var passphrase = WorkspacePassphrase.Create(passphraseText, passphraseText).Passphrase!;
        var session = Assert.IsAssignableFrom<IWorkspaceSession>(
            new EncryptedWorkspaceStore().Create(
                Path.Combine(directory.Path, "workspace.orb"),
                passphrase,
                CategoryName.Create("Home").CategoryName!).Session);
        var payload = Encoding.UTF8.GetBytes(ProposalJson(session.Proposals.Read(), "Inbound modal task"));
        Assert.Equal(
            PendingAgentProposalStoreStatus.Stored,
            session.AgentIntegration.TryStorePendingProposal(new PendingAgentProposal(
                "receipt-modal",
                "key-modal",
                "authorization-modal",
                new string('c', 64),
                "Codex",
                DateTimeOffset.UnixEpoch,
                ProposalKind.NewWork,
                1,
                1,
                payload)).Status);
        var window = new MainWindow(
            session,
            aiFeaturePreferenceService: new TransientAiFeaturePreferenceService(aiFeaturesEnabled: true));
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        await window.CompleteInboundProposalReviewAsync("receipt-modal");
        Dispatcher.UIThread.RunJobs();

        var review = Assert.Single(window.OwnedWindows.OfType<AgentProposalReviewWindow>());
        var reviewModel = Assert.IsType<AgentProposalReviewViewModel>(review.DataContext);
        Assert.Equal("Codex via local MCP", reviewModel.SourceLabel);
        Assert.Contains("Self-reported", reviewModel.SourceDetail, StringComparison.Ordinal);
        Assert.Single(session.AgentIntegration.ReadPendingProposals());
        review.Close();
        window.Close();
    }

    [AvaloniaFact]
    public async Task ApplyingOneQueuedChangeProposalRefreshesAnUnrelatedProposalForReview()
    {
        using var directory = new TemporaryDirectory();
        const string passphraseText = "synthetic stale inbound queue password";
        var passphrase = WorkspacePassphrase.Create(passphraseText, passphraseText).Passphrase!;
        var session = Assert.IsAssignableFrom<IWorkspaceSession>(
            new EncryptedWorkspaceStore().Create(
                Path.Combine(directory.Path, "workspace.orb"),
                passphrase,
                CategoryName.Create("Home").CategoryName!).Session);
        Assert.Equal(
            RecoveryDirectoryConfigurationStatus.Configured,
            session.Recovery.ConfigureAutomaticRecoveryDirectory(
                Path.Combine(directory.Path, "recovery")).Status);
        var state = session.Proposals.Read();
        var proposals = new[]
        {
            (Receipt: "receipt-first", Title: "First inbound task"),
            (Receipt: "receipt-second", Title: "Second inbound task"),
        };
        foreach (var (receipt, title) in proposals)
        {
            var payload = Encoding.UTF8.GetBytes(ProposalJson(state, title));
            var admitted = new ProposalReviewBoundary(session.Proposals, TimeProvider.System)
                .PrepareQueued(payload);
            Assert.NotNull(admitted.Plan);
            Assert.Equal(
                PendingAgentProposalStoreStatus.Stored,
                session.AgentIntegration.TryStorePendingProposal(new PendingAgentProposal(
                    receipt,
                    $"key-{receipt}",
                    "authorization-stale",
                    new string(receipt == "receipt-first" ? 'd' : 'e', 64),
                    "Codex",
                    DateTimeOffset.UnixEpoch,
                    ProposalKind.WorkspaceChange,
                    1,
                    1,
                    admitted.ReviewBasis.Span,
                    payload)).Status);
        }

        var window = new MainWindow(
            session,
            aiFeaturePreferenceService: new TransientAiFeaturePreferenceService(aiFeaturesEnabled: true));
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        await window.CompleteInboundProposalReviewAsync("receipt-first");
        Dispatcher.UIThread.RunJobs();
        var firstReview = Assert.Single(window.OwnedWindows.OfType<AgentProposalReviewWindow>());
        var firstReviewModel = Assert.IsType<AgentProposalReviewViewModel>(firstReview.DataContext);
        await firstReviewModel.ApplyAsync(new ReadyProposalApplicationHost());
        firstReview.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(
            session.AgentIntegration.ReadPendingProposals(),
            proposal => proposal.Receipt == "receipt-first");
        Assert.Contains(
            session.AgentIntegration.ReadPendingProposals(),
            proposal => proposal.Receipt == "receipt-second");

        var secondReview = window.GetVisualDescendants().OfType<Button>().Single(button =>
            button.DataContext is PendingProposalRowViewModel { Receipt: "receipt-second" }
            && string.Equals(button.Content as string, "Review", StringComparison.Ordinal));
        secondReview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await DrainUiUntilAsync(() => window.OwnedWindows.OfType<AgentProposalReviewWindow>().Any());

        var refreshedReview = Assert.Single(window.OwnedWindows.OfType<AgentProposalReviewWindow>());
        var refreshedModel = Assert.IsType<AgentProposalReviewViewModel>(refreshedReview.DataContext);
        Assert.True(refreshedModel.ProposalWasRefreshed);
        Assert.True(refreshedModel.CanApply);
        Assert.Equal("2", refreshedModel.SourceWorkspaceRevision);
        await refreshedModel.ApplyAsync(new ReadyProposalApplicationHost());
        refreshedReview.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(session.AgentIntegration.ReadPendingProposals());
        Assert.Contains(session.Work.Read().Tasks, task => task.Title == "First inbound task");
        Assert.Contains(session.Work.Read().Tasks, task => task.Title == "Second inbound task");
        window.Close();
    }

    [AvaloniaFact]
    public async Task AddingTwoQueuedTasksLeavesAnExistingTaskEditReviewable()
    {
        using var directory = new TemporaryDirectory();
        const string passphraseText = "synthetic mergeable inbound queue password";
        var passphrase = WorkspacePassphrase.Create(passphraseText, passphraseText).Passphrase!;
        var session = Assert.IsAssignableFrom<IWorkspaceSession>(
            new EncryptedWorkspaceStore().Create(
                Path.Combine(directory.Path, "workspace.orb"),
                passphrase,
                CategoryName.Create("Home").CategoryName!).Session);
        Assert.Equal(
            RecoveryDirectoryConfigurationStatus.Configured,
            session.Recovery.ConfigureAutomaticRecoveryDirectory(
                Path.Combine(directory.Path, "recovery")).Status);
        var category = Assert.Single(session.Work.Read().Categories);
        var existingTask = session.Work.CreateStandaloneTask(
            "Existing task",
            "",
            category.Id,
            null);
        var state = session.Proposals.Read();
        var proposals = new[]
        {
            (Receipt: "receipt-new-one", Payload: Encoding.UTF8.GetBytes(ProposalJson(state, "New task one"))),
            (Receipt: "receipt-new-two", Payload: Encoding.UTF8.GetBytes(ProposalJson(state, "New task two"))),
            (Receipt: "receipt-edit-existing", Payload: Encoding.UTF8.GetBytes(EditTaskProposalJson(
                state,
                existingTask.Id,
                "Edited existing task"))),
        };
        foreach (var (receipt, payload) in proposals)
        {
            var admitted = new ProposalReviewBoundary(session.Proposals, TimeProvider.System)
                .PrepareQueued(payload);
            Assert.NotNull(admitted.Plan);
            Assert.Equal(
                PendingAgentProposalStoreStatus.Stored,
                session.AgentIntegration.TryStorePendingProposal(new PendingAgentProposal(
                    receipt,
                    $"key-{receipt}",
                    "authorization-mergeable",
                    Convert.ToHexStringLower(SHA256.HashData(payload)),
                    "Codex",
                    DateTimeOffset.UnixEpoch,
                    ProposalKind.WorkspaceChange,
                    1,
                    1,
                    admitted.ReviewBasis.Span,
                    payload)).Status);
        }

        var window = new MainWindow(
            session,
            aiFeaturePreferenceService: new TransientAiFeaturePreferenceService(aiFeaturesEnabled: true));
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        foreach (var receipt in new[] { "receipt-new-one", "receipt-new-two" })
        {
            await window.CompleteInboundProposalReviewAsync(receipt);
            Dispatcher.UIThread.RunJobs();
            var review = Assert.Single(window.OwnedWindows.OfType<AgentProposalReviewWindow>());
            var reviewModel = Assert.IsType<AgentProposalReviewViewModel>(review.DataContext);
            await reviewModel.ApplyAsync(new ReadyProposalApplicationHost());
            review.Close();
            Dispatcher.UIThread.RunJobs();
        }

        await window.CompleteInboundProposalReviewAsync("receipt-edit-existing");
        Dispatcher.UIThread.RunJobs();

        var refreshedReview = Assert.Single(window.OwnedWindows.OfType<AgentProposalReviewWindow>());
        var refreshedModel = Assert.IsType<AgentProposalReviewViewModel>(refreshedReview.DataContext);
        Assert.True(refreshedModel.ProposalWasRefreshed);
        Assert.True(refreshedModel.CanApply);
        await refreshedModel.ApplyAsync(new ReadyProposalApplicationHost());
        refreshedReview.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            "Edited existing task",
            session.Work.Read().Tasks.Single(task => task.Id == existingTask.Id).Title);
        Assert.Empty(session.AgentIntegration.ReadPendingProposals());
        window.Close();
    }

    [AvaloniaFact]
    public async Task ApplyingOneQueuedNewWorkProposalLeavesTheNextRowReviewable()
    {
        using var directory = new TemporaryDirectory();
        const string passphraseText = "synthetic sequential inbound queue password";
        var passphrase = WorkspacePassphrase.Create(passphraseText, passphraseText).Passphrase!;
        var session = Assert.IsAssignableFrom<IWorkspaceSession>(
            new EncryptedWorkspaceStore().Create(
                Path.Combine(directory.Path, "workspace.orb"),
                passphrase,
                CategoryName.Create("Home").CategoryName!).Session);
        Assert.Equal(
            RecoveryDirectoryConfigurationStatus.Configured,
            session.Recovery.ConfigureAutomaticRecoveryDirectory(
                Path.Combine(directory.Path, "recovery")).Status);
        var proposals = new[]
        {
            (Receipt: "receipt-first-new", Suffix: "first"),
            (Receipt: "receipt-second-new", Suffix: "second"),
        };
        foreach (var (receipt, suffix) in proposals)
        {
            var payload = Encoding.UTF8.GetBytes(NewWorkProposalJson(suffix));
            Assert.Equal(
                PendingAgentProposalStoreStatus.Stored,
                session.AgentIntegration.TryStorePendingProposal(new PendingAgentProposal(
                    receipt,
                    $"key-{receipt}",
                    "authorization-sequential",
                    new string(suffix == "first" ? 'f' : 'a', 64),
                    "Codex",
                    DateTimeOffset.UnixEpoch,
                    ProposalKind.NewWork,
                    2,
                    2,
                    payload)).Status);
        }

        var window = new MainWindow(
            session,
            aiFeaturePreferenceService: new TransientAiFeaturePreferenceService(aiFeaturesEnabled: true));
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        await window.CompleteInboundProposalReviewAsync("receipt-first-new");
        Dispatcher.UIThread.RunJobs();
        var firstReview = Assert.Single(window.OwnedWindows.OfType<AgentProposalReviewWindow>());
        var firstReviewModel = Assert.IsType<AgentProposalReviewViewModel>(firstReview.DataContext);
        await firstReviewModel.ApplyAsync(new ReadyProposalApplicationHost());
        firstReview.Close();
        Dispatcher.UIThread.RunJobs();

        var secondReviewButton = window.GetVisualDescendants().OfType<Button>().Single(button =>
            button.DataContext is PendingProposalRowViewModel { Receipt: "receipt-second-new" }
            && string.Equals(button.Content as string, "Review", StringComparison.Ordinal));
        secondReviewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await DrainUiUntilAsync(() => window.OwnedWindows.OfType<AgentProposalReviewWindow>().Any());

        var secondReview = Assert.Single(window.OwnedWindows.OfType<AgentProposalReviewWindow>());
        Assert.True(Assert.IsType<AgentProposalReviewViewModel>(secondReview.DataContext).CanApply);
        secondReview.Close();
        window.Close();
    }

    [AvaloniaFact]
    public async Task PendingReviewFailureAppearsInTheSummaryWithoutChangingRowHeight()
    {
        using var directory = new TemporaryDirectory();
        const string passphraseText = "synthetic inbound review summary password";
        var passphrase = WorkspacePassphrase.Create(passphraseText, passphraseText).Passphrase!;
        var session = Assert.IsAssignableFrom<IWorkspaceSession>(
            new EncryptedWorkspaceStore().Create(
                Path.Combine(directory.Path, "workspace.orb"),
                passphrase,
                CategoryName.Create("Home").CategoryName!).Session);
        var state = session.Proposals.Read();
        var payload = Encoding.UTF8.GetBytes(ProposalJson(state, "Legacy queued task"));
        Assert.Equal(
            PendingAgentProposalStoreStatus.Stored,
            session.AgentIntegration.TryStorePendingProposal(new PendingAgentProposal(
                "receipt-summary-status",
                "key-summary-status",
                "authorization-summary-status",
                new string('f', 64),
                "Codex",
                DateTimeOffset.UnixEpoch,
                ProposalKind.WorkspaceChange,
                1,
                1,
                payload)).Status);
        var category = Assert.Single(session.Work.Read().Categories);
        session.Work.CreateStandaloneTask("Unrelated task", string.Empty, category.Id, null);
        var window = new MainWindow(
            session,
            aiFeaturePreferenceService: new TransientAiFeaturePreferenceService(aiFeaturesEnabled: true));
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var row = window.GetVisualDescendants().OfType<Border>().Single(border =>
            border.DataContext is PendingProposalRowViewModel { Receipt: "receipt-summary-status" });
        var originalHeight = row.Bounds.Height;

        var review = row.GetVisualDescendants().OfType<Button>().Single(button =>
            string.Equals(button.Content as string, "Review", StringComparison.Ordinal));
        review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await DrainUiUntilAsync(() => shell.Ai!.HasPendingProposalReviewStatus);

        var summary = Assert.IsType<Border>(window.FindControl<Border>("AiPendingProposalSummary"));
        var list = Assert.IsType<ItemsControl>(window.FindControl<ItemsControl>("AiPendingProposalList"));
        Assert.Contains(
            summary.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text?.Contains("earlier proposal", StringComparison.OrdinalIgnoreCase) == true
                    && text.IsEffectivelyVisible);
        Assert.DoesNotContain(
            list.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text?.Contains("earlier proposal", StringComparison.OrdinalIgnoreCase) == true);
        Assert.False(Assert.IsType<SelectableTextBlock>(
            window.FindControl<SelectableTextBlock>("AiProposalStatus")).IsEffectivelyVisible);
        Assert.Equal(originalHeight, row.Bounds.Height);
        Assert.All(
            row.GetVisualDescendants().OfType<Button>(),
            button => Assert.Equal(VerticalAlignment.Center, button.VerticalAlignment));
        Assert.Empty(window.OwnedWindows.OfType<AgentProposalReviewWindow>());
        Assert.NotNull(session.AgentIntegration.FindPendingProposal("receipt-summary-status"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ArrivalReviewWhenAiIsAlreadySelectedOpensTheOldestPendingProposal()
    {
        using var directory = new TemporaryDirectory();
        const string passphraseText = "synthetic arrival review password";
        var passphrase = WorkspacePassphrase.Create(passphraseText, passphraseText).Passphrase!;
        var session = Assert.IsAssignableFrom<IWorkspaceSession>(
            new EncryptedWorkspaceStore().Create(
                Path.Combine(directory.Path, "workspace.orb"),
                passphrase,
                CategoryName.Create("Home").CategoryName!).Session);
        var window = new MainWindow(
            session,
            aiFeaturePreferenceService: new TransientAiFeaturePreferenceService(aiFeaturesEnabled: true));
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var submission = await Assert.IsAssignableFrom<IInboundProposalSink>(window.PendingProposalSink)
            .TrySubmitAsync(
                new InboundProposalSubmission(
                    "receipt-arrival",
                    "key-arrival",
                    "authorization-arrival",
                    new string('b', 64),
                    "Codex",
                    DateTimeOffset.UnixEpoch,
                    Encoding.UTF8.GetBytes(NewWorkProposalJson("arrival"))),
                TestContext.Current.CancellationToken);
        Assert.Equal(InboundProposalSubmissionStatus.Accepted, submission.Status);
        await DrainUiUntilAsync(() => shell.HasProposalArrivalNotification);
        var notification = Assert.IsType<Border>(
            window.FindControl<Border>("ProposalArrivalNotification"));
        Assert.True(notification.IsEffectivelyVisible);
        var reviewButton = notification.GetVisualDescendants().OfType<Button>().Single(button =>
            string.Equals(button.Content as string, "Review", StringComparison.Ordinal));

        reviewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await DrainUiUntilAsync(() => window.OwnedWindows.OfType<AgentProposalReviewWindow>().Any());

        var review = Assert.Single(window.OwnedWindows.OfType<AgentProposalReviewWindow>());
        Assert.Equal(
            "Codex via local MCP",
            Assert.IsType<AgentProposalReviewViewModel>(review.DataContext).SourceLabel);
        Assert.False(shell.HasProposalArrivalNotification);
        review.Close();
        window.Close();
    }

    [AvaloniaFact]
    public async Task ArrivalReviewKeepsTheBannerWhenInspectorNavigationStaysPut()
    {
        using var directory = new TemporaryDirectory();
        const string passphraseText = "synthetic deferred arrival review password";
        var passphrase = WorkspacePassphrase.Create(passphraseText, passphraseText).Passphrase!;
        var innerSession = Assert.IsAssignableFrom<IWorkspaceSession>(
            new EncryptedWorkspaceStore().Create(
                Path.Combine(directory.Path, "workspace.orb"),
                passphrase,
                CategoryName.Create("Home").CategoryName!).Session);
        var session = new DecisionWorkspaceSession(innerSession);
        var window = new MainWindow(
            session,
            aiFeaturePreferenceService: new TransientAiFeaturePreferenceService(aiFeaturesEnabled: true));
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.Work!.NewProjectCommand.Execute(null);
        shell.Work.Title = "Unsaved";
        session.MemoryWork.FailWrites = true;
        var submission = await Assert.IsAssignableFrom<IInboundProposalSink>(window.PendingProposalSink)
            .TrySubmitAsync(
                new InboundProposalSubmission(
                    "receipt-deferred-arrival",
                    "key-deferred-arrival",
                    "authorization-deferred-arrival",
                    new string('c', 64),
                    "Codex",
                    DateTimeOffset.UnixEpoch,
                    Encoding.UTF8.GetBytes(NewWorkProposalJson("deferred-arrival"))),
                TestContext.Current.CancellationToken);
        Assert.Equal(InboundProposalSubmissionStatus.Accepted, submission.Status);
        await DrainUiUntilAsync(() => shell.HasProposalArrivalNotification);
        var notification = Assert.IsType<Border>(
            window.FindControl<Border>("ProposalArrivalNotification"));
        var reviewButton = notification.GetVisualDescendants().OfType<Button>().Single(button =>
            string.Equals(button.Content as string, "Review", StringComparison.Ordinal));

        reviewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await DrainUiUntilAsync(() => shell.Work.NeedsDecision);

        Assert.True(shell.HasProposalArrivalNotification);
        Assert.False(shell.ShowAI);
        shell.Work.StayCommand.Execute(null);
        Assert.True(shell.HasProposalArrivalNotification);
        Assert.False(shell.ShowAI);
        session.MemoryWork.FailWrites = false;
        window.Close();
        await DrainUiUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaFact]
    public async Task ArrivalReviewFromAnotherDestinationFocusesTheOldestPendingRow()
    {
        using var directory = new TemporaryDirectory();
        const string passphraseText = "synthetic arrival focus password";
        var passphrase = WorkspacePassphrase.Create(passphraseText, passphraseText).Passphrase!;
        var session = Assert.IsAssignableFrom<IWorkspaceSession>(
            new EncryptedWorkspaceStore().Create(
                Path.Combine(directory.Path, "workspace.orb"),
                passphrase,
                CategoryName.Create("Home").CategoryName!).Session);
        var window = new MainWindow(
            session,
            aiFeaturePreferenceService: new TransientAiFeaturePreferenceService(aiFeaturesEnabled: true));
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        var submission = await Assert.IsAssignableFrom<IInboundProposalSink>(window.PendingProposalSink)
            .TrySubmitAsync(
                new InboundProposalSubmission(
                    "receipt-arrival-focus",
                    "key-arrival-focus",
                    "authorization-arrival-focus",
                    new string('d', 64),
                    "Codex",
                    DateTimeOffset.UnixEpoch,
                    Encoding.UTF8.GetBytes(NewWorkProposalJson("arrival-focus"))),
                TestContext.Current.CancellationToken);
        Assert.Equal(InboundProposalSubmissionStatus.Accepted, submission.Status);
        await DrainUiUntilAsync(() => shell.HasProposalArrivalNotification);
        var notification = Assert.IsType<Border>(
            window.FindControl<Border>("ProposalArrivalNotification"));
        var bannerReview = notification.GetVisualDescendants().OfType<Button>().Single(button =>
            string.Equals(button.Content as string, "Review", StringComparison.Ordinal));

        bannerReview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await DrainUiUntilAsync(() => shell.ShowAI);
        var rowReview = window.GetVisualDescendants().OfType<Button>().Single(button =>
            button.DataContext is PendingProposalRowViewModel { Receipt: "receipt-arrival-focus" }
            && string.Equals(button.Content as string, "Review", StringComparison.Ordinal));
        await DrainUiUntilAsync(() => rowReview.IsFocused);

        Assert.False(shell.HasProposalArrivalNotification);
        Assert.True(rowReview.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public void DiscardingAnInboundProposalFocusesTheFollowingRow()
    {
        using var directory = new TemporaryDirectory();
        const string passphraseText = "synthetic inbound discard focus password";
        var passphrase = WorkspacePassphrase.Create(passphraseText, passphraseText).Passphrase!;
        var session = Assert.IsAssignableFrom<IWorkspaceSession>(
            new EncryptedWorkspaceStore().Create(
                Path.Combine(directory.Path, "workspace.orb"),
                passphrase,
                CategoryName.Create("Home").CategoryName!).Session);
        var payload = Encoding.UTF8.GetBytes(ProposalJson(session.Proposals.Read(), "Inbound focus task"));
        for (var index = 1; index <= 3; index++)
        {
            Assert.Equal(
                PendingAgentProposalStoreStatus.Stored,
                session.AgentIntegration.TryStorePendingProposal(new PendingAgentProposal(
                    $"receipt-{index}",
                    $"key-{index}",
                    "authorization-focus",
                    new string((char)('c' + index), 64),
                    $"Client {index}",
                    DateTimeOffset.UnixEpoch.AddMinutes(index),
                    ProposalKind.NewWork,
                    1,
                    1,
                    payload)).Status);
        }

        var window = new MainWindow(
            session,
            aiFeaturePreferenceService: new TransientAiFeaturePreferenceService(aiFeaturesEnabled: true));
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var list = Assert.IsType<ItemsControl>(window.FindControl<ItemsControl>("AiPendingProposalList"));
        Assert.False(list.Focusable);
        var discardSecond = window.GetVisualDescendants().OfType<Button>().Single(button =>
            button.DataContext is PendingProposalRowViewModel { Receipt: "receipt-2" }
            && string.Equals(button.Content as string, "Discard", StringComparison.Ordinal));
        var reviewThird = window.GetVisualDescendants().OfType<Button>().Single(button =>
            button.DataContext is PendingProposalRowViewModel { Receipt: "receipt-3" }
            && string.Equals(button.Content as string, "Review", StringComparison.Ordinal));

        Assert.True(discardSecond.Focus());
        discardSecond.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(reviewThird.IsFocused);
        Assert.DoesNotContain(
            session.AgentIntegration.ReadPendingProposals(),
            proposal => proposal.Receipt == "receipt-2");
        Assert.True(Assert.IsType<PendingProposalRowViewModel>(reviewThird.DataContext).IsLast);
        window.Close();
    }

    [AvaloniaFact]
    public async Task FileLoadedProposalReviewIdentifiesJsonFileSourceWithoutPath()
    {
        var state = new StaticProposalSource().Read();
        var intake = new BlockingProposalFileIntake();
        var window = new MainWindow(
            new ProposalWorkspaceSession(
                new FixedProposalSource(state),
                new RecordingProposalApplication()),
            proposalFilePicker: new StaticProposalFilePicker("/synthetic/private/proposal.json"),
            proposalFileIntake: intake,
            aiFeaturePreferenceService: EnabledAiFeatures());
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var load = window.LoadProposalFromPickerAsync(null);
        await intake.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        intake.Complete(ProposalFileIntakeResult.Loaded(ProposalJson(state, "File proposal task")));
        await load;

        await window.CompleteProposalReviewAsync(null);
        Dispatcher.UIThread.RunJobs();

        var review = Assert.Single(window.OwnedWindows.OfType<AgentProposalReviewWindow>());
        var reviewModel = Assert.IsType<AgentProposalReviewViewModel>(review.DataContext);
        Assert.Equal("JSON file import", reviewModel.SourceLabel);
        Assert.DoesNotContain("proposal.json", reviewModel.SourceDetail, StringComparison.Ordinal);
        review.Close();
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SupersededClipboardOrPickerCompletionPreservesCurrentProposal(
        bool filePicker, bool replaceSession)
    {
        var pending = new BlockingProposalSelection();
        var intake = new RecordingProposalFileIntake();
        var window = new MainWindow(
            new DisposalTrackingWorkspaceSession(),
            null,
            null,
            proposalClipboardReader: pending,
            proposalFilePicker: pending,
            proposalFileIntake: intake,
            aiFeaturePreferenceService: EnabledAiFeatures());
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var editor = Assert.IsType<BoundedProposalTextBox>(
            window.FindControl<BoundedProposalTextBox>("AiProposalTextBox"));
        editor.Text = "original synthetic proposal";
        var operation = filePicker
            ? window.LoadProposalFromPickerAsync(editor)
            : window.PasteProposalAsync(editor, editor);
        await pending.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        if (replaceSession)
        {
            await window.ReplaceSessionAsync(new DisposalTrackingWorkspaceSession());
            shell = Assert.IsType<ShellViewModel>(window.DataContext);
            shell.AiNavigation.SelectCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
        }
        editor.Text = "newer synthetic proposal";
        editor.Focus();
        pending.Complete(filePicker ? "/synthetic/proposal.json" : "older clipboard proposal");
        await operation;

        Assert.Equal("newer synthetic proposal", editor.Text);
        Assert.Equal("newer synthetic proposal", shell.Ai!.ProposalText);
        Assert.True(editor.IsFocused);
        Assert.Equal(0, intake.ReadCount);
        window.Close();
    }

    [AvaloniaFact]
    public void ProposalEditorAtomicallyAcceptsSelectionReplacementAndPreservesTextAndSelectionOnRejection()
    {
        var editor = new BoundedProposalTextBox { Text = "abcd" };
        editor.SelectionStart = 1;
        editor.SelectionEnd = 3;

        Assert.True(editor.TryReplaceSelection("é"));
        Assert.Equal("aéd", editor.Text);
        Assert.Equal(2, editor.CaretIndex);
        Assert.Equal(2, editor.SelectionStart);
        Assert.Equal(2, editor.SelectionEnd);

        editor.SelectionStart = 1;
        editor.SelectionEnd = 2;
        Assert.False(editor.TryReplaceSelection("\ud800"));
        Assert.Equal("aéd", editor.Text);
        Assert.Equal(1, editor.SelectionStart);
        Assert.Equal(2, editor.SelectionEnd);
    }

    [AvaloniaFact]
    public void AiProposalInputExposesNamedKeyboardActionsAndBoundedScrollableEditor()
    {
        var window = new MainWindow(
            new DisposalTrackingWorkspaceSession(),
            aiFeaturePreferenceService: EnabledAiFeatures());
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var editor = Assert.IsType<BoundedProposalTextBox>(window.FindControl<BoundedProposalTextBox>("AiProposalTextBox"));
        Assert.Equal("Agent proposal", AutomationProperties.GetName(editor));
        Assert.True(editor.AcceptsReturn);
        Assert.Equal(122, editor.Height);
        Assert.Equal("Paste or type a proposal…", editor.PlaceholderText);
        var paste = Assert.IsType<Button>(window.FindControl<Button>("AiProposalPasteButton"));
        var load = Assert.IsType<Button>(window.FindControl<Button>("AiProposalLoadButton"));
        var review = Assert.IsType<Button>(window.FindControl<Button>("AiProposalReviewButton"));
        Assert.Contains("Paste", AutomationProperties.GetName(paste), StringComparison.Ordinal);
        Assert.Contains("one local", AutomationProperties.GetName(load), StringComparison.Ordinal);
        Assert.Contains("without applying", AutomationProperties.GetName(review), StringComparison.Ordinal);
        Assert.False(review.IsEnabled);
        var copy = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(text => text.Text)
            .Where(text => text is not null)
            .ToArray();
        var intakeIndex = Array.IndexOf(copy, "Bring work into dot-orbit");
        var contextIndex = Array.IndexOf(copy, "Share workspace context (optional)");
        Assert.InRange(intakeIndex, 0, copy.Length - 1);
        Assert.True(contextIndex > intakeIndex);
        Assert.Contains(copy, text => text!.Contains("add entirely new work without seeing your workspace", StringComparison.Ordinal));
        window.Close();
    }

    [AvaloniaFact]
    public void AiProposalEditorAtReviewWindowSizeDoesNotDominatePage()
    {
        var window = new MainWindow(
            new DisposalTrackingWorkspaceSession(),
            aiFeaturePreferenceService: EnabledAiFeatures())
        {
            Width = 1180,
            Height = 760,
        };
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var editor = Assert.IsType<BoundedProposalTextBox>(
            window.FindControl<BoundedProposalTextBox>("AiProposalTextBox"));
        Assert.Equal(122, editor.Bounds.Height);
        window.Close();
    }

    [AvaloniaFact]
    public void AiProposalEditorAcceptsAndRendersTypedText()
    {
        var window = new MainWindow(
            new DisposalTrackingWorkspaceSession(),
            aiFeaturePreferenceService: EnabledAiFeatures());
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var editor = Assert.IsType<BoundedProposalTextBox>(
            window.FindControl<BoundedProposalTextBox>("AiProposalTextBox"));

        Assert.True(editor.Focus());
        window.KeyTextInput("visible proposal text");
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(editor.Template);
        Assert.Equal("visible proposal text", editor.Text);
        Assert.Contains(
            editor.GetVisualDescendants().OfType<TextPresenter>(),
            presenter => presenter.IsEffectivelyVisible && presenter.Text == "visible proposal text");
        window.Close();
    }

    [AvaloniaFact]
    public async Task ReviewButtonExplainsPlanningSnapshotIsNotAProposal()
    {
        var window = new MainWindow(
            new ProposalWorkspaceSession(new StaticProposalSource()),
            aiFeaturePreferenceService: EnabledAiFeatures());
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var editor = Assert.IsType<BoundedProposalTextBox>(
            window.FindControl<BoundedProposalTextBox>("AiProposalTextBox"));
        editor.Text = "{\"format\":\"dot-orbit-planning-snapshot\",\"schemaVersion\":1}";
        var initialStatus = shell.Ai!.ProposalStatus;
        var review = Assert.IsType<Button>(window.FindControl<Button>("AiProposalReviewButton"));

        review.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await DrainUiUntilAsync(() => shell.Ai.ProposalStatus != initialStatus);

        var status = Assert.IsType<SelectableTextBlock>(
            window.FindControl<SelectableTextBlock>("AiProposalStatus"));
        Assert.True(status.IsVisible);
        Assert.Equal(shell.Ai.ProposalStatus, status.Text);
        Assert.Contains("Workspace context", status.Text, StringComparison.Ordinal);
        Assert.Contains("New-work", status.Text, StringComparison.Ordinal);
        Assert.Contains("Change proposal", status.Text, StringComparison.Ordinal);
        Assert.True(review.IsFocused);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RejectedProposalExplainsDuplicateCategoryAtTheFailingOperation()
    {
        var window = new MainWindow(
            new ProposalWorkspaceSession(new StaticProposalSource()),
            aiFeaturePreferenceService: EnabledAiFeatures());
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var editor = Assert.IsType<BoundedProposalTextBox>(
            window.FindControl<BoundedProposalTextBox>("AiProposalTextBox"));
        editor.Text = """
            {
              "format": "dot-orbit-new-work-proposal",
              "schemaVersion": 1,
              "operations": [
                { "operation": "createCategory", "operationId": "1", "localId": "category", "name": "Synthetic" }
              ]
            }
            """;
        var review = Assert.IsType<Button>(window.FindControl<Button>("AiProposalReviewButton"));

        await window.CompleteProposalReviewAsync(review);

        Assert.Contains("already exists", shell.Ai!.ProposalStatus, StringComparison.OrdinalIgnoreCase);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RejectedProposalExplainsRawJsonRequirement()
    {
        var window = new MainWindow(
            new ProposalWorkspaceSession(new StaticProposalSource()),
            aiFeaturePreferenceService: EnabledAiFeatures());
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var editor = Assert.IsType<BoundedProposalTextBox>(
            window.FindControl<BoundedProposalTextBox>("AiProposalTextBox"));
        editor.Text = """
            ```json
            {"format":"dot-orbit-new-work-proposal","schemaVersion":1,"operations":[]}
            ```
            """;
        var review = Assert.IsType<Button>(window.FindControl<Button>("AiProposalReviewButton"));

        await window.CompleteProposalReviewAsync(review);

        Assert.Contains("Markdown", shell.Ai!.ProposalStatus, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RejectedProposalFeedbackIsSelectableAndCopiesTheExactMessage()
    {
        var clipboard = new RecordingPlainTextClipboard();
        var window = new MainWindow(
            new ProposalWorkspaceSession(new StaticProposalSource()),
            null,
            null,
            plainTextClipboard: clipboard,
            aiFeaturePreferenceService: EnabledAiFeatures());
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var editor = Assert.IsType<BoundedProposalTextBox>(
            window.FindControl<BoundedProposalTextBox>("AiProposalTextBox"));
        editor.Text = "not JSON";
        var review = Assert.IsType<Button>(window.FindControl<Button>("AiProposalReviewButton"));

        await window.CompleteProposalReviewAsync(review);

        var status = Assert.IsType<SelectableTextBlock>(
            window.FindControl<SelectableTextBlock>("AiProposalStatus"));
        status.SelectAll();
        Assert.Equal(shell.Ai!.ProposalStatus, status.SelectedText);
        Assert.Contains("copy", AutomationProperties.GetHelpText(status), StringComparison.OrdinalIgnoreCase);
        var copy = Assert.IsType<Button>(window.FindControl<Button>("AiProposalFeedbackCopyButton"));
        Assert.Equal("Copy complete proposal feedback", AutomationProperties.GetName(copy));
        Assert.True(copy.Focus());
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        await DrainUiUntilAsync(() => clipboard.Text is not null);
        Assert.Equal(shell.Ai.ProposalStatus, clipboard.Text);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RejectedProposalExplainsNoOpAtTheFailingOperation()
    {
        var state = new ProposalWorkspaceState(
            new(1),
            new(
                [new("category", "Category", 0)],
                [new("project", "Project", "", "category", null, 0)],
                [], []),
            [], []);
        var window = new MainWindow(
            new ProposalWorkspaceSession(new FixedProposalSource(state)),
            aiFeaturePreferenceService: EnabledAiFeatures());
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.AiNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var editor = Assert.IsType<BoundedProposalTextBox>(
            window.FindControl<BoundedProposalTextBox>("AiProposalTextBox"));
        var snapshot = PlanningSnapshotProjection.Create(state.Revision, state.Workspace);
        using var snapshotDocument = JsonDocument.Parse(
            PlanningSnapshotJsonGenerator.Generate(snapshot).Payload!.Value);
        editor.Text = JsonSerializer.Serialize(new
        {
            format = AgentInterchangeV1.ChangeProposalFormat,
            schemaVersion = 1,
            sourceSnapshotId = snapshotDocument.RootElement.GetProperty("snapshotId").GetString(),
            sourceWorkspaceRevision = "1",
            operations = new[]
            {
                new
                {
                    operation = "setProjectCategory",
                    operationId = "redundant-category",
                    project = new { existingId = "project" },
                    category = new { existingId = "category" },
                },
            },
        });
        var review = Assert.IsType<Button>(window.FindControl<Button>("AiProposalReviewButton"));

        await window.CompleteProposalReviewAsync(review);

        Assert.Contains("Operation 1", shell.Ai!.ProposalStatus, StringComparison.Ordinal);
        Assert.Contains("would not change", shell.Ai.ProposalStatus, StringComparison.OrdinalIgnoreCase);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ClosingWaitsForCommittedInspectorSaveBeforeDisposingSession()
    {
        var session = new DisposalTrackingWorkspaceSession();
        var project = session.Work.CreateProject("Original", "", "home", null);
        BlockingInspectorSaveWriter? writer = null;
        var window = new MainWindow(
            session,
            null,
            null,
            timeProvider: null,
            inspectorSaveWriterFactory: work => writer = new BlockingInspectorSaveWriter(work));
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.Work!.SelectProject(project.Id);
        shell.Work.Title = "Committed before close";
        Assert.True(shell.Work.RunScheduledAutosave());
        await writer!.Submitted;

        window.Close();

        Assert.False(session.IsDisposed);
        writer.Complete();
        await session.Disposed;
        Assert.Equal("Committed before close", session.Work.Read().Projects.Single().Title);
    }

    [AvaloniaFact]
    public async Task MainWindowRestoresAndPersistsNormalSizeAfterAcceptedClose()
    {
        var preferences = new RecordingMainWindowSizePreferenceStore(new(1260, 700));
        var screen = new FixedMainWindowScreenProvider(new(1600, 1000));
        var window = new MainWindow(
            session: null,
            mainWindowSizePreferenceStore: preferences,
            mainWindowScreenProvider: screen);

        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1260, window.Width);
        Assert.Equal(700, window.Height);
        Assert.Equal(1120, window.MinWidth);
        Assert.Equal(600, window.MinHeight);

        window.Width = 1320;
        window.Height = 740;
        Dispatcher.UIThread.RunJobs();
        window.Close();
        await DrainUiUntilAsync(() => !window.IsVisible);

        var savedSize = Assert.NotNull(preferences.SavedSize);
        Assert.Equal(window.ClientSize.Width, savedSize.Width);
        Assert.Equal(window.ClientSize.Height, savedSize.Height);

        var reopened = new MainWindow(
            session: null,
            mainWindowSizePreferenceStore: preferences,
            mainWindowScreenProvider: screen);
        reopened.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(savedSize.Width, reopened.Width);
        Assert.Equal(savedSize.Height, reopened.Height);
        reopened.Close();
        await DrainUiUntilAsync(() => !reopened.IsVisible);
    }

    [AvaloniaTheory]
    [InlineData(WindowState.Maximized, WindowResizeReason.User)]
    [InlineData(WindowState.Minimized, WindowResizeReason.User)]
    [InlineData(WindowState.FullScreen, WindowResizeReason.User)]
    [InlineData(WindowState.Normal, WindowResizeReason.DpiChange)]
    public async Task AcceptedCloseDoesNotReclassifyTransientResizeAsNormal(
        WindowState transientState,
        WindowResizeReason resizeReason)
    {
        var original = new MainWindowSize(1260, 700);
        var preferences = new RecordingMainWindowSizePreferenceStore(original);
        var window = new MainWindow(
            session: null,
            mainWindowSizePreferenceStore: preferences,
            mainWindowScreenProvider: new FixedMainWindowScreenProvider(new(1600, 1000)));
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.RecordWindowSize(new MainWindowSize(1500, 900), transientState, resizeReason);

        window.Close();
        await DrainUiUntilAsync(() => !window.IsVisible);

        Assert.Equal(original, preferences.SavedSize);
    }

    [AvaloniaFact]
    public async Task ClosingPersistsWindowSizeOnlyAfterWorkspaceWritesDrain()
    {
        var session = new DisposalTrackingWorkspaceSession();
        var project = session.Work.CreateProject("Original", "", "home", null);
        var preferences = new RecordingMainWindowSizePreferenceStore();
        BlockingInspectorSaveWriter? writer = null;
        var window = new MainWindow(
            session,
            timeProvider: null,
            inspectorSaveWriterFactory: work => writer = new BlockingInspectorSaveWriter(work),
            mainWindowSizePreferenceStore: preferences,
            mainWindowScreenProvider: new FixedMainWindowScreenProvider(new(1600, 1000)));
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.Work!.SelectProject(project.Id);
        shell.Work.Title = "Committed before geometry";
        Assert.True(shell.Work.RunScheduledAutosave());
        await writer!.Submitted;

        window.Close();

        Assert.Equal(0, preferences.SaveAttempts);
        writer.Complete();
        await session.Disposed;
        Assert.Equal(1, preferences.SaveAttempts);
    }

    [AvaloniaFact]
    public async Task WindowSizePreferenceFailureDoesNotBlockShutdown()
    {
        var session = new DisposalTrackingWorkspaceSession();
        var preferences = new RecordingMainWindowSizePreferenceStore(allowSave: false);
        var window = new MainWindow(
            session,
            mainWindowSizePreferenceStore: preferences,
            mainWindowScreenProvider: new FixedMainWindowScreenProvider(new(1600, 1000)));
        window.Show();

        window.Close();
        await session.Disposed;

        Assert.Equal(1, preferences.SaveAttempts);
        Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task AuxiliaryWindowCloseDoesNotPersistMainWindowSize()
    {
        var preferences = new RecordingMainWindowSizePreferenceStore();
        var window = new MainWindow(
            session: null,
            mainWindowSizePreferenceStore: preferences,
            mainWindowScreenProvider: new FixedMainWindowScreenProvider(new(1600, 1000)));
        window.Show();
        var recovery = new RecoveryWindow(
            new RecoveryViewModelTests.StubWorkspaceRecovery(),
            _ => { });
        recovery.Show(window);

        recovery.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, preferences.SaveAttempts);
        window.Close();
        await DrainUiUntilAsync(() => !window.IsVisible);
        Assert.Equal(1, preferences.SaveAttempts);
    }

    [AvaloniaFact]
    public async Task SessionReplacementDrainsWriterBeforeDisposingOldSession()
    {
        var session = new DisposalTrackingWorkspaceSession();
        var replacement = new DisposalTrackingWorkspaceSession();
        var replacementProject = replacement.Work.CreateProject("Replacement", "", "home", null);
        var drainingWriter = new BlockingDisposeInspectorSaveWriter(session.Work);
        var window = new MainWindow(
            session,
            null,
            null,
            timeProvider: null,
            inspectorSaveWriterFactory: work => ReferenceEquals(work, session.Work)
                ? drainingWriter
                : new InlineWorkspacePersistenceExecutor(new WorkspacePersistenceOperation(work)));
        window.Show();

        var replace = window.ReplaceSessionAsync(replacement);
        await drainingWriter.DisposeStarted;
        Assert.False(session.IsDisposed);

        drainingWriter.ReleaseDispose();
        await replace;
        Assert.True(session.IsDisposed);
        Assert.Equal(
            replacementProject.Id,
            Assert.Single(Assert.IsType<ShellViewModel>(window.DataContext).Work!.Projects).Id);
        window.Close();
    }

    [AvaloniaFact]
    public async Task SessionReplacementWaitsForAcceptedWorkspaceActionBeforeDisposingOldSession()
    {
        var session = new DisposalTrackingWorkspaceSession();
        var replacement = new DisposalTrackingWorkspaceSession();
        var task = session.Work.CreateStandaloneTask("Replace", "", "home", null);
        BlockingWorkspacePersistenceExecutor? executor = null;
        var window = new MainWindow(
            session,
            null,
            null,
            timeProvider: null,
            inspectorSaveWriterFactory: work => ReferenceEquals(work, session.Work)
                ? executor = new BlockingWorkspacePersistenceExecutor(work)
                : new InlineWorkspacePersistenceExecutor(new WorkspacePersistenceOperation(work)));
        window.Show();
        var model = Assert.IsType<ShellViewModel>(window.DataContext).Work!;
        model.ToggleToday(task.Id);
        await executor!.Submitted.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var replace = window.ReplaceSessionAsync(replacement);
        Dispatcher.UIThread.RunJobs();
        Assert.False(session.IsDisposed);

        executor.CompleteSuccess();
        await DrainUiUntilCompletedAsync(replace);
        Assert.True(session.IsDisposed);
        Assert.Equal(TodayLane.Planned, session.Work.Read().Tasks.Single(item => item.Id == task.Id).TodayLane);
        window.Close();
    }

    [AvaloniaFact]
    public async Task BlockedWorkspaceActionKeepsDispatcherResponsiveAndExposesAccessibleRefreshRetry()
    {
        var session = new DisposalTrackingWorkspaceSession();
        var task = session.Work.CreateStandaloneTask("Queued", "", "home", null);
        BlockingWorkspacePersistenceExecutor? executor = null;
        var window = new MainWindow(
            session,
            null,
            null,
            timeProvider: null,
            inspectorSaveWriterFactory: work => executor = new BlockingWorkspacePersistenceExecutor(work));
        window.Show();
        var model = Assert.IsType<ShellViewModel>(window.DataContext).Work!;

        model.ToggleToday(task.Id);
        await executor!.Submitted.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var callbackRan = false;
        Dispatcher.UIThread.Post(() => callbackRan = true);
        Dispatcher.UIThread.RunJobs();

        Assert.True(callbackRan);
        Assert.Equal(1, model.PendingWorkspaceActionCount);
        Assert.Equal("Saving 1 change…", model.Message);
        var status = Assert.IsType<TextBlock>(window.FindControl<TextBlock>("TopBarStatusMessage"));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));

        executor.CompleteCommittedRefreshFailure();
        await DrainUiUntilCompletedAsync(model.WaitForWorkspaceActionsAsync());
        var retry = Assert.IsType<Button>(window.FindControl<Button>("RetryWorkspaceRefreshButton"));
        Assert.True(retry.IsVisible);
        Assert.Equal("Retry workspace refresh", AutomationProperties.GetName(retry));

        retry.Command!.Execute(null);
        await executor.Submitted.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        executor.CompleteRefresh();
        await DrainUiUntilCompletedAsync(model.WaitForWorkspaceActionsAsync());
        Assert.False(retry.IsVisible);
        Assert.Equal([WorkspaceActionKind.ToggleToday, WorkspaceActionKind.Refresh],
            executor.Requests.Select(request => request.Kind));
        window.Close();
    }

    [AvaloniaFact]
    public async Task ClosingWaitsForAcceptedWorkspaceActionBeforeDisposingSession()
    {
        var session = new DisposalTrackingWorkspaceSession();
        var task = session.Work.CreateStandaloneTask("Close", "", "home", null);
        BlockingWorkspacePersistenceExecutor? executor = null;
        var window = new MainWindow(
            session,
            null,
            null,
            timeProvider: null,
            inspectorSaveWriterFactory: work => executor = new BlockingWorkspacePersistenceExecutor(work));
        window.Show();
        var model = Assert.IsType<ShellViewModel>(window.DataContext).Work!;
        model.ToggleToday(task.Id);
        await executor!.Submitted.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(session.IsDisposed);

        executor.CompleteSuccess();
        await DrainUiUntilCompletedAsync(session.Disposed);
        Assert.True(session.IsDisposed);
        Assert.Equal(TodayLane.Planned, session.Work.Read().Tasks.Single(item => item.Id == task.Id).TodayLane);
    }

    private static TransientAiFeaturePreferenceService EnabledAiFeatures() =>
        new TransientAiFeaturePreferenceService(aiFeaturesEnabled: true);

    private static async Task DrainUiUntilCompletedAsync(Task task)
    {
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        await task;
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task DrainUiUntilAsync(Func<bool> condition)
    {
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task WorkspaceUnavailableShowsReplacementAndClosesTheShell()
    {
        var replacementShown = 0;
        var session = new DisposalTrackingWorkspaceSession();
        var oldSessionWasDisposedBeforeReplacement = false;
        var preferences = new RecordingMainWindowSizePreferenceStore();
        var window = new MainWindow(
            session,
            mainWindowSizePreferenceStore: preferences,
            mainWindowScreenProvider: new FixedMainWindowScreenProvider(new(1600, 1000)));
        window.Show();

        window.HandleWorkspaceUnavailable(() =>
        {
            oldSessionWasDisposedBeforeReplacement = session.IsDisposed;
            replacementShown++;
        });
        window.HandleWorkspaceUnavailable(() => replacementShown++);

        await DrainUiUntilAsync(() => replacementShown == 1 && !window.IsVisible);

        Assert.Equal(1, replacementShown);
        Assert.True(oldSessionWasDisposedBeforeReplacement);
        Assert.True(session.IsDisposed);
        Assert.False(window.IsVisible);
        Assert.Equal(1, preferences.SaveAttempts);
    }

    private sealed class RecordingMainWindowSizePreferenceStore(
        MainWindowSize? loadedSize = null,
        bool allowSave = true) : IMainWindowSizePreferenceStore
    {
        private MainWindowSize? _loadedSize = loadedSize;

        public int SaveAttempts { get; private set; }

        public MainWindowSize? SavedSize { get; private set; }

        public MainWindowSize? LoadMainWindowSize() => _loadedSize;

        public bool TrySaveMainWindowSize(MainWindowSize size)
        {
            SaveAttempts++;
            if (!allowSave)
            {
                return false;
            }

            SavedSize = size;
            _loadedSize = size;
            return true;
        }
    }

    private sealed class FixedMainWindowScreenProvider(MainWindowSize? size)
        : IMainWindowScreenProvider
    {
        public MainWindowSize? GetUsableLogicalSize(Window window) => size;
    }

    private sealed class DisposalTrackingWorkspaceSession : IWorkspaceSession
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IWorkspaceWork Work { get; } = new MemoryWorkspaceWork();
        public int SchemaVersion => 1;
        public string FirstCategoryName => "Home";
        public IWorkspaceRecovery Recovery { get; } = new RecoveryViewModelTests.StubWorkspaceRecovery();

        public DotOrbit.Core.Interchange.IProposalWorkspaceSource Proposals => throw new NotSupportedException();

        public DotOrbit.Core.Interchange.IPlanningSnapshotSource Planning => throw new NotSupportedException();
        public bool IsDisposed { get; private set; }
        public Task Disposed => _disposed.Task;

        public PassphraseRotationResult RotatePassphrase(
            WorkspacePassphrase currentPassphrase,
            WorkspacePassphrase newPassphrase) => PassphraseRotationResult.Failed();

        public void Dispose()
        {
            IsDisposed = true;
            _disposed.TrySetResult();
        }
    }

    private sealed class ProposalWorkspaceSession(
        IProposalWorkspaceSource source,
        IProposalApplication? proposalApplication = null) : IWorkspaceSession
    {
        public IWorkspaceWork Work { get; } = new MemoryWorkspaceWork();
        public int SchemaVersion => 1;
        public string FirstCategoryName => "Home";
        public IWorkspaceRecovery Recovery { get; } = new RecoveryViewModelTests.StubWorkspaceRecovery();
        public IProposalWorkspaceSource Proposals { get; } = source;
        public IProposalApplication ProposalApplication => proposalApplication
            ?? throw new NotSupportedException("This test session does not support proposal application.");
        public IPlanningSnapshotSource Planning => throw new NotSupportedException();
        public PassphraseRotationResult RotatePassphrase(
            WorkspacePassphrase currentPassphrase,
            WorkspacePassphrase newPassphrase) => PassphraseRotationResult.Failed();
        public void Dispose() { }
    }

    private sealed class RecordingProposalApplication : IProposalApplication
    {
        public int ApproveCount { get; private set; }

        public ProposalApproval? Approve(ProposalExecutionPlan plan)
        {
            ApproveCount++;
            return null;
        }

        public ProposalApplyResult Apply(
            ProposalApproval approval,
            IProposalApplicationHost host,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("An unapproved proposal cannot be applied.");
    }

    private sealed class ProposalApplicationWorkspaceSession(
        IWorkspaceSession inner,
        IProposalApplication proposalApplication) : IWorkspaceSession
    {
        public int SchemaVersion => inner.SchemaVersion;
        public string FirstCategoryName => inner.FirstCategoryName;
        public IWorkspaceRecovery Recovery => inner.Recovery;
        public IPlanningSnapshotSource Planning => inner.Planning;
        public IProposalWorkspaceSource Proposals => inner.Proposals;
        public IProposalApplication ProposalApplication => proposalApplication;
        public IWorkspaceWork Work => inner.Work;

        public PassphraseRotationResult RotatePassphrase(
            WorkspacePassphrase currentPassphrase,
            WorkspacePassphrase newPassphrase) =>
            inner.RotatePassphrase(currentPassphrase, newPassphrase);

        public void Dispose() => inner.Dispose();
    }

    private sealed class DecisionWorkspaceSession(IWorkspaceSession inner) : IWorkspaceSession
    {
        public MemoryWorkspaceWork MemoryWork { get; } = new();
        public IWorkspaceWork Work => MemoryWork;
        public int SchemaVersion => inner.SchemaVersion;
        public string FirstCategoryName => inner.FirstCategoryName;
        public IWorkspaceRecovery Recovery => inner.Recovery;
        public IPlanningSnapshotSource Planning => inner.Planning;
        public IProposalWorkspaceSource Proposals => inner.Proposals;
        public IAgentIntegrationStore AgentIntegration => inner.AgentIntegration;
        public IProposalApplication ProposalApplication => inner.ProposalApplication;

        public PassphraseRotationResult RotatePassphrase(
            WorkspacePassphrase currentPassphrase,
            WorkspacePassphrase newPassphrase) =>
            inner.RotatePassphrase(currentPassphrase, newPassphrase);

        public void Dispose() => inner.Dispose();
    }

    private sealed class BlockingProposalApplication(IProposalApplication inner) : IProposalApplication, IDisposable
    {
        public TaskCompletionSource ApplyStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseApply { get; } = new(false);
        public int ApproveCount { get; private set; }
        public int ApplyCount { get; private set; }

        public ProposalApproval? Approve(ProposalExecutionPlan plan)
        {
            ApproveCount++;
            return inner.Approve(plan);
        }

        public ProposalApplyResult Apply(
            ProposalApproval approval,
            IProposalApplicationHost host,
            CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            ApplyStarted.TrySetResult();
            ReleaseApply.Wait(cancellationToken);
            return inner.Apply(approval, host, cancellationToken);
        }

        public void Dispose() => ReleaseApply.Dispose();
    }

    private sealed class ReadyProposalApplicationHost : IProposalApplicationHost
    {
        public bool FlushPendingInspectorWork() => true;
        public bool RefreshProjections() => true;
    }

    private sealed class BlockingProposalSource : IProposalWorkspaceSource
    {
        public ProposalWorkspaceState State { get; } = new(
            new(1),
            new([new("c", "Synthetic", 0)], [], [], []),
            [],
            []);

        public TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseRead { get; } = new(false);

        public ProposalWorkspaceState Read()
        {
            ReadStarted.TrySetResult();
            ReleaseRead.Wait(TestContext.Current.CancellationToken);
            return State;
        }
    }

    private sealed class StaticProposalSource : IProposalWorkspaceSource
    {
        public ProposalWorkspaceState Read() => new(
            new(1),
            new([new("c", "Synthetic", 0)], [], [], []),
            [],
            []);
    }

    private sealed class FixedProposalSource(ProposalWorkspaceState state) : IProposalWorkspaceSource
    {
        public ProposalWorkspaceState Read() => state;
    }

    private sealed class StaticProposalClipboardReader(string? text) : IProposalClipboardReader
    {
        public Task<string?> ReadTextAsync() => Task.FromResult(text);
    }

    private sealed class ThrowingProposalClipboardReader : IProposalClipboardReader
    {
        public Task<string?> ReadTextAsync() =>
            throw new InvalidOperationException("The disabled AI gate accessed the clipboard.");
    }

    private sealed class RecordingPlainTextClipboard : IPlainTextClipboard
    {
        public string? Text { get; private set; }

        public ValueTask WriteTextAsync(ReadOnlyMemory<byte> strictUtf8Text)
        {
            Text = Encoding.UTF8.GetString(strictUtf8Text.Span);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StaticProposalFilePicker(string path) : IProposalFilePicker
    {
        public Task<string?> PickAsync(Window owner) => Task.FromResult<string?>(path);
    }

    private sealed class ThrowingProposalFilePicker : IProposalFilePicker
    {
        public Task<string?> PickAsync(Window owner) =>
            throw new InvalidOperationException("The disabled AI gate opened the file picker.");
    }

    private sealed class ThrowingProposalFileIntake : IProposalFileIntake
    {
        public Task<ProposalFileIntakeResult> ReadAsync(
            string localPath,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The disabled AI gate read a proposal file.");
    }

    private sealed class BlockingProposalSelection : IProposalClipboardReader, IProposalFilePicker
    {
        private readonly TaskCompletionSource<string?> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> ReadTextAsync()
        {
            Started.TrySetResult();
            return _completion.Task;
        }

        public Task<string?> PickAsync(Window owner) => ReadTextAsync();
        public void Complete(string value) => _completion.TrySetResult(value);
    }

    private sealed class RecordingProposalFileIntake : IProposalFileIntake
    {
        public int ReadCount { get; private set; }
        public Task<ProposalFileIntakeResult> ReadAsync(
            string localPath, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(ProposalFileIntakeResult.Loaded("older file proposal"));
        }
    }

    private sealed class BlockingProposalFileIntake : IProposalFileIntake
    {
        private readonly TaskCompletionSource<ProposalFileIntakeResult> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ObservedCancellation { get; private set; }

        public async Task<ProposalFileIntakeResult> ReadAsync(
            string localPath,
            CancellationToken cancellationToken = default)
        {
            using var registration = cancellationToken.Register(() => ObservedCancellation = true);
            Started.TrySetResult();
            return await _completion.Task;
        }

        public void Complete(ProposalFileIntakeResult result) => _completion.TrySetResult(result);
    }

    private static string ProposalJson(ProposalWorkspaceState state, string title)
    {
        var content = PlanningSnapshotProjection.Create(state.Revision, state.Workspace);
        var categoryId = Assert.Single(state.Workspace.Categories).Id;
        using var snapshot = JsonDocument.Parse(
            PlanningSnapshotJsonGenerator.Generate(content).Payload!.Value);
        return JsonSerializer.Serialize(new
        {
            format = "dot-orbit-change-proposal",
            schemaVersion = 1,
            sourceSnapshotId = snapshot.RootElement.GetProperty("snapshotId").GetString(),
            sourceWorkspaceRevision = state.Revision.ToString(),
            operations = new[]
            {
                new
                {
                    operation = "createTask",
                    operationId = "create-synthetic-task",
                    localId = "new-task",
                    title,
                    description = string.Empty,
                    project = (object?)null,
                    category = new { mode = "explicit", category = new { existingId = categoryId } },
                    dueDate = (string?)null,
                    participants = Array.Empty<object>(),
                },
            },
        });
    }

    private static string EditTaskProposalJson(
        ProposalWorkspaceState state,
        string taskId,
        string title)
    {
        var content = PlanningSnapshotProjection.Create(state.Revision, state.Workspace);
        using var snapshot = JsonDocument.Parse(
            PlanningSnapshotJsonGenerator.Generate(content).Payload!.Value);
        return JsonSerializer.Serialize(new
        {
            format = "dot-orbit-change-proposal",
            schemaVersion = 1,
            sourceSnapshotId = snapshot.RootElement.GetProperty("snapshotId").GetString(),
            sourceWorkspaceRevision = state.Revision.ToString(),
            operations = new[]
            {
                new
                {
                    operation = "editTask",
                    operationId = "edit-existing-task",
                    task = new { existingId = taskId },
                    changes = new { title },
                },
            },
        });
    }

    private static string NewWorkProposalJson(string suffix) => JsonSerializer.Serialize(new
    {
        format = "dot-orbit-new-work-proposal",
        schemaVersion = 1,
        operations = new object[]
        {
            new
            {
                operation = "createCategory",
                operationId = $"create-category-{suffix}",
                localId = $"category-{suffix}",
                name = $"Category {suffix}",
            },
            new
            {
                operation = "createTask",
                operationId = $"create-task-{suffix}",
                localId = $"task-{suffix}",
                title = $"Task {suffix}",
                description = string.Empty,
                project = (object?)null,
                category = new { mode = "explicit", category = new { localId = $"category-{suffix}" } },
                dueDate = (string?)null,
                participants = Array.Empty<object>(),
            },
        },
    });

    private sealed class BlockingInspectorSaveWriter(IWorkspaceWork work)
        : IInspectorSaveWriter, IWorkspaceActionExecutor
    {
        private readonly WorkspacePersistenceOperation _operation = new(work);
        private readonly TaskCompletionSource _submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<InspectorSaveResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private InspectorSaveRequest? _request;

        public Task Submitted => _submitted.Task;

        public Task<InspectorSaveResult> SubmitAsync(InspectorSaveRequest request)
        {
            _request = request;
            _submitted.TrySetResult();
            return _completion.Task;
        }

        public void Complete() => _completion.TrySetResult(_operation.Execute(_request!));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<WorkspaceActionResult> SubmitActionAsync(WorkspaceActionRequest request) =>
            Task.FromResult(_operation.ExecuteAction(request));
    }

    private sealed class BlockingDisposeInspectorSaveWriter(IWorkspaceWork work)
        : IInspectorSaveWriter, IWorkspaceActionExecutor
    {
        private readonly WorkspacePersistenceOperation _operation = new(work);
        private readonly TaskCompletionSource _disposeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseDispose =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DisposeStarted => _disposeStarted.Task;

        public Task<InspectorSaveResult> SubmitAsync(InspectorSaveRequest request) =>
            Task.FromResult(_operation.Execute(request));

        public async ValueTask DisposeAsync()
        {
            _disposeStarted.TrySetResult();
            await _releaseDispose.Task;
        }

        public void ReleaseDispose() => _releaseDispose.TrySetResult();

        public Task<WorkspaceActionResult> SubmitActionAsync(WorkspaceActionRequest request) =>
            Task.FromResult(_operation.ExecuteAction(request));
    }

    private sealed class BlockingWorkspacePersistenceExecutor(IWorkspaceWork work)
        : IInspectorSaveWriter, IWorkspaceActionExecutor
    {
        private readonly WorkspacePersistenceOperation _operation = new(work);
        private TaskCompletionSource<WorkspaceActionResult>? _pending;
        private TaskCompletionSource _submitted = NewSignal();

        public List<WorkspaceActionRequest> Requests { get; } = [];
        public Task Submitted => _submitted.Task;

        public Task<InspectorSaveResult> SubmitAsync(InspectorSaveRequest request) =>
            Task.FromResult(_operation.Execute(request));

        public Task<WorkspaceActionResult> SubmitActionAsync(WorkspaceActionRequest request)
        {
            Requests.Add(request);
            _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _submitted.TrySetResult();
            return _pending.Task;
        }

        public void CompleteCommittedRefreshFailure()
        {
            var request = Requests[^1];
            var committed = _operation.ExecuteAction(request);
            _pending!.TrySetResult(committed with
            {
                Outcome = WorkspaceActionOutcome.CommittedRefreshFailed,
                Reload = null,
            });
            _submitted = NewSignal();
        }

        public void CompleteRefresh()
        {
            _pending!.TrySetResult(_operation.ExecuteAction(Requests[^1]));
            _submitted = NewSignal();
        }

        public void CompleteSuccess()
        {
            _pending!.TrySetResult(_operation.ExecuteAction(Requests[^1]));
            _submitted = NewSignal();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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
    public void SettingsAiToggleControlsTheVisibleAccessibleNavigationDestination()
    {
        var preferences = new TransientAiFeaturePreferenceService();
        var window = new MainWindow(
            new DisposalTrackingWorkspaceSession(),
            aiFeaturePreferenceService: preferences);
        window.Show();
        var shell = Assert.IsType<ShellViewModel>(window.DataContext);
        shell.SettingsNavigation.SelectCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var toggle = Assert.IsType<ToggleSwitch>(
            window.FindControl<ToggleSwitch>("SettingsAiFeaturesToggle"));
        var localMcpToggle = Assert.IsType<ToggleSwitch>(
            window.FindControl<ToggleSwitch>("SettingsLocalMcpToggle"));
        var announcement = Assert.IsType<TextBlock>(
            window.FindControl<TextBlock>("SettingsAnnouncement"));

        Assert.False(toggle.IsChecked);
        Assert.Equal("settings-ai-features", AutomationProperties.GetAutomationId(toggle));
        Assert.Equal("Enable AI features", AutomationProperties.GetName(toggle));
        Assert.Equal("AI features disabled", AutomationProperties.GetItemStatus(toggle));
        Assert.DoesNotContain(
            window.GetVisualDescendants().OfType<RadioButton>(),
            item => AutomationProperties.GetAutomationId(item) == "navigation-ai");
        Assert.False(localMcpToggle.IsEffectivelyVisible);
        Assert.True(toggle.Focus(NavigationMethod.Tab));
        Assert.True(toggle.IsKeyboardFocusWithin);
        Assert.Equal("settings-announcement", AutomationProperties.GetAutomationId(announcement));
        Assert.Equal("Settings status", AutomationProperties.GetName(announcement));
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(announcement));

        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        var aiNavigation = Assert.Single(
            window.GetVisualDescendants().OfType<RadioButton>(),
            item => AutomationProperties.GetAutomationId(item) == "navigation-ai");
        Assert.True(shell.AiFeaturesEnabled);
        Assert.NotNull(shell.Ai);
        Assert.Equal("Open AI", AutomationProperties.GetName(aiNavigation));
        Assert.True(aiNavigation.Focusable);
        Assert.Equal("AI features enabled", AutomationProperties.GetItemStatus(toggle));
        Assert.Equal(
            "AI features enabled. Manual agent interchange is now available.",
            announcement.Text);
        Assert.True(localMcpToggle.IsEffectivelyVisible);
        Assert.Equal("settings-local-mcp", AutomationProperties.GetAutomationId(localMcpToggle));
        Assert.Equal("Enable local MCP", AutomationProperties.GetName(localMcpToggle));
        Assert.Equal("Local MCP disabled", AutomationProperties.GetItemStatus(localMcpToggle));

        Assert.True(localMcpToggle.Focus(NavigationMethod.Tab));
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.True(preferences.LocalMcpEnabled);
        Assert.True(localMcpToggle.IsChecked);

        Assert.True(toggle.Focus(NavigationMethod.Tab));
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.AiFeaturesEnabled);
        Assert.False(preferences.LocalMcpEnabled);
        Assert.Null(shell.Ai);
        Assert.Equal(
            "AI features disabled. Agent interchange is unavailable.",
            announcement.Text);
        Assert.DoesNotContain(
            window.GetVisualDescendants().OfType<RadioButton>(),
            item => AutomationProperties.GetAutomationId(item) == "navigation-ai");
        Assert.False(localMcpToggle.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task LocalMcpApprovalSeparatesIdentityEvidenceAndDefaultsFocusToDeny()
    {
        var request = new LocalMcpApprovalRequest(new LocalMcpClientIdentity(
            "verified-user",
            1234,
            "self-asserted-client",
            "1.2.3",
            new string('a', 64),
            LocalMcpScopes.ReadInterchangeContext | LocalMcpScopes.SubmitProposals));
        var window = new LocalMcpApprovalWindow(request);

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var viewModel = Assert.IsType<LocalMcpApprovalViewModel>(window.DataContext);
        var buttons = window.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => AutomationProperties.GetAutomationId(button) is not null)
            .ToDictionary(
                button => AutomationProperties.GetAutomationId(button)!,
                StringComparer.Ordinal);
        var deny = buttons["local-mcp-deny"];
        var allowOnce = buttons["local-mcp-allow-once"];
        var alwaysAllow = buttons["local-mcp-always-allow"];
        Assert.True(deny.IsFocused);
        Assert.Equal("Deny this local MCP client", AutomationProperties.GetName(deny));
        Assert.Equal(
            "Allow requested scopes until disconnect or dot-orbit closes",
            AutomationProperties.GetName(allowOnce));
        Assert.Equal(
            "Remember this exact local MCP client credential, OS account, and scope set",
            AutomationProperties.GetName(alwaysAllow));
        Assert.Contains("Verified same OS account: verified-user", viewModel.VerifiedAccount, StringComparison.Ordinal);
        Assert.Contains("Verified local process ID: 1234", viewModel.VerifiedProcess, StringComparison.Ordinal);
        Assert.Contains("Self-asserted client: self-asserted-client 1.2.3", viewModel.UnverifiedClient, StringComparison.Ordinal);
        Assert.Contains("Read interchange", viewModel.RequestedScopes, StringComparison.Ordinal);
        Assert.Contains("Submit proposals", viewModel.RequestedScopes, StringComparison.Ordinal);
        Assert.Contains("cannot approve or apply", viewModel.Powers, StringComparison.Ordinal);

        Assert.True(allowOnce.Focus(NavigationMethod.Tab));
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(LocalMcpApprovalChoice.AllowOnce, await request.Completion);
    }

    [AvaloniaFact]
    public async Task ClosingLocalMcpApprovalDefaultsToDeny()
    {
        var request = new LocalMcpApprovalRequest(new LocalMcpClientIdentity(
            "verified-user",
            null,
            "self-asserted-client",
            "1.0",
            new string('a', 64),
            LocalMcpScopes.ReadInterchangeContext));
        var window = new LocalMcpApprovalWindow(request);
        window.Show();

        window.Close();

        Assert.Equal(LocalMcpApprovalChoice.Deny, await request.Completion);
    }

    [AvaloniaFact]
    public async Task CancelledLocalMcpApprovalClosesItsStaleWindow()
    {
        var request = new LocalMcpApprovalRequest(new LocalMcpClientIdentity(
            "verified-user",
            null,
            "self-asserted-client",
            "1.0",
            new string('a', 64),
            LocalMcpScopes.ReadInterchangeContext));
        var window = new LocalMcpApprovalWindow(request);
        window.Show();

        request.Cancel(new CancellationToken(canceled: true));
        await DrainUiUntilAsync(() => !window.IsVisible);

        Assert.False(window.IsVisible);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request.Completion);
    }

    [AvaloniaFact]
    public async Task QueuedLocalMcpTransitionReturnsToTheUiThread()
    {
        var prior = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ranOnUiThread = false;
        var transition = MainWindow.RunLocalMcpTransitionAsync(
            prior.Task,
            () =>
            {
                ranOnUiThread = Dispatcher.UIThread.CheckAccess();
                return Task.CompletedTask;
            });

        await Task.Run(prior.SetResult, TestContext.Current.CancellationToken);
        await transition;

        Assert.True(ranOnUiThread);
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
    public void SettingsDescriptionsKeepWrappedLinesLeftAligned()
    {
        var window = new MainWindow();
        window.Show();

        var aiDescription = Assert.IsType<TextBlock>(
            window.FindControl<TextBlock>("SettingsAiFeaturesDescription"));
        var mcpDescription = Assert.IsType<TextBlock>(
            window.FindControl<TextBlock>("SettingsLocalMcpDescription"));

        Assert.Equal(TextAlignment.Left, aiDescription.TextAlignment);
        Assert.Equal(HorizontalAlignment.Stretch, aiDescription.HorizontalAlignment);
        Assert.Equal(TextAlignment.Left, mcpDescription.TextAlignment);
        Assert.Equal(HorizontalAlignment.Stretch, mcpDescription.HorizontalAlignment);

        window.Close();
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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "orbit-ai-gate-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
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
