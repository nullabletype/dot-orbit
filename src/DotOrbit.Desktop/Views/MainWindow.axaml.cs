using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.ComponentModel;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.Clipboard;
using DotOrbit.Desktop.Markdown;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Interchange.Json;
using System.Security.Cryptography;
using System.Text;

namespace DotOrbit.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private enum DragScope { None, TodayPlanned, TodayInProgress, Backlog, Projects, ProjectTasks, Categories }

    private IWorkspaceSession? _session;
    private IInspectorSaveWriter? _inspectorSaveWriter;
    private IWorkspaceActionExecutor? _actionExecutor;
    private bool _closingApproved;
    private bool _closeCompletionStarted;
    private DragScope _dragScope;
    private string? _draggedId;
    private string? _draggedProjectId;
    private Border? _pressedSelectableRow;
    private Border? _dragTarget;
    private Border? _dragSource;
    private Border? _dragPlaceholder;
    private bool _dragAfter;
    private Border[]? _dragRows;
    private bool _markdownPointerStartedOutside;
    private DateTime? _calendarDateAtOpen;
    private ShellViewModel? _subscribedShell;
    private ProjectCaptureViewModel? _subscribedWork;
    private SettingsViewModel? _subscribedSettings;
    private readonly DispatcherTimer _dateRefreshTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly IInspectorAutosaveScheduler _autosaveScheduler;
    private readonly ITransientMessageScheduler _transientMessageScheduler;
    private readonly IMarkdownClipboard? _markdownClipboard;
    private readonly IPlainTextClipboard? _plainTextClipboard;
    private readonly IProposalClipboardReader? _proposalClipboardReader;
    private readonly IProposalFilePicker _proposalFilePicker;
    private readonly IProposalFileIntake _proposalFileIntake;
    private readonly Action _workspaceUnavailable;
    private readonly IApplicationThemeService _applicationThemeService;
    private readonly IInterchangePreferenceService _interchangePreferenceService;
    private readonly IAiFeaturePreferenceService _aiFeaturePreferenceService;
    private readonly IMainWindowSizePreferenceStore _mainWindowSizePreferenceStore;
    private readonly IMainWindowScreenProvider _mainWindowScreenProvider;
    private readonly TimeProvider _timeProvider;
    private readonly Func<IWorkspaceWork, IInspectorSaveWriter> _inspectorSaveWriterFactory;
    private CancellationTokenSource? _proposalIntakeCancellation;
    private AgentProposalReviewViewModel? _activeProposalReview;
    private AgentProposalReviewWindow? _activeProposalReviewWindow;
    private PendingProposalCoordinator? _pendingProposalCoordinator;
    private string? _pendingProposalFocusReceipt;
    private readonly object _localMcpTransitionGate = new();
    private Task _localMcpTransition = Task.CompletedTask;
    private LocalMcpDesktopService? _localMcpService;
    private LocalMcpApprovalWindow? _localMcpApprovalWindow;
    private long _proposalReviewRevision;
    private long _transientMessageRevision;
    private MainWindowSize _lastNormalWindowSize = MainWindowSizePolicy.DefaultSize;
    private bool _initialWindowSizeApplied;

    internal static TimeSpan TransientMessageDuration { get; } = TimeSpan.FromSeconds(5);

    public MainWindow()
        : this(null, null, null, null, null, null)
    {
    }

    internal MainWindow(IWorkspaceSession? session)
        : this(session, null, null, null, null, null)
    {
    }

    internal MainWindow(IWorkspaceSession? session, IMarkdownClipboard? markdownClipboard)
        : this(session, markdownClipboard, null, null, null, null)
    {
    }

    internal MainWindow(
        IWorkspaceSession? session,
        IMarkdownClipboard? markdownClipboard = null,
        IInspectorAutosaveScheduler? autosaveScheduler = null,
        Action? workspaceUnavailable = null,
        ITransientMessageScheduler? transientMessageScheduler = null,
        IApplicationThemeService? applicationThemeService = null,
        TimeProvider? timeProvider = null,
        Func<IWorkspaceWork, IInspectorSaveWriter>? inspectorSaveWriterFactory = null,
        IPlainTextClipboard? plainTextClipboard = null,
        IInterchangePreferenceService? interchangePreferenceService = null,
        IProposalClipboardReader? proposalClipboardReader = null,
        IProposalFilePicker? proposalFilePicker = null,
        IProposalFileIntake? proposalFileIntake = null,
        IAiFeaturePreferenceService? aiFeaturePreferenceService = null,
        IMainWindowSizePreferenceStore? mainWindowSizePreferenceStore = null,
        IMainWindowScreenProvider? mainWindowScreenProvider = null)
    {
        _session = session;
        _markdownClipboard = markdownClipboard;
        _plainTextClipboard = plainTextClipboard;
        _proposalClipboardReader = proposalClipboardReader;
        _proposalFilePicker = proposalFilePicker ?? new AvaloniaProposalFilePicker();
        _autosaveScheduler = autosaveScheduler ?? new DispatcherInspectorAutosaveScheduler();
        _transientMessageScheduler = transientMessageScheduler ?? new DispatcherTransientMessageScheduler();
        _workspaceUnavailable = () => HandleWorkspaceUnavailable(workspaceUnavailable);
        _applicationThemeService = applicationThemeService
            ?? (Application.Current as App)?.ThemeService
            ?? new TransientApplicationThemeService(application: Application.Current);
        _interchangePreferenceService = interchangePreferenceService
            ?? (Application.Current as App)?.InterchangePreferenceService
            ?? new TransientInterchangePreferenceService();
        _aiFeaturePreferenceService = aiFeaturePreferenceService
            ?? (Application.Current as App)?.AiFeaturePreferenceService
            ?? new TransientAiFeaturePreferenceService();
        _mainWindowSizePreferenceStore = mainWindowSizePreferenceStore
            ?? (Application.Current as App)?.MainWindowSizePreferenceStore
            ?? new TransientMainWindowSizePreferenceStore();
        _mainWindowScreenProvider = mainWindowScreenProvider
            ?? new AvaloniaMainWindowScreenProvider();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _proposalFileIntake = proposalFileIntake ?? new ProposalFileIntake(_timeProvider);
        _inspectorSaveWriterFactory = inspectorSaveWriterFactory
            ?? (work => new SerializedWorkspacePersistenceExecutor(work, _timeProvider));
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += OnDataContextChanged;
        AddHandler(PointerPressedEvent, OnWorkPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnWorkDragPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(ScrollViewer.ScrollChangedEvent, OnDragScrollChanged, RoutingStrategies.Bubble);
        SizeChanged += OnDragWindowSizeChanged;
        LayoutUpdated += OnDragLayoutUpdated;
        AddHandler(PointerReleasedEvent, OnWorkPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        this.FindControl<TextBox>("MarkdownSource")?.AddHandler(
            KeyDownEvent,
            OnMarkdownSourceKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        if (this.FindControl<BoundedProposalTextBox>("AiProposalTextBox") is { } proposalTextBox)
        {
            proposalTextBox.ProposalTextAttempted += OnProposalTextAttempted;
            proposalTextBox.PastingFromClipboard += OnProposalPastingFromClipboard;
        }
        AddHandler(
            PointerCaptureLostEvent,
            OnPointerCaptureLost,
            RoutingStrategies.Direct | RoutingStrategies.Bubble,
            handledEventsToo: true);
        _dateRefreshTimer.Tick += OnDateRefreshTick;
        Opened += OnOpened;
        Resized += OnWindowResized;
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        SetSessionContext();
        if (this.FindControl<Button>("SettingsRecoveryButton") is { } recoveryButton)
            recoveryButton.IsVisible = session is not null;
        if (this.FindControl<Button>("SettingsPlaintextExportButton") is { } exportButton)
            exportButton.IsVisible = session is not null;
        if (this.FindControl<Button>("SettingsPassphraseButton") is { } passphraseButton)
            passphraseButton.IsVisible = session is not null;
        if (this.FindControl<Button>("AiPlanningSnapshotButton") is { } snapshotButton)
            snapshotButton.IsVisible = session is not null;
        if (this.FindControl<Button>("AiReusableInstructionsButton") is { } instructionsButton)
            instructionsButton.IsVisible = session is not null;
        if (this.FindControl<Border>("AiProposalDropSurface") is { } proposalSurface)
            proposalSurface.IsVisible = session is not null;
        Closed += OnClosed;
        Closing += OnClosing;
    }

    private void OnOpenRecovery(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var launcher = sender as Control;
        Navigate(() =>
        {
            var recovery = new RecoveryWindow(_session!.Recovery, ReplaceSession);
            recovery.Closed += (_, _) => Dispatcher.UIThread.Post(() => launcher?.Focus());
            recovery.Show(this);
        });
    }

    private void OnOpenPlaintextExport(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var launcher = sender as Control;
        Navigate(() =>
        {
            var export = new PlaintextExportWindow(_session!.Work);
            export.Closed += (_, _) => Dispatcher.UIThread.Post(() => launcher?.Focus());
            _ = export.ShowDialog(this);
        });
    }

    private async void OnOpenPlanningSnapshot(object? sender, RoutedEventArgs e) =>
        await OpenPlanningSnapshotAsync(sender as Control);

    internal async Task OpenPlanningSnapshotAsync(Control? launcher = null)
    {
        if (_session is null || DataContext is not ShellViewModel { Ai: { } })
        {
            return;
        }

        void OpenReview()
        {
            if (DataContext is not ShellViewModel { Ai: { } ai })
            {
                return;
            }

            var clipboard = PlainTextClipboard();
            if (clipboard is null)
            {
                ai.ReportInterchangeStatus("The system clipboard is unavailable. Nothing was copied.");
                return;
            }

            try
            {
                var snapshot = _session.Planning.Read();
                var viewModel = new PlanningSnapshotCopyViewModel(
                    snapshot,
                    ai.IncludeAgentInstructionsAndSchemaWithSnapshot,
                    clipboard);
                if (viewModel.HasStatusMessage)
                {
                    ai.ReportInterchangeStatus(viewModel.StatusMessage);
                }

                PropertyChangedEventHandler reportStatus = (_, args) =>
                {
                    if (args.PropertyName == nameof(PlanningSnapshotCopyViewModel.StatusMessage)
                        && viewModel.HasStatusMessage)
                    {
                        ai.ReportInterchangeStatus(viewModel.StatusMessage);
                    }
                };
                viewModel.PropertyChanged += reportStatus;
                var review = new PlanningSnapshotCopyWindow(viewModel);
                review.Closed += (_, _) =>
                {
                    viewModel.PropertyChanged -= reportStatus;
                    Dispatcher.UIThread.Post(() => launcher?.Focus());
                };
                _ = review.ShowDialog(this);
            }
            catch (Exception)
            {
                ai.ReportInterchangeStatus(
                    "The Planning snapshot could not be prepared. Nothing was copied.");
            }
        }

        if (DataContext is ShellViewModel { Work: { } work })
        {
            await work.NavigateAsync(OpenReview);
        }
        else
        {
            OpenReview();
        }
    }

    private async void OnCopyReusableInstructions(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel { Ai: { } ai })
        {
            return;
        }

        var clipboard = PlainTextClipboard();
        if (clipboard is null)
        {
            ai.ReportInterchangeStatus("The system clipboard is unavailable. Nothing was copied.");
            return;
        }

        await ai.CopyReusableInstructionsAsync(clipboard);
    }

    private async void OnCopyProposalFeedback(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel { Ai: { HasProposalStatus: true } ai })
        {
            return;
        }

        var feedback = ai.ProposalStatus;
        var clipboard = PlainTextClipboard();
        if (clipboard is null)
        {
            ai.ReportProposalStatus(
                $"The system clipboard is unavailable. Select and copy this feedback manually: {feedback}");
            return;
        }

        var payload = Encoding.UTF8.GetBytes(feedback);
        try
        {
            await clipboard.WriteTextAsync(payload);
        }
        catch (Exception)
        {
            ai.ReportProposalStatus(
                $"The feedback could not be copied automatically. Select and copy it manually: {feedback}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private void OnProposalTextAttempted(object? sender, ProposalTextAttemptedEventArgs e)
    {
        _proposalIntakeCancellation?.Cancel();
        InvalidateActiveProposalReview();
        if (DataContext is ShellViewModel { Ai: { } ai })
        {
            ai.ReportProposalTextAttempt(e.Accepted, e.ByteCount);
        }
    }

    private void OnProposalPastingFromClipboard(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _ = PasteProposalAsync(sender as BoundedProposalTextBox, sender as Control);
    }

    private async void OnPasteProposal(object? sender, RoutedEventArgs e) =>
        await PasteProposalAsync(
            this.FindControl<BoundedProposalTextBox>("AiProposalTextBox"),
            sender as Control);

    internal async Task PasteProposalAsync(BoundedProposalTextBox? editor, Control? failureFocus)
    {
        if (editor is null || DataContext is not ShellViewModel { Ai: { } ai })
        {
            return;
        }

        if (!ai.CanReplaceProposalText)
        {
            ai.ReportProposalStatus("Wait for the current proposal action to finish before replacing its text.");
            (failureFocus ?? editor).Focus();
            return;
        }

        InvalidateActiveProposalReview();
        var proposalInputRevision = _proposalReviewRevision;
        var clipboard = _proposalClipboardReader
            ?? (Clipboard is null ? null : new AvaloniaProposalClipboardReader(Clipboard));
        if (clipboard is null)
        {
            ai.ReportProposalStatus("The system clipboard is unavailable. Proposal text was not changed.");
            (failureFocus ?? editor).Focus();
            return;
        }

        string? text;
        try
        {
            text = await clipboard.ReadTextAsync();
        }
        catch (Exception)
        {
            if (!IsCurrentProposalInput(ai, proposalInputRevision)) return;
            ai.ReportProposalStatus("The clipboard could not be read. Proposal text was not changed.");
            (failureFocus ?? editor).Focus();
            return;
        }

        if (!IsCurrentProposalInput(ai, proposalInputRevision)) return;

        if (text is null)
        {
            ai.ReportProposalStatus("The clipboard does not contain text. Proposal text was not changed.");
            (failureFocus ?? editor).Focus();
            return;
        }

        if (editor.TryReplaceAll(text))
        {
            editor.Focus();
        }
        else
        {
            (failureFocus ?? editor).Focus();
        }
    }

    private async void OnLoadProposal(object? sender, RoutedEventArgs e) =>
        await LoadProposalFromPickerAsync(sender as Control);

    internal async Task LoadProposalFromPickerAsync(Control? launcher)
    {
        if (DataContext is not ShellViewModel { Ai: { } ai })
        {
            return;
        }

        if (!ai.CanReplaceProposalText)
        {
            ai.ReportProposalStatus("Wait for the current proposal action to finish before replacing its text.");
            launcher?.Focus();
            return;
        }

        InvalidateActiveProposalReview();
        var proposalInputRevision = _proposalReviewRevision;
        string? path;
        try
        {
            path = await _proposalFilePicker.PickAsync(this);
        }
        catch (Exception)
        {
            if (!IsCurrentProposalInput(ai, proposalInputRevision)) return;
            ai.ReportProposalStatus("The file picker could not be opened. Proposal text was not changed.");
            launcher?.Focus();
            return;
        }

        if (!IsCurrentProposalInput(ai, proposalInputRevision)) return;

        if (path is null)
        {
            ai.ReportProposalStatus("File loading was cancelled. Proposal text was not changed.");
            launcher?.Focus();
            return;
        }

        await LoadProposalFileAsync(path, launcher);
    }

    private bool IsCurrentProposalInput(AiViewModel ai, long revision) =>
        revision == _proposalReviewRevision
        && ai.CanReplaceProposalText
        && DataContext is ShellViewModel { Ai: { } currentAi }
        && ReferenceEquals(ai, currentAi);

    private void OnProposalDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = ProposalDragEffects(e.DataTransfer);
        e.Handled = true;
    }

    internal DragDropEffects ProposalDragEffects(IDataTransfer dataTransfer)
    {
        if (DataContext is not ShellViewModel { Ai: { CanReplaceProposalText: true } })
        {
            return DragDropEffects.None;
        }

        var files = dataTransfer.TryGetFiles()?.ToArray();
        return files?.Length == 1 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnProposalDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is not ShellViewModel { Ai: { CanReplaceProposalText: true } ai })
        {
            return;
        }

        InvalidateActiveProposalReview();
        var previousFocus = this.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(control => control.IsFocused);
        var files = e.DataTransfer.TryGetFiles()?.ToArray();
        if (files is null || files.Length != 1 || files[0].TryGetLocalPath() is not { } path)
        {
            ai.ReportProposalStatus("Drop exactly one local regular file. Proposal text was not changed.");
            previousFocus?.Focus();
            return;
        }

        await LoadProposalFileAsync(path, previousFocus);
    }

    private async Task LoadProposalFileAsync(string path, Control? launcher)
    {
        if (DataContext is not ShellViewModel { Ai: { CanReplaceProposalText: true } ai })
        {
            return;
        }

        var proposalInputRevision = _proposalReviewRevision;
        _proposalIntakeCancellation?.Cancel();
        _proposalIntakeCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _proposalIntakeCancellation = cancellation;
        ai.SetProposalActionInFlight(true);
        ProposalFileIntakeResult result;
        try
        {
            result = await _proposalFileIntake.ReadAsync(path, cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_proposalIntakeCancellation, cancellation))
            {
                _proposalIntakeCancellation = null;
                ai.SetProposalActionInFlight(false);
            }

            cancellation.Dispose();
        }

        if (DataContext is not ShellViewModel { Ai: { } currentAi }
            || !ReferenceEquals(ai, currentAi))
        {
            return;
        }

        if (proposalInputRevision != _proposalReviewRevision)
        {
            ai.ReportProposalStatus(
                "File loading was superseded by a newer proposal input. Proposal text was not changed.");
            launcher?.Focus();
            return;
        }

        var editor = this.FindControl<BoundedProposalTextBox>("AiProposalTextBox");
        if (result.Status == ProposalFileIntakeStatus.Loaded && result.Text is { } text)
        {
            _ = editor?.TryReplaceAll(text);
            ai.MarkManualProposalSource(ProposalReviewSource.JsonFileImport);
            ai.ReportProposalStatus("Proposal text loaded from one local file. Review is required before anything can be applied.");
        }
        else if (result.Status == ProposalFileIntakeStatus.Cancelled)
        {
            ai.ReportProposalStatus("File loading was cancelled. Proposal text was not changed.");
        }
        else
        {
            ai.ReportProposalStatus("That file was rejected. Choose one readable local regular file containing at most 4 MiB of strict UTF-8 JSON.");
        }

        (launcher ?? editor)?.Focus();
    }

    private async void OnReviewProposal(object? sender, RoutedEventArgs e)
    {
        var launcher = sender as Control;
        if (_session is null || DataContext is not ShellViewModel { Ai: { }, Work: { } work })
        {
            return;
        }

        await work.NavigateAsync(() => _ = CompleteProposalReviewAsync(launcher));
    }

    private async void OnOpenPendingProposals(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (DataContext is not ShellViewModel { Ai: { } ai } shell)
        {
            return;
        }

        var wasShowingAi = shell.ShowAI;
        var oldestReceipt = ai.PendingProposals.FirstOrDefault()?.Receipt;
        if (!wasShowingAi && oldestReceipt is not null)
        {
            _pendingProposalFocusReceipt = oldestReceipt;
        }
        if (!wasShowingAi)
        {
            shell.OpenPendingProposals();
        }
        else
        {
            shell.DismissProposalArrivalNotification();
        }
        if (oldestReceipt is null)
        {
            return;
        }

        if (!wasShowingAi)
        {
            return;
        }

        var launcher = FindPendingProposalReviewButton(oldestReceipt) ?? sender as Control;
        if (_session is not null && shell.Work is { } work)
        {
            Task review = Task.CompletedTask;
            await work.NavigateAsync(() => review = CompleteProposalReviewAsync(launcher, oldestReceipt));
            await review;
        }
    }

    private void OnDismissProposalArrival(object? sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        if (DataContext is ShellViewModel shell)
        {
            shell.DismissProposalArrivalNotification();
        }
    }

    private async void OnReviewInboundProposal(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: PendingProposalRowViewModel row } launcher
            || _session is null
            || DataContext is not ShellViewModel { Work: { } work })
        {
            return;
        }

        await work.NavigateAsync(() => _ = CompleteProposalReviewAsync(launcher, row.Receipt));
    }

    private void OnDiscardInboundProposal(object? sender, RoutedEventArgs e)
    {
        _ = e;
        if (sender is not Control { DataContext: PendingProposalRowViewModel row } launcher
            || DataContext is not ShellViewModel { Ai: { } ai })
        {
            return;
        }

        var nextReceipt = ai.PendingProposals
            .SkipWhile(proposal => !string.Equals(proposal.Receipt, row.Receipt, StringComparison.Ordinal))
            .Skip(1)
            .FirstOrDefault()?.Receipt;
        if (ai.DiscardInboundProposal(row.Receipt))
        {
            Dispatcher.UIThread.Post(() => FocusPendingProposalOrManual(nextReceipt));
        }
        else
        {
            launcher.Focus();
        }
    }

    internal Task CompleteProposalReviewAsync(Control? launcher) =>
        CompleteProposalReviewAsync(launcher, null);

    internal Task CompleteInboundProposalReviewAsync(string receipt, Control? launcher = null) =>
        CompleteProposalReviewAsync(launcher, receipt);

    internal IInboundProposalSink? PendingProposalSink => _pendingProposalCoordinator;

    private async Task CompleteProposalReviewAsync(Control? launcher, string? pendingReceipt)
    {
        if (_session is not { } session
            || DataContext is not ShellViewModel { Ai: { } ai, Work: { } work })
        {
            return;
        }

        PendingAgentProposal? pending = null;
        string? nextPendingReceipt = null;
        if (pendingReceipt is null)
        {
            if (!ai.CanReviewProposal)
            {
                return;
            }
        }
        else
        {
            pending = _pendingProposalCoordinator?.FindPendingProposal(pendingReceipt);
            if (pending is null)
            {
                ReportProposalReviewStatus(
                    ai,
                    pendingReceipt,
                    "That inbound proposal is no longer waiting for review.");
                launcher?.Focus();
                return;
            }

            ai.ClearPendingProposalReviewStatus(pendingReceipt);

            nextPendingReceipt = ai.PendingProposals
                .SkipWhile(proposal => !string.Equals(proposal.Receipt, pendingReceipt, StringComparison.Ordinal))
                .Skip(1)
                .FirstOrDefault()?.Receipt;
        }

        InvalidateActiveProposalReview();
        var proposalReviewRevision = _proposalReviewRevision;
        ai.SetProposalActionInFlight(true);
        byte[]? payload = null;
        var source = pending is null
            ? ai.ManualProposalSource
            : ProposalReviewSource.LocalMcp(pending.ClientName);
        ProposalPlanResult result;
        var proposalWasRefreshed = false;
        try
        {
            payload = pending is null
                ? ProposalTextRules.Encode(ai.ProposalText)
                : pending.StrictUtf8.ToArray();
            var boundary = new ProposalReviewBoundary(session.Proposals, _timeProvider);
            if (pending is null)
            {
                result = await Task.Run(() => boundary.Review(payload));
            }
            else
            {
                var queuedResult = await Task.Run(() => boundary.ReviewQueued(payload, pending.ReviewBasis));
                result = new(queuedResult.Plan, queuedResult.Failures);
                proposalWasRefreshed = queuedResult.WasRefreshed;
            }
        }
        catch (Exception exception) when (exception is EncoderFallbackException
                                          or InvalidOperationException
                                          or ObjectDisposedException)
        {
            ReportProposalReviewStatus(
                ai,
                pendingReceipt,
                "The proposal could not be reviewed. Nothing was applied.");
            launcher?.Focus();
            return;
        }
        finally
        {
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }

            ai.SetProposalActionInFlight(false);
        }

        if (!ReferenceEquals(session, _session)
            || proposalReviewRevision != _proposalReviewRevision
            || DataContext is not ShellViewModel currentShell
            || !ReferenceEquals(ai, currentShell.Ai)
            || !currentShell.ShowAI)
        {
            ReportProposalReviewStatus(
                ai,
                pendingReceipt,
                "Proposal review was invalidated by an input, navigation, or workspace change. Nothing was applied.");
            launcher?.Focus();
            return;
        }

        if (result.Plan is not { } plan)
        {
            var guidance = result.Failures
                .Take(20)
                .Select(failure => failure.OperationIndex is { } operationIndex
                    ? $"Operation {operationIndex + 1}: {failure.Guidance}"
                    : failure.Guidance)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var status = guidance.Length == 0
                ? "The proposal is invalid, unsupported, stale, or incomplete. Nothing was applied."
                : string.Join(" ", guidance);
            ReportProposalReviewStatus(ai, pendingReceipt, status);
            launcher?.Focus();
            return;
        }

        var viewModel = new AgentProposalReviewViewModel(
            plan,
            session.ProposalApplication,
            pendingReceipt,
            source,
            proposalWasRefreshed);
        var review = new AgentProposalReviewWindow(viewModel, new DesktopProposalApplicationHost(work));
        _activeProposalReview = viewModel;
        _activeProposalReviewWindow = review;
        void OnApplyFinished(object? sender, EventArgs e)
        {
            if (!viewModel.ProposalWasCommitted)
            {
                return;
            }

            if (pendingReceipt is not null)
            {
                _pendingProposalCoordinator?.CompleteApplied(pendingReceipt);
            }
            else
            {
                ai.ClearAppliedProposal();
            }
        }

        viewModel.ApplyFinished += OnApplyFinished;
        review.Closed += (_, _) =>
        {
            viewModel.ApplyFinished -= OnApplyFinished;
            if (ReferenceEquals(_activeProposalReview, viewModel))
            {
                _activeProposalReview = null;
                _activeProposalReviewWindow = null;
            }

            if (pendingReceipt is null)
            {
                ai.ReportProposalStatus(viewModel.HasStatusMessage
                    ? viewModel.StatusMessage
                    : "Proposal review closed. Review is required again before Apply.");
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (pendingReceipt is not null
                    && _pendingProposalCoordinator?.FindPendingProposal(pendingReceipt) is null)
                {
                    FocusPendingProposalOrManual(nextPendingReceipt);
                }
                else
                {
                    launcher?.Focus();
                }
            });
        };
        if (pendingReceipt is null)
        {
            ai.ReportProposalStatus(
                "Proposal ready to review. Check everything shown before deciding whether to apply it.");
        }

        _ = review.ShowDialog(this);
    }

    private void FocusPendingProposalOrManual(string? receipt)
    {
        var nextReview = receipt is null ? null : FindPendingProposalReviewButton(receipt);
        (nextReview as Control
            ?? this.FindControl<BoundedProposalTextBox>("AiProposalTextBox"))?.Focus();
    }

    private Button? FindPendingProposalReviewButton(string receipt) =>
        this.GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button => button.DataContext is PendingProposalRowViewModel row
                && string.Equals(row.Receipt, receipt, StringComparison.Ordinal)
                && string.Equals(button.Content as string, "Review", StringComparison.Ordinal));

    private static void ReportProposalReviewStatus(
        AiViewModel ai,
        string? pendingReceipt,
        string status)
    {
        if (pendingReceipt is null)
        {
            ai.ReportProposalStatus(status);
        }
        else
        {
            ai.ReportPendingProposalReviewStatus(pendingReceipt, status);
        }
    }

    private void InvalidateActiveProposalReview()
    {
        _proposalReviewRevision++;
        _activeProposalReview?.Discard();
    }

    private IPlainTextClipboard? PlainTextClipboard() =>
        _plainTextClipboard ?? (Clipboard is null ? null : new AvaloniaPlainTextClipboard(Clipboard));

    private void OnOpenPassphraseRotation(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var launcher = sender as Control;
        Navigate(() =>
        {
            var rotation = new PassphraseRotationWindow(
                _session!,
                ReplaceSession,
                _workspaceUnavailable);
            rotation.PassphraseChanged += OnPassphraseChanged;
            rotation.Closed += (_, _) => Dispatcher.UIThread.Post(() => launcher?.Focus());
            _ = rotation.ShowDialog(this);
        });
    }

    private void OnPassphraseChanged(object? sender, EventArgs e)
    {
        if (DataContext is not ShellViewModel shell || shell.Settings is null)
        {
            return;
        }

        shell.SettingsNavigation.SelectCommand.Execute(null);
        shell.Settings.ConfirmPassphraseChanged();
    }

    internal void HandleWorkspaceUnavailable(Action? showWorkspaceAccess = null)
    {
        _ = CompleteUnavailableCloseAsync(showWorkspaceAccess);
    }

    private async Task CompleteUnavailableCloseAsync(Action? showWorkspaceAccess)
    {
        if (_closeCompletionStarted) return;
        _closeCompletionStarted = true;
        await StopLocalMcpAsync();
        DiscardProposalStateForSessionEnd();
        if (DataContext is ShellViewModel { Work: { } work })
            await work.DrainWorkspaceActionsAsync();
        if (_inspectorSaveWriter is not null)
        {
            await _inspectorSaveWriter.DisposeAsync();
            _inspectorSaveWriter = null;
            _actionExecutor = null;
        }
        _session?.Dispose();
        _session = null;
        if (showWorkspaceAccess is not null)
        {
            showWorkspaceAccess();
        }
        else
        {
            var access = new WorkspaceAccessWindow();
            if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = access;
            }

            access.Show();
        }
        FinishAcceptedClose();
    }

    private void ReplaceSession(IWorkspaceSession session) => _ = ReplaceSessionAsync(session);

    internal async Task ReplaceSessionAsync(IWorkspaceSession session)
    {
        await StopLocalMcpAsync();
        DiscardProposalStateForSessionEnd();
        if (DataContext is ShellViewModel { Work: { } work })
            await work.DrainWorkspaceActionsAsync();
        if (_inspectorSaveWriter is not null)
        {
            await _inspectorSaveWriter.DisposeAsync();
            _inspectorSaveWriter = null;
            _actionExecutor = null;
        }
        _session?.Dispose();
        _session = session;
        SetSessionContext();
    }

    private Task ReconcileLocalMcpAsync() => QueueLocalMcpTransition(ReconcileLocalMcpCoreAsync);

    private async Task ReconcileLocalMcpCoreAsync()
    {
        var shouldRun = _session is not null
                        && _pendingProposalCoordinator is not null
                        && _aiFeaturePreferenceService.AiFeaturesEnabled
                        && _aiFeaturePreferenceService.LocalMcpEnabled;
        if (!shouldRun)
        {
            await StopLocalMcpCoreAsync();
            return;
        }

        if (_localMcpService is not null)
        {
            return;
        }

        LocalMcpDesktopService? service = null;
        try
        {
            service = new LocalMcpDesktopService(
                _session!.Planning,
                _session.AgentIntegration,
                _pendingProposalCoordinator!,
                _timeProvider);
            service.StatusChanged += OnLocalMcpStatusChanged;
            service.ApprovalRequested += OnLocalMcpApprovalRequested;
            service.AuthorizationsChanged += OnLocalMcpAuthorizationsChanged;
            _localMcpService = service;
            await service.StartAsync();
            ReportLocalMcpStatus(service.Status);
        }
        catch (Exception exception) when (exception is IOException
                                          or InvalidOperationException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            if (service is not null)
            {
                _ = Interlocked.CompareExchange(ref _localMcpService, null, service);
                service.StatusChanged -= OnLocalMcpStatusChanged;
                service.ApprovalRequested -= OnLocalMcpApprovalRequested;
                service.AuthorizationsChanged -= OnLocalMcpAuthorizationsChanged;
                await service.DisposeAsync();
            }

            ReportLocalMcpStatus(LocalMcpServiceStatus.Error);
        }
    }

    private Task StopLocalMcpAsync()
    {
        _localMcpService?.CloseAdmission();
        return QueueLocalMcpTransition(StopLocalMcpCoreAsync);
    }

    private Task QueueLocalMcpTransition(Func<Task> transition)
    {
        lock (_localMcpTransitionGate)
        {
            _localMcpTransition = RunLocalMcpTransitionAsync(_localMcpTransition, transition);
            return _localMcpTransition;
        }
    }

    internal static async Task RunLocalMcpTransitionAsync(Task prior, Func<Task> transition)
    {
        try
        {
            await prior.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException
                                          or InvalidOperationException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            await transition();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(transition);
    }

    private async Task StopLocalMcpCoreAsync()
    {
        if (_localMcpApprovalWindow is { } approvalWindow)
        {
            _localMcpApprovalWindow = null;
            approvalWindow.Close();
        }

        if (_localMcpService is not { } service)
        {
            ReportLocalMcpStatus(LocalMcpServiceStatus.Stopped);
            return;
        }

        _localMcpService = null;
        service.StatusChanged -= OnLocalMcpStatusChanged;
        service.ApprovalRequested -= OnLocalMcpApprovalRequested;
        service.AuthorizationsChanged -= OnLocalMcpAuthorizationsChanged;
        await service.DisposeAsync();
        ReportLocalMcpStatus(LocalMcpServiceStatus.Stopped);
    }

    private void OnLocalMcpStatusChanged(object? sender, EventArgs e)
    {
        if (sender is not LocalMcpDesktopService service)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(service, _localMcpService))
            {
                ReportLocalMcpStatus(service.Status);
            }
        });
    }

    private void OnLocalMcpApprovalRequested(object? sender, LocalMcpApprovalRequest request) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(sender, _localMcpService)
                || !_aiFeaturePreferenceService.AiFeaturesEnabled
                || !_aiFeaturePreferenceService.LocalMcpEnabled)
            {
                _ = request.Resolve(LocalMcpApprovalChoice.Deny);
                return;
            }

            if (_localMcpApprovalWindow is not null)
            {
                _ = request.Resolve(LocalMcpApprovalChoice.Deny);
                return;
            }

            var window = new LocalMcpApprovalWindow(request);
            _localMcpApprovalWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_localMcpApprovalWindow, window))
                {
                    _localMcpApprovalWindow = null;
                }
            };
            _ = window.ShowDialog(this);
        });

    private void OnLocalMcpAuthorizationsChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(RefreshAuthorizedMcpClients);

    private void RefreshAuthorizedMcpClients()
    {
        if (DataContext is not ShellViewModel { Settings: { } settings })
        {
            return;
        }

        try
        {
            var authorizations = _session?.AgentIntegration.ReadAuthorizations() ?? [];
            settings.ReportAuthorizedMcpClients(
                authorizations,
                authorizationId => _ = RevokeLocalMcpAuthorizationAsync(authorizationId));
        }
        catch (Exception exception) when (exception is NotSupportedException
                                          or WorkspaceWorkException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or ObjectDisposedException)
        {
            settings.ReportAuthorizedMcpClients([], _ => { });
        }
    }

    private async Task RevokeLocalMcpAuthorizationAsync(string authorizationId)
    {
        var removed = false;
        try
        {
            if (_localMcpService is { } service)
            {
                removed = await service.RevokeAsync(authorizationId);
            }
            else
            {
                removed = _session?.AgentIntegration.RevokeAuthorization(authorizationId) == true;
            }
        }
        catch (Exception exception) when (exception is WorkspaceWorkException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or ObjectDisposedException)
        {
        }

        RefreshAuthorizedMcpClients();
        if (DataContext is ShellViewModel { Settings: { } settings })
        {
            var focus = removed
                ? settings.AuthorizedMcpClients.FirstOrDefault()?.RevokeAutomationId
                    ?? "settings-local-mcp"
                : settings.AuthorizedMcpClients
                    .FirstOrDefault(client => client.Id == authorizationId)?.RevokeAutomationId
                    ?? "settings-local-mcp";
            settings.ReportLocalMcpRevocation(removed, focus);
        }
    }

    private void ReportLocalMcpStatus(LocalMcpServiceStatus status) =>
        (DataContext as ShellViewModel)?.Settings?.ReportLocalMcpRuntimeStatus(status);

    private void SetSessionContext()
    {
        _inspectorSaveWriter = _session is null ? null : _inspectorSaveWriterFactory(_session.Work);
        _actionExecutor = _inspectorSaveWriter as IWorkspaceActionExecutor;
        _pendingProposalCoordinator = CreatePendingProposalCoordinator(_session);
        DataContext = new ShellViewModel(
            _session?.Work,
            _timeProvider,
            applicationThemeService: _applicationThemeService,
            inspectorSaveWriter: _inspectorSaveWriter,
            actionExecutor: _actionExecutor,
            uiDispatcher: new AvaloniaUiDispatcher(),
            interchangePreferenceService: _interchangePreferenceService,
            aiFeaturePreferenceService: _aiFeaturePreferenceService,
            pendingProposalCoordinator: _pendingProposalCoordinator);
        RefreshAuthorizedMcpClients();
        _ = ReconcileLocalMcpAsync();
    }

    private PendingProposalCoordinator? CreatePendingProposalCoordinator(IWorkspaceSession? session)
    {
        if (session is null)
        {
            return null;
        }

        try
        {
            return new PendingProposalCoordinator(session.AgentIntegration, session.Proposals, _timeProvider);
        }
        catch (Exception exception) when (exception is NotSupportedException
                                          or WorkspaceWorkException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException
                                          or ObjectDisposedException)
        {
            return null;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        ResetPointerGesture();
        _autosaveScheduler.Cancel();
        CancelTransientMessageDismissal();
        if (_subscribedShell is not null)
            _subscribedShell.PropertyChanged -= OnShellChanged;
        if (_subscribedWork is not null)
        {
            _subscribedWork.PropertyChanged -= OnWorkChanged;
            _subscribedWork.AutosaveRequested -= OnAutosaveRequested;
        }
        if (_subscribedSettings is not null)
            _subscribedSettings.PropertyChanged -= OnSettingsChanged;
        _subscribedShell = DataContext as ShellViewModel;
        _subscribedWork = _subscribedShell?.Work;
        _subscribedSettings = _subscribedShell?.Settings;
        if (_subscribedShell is not null)
            _subscribedShell.PropertyChanged += OnShellChanged;
        if (_subscribedWork is not null)
        {
            _subscribedWork.PropertyChanged += OnWorkChanged;
            _subscribedWork.AutosaveRequested += OnAutosaveRequested;
            ScheduleTransientMessageDismissal(_subscribedWork);
        }
        if (_subscribedSettings is not null)
            _subscribedSettings.PropertyChanged += OnSettingsChanged;
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.MainViewTargetAutomationId)
            && sender is ShellViewModel { MainViewTargetAutomationId.Length: > 0 } targetShell)
        {
            Dispatcher.UIThread.Post(
                () => BringIntoViewAndFocusAutomationId(targetShell.MainViewTargetAutomationId),
                DispatcherPriority.ApplicationIdle);
            return;
        }

        if (e.PropertyName == nameof(ShellViewModel.AiFeaturesEnabled))
        {
            if (sender is ShellViewModel { AiFeaturesEnabled: false })
            {
                _ = StopLocalMcpAsync();
                InvalidateProposalReviewState();
                return;
            }

            _ = ReconcileLocalMcpAsync();
            return;
        }

        if (e.PropertyName == nameof(ShellViewModel.LocalMcpEnabled))
        {
            _ = sender is ShellViewModel { LocalMcpEnabled: false }
                ? StopLocalMcpAsync()
                : ReconcileLocalMcpAsync();
            return;
        }

        if (e.PropertyName != nameof(ShellViewModel.SelectedItem))
        {
            return;
        }

        var hadReview = _activeProposalReview is not null;
        InvalidateActiveProposalReview();
        if (hadReview && sender is ShellViewModel { Ai: { } ai })
        {
            ai.ReportProposalStatus(
                "Proposal review was invalidated by navigation. Review the current proposal again before Apply.");
        }

        if (_pendingProposalFocusReceipt is { } pendingReceipt
            && sender is ShellViewModel shell)
        {
            _pendingProposalFocusReceipt = null;
            if (shell.ShowAI)
            {
                Dispatcher.UIThread.Post(
                    () => FindPendingProposalReviewButton(pendingReceipt)?.Focus(),
                    DispatcherPriority.ApplicationIdle);
            }
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.FocusAutomationId)
            && sender is SettingsViewModel { FocusAutomationId.Length: > 0 } settings)
            Dispatcher.UIThread.Post(() => FocusAutomationId(settings.FocusAutomationId), DispatcherPriority.ApplicationIdle);
    }

    private void OnAutosaveRequested(object? sender, AutosaveRequestEventArgs e)
    {
        if (sender is not ProjectCaptureViewModel work) return;
        _autosaveScheduler.Schedule(
            ProjectCaptureViewModel.AutosaveDelay,
            e.Revision,
            revision => work.RunScheduledAutosave(revision));
    }

    private void ScheduleTransientMessageDismissal(ProjectCaptureViewModel work)
    {
        var revision = ++_transientMessageRevision;
        if (!work.HasMessage)
        {
            _transientMessageScheduler.Cancel();
            return;
        }
        if (work.HasPendingWorkspaceActions || work.CanRetryWorkspaceRefresh)
        {
            _transientMessageScheduler.Cancel();
            return;
        }

        _transientMessageScheduler.Schedule(
            TransientMessageDuration,
            revision,
            scheduledRevision =>
            {
                if (scheduledRevision == _transientMessageRevision
                    && DataContext is ShellViewModel { Work: { } currentWork }
                    && ReferenceEquals(work, currentWork))
                    currentWork.ClearMessage();
            });
    }

    private void CancelTransientMessageDismissal()
    {
        _transientMessageRevision++;
        _transientMessageScheduler.Cancel();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ApplyInitialWindowSize();
        RefreshDatePresentation();
        _dateRefreshTimer.Start();
    }

    private void ApplyInitialWindowSize()
    {
        MainWindowSize? frameSize = FrameSize is { } frame
            ? new MainWindowSize(frame.Width, frame.Height)
            : null;
        var restoration = MainWindowSizePolicy.Restore(
            _mainWindowSizePreferenceStore.LoadMainWindowSize(),
            _mainWindowScreenProvider.GetUsableLogicalSize(this),
            new MainWindowSize(Width, Height),
            MainWindowSizePolicy.CalculateFrameOverhead(
                new MainWindowSize(ClientSize.Width, ClientSize.Height),
                frameSize));
        MinWidth = restoration.MinimumSize.Width;
        MinHeight = restoration.MinimumSize.Height;
        _lastNormalWindowSize = restoration.Size;
        _initialWindowSizeApplied = true;
        Width = restoration.Size.Width;
        Height = restoration.Size.Height;
    }

    private void OnWindowResized(object? sender, WindowResizedEventArgs e)
    {
        if (!_initialWindowSizeApplied)
        {
            return;
        }

        RecordWindowSize(
            new MainWindowSize(e.ClientSize.Width, e.ClientSize.Height),
            WindowState,
            e.Reason);
    }

    internal void RecordWindowSize(
        MainWindowSize reportedSize,
        WindowState state,
        WindowResizeReason reason)
    {
        _lastNormalWindowSize = MainWindowSizePolicy.RememberNormalSize(
            _lastNormalWindowSize,
            reportedSize,
            state,
            reason);
    }

    private void OnActivated(object? sender, EventArgs e) => RefreshDatePresentation();
    private void OnDateRefreshTick(object? sender, EventArgs e) => RefreshDatePresentation();
    private void RefreshDatePresentation() => (DataContext as ShellViewModel)?.Work?.RefreshDatePresentation();

    private void OnDateValidationError(object? sender, CalendarDatePickerDateValidationErrorEventArgs e)
    {
        e.ThrowException = false;
    }

    private void OnCalendarDateOpened(object? sender, EventArgs e)
    {
        if (sender is not CalendarDatePicker picker) return;
        _calendarDateAtOpen = picker.SelectedDate;
        picker.SetCurrentValue(
            CalendarDatePicker.DisplayDateProperty,
            picker.SelectedDate ?? _timeProvider.GetLocalNow().Date);
    }

    private void OnCalendarDateCommitted(object? sender, EventArgs e)
    {
        if (sender is CalendarDatePicker picker
            && picker.SelectedDate != _calendarDateAtOpen
            && DataContext is ShellViewModel { Work: { } work })
            work.CommitCalendarDate(picker.SelectedDate);
        _calendarDateAtOpen = null;
    }

    private void OnWorkChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Projection changes can replace rows while a pointer gesture is in progress.
        if (_dragScope != DragScope.None) ResetPointerGesture();
        if (e.PropertyName is nameof(ProjectCaptureViewModel.Message)
                or nameof(ProjectCaptureViewModel.PendingWorkspaceActionCount)
                or nameof(ProjectCaptureViewModel.CanRetryWorkspaceRefresh)
            && sender is ProjectCaptureViewModel messageWork)
            ScheduleTransientMessageDismissal(messageWork);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.NeedsDecision)
            && DataContext is ShellViewModel { Work.NeedsDecision: true })
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var radio in this.GetVisualDescendants().OfType<RadioButton>())
                    if (radio.DataContext is NavigationItemViewModel navigation)
                        radio.SetCurrentValue(RadioButton.IsCheckedProperty, navigation.IsSelected);
                this.FindControl<Button>("GuardSave")?.Focus();
            });
        if (e.PropertyName == nameof(ProjectCaptureViewModel.DateValidationMessage)
            && sender is ProjectCaptureViewModel { HasDateValidationError: true })
            Dispatcher.UIThread.Post(
                () => this.FindControl<CalendarDatePicker>("DraftDate")?.Focus(),
                DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.CompletionFocusAutomationId)
            && sender is ProjectCaptureViewModel { CompletionFocusAutomationId.Length: > 0 } work)
            Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(control => control.IsEffectivelyVisible
                    && AutomationProperties.GetAutomationId(control) == work.CompletionFocusAutomationId)?.Focus(),
                DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.ReorderFocusAutomationId)
            && sender is ProjectCaptureViewModel { ReorderFocusAutomationId.Length: > 0 } reordered)
            Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(control => AutomationProperties.GetAutomationId(control) == reordered.ReorderFocusAutomationId)?.Focus(),
                DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.TodayFocusAutomationId)
            && sender is ProjectCaptureViewModel { TodayFocusAutomationId.Length: > 0 } todayWork)
            Dispatcher.UIThread.Post(() => FocusAutomationId(todayWork.TodayFocusAutomationId), DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.DialogReturnFocusAutomationId)
            && sender is ProjectCaptureViewModel { DialogReturnFocusAutomationId.Length: > 0 } dialogWork)
            Dispatcher.UIThread.Post(() => FocusAutomationId(dialogWork.DialogReturnFocusAutomationId), DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.ParticipantFocusAutomationId)
            && sender is ProjectCaptureViewModel { ParticipantFocusAutomationId.Length: > 0 } participantWork)
            Dispatcher.UIThread.Post(() => FocusAutomationId(participantWork.ParticipantFocusAutomationId), DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.IsEditingMarkdown)
            && sender is ProjectCaptureViewModel { IsEditingMarkdown: true })
            Dispatcher.UIThread.Post(
                () => this.FindControl<TextBox>("MarkdownSource")?.Focus(),
                DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.NeedsArchiveConfirmation)
            && sender is ProjectCaptureViewModel archiveConfirmation)
            Dispatcher.UIThread.Post(() =>
            {
                if (archiveConfirmation.NeedsArchiveConfirmation)
                    this.FindControl<Button>("ConfirmArchiveTaskButton")?.Focus();
                else
                    FocusAutomationId(archiveConfirmation.DialogReturnFocusAutomationId);
            }, DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.NeedsProjectDuplicationConfirmation)
            && sender is ProjectCaptureViewModel projectDuplication)
            Dispatcher.UIThread.Post(() =>
            {
                if (projectDuplication.NeedsProjectDuplicationConfirmation)
                    this.FindControl<TextBox>("ProjectDuplicationTitle")?.Focus();
                else
                    FocusAutomationId(projectDuplication.DialogReturnFocusAutomationId);
            }, DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.NeedsBulkTaskArchiveConfirmation)
            && sender is ProjectCaptureViewModel bulkArchiveConfirmation)
            Dispatcher.UIThread.Post(() =>
            {
                if (bulkArchiveConfirmation.NeedsBulkTaskArchiveConfirmation)
                    this.FindControl<Button>("ConfirmBulkTaskArchiveButton")?.Focus();
                else
                    FocusAutomationId(bulkArchiveConfirmation.DialogReturnFocusAutomationId);
            }, DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.NeedsEmptyBinConfirmation)
            && sender is ProjectCaptureViewModel emptyBinConfirmation)
            Dispatcher.UIThread.Post(() =>
            {
                if (emptyBinConfirmation.NeedsEmptyBinConfirmation)
                    this.FindControl<Button>("ConfirmEmptyBinButton")?.Focus();
                else
                    FocusAutomationId(emptyBinConfirmation.BinFocusAutomationId);
            }, DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.ArchiveFocusAutomationId)
            && sender is ProjectCaptureViewModel { ArchiveFocusAutomationId.Length: > 0 } archiveWork)
            Dispatcher.UIThread.Post(() => FocusAutomationId(archiveWork.ArchiveFocusAutomationId), DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.BinFocusAutomationId)
            && sender is ProjectCaptureViewModel { BinFocusAutomationId.Length: > 0 } binWork)
            Dispatcher.UIThread.Post(() => FocusAutomationId(binWork.BinFocusAutomationId), DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.NeedsCategoryReplacement)
            && sender is ProjectCaptureViewModel categoryWork)
            Dispatcher.UIThread.Post(() =>
            {
                if (categoryWork.NeedsCategoryReplacement) this.FindControl<ComboBox>("CategoryReplacement")?.Focus();
                else FocusAutomationId(categoryWork.DialogReturnFocusAutomationId);
            }, DispatcherPriority.ApplicationIdle);
        if (e.PropertyName == nameof(ProjectCaptureViewModel.NeedsAttachmentChoice)
            && sender is ProjectCaptureViewModel attachmentWork)
            Dispatcher.UIThread.Post(() =>
            {
                if (attachmentWork.NeedsAttachmentChoice) this.FindControl<Button>("PreserveTaskCategory")?.Focus();
                else this.FindControl<Button>("ChangeTaskContextButton")?.Focus();
            }, DispatcherPriority.ApplicationIdle);
    }

    private void FocusAutomationId(string automationId) => this.GetVisualDescendants().OfType<Control>()
        .FirstOrDefault(control => control.IsEffectivelyVisible
            && AutomationProperties.GetAutomationId(control) == automationId)?.Focus();

    private void BringIntoViewAndFocusAutomationId(string automationId)
    {
        var target = this.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => control.IsEffectivelyVisible
                && AutomationProperties.GetAutomationId(control) == automationId);
        if (target is null) return;

        var alignmentTarget = target.GetVisualAncestors().OfType<Control>()
            .FirstOrDefault(control => control.Classes.Contains("project-row")
                || control.Classes.Contains("category-row")
                || control.Classes.Contains("archive-row"))
            ?? target;
        var viewer = target.GetVisualAncestors().OfType<ScrollViewer>()
            .FirstOrDefault(candidate => candidate.IsEffectivelyVisible);
        var origin = viewer is null ? null : alignmentTarget.TranslatePoint(default, viewer);
        if (viewer is not null && origin is not null)
        {
            var maximumOffset = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
            var targetOffset = viewer.Offset.Y + origin.Value.Y;
            viewer.Offset = new(
                viewer.Offset.X,
                Math.Clamp(targetOffset, 0, maximumOffset));
        }
        else
        {
            target.BringIntoView();
        }
        target.Focus();
    }

    private void Navigate(Action action)
    {
        if (DataContext is ShellViewModel { Work: { } work }) work.Navigate(action);
        else action();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closingApproved) return;
        e.Cancel = true;
        await StopLocalMcpAsync();
        InvalidateProposalReviewState();
        if (DataContext is ShellViewModel { Work: { } work })
        {
            await work.NavigateAsync(() => _ = CompleteCloseAsync());
            if (!_closeCompletionStarted)
            {
                _ = ReconcileLocalMcpAsync();
            }
            return;
        }
        await CompleteCloseAsync();
    }

    private async Task CompleteCloseAsync()
    {
        if (_closeCompletionStarted) return;
        _closeCompletionStarted = true;
        await StopLocalMcpAsync();
        if (_inspectorSaveWriter is not null)
        {
            await _inspectorSaveWriter.DisposeAsync();
            _inspectorSaveWriter = null;
            _actionExecutor = null;
        }
        FinishAcceptedClose();
    }

    private void FinishAcceptedClose()
    {
        if (_initialWindowSizeApplied)
        {
            _ = _mainWindowSizePreferenceStore.TrySaveMainWindowSize(_lastNormalWindowSize);
        }
        _closingApproved = true;
        Close();
    }

    private void OnFocusInspector(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (DataContext is ShellViewModel { Work.NeedsDecision: true }) this.FindControl<Button>("GuardSave")?.Focus();
        else if (DataContext is ShellViewModel { Work.HasInspector: true }) this.FindControl<TextBox>("DraftTitle")?.Focus();
        else if (this.FindControl<Button>("NewProjectButton") is { IsEffectivelyVisible: true } projectButton) projectButton.Focus();
        else if (this.FindControl<Button>("NewTaskButton") is { IsEffectivelyVisible: true } taskButton) taskButton.Focus();
        else if (this.FindControl<Button>("NewCategoryButton") is { IsEffectivelyVisible: true } categoryButton) categoryButton.Focus();
    });

    private void OnQuickAddKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ProjectRowViewModel project } field) return;
        if (e.Key == Key.Escape) { project.QuickTitle = string.Empty; e.Handled = true; return; }
        if (e.Key is not (Key.Enter or Key.Tab) || string.IsNullOrWhiteSpace(field.Text)) return;
        project.QuickTitle = field.Text;
        e.Handled = true;
        project.Submit();
        Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(box => box.Classes.Contains("quick-add") && box.DataContext is ProjectRowViewModel row && row.Id == project.Id)?.Focus());
    }

    private void OnBacklogQuickAddKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox field || DataContext is not ShellViewModel { Work: { } work }) return;
        if (e.Key == Key.Escape) { work.BacklogQuickTitle = string.Empty; e.Handled = true; return; }
        if (e.Key is not (Key.Enter or Key.Tab) || string.IsNullOrWhiteSpace(field.Text)) return;
        work.BacklogQuickTitle = field.Text;
        e.Handled = true;
        if (work.SubmitBacklogQuickAdd())
            Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("BacklogQuickTitle")?.Focus());
    }

    private void OnNewParticipantKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ShellViewModel { Work: { } work }) return;
        if (e.Key == Key.Escape)
        {
            work.NewParticipantLabel = string.Empty;
            work.ParticipantToAdd = null;
            e.Handled = true;
            Dispatcher.UIThread.Post(() => this.FindControl<ComboBox>("ParticipantPicker")?.Focus());
            return;
        }
        if (e.Key == Key.Enter)
        {
            work.AddNewParticipantCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnParticipantRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ParticipantSettingViewModel participant }) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            participant.SaveRenameCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            participant.CancelRenameCommand.Execute(null);
            Dispatcher.UIThread.Post(() => FocusAutomationId(participant.RenameAutomationId));
        }
    }

    private async void OnCopyRendered(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel { Work: { } work }) return;
        var clipboard = _markdownClipboard ?? (Clipboard is null ? null : new AvaloniaMarkdownClipboard(Clipboard));
        if (clipboard is null)
        {
            work.MarkdownCopyFailed();
            return;
        }

        try
        {
            await clipboard.WriteAsync(work.RenderedDescription);
            work.MarkdownCopySucceeded();
        }
        catch (Exception)
        {
            work.MarkdownCopyFailed();
        }
    }

    private void OnMarkdownPreviewActivated(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel { Work: { } work } && work.EditMarkdownCommand.CanExecute(null))
            work.EditMarkdownCommand.Execute(null);
    }

    private void OnMarkdownSourceLostFocus(object? sender, RoutedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (this.FindControl<TextBox>("MarkdownSource") is { IsKeyboardFocusWithin: false }
                && DataContext is ShellViewModel { Work: { IsEditingMarkdown: true } work })
                work.FinishMarkdownEditing();
        }, DispatcherPriority.ApplicationIdle);
    }

    private void OnMarkdownSourceKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is TextBox editor) MarkdownSourceEditor.TryHandleKeyDown(editor, e);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _dragScope != DragScope.None)
        {
            ResetPointerGesture();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Escape || DataContext is not ShellViewModel { Work: { } work }) return;
        if (work.NeedsArchiveConfirmation)
        {
            work.CancelArchiveTaskCommand.Execute(null);
            e.Handled = true;
        }
        else if (work.NeedsProjectDuplicationConfirmation)
        {
            work.CancelProjectDuplicationCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnWorkPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ResetPointerGesture();
        var hit = this.InputHitTest(e.GetPosition(this)) as Control;
        _markdownPointerStartedOutside = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && DataContext is ShellViewModel { Work.IsEditingMarkdown: true }
            && this.FindControl<TextBox>("MarkdownSource") is { } markdownSource
            && hit != markdownSource
            && hit?.GetVisualAncestors().Contains(markdownSource) != true;
        Button? handle = (hit as Button)?.Classes.Contains("drag-handle") == true
            ? (Button?)hit
            : hit?.GetVisualAncestors().OfType<Button>().FirstOrDefault(button => button.Classes.Contains("drag-handle"));
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (handle?.Classes.Contains("backlog-drag-handle") == true && handle.DataContext is TaskRowViewModel backlogTask)
        {
            _dragScope = DragScope.Backlog;
            _draggedId = backlogTask.Id;
            return;
        }
        if (handle?.Classes.Contains("today-planned-drag-handle") == true && handle.DataContext is TodayTaskRowViewModel plannedTask)
        {
            _dragScope = DragScope.TodayPlanned;
            _draggedId = plannedTask.Task.Id;
            return;
        }
        if (handle?.Classes.Contains("today-in-progress-drag-handle") == true && handle.DataContext is TodayTaskRowViewModel activeTask)
        {
            _dragScope = DragScope.TodayInProgress;
            _draggedId = activeTask.Task.Id;
            return;
        }
        if (handle?.Classes.Contains("project-drag-handle") == true && handle.DataContext is ProjectRowViewModel project)
        {
            _dragScope = DragScope.Projects;
            _draggedId = project.Id;
            return;
        }
        if (handle?.Classes.Contains("project-task-drag-handle") == true
            && handle.DataContext is TaskRowViewModel { ProjectId: { } projectId } projectTask)
        {
            _dragScope = DragScope.ProjectTasks;
            _draggedId = projectTask.Id;
            _draggedProjectId = projectId;
            return;
        }
        if (handle?.Classes.Contains("category-drag-handle") == true && handle.DataContext is CategoryGroupViewModel category)
        {
            _dragScope = DragScope.Categories;
            _draggedId = category.Id;
            return;
        }
        if (hit is Button || hit?.GetVisualAncestors().OfType<Button>().Any() == true) return;
        _pressedSelectableRow = FindRow(hit, "selectable-row");
    }

    private void OnWorkDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragScope == DragScope.None || _draggedId is null) return;
        UpdateDragTarget(e.GetPosition(this));
    }

    private void UpdateDragTarget(Point point)
    {
        var targetClass = _dragScope switch
        {
            DragScope.Backlog => "backlog-row",
            DragScope.TodayPlanned => "today-planned-row",
            DragScope.TodayInProgress => "today-in-progress-row",
            DragScope.Projects => "project-row",
            DragScope.ProjectTasks => "project-task-row",
            DragScope.Categories => "category-row",
            _ => string.Empty,
        };
        // The gap owns its entire displayed area: crossing its midpoint must not
        // retarget the row which moved out from underneath the pointer.
        if (_dragPlaceholder is { } placeholder && IsInsideDragViewport(placeholder, point)
            && new Rect(placeholder.TranslatePoint(default, this)!.Value, placeholder.Bounds.Size).Contains(point)) return;
        _dragRows ??= this.GetVisualDescendants().OfType<Border>()
            .Where(row => row.IsEffectivelyVisible && row.Classes.Contains(targetClass)
                && (_dragScope != DragScope.ProjectTasks
                    || row.DataContext is TaskRowViewModel task && task.ProjectId == _draggedProjectId))
            .ToArray();
        _dragSource ??= _dragRows.FirstOrDefault(row => DragId(row) == _draggedId);
        var candidate = _dragRows.FirstOrDefault(row => row.IsEffectivelyVisible && TopLevel.GetTopLevel(row) == this
            && new Rect(row.TranslatePoint(default, this)!.Value, row.Bounds.Size).Contains(point)
            && IsInsideDragViewport(row, point));
        var target = candidate;
        var after = target is not null && point.Y >= target.TranslatePoint(default, this)!.Value.Y + DragHeading(target).Bounds.Height / 2;
        if (ReferenceEquals(target, _dragSource)) target = null;
        if (ReferenceEquals(target, _dragTarget) && after == _dragAfter) return;
        ClearDragPreview();
        _dragTarget = target;
        _dragAfter = after;
        if (target?.Parent is not StackPanel host || _dragSource is null) return;
        var sourceIndex = Array.FindIndex(_dragRows, row => ReferenceEquals(row, _dragSource));
        var targetIndex = Array.FindIndex(_dragRows, row => ReferenceEquals(row, target));
        var position = targetIndex - (sourceIndex < targetIndex ? 1 : 0) + (after ? 1 : 0) + 1;
        var label = $"Drop here: {DragTitle(_dragSource)} · position {position} of {_dragRows.Length}";
        var title = new TextBlock
        {
            Text = $"Drop here: {DragTitle(_dragSource)}",
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
        };
        var destination = new TextBlock { Text = $"Position {position} of {_dragRows.Length}", FontSize = 11 };
        Grid.SetRow(destination, 1);
        _dragPlaceholder = new Border
        {
            Classes = { "reorder-placeholder" },
            Height = DragHeading(_dragSource).Bounds.Height,
            IsHitTestVisible = false,
            ClipToBounds = true,
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Children = { title, destination },
            },
        };
        AutomationProperties.SetName(_dragPlaceholder, label);
        host.Children.Insert(after ? 1 : 0, _dragPlaceholder);
        target.Classes.Add("drag-target");
        _dragSource.Classes.Add("drag-source");
    }

    private bool IsInsideDragViewport(Control control, Point point) =>
        control.GetVisualAncestors().OfType<ScrollViewer>().All(viewer =>
            new Rect(viewer.TranslatePoint(default, this)!.Value, viewer.Bounds.Size).Contains(point));

    private static Border DragHeading(Border row) => row.GetVisualDescendants().OfType<Border>()
        .FirstOrDefault(border => border.Classes.Contains("project-header-row") || border.Classes.Contains("category-header-row")) ?? row;

    private static string? DragId(Border row) => row.DataContext switch
    {
        TaskRowViewModel task => task.Id,
        TodayTaskRowViewModel today => today.Task.Id,
        ProjectRowViewModel project => project.Id,
        CategoryGroupViewModel category => category.Id,
        _ => null,
    };

    private static string DragTitle(Border row) => row.DataContext switch
    {
        TaskRowViewModel task => task.Title,
        TodayTaskRowViewModel today => today.Task.Title,
        ProjectRowViewModel project => project.Title,
        CategoryGroupViewModel category => category.Name,
        _ => string.Empty,
    };

    private void ClearDragPreview()
    {
        if (_dragPlaceholder?.Parent is StackPanel host) host.Children.Remove(_dragPlaceholder);
        _dragPlaceholder = null;
        _dragTarget?.Classes.Remove("drag-target");
        _dragSource?.Classes.Remove("drag-source");
    }

    private void OnWorkPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var finishMarkdownEditing = _markdownPointerStartedOutside
            && e.InitialPressMouseButton == MouseButton.Left;
        _markdownPointerStartedOutside = false;
        if (finishMarkdownEditing && DataContext is ShellViewModel { Work: { IsEditingMarkdown: true } markdownWork })
            markdownWork.FinishMarkdownEditing();

        if (DataContext is not ShellViewModel { Work: { } work }
            || e.InitialPressMouseButton != MouseButton.Left)
        {
            ResetPointerGesture();
            return;
        }
        if (_dragScope == DragScope.None || _draggedId is null)
        {
            var pressedSelectableRow = _pressedSelectableRow;
            ResetPointerGesture();
            if (pressedSelectableRow is null) return;
            var hit = this.InputHitTest(e.GetPosition(this)) as Control;
            if (hit is Button || hit?.GetVisualAncestors().OfType<Button>().Any() == true) return;
            var releasedSelectableRow = FindRow(hit, "selectable-row");
            if (!ReferenceEquals(releasedSelectableRow, pressedSelectableRow)
                || SelectionCommandForRow(releasedSelectableRow) is not { } selectionCommand)
                return;
            selectionCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (_dragRows is not null) UpdateDragTarget(e.GetPosition(this));
        var after = _dragAfter;
        var target = _dragTarget?.DataContext as TaskRowViewModel;
        var targetToday = _dragTarget?.DataContext as TodayTaskRowViewModel;
        var targetProject = _dragTarget?.DataContext as ProjectRowViewModel;
        var targetCategory = _dragTarget?.DataContext as CategoryGroupViewModel;
        var draggedId = _draggedId;
        var draggedProjectId = _draggedProjectId;
        var dragScope = _dragScope;
        ResetPointerGesture();
        if (dragScope == DragScope.Backlog && target is not null)
            work.DragTask(draggedId, target.Id, after);
        else if ((dragScope == DragScope.TodayPlanned || dragScope == DragScope.TodayInProgress) && targetToday is not null)
            work.DragTodayTask(draggedId, targetToday.Task.Id, after);
        else if (dragScope == DragScope.Projects && targetProject is not null)
            work.DragProject(draggedId, targetProject.Id, after);
        else if (dragScope == DragScope.ProjectTasks && target is not null && draggedProjectId is not null)
            work.DragProjectTask(draggedProjectId, draggedId, target.Id, after);
        else if (dragScope == DragScope.Categories && targetCategory is not null)
            work.DragCategory(draggedId, targetCategory.Id, after);
        else return;
        e.Handled = true;
    }

    private void OnDragLayoutUpdated(object? sender, EventArgs e)
    {
        if (_dragSource is { } source && (!source.IsEffectivelyVisible || TopLevel.GetTopLevel(source) != this))
            ResetPointerGesture();
    }

    private void OnDragScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta != default) ResetPointerGesture();
    }

    private void OnDragWindowSizeChanged(object? sender, SizeChangedEventArgs e) => ResetPointerGesture();

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _markdownPointerStartedOutside = false;
        ResetPointerGesture();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        _markdownPointerStartedOutside = false;
        ResetPointerGesture();
    }

    private void ResetPointerGesture()
    {
        ClearDragPreview();
        _dragTarget = null;
        _dragSource = null;
        _dragRows = null;
        _dragScope = DragScope.None;
        _draggedId = null;
        _draggedProjectId = null;
        _pressedSelectableRow = null;
    }

    private static Border? FindRow(Control? hit, string rowClass) =>
        (hit as Border)?.Classes.Contains(rowClass) == true
            ? (Border?)hit
            : hit?.GetVisualAncestors().OfType<Border>().FirstOrDefault(border => border.Classes.Contains(rowClass));

    private static RelayCommand? SelectionCommandForRow(Border? row) => row?.DataContext switch
    {
        TaskRowViewModel task => task.SelectCommand,
        CategoryTaskRowViewModel categoryTask => categoryTask.Task.SelectCommand,
        CompletedTaskRowViewModel completedTask => completedTask.Task.SelectCommand,
        ArchivedTaskRowViewModel archivedTask => archivedTask.Task.SelectCommand,
        ArchivedWorkRowViewModel { Task: not null } archivedWork => archivedWork.Task.SelectCommand,
        ArchiveSearchResultViewModel archiveSearchResult => archiveSearchResult.OpenCommand,
        UpcomingTaskRowViewModel upcomingTask => upcomingTask.Task.SelectCommand,
        TodayTaskRowViewModel todayTask => todayTask.Task.SelectCommand,
        ProjectRowViewModel project => project.SelectCommand,
        CategoryProjectRowViewModel categoryProject => categoryProject.Project.SelectCommand,
        CategoryGroupViewModel category => category.SelectCommand,
        _ => null,
    };

    private void OnReorderMenuClosed(object? sender, EventArgs e)
    {
        if (sender is not FlyoutBase { Target: Button target }) return;
        var automationId = AutomationProperties.GetAutomationId(target);
        Dispatcher.UIThread.Post(() => this.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(candidate => AutomationProperties.GetAutomationId(candidate) == automationId)?.Focus(),
            DispatcherPriority.ApplicationIdle);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        Opened -= OnOpened;
        Resized -= OnWindowResized;
        Activated -= OnActivated;
        Deactivated -= OnDeactivated;
        DataContextChanged -= OnDataContextChanged;
        ResetPointerGesture();
        _dateRefreshTimer.Stop();
        _dateRefreshTimer.Tick -= OnDateRefreshTick;
        _autosaveScheduler.Dispose();
        _transientMessageScheduler.Dispose();
        if (_localMcpService is { } localMcpService)
        {
            _localMcpService = null;
            _ = localMcpService.DisposeAsync().AsTask();
        }
        DiscardProposalStateForSessionEnd();
        if (this.FindControl<BoundedProposalTextBox>("AiProposalTextBox") is { } proposalTextBox)
        {
            proposalTextBox.ProposalTextAttempted -= OnProposalTextAttempted;
            proposalTextBox.PastingFromClipboard -= OnProposalPastingFromClipboard;
        }
        if (_subscribedWork is not null)
        {
            _subscribedWork.PropertyChanged -= OnWorkChanged;
            _subscribedWork.AutosaveRequested -= OnAutosaveRequested;
        }
        if (_subscribedShell is not null)
            _subscribedShell.PropertyChanged -= OnShellChanged;
        // Normal close and session-unavailable close drain the session executor before
        // reaching this event. Never synchronously wait for storage on the UI thread here.
        _session?.Dispose();
        _session = null;
    }

    private void DiscardProposalStateForSessionEnd()
    {
        _localMcpService?.CloseAdmission();
        InvalidateProposalReviewState();
        _pendingProposalCoordinator?.Dispose();
        _pendingProposalCoordinator = null;
    }

    private void InvalidateProposalReviewState()
    {
        _proposalReviewRevision++;
        _proposalIntakeCancellation?.Cancel();
        _proposalIntakeCancellation?.Dispose();
        _proposalIntakeCancellation = null;
        _activeProposalReview?.Discard();
        if (_activeProposalReviewWindow?.DataContext is AgentProposalReviewViewModel { CanClose: true })
        {
            _activeProposalReviewWindow.Close();
        }

        _activeProposalReview = null;
        _activeProposalReviewWindow = null;
    }
}
