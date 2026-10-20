using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.Clipboard;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using DotOrbit.Interchange.Json;
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
        var window = new MainWindow(session);
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
            proposalClipboardReader: new StaticProposalClipboardReader("\ud800"));
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
            proposalClipboardReader: new StaticProposalClipboardReader("replacement"));
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
            proposalFileIntake: intake);
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
        var window = new MainWindow(new DisposalTrackingWorkspaceSession());
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
        var window = new MainWindow(new DisposalTrackingWorkspaceSession())
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
        var window = new MainWindow(new DisposalTrackingWorkspaceSession());
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
        var window = new MainWindow(new ProposalWorkspaceSession(new StaticProposalSource()));
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
        var window = new MainWindow(new ProposalWorkspaceSession(new StaticProposalSource()));
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
        var window = new MainWindow(new ProposalWorkspaceSession(new StaticProposalSource()));
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
            plainTextClipboard: clipboard);
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
        var window = new MainWindow(new ProposalWorkspaceSession(new FixedProposalSource(state)));
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
    public void WorkspaceUnavailableShowsReplacementAndClosesTheShell()
    {
        var replacementShown = 0;
        var window = new MainWindow();
        window.Show();

        window.HandleWorkspaceUnavailable(() => replacementShown++);

        Assert.Equal(1, replacementShown);
        Assert.False(window.IsVisible);
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

    private sealed class ProposalWorkspaceSession(IProposalWorkspaceSource source) : IWorkspaceSession
    {
        public IWorkspaceWork Work { get; } = new MemoryWorkspaceWork();
        public int SchemaVersion => 1;
        public string FirstCategoryName => "Home";
        public IWorkspaceRecovery Recovery { get; } = new RecoveryViewModelTests.StubWorkspaceRecovery();
        public IProposalWorkspaceSource Proposals { get; } = source;
        public IPlanningSnapshotSource Planning => throw new NotSupportedException();
        public PassphraseRotationResult RotatePassphrase(
            WorkspacePassphrase currentPassphrase,
            WorkspacePassphrase newPassphrase) => PassphraseRotationResult.Failed();
        public void Dispose() { }
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
        using var snapshot = JsonDocument.Parse(
            PlanningSnapshotJsonGenerator.Generate(content).Payload!.Value);
        return JsonSerializer.Serialize(new
        {
            format = "dot-orbit-change-proposal",
            schemaVersion = 1,
            sourceSnapshotId = snapshot.RootElement.GetProperty("snapshotId").GetString(),
            sourceWorkspaceRevision = "1",
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
                    category = new { mode = "explicit", category = new { existingId = "c" } },
                    dueDate = (string?)null,
                    participants = Array.Empty<object>(),
                },
            },
        });
    }

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

        Assert.Equal(10, navigation.Length);
        Assert.Equal(
            [
                "Open Today",
                "Open Upcoming",
                "Open Backlog",
                "Open Projects",
                "Open Categories",
                "Open Completed",
                "Open Archive",
                "Open AI",
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
                "navigation-ai",
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
