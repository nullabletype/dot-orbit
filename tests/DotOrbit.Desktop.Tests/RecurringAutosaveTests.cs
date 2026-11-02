using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class RecurringAutosaveTests
{
    private static FixedTimeProvider Clock() => new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private static RecurringMemoryWorkspace Existing()
    {
        var store = new RecurringMemoryWorkspace();
        store.CreateRecurringTask(new("Original", "Original details", null, "home", [], RecurrenceCadence.Daily, new(2026, 11, 2), null, false));
        return store;
    }
    private static RecurringTasksViewModel Edit(ProjectCaptureViewModel work)
    {
        work.Recurring.Definitions.Single().EditCommand.Execute(null);
        return work.Recurring;
    }
    private static void Stage(RecurringTasksViewModel model, string label)
    {
        model.ParticipantToAdd = ParticipantChoice.New;
        model.NewParticipantLabel = label;
        model.AddNewParticipantCommand.Execute(null);
    }

    [Fact]
    public void TextCoalescesByRevisionAndDiscreteChangesCommitCurrentDraftImmediately()
    {
        var store = Existing();
        var model = Edit(new(store, Clock()));
        var revisions = new List<long>();
        model.AutosaveRequested += (_, request) => revisions.Add(request.Revision);
        model.Title = "First";
        model.Title = "Latest";
        model.Description = "Latest details";
        Assert.Equal(TimeSpan.FromMilliseconds(600), RecurringTasksViewModel.AutosaveDelay);
        Assert.Equal(3, revisions.Count);
        Assert.Equal(0, store.UpdateCalls);
        model.RunScheduledAutosave(revisions[0]);
        Assert.Equal(0, store.UpdateCalls);
        model.RunScheduledAutosave(revisions[^1]);
        Assert.Equal(1, store.UpdateCalls);
        Assert.Equal("Latest", store.ReadRecurringTasks().Single().Title);
        Assert.Equal("Latest details", store.ReadRecurringTasks().Single().Description);
        Assert.True(model.HasEditor);
        Assert.False(model.IsDirty);
        Assert.Equal("Saved", model.AutosaveStatus);
        model.Title = "Included by selection";
        model.Cadence = RecurrenceCadence.Monthly;
        Assert.Equal(2, store.UpdateCalls);
        Assert.Equal("Included by selection", store.ReadRecurringTasks().Single().Title);
        Assert.Equal(RecurrenceCadence.Monthly, store.ReadRecurringTasks().Single().Cadence);
    }

    [Fact]
    public void NewDraftAutosavesPausedOnceAndExplicitEnableUsesTheLatestCompleteContent()
    {
        var store = new RecurringMemoryWorkspace();
        var model = new ProjectCaptureViewModel(store, Clock()).Recurring;
        model.NewCommand.Execute(null);
        Assert.False(model.IsEnabled);
        Assert.False(model.RunScheduledAutosave());
        Assert.Empty(store.ReadRecurringTasks());
        model.Title = "Paused definition";
        model.Category = model.Categories.Single(item => item.Id == "home");
        Assert.Empty(store.ReadRecurringTasks());
        model.RunScheduledAutosave();
        var created = store.ReadRecurringTasks().Single();
        Assert.False(created.IsEnabled);
        Assert.Equal(created.Id, model.EditingDefinitionId);
        Assert.True(model.HasEditor);
        Assert.Empty(store.Read().Tasks);
        model.Title = "Final content before enabling";
        model.Description = "Full **description**";
        model.IsEnabled = true;
        Assert.Equal(1, store.CreateCalls);
        var enabled = store.ReadRecurringTasks().Single();
        Assert.Equal(created.Id, enabled.Id);
        Assert.True(enabled.IsEnabled);
        var generated = store.Read().Tasks.Single();
        Assert.Equal(enabled.Title, generated.Title);
        Assert.Equal(enabled.Description, generated.Description);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HeldSavePreservesNewerEditsAndNeverCreatesTwice(bool creating)
    {
        var store = creating ? new RecurringMemoryWorkspace() : Existing();
        using var release = new ManualResetEventSlim();
        using var releaseProcessing = new ManualResetEventSlim();
        var operation = new HeldEditorSave(store, release, releaseProcessing);
        await using var executor = new SerializedWorkspacePersistenceExecutor(operation, Clock());
        var work = new ProjectCaptureViewModel(store, Clock(), executor, executor, new AvaloniaUiDispatcher());
        var model = creating ? work.Recurring : Edit(work);
        if (creating) { model.NewCommand.Execute(null); model.Category = model.Categories.Single(item => item.Id == "home"); }
        model.Title = "Captured";
        model.RunScheduledAutosave();
        await operation.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.True(model.CanEdit);
            Assert.Equal("Saving…", model.AutosaveStatus);
            model.Title = "Latest while held";
            model.Description = "Latest description";
            Stage(model, "Morgan");
            model.IsEnabled = true;
            Assert.Equal("Captured", operation.Captured!.Title);
            Assert.False(operation.Captured.IsEnabled);
            Assert.Empty(operation.Captured.NewParticipantLabels ?? []);
            release.Set();
            await operation.ProcessingStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal("Saved", model.AutosaveStatus);
            Assert.True(work.HasPendingWorkspaceActions);
            Assert.Empty(store.Read().Tasks);
            var definition = store.ReadRecurringTasks().Single();
            Assert.Equal(1, store.CreateCalls);
            Assert.Equal(definition.Id, model.EditingDefinitionId);
            Assert.Equal("Latest while held", definition.Title);
            Assert.Equal("Latest description", definition.Description);
            Assert.True(definition.IsEnabled);
            // Saving the definition settles before its separately queued materialization.
            // Wait for that admitted action, rather than observing only the Saved label.
            releaseProcessing.Set();
            await work.WaitForWorkspaceActionsAsync();
            if (creating) Assert.Equal("Latest while held", store.Read().Tasks.Single().Title);
            Assert.Equal("Latest while held", model.Title);
            Assert.True(model.HasEditor);
            Assert.False(model.IsDirty);
            var selected = model.SelectedParticipants.Single();
            Assert.False(selected.IsDraft);
            Assert.Equal(store.Read().Participants.Single().Id, selected.Id);
            Assert.Equal([selected.Id], definition.ParticipantIds);
            selected.RemoveCommand.Execute(null);
            await work.WaitForWorkspaceActionsAsync();
            Assert.Empty(store.ReadRecurringTasks().Single().ParticipantIds);
            model.ParticipantToAdd = model.AvailableParticipants.Single(item => item.Id == selected.Id);
            model.AddParticipantCommand.Execute(null);
            await work.WaitForWorkspaceActionsAsync();
            Assert.Equal([selected.Id], store.ReadRecurringTasks().Single().ParticipantIds);
            Assert.Single(store.Read().Participants);
        }
        finally { release.Set(); releaseProcessing.Set(); }
    }

    [Fact]
    public void MutationFailureRetainsEditableDraftAndRetryDoesNotCreateAnotherDefinition()
    {
        var store = Existing();
        var model = Edit(new(store, Clock()));
        store.FailUpdate = true;
        model.Title = "Retained";
        Assert.True(model.RunScheduledAutosave()); // accepted request; result reports its failure
        Assert.True(model.HasAutosaveError);
        Assert.Contains("Could not save", model.AutosaveStatus, StringComparison.Ordinal);
        Assert.True(model.CanEdit);
        Assert.Equal("Retained", model.Title);
        Assert.Equal("Original", store.ReadRecurringTasks().Single().Title);
        store.FailUpdate = false;
        model.RetryAutosaveCommand.Execute(null);
        Assert.False(model.HasAutosaveError);
        Assert.Equal("Saved", model.AutosaveStatus);
        Assert.Equal("Retained", store.ReadRecurringTasks().Single().Title);
        Assert.Equal(1, store.CreateCalls);
        Assert.Equal(2, store.UpdateCalls);
    }

    [Fact]
    public void CommittedCreateRefreshFailureAdoptsIdentityAndRetriesOnlyRead()
    {
        var store = new RecurringMemoryWorkspace();
        var work = new ProjectCaptureViewModel(store, Clock());
        var model = work.Recurring;
        model.NewCommand.Execute(null);
        model.Title = "Saved despite failed refresh";
        model.Category = model.Categories.Single(item => item.Id == "home");
        store.FailReadAfterMutation = true;
        model.RunScheduledAutosave();
        Assert.Equal(store.ReadRecurringTasks().Single().Id, model.EditingDefinitionId);
        Assert.False(model.HasAutosaveError);
        Assert.Contains("Saved. View refresh failed", model.AutosaveStatus, StringComparison.Ordinal);
        Assert.True(work.CanRetryWorkspaceRefresh);
        model.RetryAutosaveCommand.Execute(null);
        Assert.Equal(1, store.CreateCalls);
        store.FailReads = false; store.FailReadAfterMutation = false;
        work.RetryWorkspaceRefreshCommand.Execute(null);
        Assert.Equal(1, store.CreateCalls);
        Assert.True(model.HasEditor);
    }

    [Fact]
    public void FailedParticipantRefreshThenNewParticipantSaveLeavesOnlyResolvedChoices()
    {
        var store = Existing();
        var work = new ProjectCaptureViewModel(store, Clock());
        var model = Edit(work);
        store.FailReadAfterMutation = true;
        Stage(model, "First Person");
        Assert.True(work.CanRetryWorkspaceRefresh);
        model.SelectedParticipants.Single().RemoveCommand.Execute(null);
        store.FailReads = false; store.FailReadAfterMutation = false;
        Stage(model, "Second Person");
        model.RunScheduledAutosave(force: true);
        Assert.False(model.SelectedParticipants.Single().IsDraft);
        Assert.Equal("Second Person", model.SelectedParticipants.Single().Label);
        Assert.Equal(2, model.Participants.Count);
        Assert.All(model.Participants, participant => Assert.False(participant.IsDraft));
        Assert.Equal(2, model.AvailableParticipants.Where(item => !item.IsNew).Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Single(store.ReadRecurringTasks().Single().ParticipantIds);
    }

    [AvaloniaFact]
    public void AutomaticValidationKeepsFocusAndHeaderExposesStatusRetryWithoutSaveOrCancel()
    {
        var store = Existing();
        var shell = new ShellViewModel(store, Clock());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            var model = Edit(shell.Work!);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button =>
                AutomationProperties.GetAutomationId(button) == "recurring-save" || AutomationProperties.GetName(button) == "Cancel recurring Task changes");
            var status = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), text => AutomationProperties.GetName(text) == "Recurring definition autosave status");
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
            var title = Assert.Single(window.GetVisualDescendants().OfType<TextBox>(), text => AutomationProperties.GetAutomationId(text) == "recurring-title");
            model.ParticipantToAdd = ParticipantChoice.New;
            model.NewParticipantLabel = "Pending Participant";
            Dispatcher.UIThread.RunJobs();
            title.Focus();
            model.Description = "Text while a Participant needs Add";
            model.RunScheduledAutosave();
            Dispatcher.UIThread.RunJobs();
            Assert.True(title.IsFocused);
            Assert.Contains("Add or cancel", model.ParticipantValidation, StringComparison.Ordinal);
            model.CancelNewParticipantCommand.Execute(null);
            Assert.Equal("Text while a Participant needs Add", store.ReadRecurringTasks().Single().Description);
            Assert.Equal(1, store.UpdateCalls);
            store.FailUpdate = true;
            model.Title = "Retry title";
            model.RunScheduledAutosave();
            Dispatcher.UIThread.RunJobs();
            var retry = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => AutomationProperties.GetName(button) == "Retry recurring definition automatic save");
            Assert.True(retry.IsEffectivelyVisible);
            Assert.Contains("Could not save", status.Text, StringComparison.Ordinal);
            Assert.True(title.Focusable && title.IsEnabled);
            store.FailUpdate = false;
            model.RetryAutosaveCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(retry.IsEffectivelyVisible);
            Assert.Equal("Saved", status.Text);
            Assert.DoesNotContain("Could not save", shell.Work!.Message, StringComparison.Ordinal);
        }
        finally { shell.Work!.Recurring.CancelCommand.Execute(null); window.Close(); }
    }

    [AvaloniaFact]
    public void NativeInspectorSchedulerCreatesPausedDraftAfterQuietPeriodWithoutMovingTitleFocus()
    {
        var scheduler = new ManualScheduler();
        var store = new RecurringMemoryWorkspace();
        var shell = new ShellViewModel(store, Clock());
        var window = new MainWindow(session: null, autosaveScheduler: scheduler, timeProvider: Clock()) { DataContext = shell };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            var model = shell.Work!.Recurring;
            model.NewCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var title = window.GetVisualDescendants().OfType<TextBox>().Single(text => AutomationProperties.GetAutomationId(text) == "recurring-title");
            title.Focus();
            title.Text = "First";
            model.Category = model.Categories.Single(item => item.Id == "home");
            title.Text = "Latest paused definition";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(TimeSpan.FromMilliseconds(600), scheduler.Delay);
            Assert.True(scheduler.Count >= 3);
            Assert.Empty(store.ReadRecurringTasks());
            scheduler.Fire();
            Dispatcher.UIThread.RunJobs();
            var definition = store.ReadRecurringTasks().Single();
            Assert.Equal("Latest paused definition", definition.Title);
            Assert.False(definition.IsEnabled);
            Assert.Empty(store.Read().Tasks);
            Assert.True(model.HasEditor);
            Assert.True(title.IsFocused);
            Assert.Equal("Saved", model.AutosaveStatus);
        }
        finally { shell.Work!.Recurring.CancelCommand.Execute(null); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticFollowupAfterHeldWriteDoesNotFocusInvalidOrPendingParticipantField(bool participantEntry)
    {
        var store = Existing();
        using var release = new ManualResetEventSlim();
        var operation = new HeldEditorSave(store, release);
        await using var executor = new SerializedWorkspacePersistenceExecutor(operation, Clock());
        var shell = new ShellViewModel(store, Clock(), null, executor, executor, new AvaloniaUiDispatcher());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            await shell.Work!.WaitForWorkspaceActionsAsync();
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            var model = Edit(shell.Work);
            model.Title = "Accepted";
            model.RunScheduledAutosave();
            await operation.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (participantEntry) { model.ParticipantToAdd = ParticipantChoice.New; model.NewParticipantLabel = "Pending"; }
            else model.Title = string.Empty;
            Dispatcher.UIThread.RunJobs();
            var category = window.GetVisualDescendants().OfType<ComboBox>().Single(control => AutomationProperties.GetAutomationId(control) == "recurring-category");
            Assert.True(category.Focus());
            model.RunScheduledAutosave();
            release.Set();
            await shell.Work.WaitForWorkspaceActionsAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(category.IsKeyboardFocusWithin);
            Assert.True(model.HasEditor);
            Assert.Contains("Not saved", model.AutosaveStatus, StringComparison.Ordinal);
            Assert.Equal("Accepted", store.ReadRecurringTasks().Single().Title);
            Assert.True(model.IsDirty);
        }
        finally { release.Set(); shell.Work!.Recurring.CancelCommand.Execute(null); window.Close(); }
    }

    [Fact]
    public void SuccessfulEmptyReconciliationClearsOnlyItsOwnPriorFailure()
    {
        var store = Existing();
        var clock = Clock();
        var work = new ProjectCaptureViewModel(store, clock);
        store.FailProcess = true;
        work.Recurring.ReconcileHint();
        Assert.Contains("Could not add scheduled Tasks", work.Message, StringComparison.Ordinal);
        store.FailProcess = false;
        work.Recurring.ReconcileHint();
        Assert.DoesNotContain("Could not add scheduled Tasks", work.Message, StringComparison.Ordinal);
        work.Recurring.ReportMarkdownCopy(false);
        clock.Set(clock.GetUtcNow().AddDays(1));
        work.Recurring.ReconcileHint();
        Assert.Contains("Could not copy", work.Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void NativeSidebarRetainsActualSelectionWhenInvalidAutosaveIsBlockedAndUserStays()
    {
        var store = Existing();
        var shell = new ShellViewModel(store, Clock());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            var model = Edit(shell.Work!);
            model.Title = string.Empty;
            Dispatcher.UIThread.RunJobs();
            var backlog = window.GetVisualDescendants().OfType<RadioButton>().Single(control => AutomationProperties.GetAutomationId(control) == "navigation-backlog");
            var recurring = window.GetVisualDescendants().OfType<RadioButton>().Single(control => AutomationProperties.GetAutomationId(control) == "navigation-recurring");
            var point = backlog.TranslatePoint(new Point(backlog.Bounds.Width / 2, backlog.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left, Avalonia.Input.RawInputModifiers.LeftMouseButton);
            window.MouseUp(point, MouseButton.Left, Avalonia.Input.RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.NeedsDecision);
            Assert.True(recurring.IsChecked);
            Assert.False(backlog.IsChecked);
            Assert.Equal("Recurring", shell.ViewTitle);
            model.StayCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(recurring.IsChecked);
            Assert.False(backlog.IsChecked);
            Assert.Empty(model.Title);
            Assert.Equal("Original", store.ReadRecurringTasks().Single().Title);
        }
        finally { shell.Work!.Recurring.CancelCommand.Execute(null); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutosavedPausedDefinitionsRenderWithoutPermanentActionsAndWithPersistentStatus(bool light)
    {
        var app = Assert.IsType<App>(Application.Current);
        app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var scheduler = new ManualScheduler();
        var store = new RecurringMemoryWorkspace();
        var project = store.CreateProject("Household", "", "home", null);
        var shell = new ShellViewModel(store, Clock());
        var window = new MainWindow(session: null, autosaveScheduler: scheduler, timeProvider: Clock()) { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            var model = shell.Work!.Recurring;
            model.NewCommand.Execute(null);
            model.Title = "Review the week";
            model.Description = "A **Markdown** description.";
            model.Project = model.Projects.Single(item => item.ProjectId == project.Id);
            scheduler.Fire();
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.HasEditor);
            Assert.False(model.IsEnabled);
            Assert.False(store.ReadRecurringTasks().Single().IsEnabled);
            Assert.Empty(store.Read().Tasks);
            Assert.Equal("Saved", model.AutosaveStatus);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button =>
                AutomationProperties.GetAutomationId(button) == "recurring-save" || AutomationProperties.GetName(button) == "Cancel recurring Task changes");
            using var normal = new RenderTargetBitmap(new PixelSize(1120, 600), new Vector(96, 96));
            normal.Render(window);
            model.HasDueDate = true;
            model.DueOffset = "Potato";
            model.RunScheduledAutosave();
            Dispatcher.UIThread.RunJobs();
            var picker = window.GetVisualDescendants().OfType<WholeDayOffsetPicker>().Single();
            var editor = window.GetVisualDescendants().OfType<RecurringTaskEditor>().Single();
            var scroll = editor.FindControl<ScrollViewer>("RecurringEditorScroll")!;
            scroll.Offset = new Vector(0, Math.Max(0, picker.TranslatePoint(default, (Control)scroll.Content!)!.Value.Y - 80));
            picker.GetVisualDescendants().OfType<TextBox>().Single().Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Not saved", model.AutosaveStatus, StringComparison.Ordinal);
            Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Classes.Contains("validation-message") && !string.IsNullOrWhiteSpace(text.Text));
            using var invalid = new RenderTargetBitmap(new PixelSize(1120, 600), new Vector(96, 96));
            invalid.Render(window);
        }
        finally { shell.Work!.Recurring.CancelCommand.Execute(null); window.Close(); app.RequestedThemeVariant = ThemeVariant.Dark; }
    }

    [Theory]
    [InlineData("Restore or reassign the Project, then save to resume.", "Restore or reassign the Project, then enable this recurring Task to resume.")]
    [InlineData("The Project was deleted. Review the Category and save to resume.", "The Project was deleted. Review the Category, then enable this recurring Task to resume.")]
    [InlineData("Choose a smaller due offset.", "Choose a smaller due offset.")]
    public void LegacyFixedPauseGuidanceNamesTheExplicitEnableAction(string stored, string expected)
    {
        var store = Existing();
        var owner = new ProjectCaptureViewModel(store, Clock()).Recurring;
        var definition = store.ReadRecurringTasks().Single() with { PauseReason = stored };
        Assert.Equal(expected, new RecurringTaskRowViewModel(owner, definition, store.Read()).Guidance);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void LifecycleRowsRenderInheritedOverrideStandaloneAndRestoreGuidance(bool light)
    {
        var app = Assert.IsType<App>(Application.Current);
        app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var store = new RecurringMemoryWorkspace();
        var active = store.CreateProject("Household", "", "home", null);
        var archived = store.CreateProject("Archived planning", "", "work", null);
        var binned = store.CreateProject("Removed planning", "", "work", null);
        var definitions = new[]
        {
            store.CreateRecurringTask(new("Inherited Category", "", active.Id, null, [], RecurrenceCadence.Daily, new(2026, 11, 2), null, false)),
            store.CreateRecurringTask(new("Explicit Category override", "", active.Id, "work", [], RecurrenceCadence.Weekly, new(2026, 11, 2), 5, false)),
            store.CreateRecurringTask(new("Standalone definition", "", null, "home", [], RecurrenceCadence.Monthly, new(2026, 11, 2), 0, false)),
            store.CreateRecurringTask(new("Archived Project definition", "", archived.Id, null, [], RecurrenceCadence.Weekly, new(2026, 11, 2), null, false)),
            store.CreateRecurringTask(new("Binned Project definition", "", binned.Id, null, [], RecurrenceCadence.Weekly, new(2026, 11, 2), null, false)) with { PauseReason = "Restore or reassign the Project, then save to resume." },
            store.CreateRecurringTask(new("Deleted Project definition", "", null, "work", [], RecurrenceCadence.Monthly, new(2026, 11, 2), null, false)) with { PauseReason = "The Project was deleted. Review the Category and save to resume." },
        };
        store.ArchiveProject(archived.Id);
        store.MoveProjectToBin(binned.Id);
        var shell = new ShellViewModel(store, Clock());
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            shell.Work!.Recurring.Refresh(store.Read(), definitions, store.ReadProjectBin());
            Dispatcher.UIThread.RunJobs();
            var binRow = shell.Work.Recurring.Definitions.Single(row => row.Title == "Binned Project definition");
            Assert.False(binRow.OpenProjectCommand!.CanExecute(null));
            Assert.Contains("enable this recurring Task", binRow.Guidance, StringComparison.Ordinal);
            Assert.Contains("Category override", shell.Work.Recurring.Definitions.Single(row => row.Title == "Explicit Category override").AccessibleName, StringComparison.Ordinal);
            using var top = new RenderTargetBitmap(new PixelSize(1120, 600), new Vector(96, 96));
            top.Render(window);
            var list = window.GetVisualDescendants().OfType<ScrollViewer>().Single(scroll => scroll.IsEffectivelyVisible && scroll.Content is StackPanel panel && panel.Margin.Right == 32);
            list.Offset = new Vector(0, list.Extent.Height);
            Dispatcher.UIThread.RunJobs();
            using var lower = new RenderTargetBitmap(new PixelSize(1120, 600), new Vector(96, 96));
            lower.Render(window);
        }
        finally { window.Close(); app.RequestedThemeVariant = ThemeVariant.Dark; }
    }

    private sealed class ManualScheduler : IInspectorAutosaveScheduler
    {
        private Action<long>? _callback;
        private long _revision;
        public TimeSpan Delay { get; private set; }
        public int Count { get; private set; }
        public void Schedule(TimeSpan delay, long revision, Action<long> callback) { Delay = delay; _revision = revision; _callback = callback; Count++; }
        public void Cancel() => _callback = null;
        public void Dispose() => Cancel();
        public void Fire() => _callback?.Invoke(_revision);
    }

    private sealed class HeldEditorSave(IWorkspaceWork store, ManualResetEventSlim release, ManualResetEventSlim? releaseProcessing = null) : IWorkspacePersistenceOperation
    {
        private readonly WorkspacePersistenceOperation _inner = new(store);
        private bool _held;
        public RecurringTaskDraft? Captured { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ProcessingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public InspectorSaveResult Execute(InspectorSaveRequest request) => _inner.Execute(request);
        public WorkspaceActionResult ExecuteAction(WorkspaceActionRequest request)
        {
            if (!_held && request.RecurringSaveGeneration is not null)
            {
                _held = true; Captured = request.RecurringDraft; Started.TrySetResult();
                release.Wait(TestContext.Current.CancellationToken);
            }
            if (request.Kind == WorkspaceActionKind.ProcessRecurring && releaseProcessing is not null)
            {
                ProcessingStarted.TrySetResult();
                releaseProcessing.Wait(TestContext.Current.CancellationToken);
            }
            return _inner.ExecuteAction(request);
        }
    }
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }
}
