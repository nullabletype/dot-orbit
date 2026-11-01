using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class RecurringTasksTests
{
    private static FixedTimeProvider Clock() => new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
    private static RecurringTasksViewModel Begin(ProjectCaptureViewModel work)
    {
        var model = work.Recurring;
        model.NewCommand.Execute(null);
        model.Title = "Recurring";
        model.Category = model.Categories.Single(item => item.Id == "home");
        return model;
    }

    [Fact]
    public async Task CreateCopiesFullContentAndDueOffsetIntoIndependentOrdinaryTasks()
    {
        var store = new RecurringMemoryWorkspace();
        var participant = store.CreateParticipant("Person");
        var project = store.CreateProject("Project", "", "work", null);
        var work = new ProjectCaptureViewModel(store, Clock());
        var model = Begin(work);
        model.Project = model.Projects.Single(item => item.ProjectId == project.Id);
        Assert.Null(model.Category!.Id);
        model.Description = "**Details**";
        model.Participants.Single().IsSelected = true;
        model.Cadence = RecurrenceCadence.Monthly;
        model.HasDueDate = true;
        model.DueOffset = "5";
        model.SaveCommand.Execute(null);
        await work.WaitForWorkspaceActionsAsync();

        var definition = Assert.Single(store.ReadRecurringTasks());
        Assert.Equal(project.Id, definition.ProjectId);
        Assert.Null(definition.ExplicitCategoryId);
        Assert.Equal([participant.Id], definition.ParticipantIds);
        Assert.Equal(RecurrenceCadence.Monthly, definition.Cadence);
        var task = Assert.Single(store.Read().Tasks);
        Assert.Equal("Recurring", task.Title);
        Assert.Equal("**Details**", task.Description);
        Assert.Equal(project.Id, task.ProjectId);
        Assert.Null(task.ExplicitCategoryId);
        Assert.Equal([participant.Id], task.Participants);
        Assert.Equal(new DateOnly(2026, 11, 6), task.DueDate);
        Assert.Null(task.TodayLane);
        Assert.False(task.IsComplete);
        Assert.Equal(task.Id, Assert.Single(work.Backlog).Id);
        Assert.Equal(task.Id, Assert.Single(Assert.Single(work.UpcomingGroups).Rows).Task.Id);
        Assert.Empty(work.TodayPlanned);
        Assert.False(model.HasEditor);
        Assert.Contains("Added 1 scheduled Task", model.Announcement, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "2026-11-01", false, "0", "title")]
    [InlineData("Valid", "2026-10-31", false, "0", "today or later")]
    [InlineData("Valid", "invalid", false, "0", "YYYY-MM-DD")]
    [InlineData("Valid", "2026-11-01", true, "-1", "whole-day")]
    [InlineData("Valid", "2026-11-01", true, "1.5", "whole-day")]
    [InlineData("Valid", "9999-12-31", true, "1", "valid due date")]
    public void InvalidDraftRetainsContentAndDoesNotWrite(string title, string first, bool hasDue, string offset, string correction)
    {
        var store = new RecurringMemoryWorkspace();
        var model = Begin(new(store, Clock()));
        model.Title = title;
        model.FirstDate = first;
        model.HasDueDate = hasDue;
        model.DueOffset = offset;
        model.SaveCommand.Execute(null);
        Assert.Empty(store.ReadRecurringTasks());
        Assert.Equal(title, model.Title);
        Assert.True(model.HasEditor);
        Assert.Contains(correction, model.Validation, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, "0", null)]
    [InlineData(true, "0", 0)]
    [InlineData(true, "10", 10)]
    public void NoZeroAndPositiveDueRulesAreDistinct(bool hasDue, string text, int? expected)
    {
        var store = new RecurringMemoryWorkspace();
        var model = Begin(new(store, Clock()));
        model.HasDueDate = hasDue;
        model.DueOffset = text;
        model.SaveCommand.Execute(null);
        Assert.Equal(expected, Assert.Single(store.ReadRecurringTasks()).DueDateOffsetDays);
    }

    [Fact]
    public void StandaloneRequiresExplicitCategoryAndProjectMayOverrideInheritance()
    {
        var store = new RecurringMemoryWorkspace();
        var project = store.CreateProject("Project", "", "work", null);
        var model = Begin(new(store, Clock()));
        model.Category = null;
        model.SaveCommand.Execute(null);
        Assert.Contains("Choose a Category", model.Validation, StringComparison.Ordinal);
        Assert.Empty(store.ReadRecurringTasks());
        model.Project = model.Projects.Single(item => item.ProjectId == project.Id);
        Assert.Null(model.Category!.Id);
        model.Category = model.Categories.Single(item => item.Id == "home");
        model.SaveCommand.Execute(null);
        Assert.Equal("home", Assert.Single(store.ReadRecurringTasks()).ExplicitCategoryId);
    }

    [Fact]
    public async Task DirtyNavigationSupportsStayDiscardAndSaveWithoutLosingDraft()
    {
        var store = new RecurringMemoryWorkspace();
        var shell = new ShellViewModel(store, Clock());
        var navigation = shell.PrimaryNavigation.Single(item => item.Title == "Recurring");
        navigation.SelectCommand.Execute(null);
        var model = Begin(shell.Work!);
        shell.PrimaryNavigation[2].SelectCommand.Execute(null);
        Assert.True(model.NeedsDecision);
        Assert.Equal("Recurring", shell.ViewTitle);
        model.StayCommand.Execute(null);
        Assert.False(model.NeedsDecision);
        Assert.Equal("Recurring", model.Title);
        shell.PrimaryNavigation[2].SelectCommand.Execute(null);
        model.DiscardAndLeaveCommand.Execute(null);
        Assert.Equal("Backlog", shell.ViewTitle);
        Assert.Empty(store.ReadRecurringTasks());
        navigation.SelectCommand.Execute(null);
        model = Begin(shell.Work!);
        shell.PrimaryNavigation[2].SelectCommand.Execute(null);
        model.SaveAndLeaveCommand.Execute(null);
        await WaitUntilAsync(() => shell.ViewTitle == "Backlog");
        Assert.Equal("Backlog", shell.ViewTitle);
        Assert.Single(store.ReadRecurringTasks());
        Assert.False(model.NeedsDecision);
    }

    [Fact]
    public void PauseResumeEditAndConfirmedDeleteLeaveGeneratedTasksIndependent()
    {
        var store = new RecurringMemoryWorkspace();
        var work = new ProjectCaptureViewModel(store, Clock());
        var model = Begin(work);
        model.SaveCommand.Execute(null);
        var original = Assert.Single(store.Read().Tasks);
        model.Definitions.Single().ToggleCommand.Execute(null);
        Assert.False(Assert.Single(store.ReadRecurringTasks()).IsEnabled);
        Assert.Equal("Paused", model.Definitions.Single().State);
        model.Definitions.Single().ToggleCommand.Execute(null);
        Assert.True(Assert.Single(store.ReadRecurringTasks()).IsEnabled);
        model.Definitions.Single().EditCommand.Execute(null);
        model.Title = "Future title";
        model.SaveCommand.Execute(null);
        Assert.Equal("Future title", Assert.Single(store.ReadRecurringTasks()).Title);
        model.Definitions.Single().DeleteCommand.Execute(null);
        Assert.True(model.NeedsDeleteConfirmation);
        Assert.Single(store.ReadRecurringTasks());
        model.CancelDeleteCommand.Execute(null);
        Assert.False(model.NeedsDeleteConfirmation);
        model.Definitions.Single().DeleteCommand.Execute(null);
        model.ConfirmDeleteCommand.Execute(null);
        Assert.Empty(store.ReadRecurringTasks());
        Assert.Equal(original, Assert.Single(store.Read().Tasks));
    }

    [Fact]
    public void EditingExistingPastFirstDateIsAllowedWithoutChangingTheAnchor()
    {
        var store = new RecurringMemoryWorkspace();
        store.CreateRecurringTask(new("Earlier", "", null, "home", [], RecurrenceCadence.Monthly,
            new(2026, 10, 31), null, false));
        var work = new ProjectCaptureViewModel(store, Clock());
        var model = work.Recurring;
        model.Definitions.Single().EditCommand.Execute(null);
        model.Title = "Updated";
        model.SaveCommand.Execute(null);
        Assert.Equal("Updated", Assert.Single(store.ReadRecurringTasks()).Title);
        Assert.Equal(new DateOnly(2026, 10, 31), Assert.Single(store.ReadRecurringTasks()).FirstOccurrenceDate);
        Assert.Empty(model.Validation);
    }

    [Fact]
    public void DateHintsCoalesceRetryAndReactToRolloverZoneAndBackwardClockChanges()
    {
        var clock = Clock();
        var submissions = 0;
        var reconciler = new RecurringDateReconciler(clock, () => { submissions++; return true; });
        reconciler.Hint();
        reconciler.Hint();
        Assert.Equal(1, submissions);
        reconciler.Complete(false);
        reconciler.Hint();
        Assert.Equal(2, submissions);
        reconciler.Complete(true);
        reconciler.Hint();
        Assert.Equal(2, submissions);
        clock.Set(clock.GetUtcNow().AddDays(1));
        reconciler.Hint();
        Assert.Equal(3, submissions);
        reconciler.Complete(true);
        clock.Set(clock.GetUtcNow().AddDays(-1));
        reconciler.Hint();
        Assert.Equal(4, submissions);
        reconciler.Complete(true);
        clock.Set(clock.GetUtcNow(), TimeZoneInfo.CreateCustomTimeZone("Other local zone", TimeSpan.FromHours(1), "Other", "Other"));
        reconciler.Hint();
        Assert.Equal(5, submissions);
    }

    [Fact]
    public void RejectedDateHintAdmissionRetriesAndLaterWakeAfterMidnightUsesNewLocalDate()
    {
        var clock = Clock();
        var accept = false;
        var attempts = 0;
        var reconciler = new RecurringDateReconciler(clock, () => { attempts++; return accept; });
        reconciler.Hint();
        accept = true;
        reconciler.Hint();
        Assert.Equal(2, attempts);
        reconciler.Complete(true);
        clock.Set(clock.GetUtcNow().AddDays(3)); // timer/activation after sleep, with no timer ticks while asleep
        reconciler.Hint();
        reconciler.Hint();
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task CommittedRefreshFailureRetriesOnlyReadAndNeverReplaysCreationOrMaterialisation()
    {
        var store = new RecurringMemoryWorkspace { FailReadAfterMutation = true };
        await using var executor = new SerializedWorkspacePersistenceExecutor(store, Clock());
        var draft = new RecurringTaskDraft("Definition", "", null, "home", [], RecurrenceCadence.Daily, new(2026, 11, 1), 0, true);
        var create = await executor.SubmitActionAsync(new(WorkspaceActionKind.CreateRecurring, RecurringDraft: draft));
        Assert.Equal(WorkspaceActionOutcome.CommittedRefreshFailed, create.Outcome);
        Assert.Single(store.ReadRecurringTasks());
        store.FailReads = false;
        store.FailReadAfterMutation = false;
        Assert.Equal(WorkspaceActionOutcome.Refreshed, (await executor.SubmitActionAsync(new(WorkspaceActionKind.Refresh))).Outcome);
        Assert.Equal(1, store.CreateCalls);
        store.FailReadAfterMutation = true;
        var process = await executor.SubmitActionAsync(new(WorkspaceActionKind.ProcessRecurring));
        Assert.Equal(WorkspaceActionOutcome.CommittedRefreshFailed, process.Outcome);
        Assert.Equal(1, process.GeneratedTaskCount);
        store.FailReads = false;
        store.FailReadAfterMutation = false;
        Assert.Equal(WorkspaceActionOutcome.Refreshed, (await executor.SubmitActionAsync(new(WorkspaceActionKind.Refresh))).Outcome);
        Assert.Single(store.Read().Tasks);
        Assert.Equal(1, store.ProcessCalls);
        await executor.SubmitActionAsync(new(WorkspaceActionKind.ProcessRecurring));
        Assert.Single(store.Read().Tasks);
    }

    [Fact]
    public void FailedReconciliationRetriesWhileUnlockedAndRefreshKeepsDirtyDefinitionDraft()
    {
        var clock = Clock();
        var store = new RecurringMemoryWorkspace();
        store.CreateRecurringTask(new("Due", "", null, "home", [], RecurrenceCadence.Daily, new(2026, 11, 1), null, true));
        store.FailProcess = true;
        var work = new ProjectCaptureViewModel(store, clock);
        var model = Begin(work);
        model.Title = "Unsaved recurring title";
        model.ReconcileHint();
        Assert.Contains("Will retry", model.Announcement, StringComparison.Ordinal);
        Assert.Empty(store.Read().Tasks);
        store.FailProcess = false;
        model.ReconcileHint();
        Assert.Single(work.Backlog);
        Assert.Equal("Unsaved recurring title", model.Title);
        Assert.True(model.IsDirty);
        model.ReconcileHint();
        Assert.Single(store.Read().Tasks);
        Assert.Equal(2, store.ProcessCalls);
    }

    [Fact]
    public void CategoryAndParticipantLifecyclePresentationIncludesDefinitionOnlyReferences()
    {
        var store = new RecurringMemoryWorkspace();
        var participant = store.CreateParticipant("Person");
        store.CreateRecurringTask(new("Paused", "", null, "home", [participant.Id], RecurrenceCadence.Daily, new(2026, 11, 1), null, false));
        var shell = new ShellViewModel(store, Clock());
        shell.Work!.RequestDeleteCategory("home");
        Assert.True(shell.Work.NeedsCategoryReplacement);
        shell.Settings!.Refresh();
        var row = Assert.Single(shell.Settings.Participants);
        Assert.Equal(1, row.UsageCount);
        Assert.Contains("1 recurring definition", row.UsageText, StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecurringNavigationEditorKeyboardValidationAndDeletionWorkAtMinimumSize(bool light)
    {
        var app = Assert.IsType<App>(Application.Current);
        app.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var shell = new ShellViewModel(new RecurringMemoryWorkspace(), Clock());
        var window = new MainWindow { DataContext = shell, Width = 1120, Height = 600 };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var button = Find<Button>(window, "recurring-new");
            Assert.True(button.Focus());
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Dispatcher.UIThread.RunJobs();
            var title = Find<TextBox>(window, "recurring-title");
            Assert.True(title.IsFocused);
            Assert.Equal("Recurring Task title", AutomationProperties.GetName(title));
            Assert.True(title.Bounds.Width > 100);
            title.Text = "Keyboard recurring";
            shell.Work!.Recurring.Category = shell.Work.Recurring.Categories.Single(item => item.Id == "home");
            var first = Find<InspectorDatePicker>(window, "recurring-first-date");
            var firstText = Assert.Single(first.GetVisualDescendants().OfType<TextBox>());
            firstText.Text = "invalid";
            var save = Find<Button>(window, "recurring-save");
            Assert.True(save.Focus());
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Dispatcher.UIThread.RunJobs();
            Assert.True(first.IsKeyboardFocusWithin);
            Assert.Contains("error", first.Classes);
            Assert.Contains("YYYY-MM-DD", AutomationProperties.GetHelpText(first), StringComparison.Ordinal);
            Assert.Contains("YYYY-MM-DD", shell.Work.Recurring.Validation, StringComparison.Ordinal);
            firstText.Text = "2026-11-01";
            save.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Dispatcher.UIThread.RunJobs();
            var row = Assert.Single(shell.Work.Recurring.Definitions);
            var delete = Find<Button>(window, row.DeleteAutomationId);
            delete.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Dispatcher.UIThread.RunJobs();
            Assert.True(Find<Button>(window, "recurring-confirm-delete").IsFocused);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(delete.IsFocused);
            Assert.False(shell.Work.Recurring.NeedsDeleteConfirmation);
            Assert.Null(shell.PrimaryNavigation.Single(item => item.Title == "Recurring").CountText);
            Assert.False(shell.ShowEmpty);
        }
        finally { window.Close(); app.RequestedThemeVariant = ThemeVariant.Dark; }
    }

    [AvaloniaFact]
    public void RecurringMarkdownPreviewActivatesSourceWithKeyboardAndKeepsDraftUncommitted()
    {
        var store = new RecurringMemoryWorkspace();
        var shell = new ShellViewModel(store, Clock());
        var window = new MainWindow { DataContext = shell };
        window.Show();
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
            var model = Begin(shell.Work!);
            model.Description = "**Rendered** [unsafe](javascript:alert(1))";
            Dispatcher.UIThread.RunJobs();
            var preview = Assert.Single(window.GetVisualDescendants().OfType<MarkdownPreviewSurface>(), item => item.IsEffectivelyVisible);
            preview.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Dispatcher.UIThread.RunJobs();
            var source = Assert.Single(window.GetVisualDescendants().OfType<TextBox>(), item => AutomationProperties.GetName(item) == "Recurring Task description, Markdown source");
            Assert.True(source.IsFocused);
            Assert.Equal(model.Description, source.Text);
            Find<TextBox>(window, "recurring-title").Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.True(preview.IsEffectivelyVisible);
            Assert.Empty(store.ReadRecurringTasks());
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<HyperlinkButton>(), link => link.NavigateUri?.Scheme == "javascript");
        }
        finally { shell.Work!.Recurring.CancelCommand.Execute(null); window.Close(); }
    }

    [Fact]
    public async Task RecurrenceCrudAndMaterialisationShareFifoWithInspectorSaves()
    {
        var store = new RecurringMemoryWorkspace();
        var project = store.CreateProject("Before", "", "home", null);
        var draft = new RecurringTaskDraft("Recurring", "", null, "home", [], RecurrenceCadence.Daily, new(2026, 11, 1), null, true);
        var definition = store.CreateRecurringTask(draft);
        using var release = new ManualResetEventSlim();
        var operation = new HeldRecurringOperation(store, release);
        await using var executor = new SerializedWorkspacePersistenceExecutor(operation, Clock());
        var process = executor.SubmitActionAsync(new(WorkspaceActionKind.ProcessRecurring));
        await operation.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var save = executor.SubmitAsync(new(1, 1, InspectorSaveKind.Project, false, false, project.Id,
            "After", "", null, "home", "orchid", null, null, [], [], new("After", "", "", "home", "orchid", "orchid", "")));
        var pause = executor.SubmitActionAsync(new(WorkspaceActionKind.UpdateRecurring,
            RecurringId: definition.Id, RecurringDraft: draft with { IsEnabled = false }));
        var again = executor.SubmitActionAsync(new(WorkspaceActionKind.ProcessRecurring));
        var delete = executor.SubmitActionAsync(new(WorkspaceActionKind.DeleteRecurring, RecurringId: definition.Id));
        try
        {
            Assert.False(save.IsCompleted);
            Assert.False(pause.IsCompleted);
            release.Set();
            Assert.Equal(1, (await process).GeneratedTaskCount);
            Assert.Equal(InspectorSaveOutcome.Saved, (await save).Outcome);
            Assert.Equal(WorkspaceActionOutcome.Committed, (await pause).Outcome);
            Assert.Equal(0, (await again).GeneratedTaskCount);
            Assert.Equal(WorkspaceActionOutcome.Committed, (await delete).Outcome);
            Assert.Equal(["ProcessRecurring", "Inspector", "UpdateRecurring", "ProcessRecurring", "DeleteRecurring"], operation.Order);
            Assert.Equal("After", Assert.Single(store.Read().Projects).Title);
            Assert.Single(store.Read().Tasks);
            Assert.Empty(store.ReadRecurringTasks());
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NavigationWaitsForAcceptedRecurringSaveAndRetainsFailedDraft(bool fail)
    {
        var store = new RecurringMemoryWorkspace { FailCreate = fail };
        using var release = new ManualResetEventSlim();
        var operation = new HeldRecurringOperation(store, release);
        await using var executor = new SerializedWorkspacePersistenceExecutor(operation, Clock());
        var shell = new ShellViewModel(store, Clock(), null, executor, executor, new InlineUiDispatcher());
        shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
        var model = Begin(shell.Work!);
        model.SaveCommand.Execute(null);
        await operation.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
            Assert.Equal("Recurring", shell.ViewTitle);
            release.Set();
            await shell.Work!.WaitForWorkspaceActionsAsync();
            await WaitUntilAsync(() => fail ? model.CanEdit && model.NeedsDecision : shell.ViewTitle == "Backlog");
            if (fail)
            {
                Assert.Equal("Recurring", shell.ViewTitle);
                Assert.True(model.HasEditor);
                Assert.Equal("Recurring", model.Title);
                Assert.Empty(store.ReadRecurringTasks());
                model.DiscardAndLeaveCommand.Execute(null);
                Assert.Equal("Backlog", shell.ViewTitle);
            }
            else
            {
                Assert.False(model.NeedsDecision);
                Assert.Single(store.ReadRecurringTasks());
            }
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task BackgroundReconciliationFailureDoesNotCancelNavigationAfterQueuedDefinitionSave()
    {
        var store = new RecurringMemoryWorkspace { FailProcess = true };
        using var release = new ManualResetEventSlim();
        var operation = new HeldRecurringOperation(store, release);
        await using var executor = new SerializedWorkspacePersistenceExecutor(operation, Clock());
        var shell = new ShellViewModel(store, Clock(), null, executor, executor, new InlineUiDispatcher());
        shell.PrimaryNavigation.Single(item => item.Title == "Recurring").SelectCommand.Execute(null);
        var model = Begin(shell.Work!);
        model.ReconcileHint();
        await operation.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            model.SaveCommand.Execute(null);
            shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
            release.Set();
            await shell.Work!.WaitForWorkspaceActionsAsync();
            await WaitUntilAsync(() => shell.ViewTitle == "Backlog");
            Assert.False(model.NeedsDecision);
            Assert.Single(store.ReadRecurringTasks());
            Assert.Empty(store.Read().Tasks);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void FailedDefinitionSaveRetainsDraftAndRetryCreatesExactlyOneDefinition()
    {
        var store = new RecurringMemoryWorkspace { FailCreate = true };
        var model = Begin(new(store, Clock()));
        model.Description = "Retain this draft";
        model.SaveCommand.Execute(null);
        Assert.True(model.HasEditor);
        Assert.Equal("Retain this draft", model.Description);
        Assert.Contains("Could not save", model.Validation, StringComparison.Ordinal);
        Assert.True(model.CanEdit);
        Assert.Empty(store.ReadRecurringTasks());
        store.FailCreate = false;
        model.SaveCommand.Execute(null);
        Assert.Single(store.ReadRecurringTasks());
        Assert.Single(store.Read().Tasks);
        Assert.False(model.HasEditor);
    }

    private sealed class HeldRecurringOperation(IWorkspaceWork store, ManualResetEventSlim release) : IWorkspacePersistenceOperation
    {
        private readonly WorkspacePersistenceOperation _inner = new(store);
        private bool _held;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Order { get; } = [];
        public InspectorSaveResult Execute(InspectorSaveRequest request)
        {
            Order.Add("Inspector");
            return _inner.Execute(request);
        }
        public WorkspaceActionResult ExecuteAction(WorkspaceActionRequest request)
        {
            Order.Add(request.Kind.ToString());
            if (!_held)
            {
                _held = true;
                Started.TrySetResult();
                release.Wait(TestContext.Current.CancellationToken);
            }
            return _inner.ExecuteAction(request);
        }
    }

    [Fact]
    public void PausedDefinitionRetainsBinnedProjectIdentityAndCategoryReplacementReferences()
    {
        var store = new RecurringMemoryWorkspace();
        var project = store.CreateProject("Binned Project", "", "home", null);
        store.CreateRecurringTask(new("Paused definition", "", project.Id, null, [], RecurrenceCadence.Daily,
            new(2026, 11, 1), null, false));
        store.MoveProjectToBin(project.Id);
        var work = new ProjectCaptureViewModel(store, Clock());
        var row = Assert.Single(work.Recurring.Definitions);
        Assert.Equal("Binned Project", row.ProjectTitle);
        Assert.Equal("Home", row.CategoryName);
        work.RequestDeleteCategory("home");
        Assert.True(work.NeedsCategoryReplacement);
        work.CancelDeleteCategoryCommand.Execute(null);
        row.EditCommand.Execute(null);
        work.Recurring.Title = "Retained draft";
        work.Recurring.SaveCommand.Execute(null);
        Assert.Contains("Restore the Project first", work.Recurring.Validation, StringComparison.Ordinal);
    }

    [Fact]
    public void DirtyFingerprintDistinguishesSeparatorsWithinUserContent()
    {
        var store = new RecurringMemoryWorkspace();
        var work = new ProjectCaptureViewModel(store, Clock());
        var model = Begin(work);
        model.Title = "A\u001fB";
        model.Description = "C";
        model.SaveCommand.Execute(null);
        model.Definitions.Single().EditCommand.Execute(null);
        Assert.False(model.IsDirty);
        model.Title = "A";
        model.Description = "B\u001fC";
        Assert.True(model.IsDirty);
        Assert.True(model.GuardNavigation(() => { }));
    }

    [Fact]
    public void DuePresentationDistinguishesNoRuleOverflowAndExhaustedSchedule()
    {
        var owner = new ProjectCaptureViewModel(new RecurringMemoryWorkspace(), Clock()).Recurring;
        var definition = new RecurringTaskDefinition("id", "Definition", "", null, "home", [], RecurrenceCadence.Daily,
            DateOnly.MaxValue, 1, false, DateOnly.MaxValue, "Choose a smaller due offset.");
        var snapshot = new MemoryWorkspaceWork().Read();
        Assert.Equal("Due date outside supported range", new RecurringTaskRowViewModel(owner, definition, snapshot).DueDate);
        Assert.Equal("No future additions", new RecurringTaskRowViewModel(owner, definition with { NextOccurrenceDate = null }, snapshot).DueDate);
        Assert.Equal("No due date", new RecurringTaskRowViewModel(owner, definition with { DueDateOffsetDays = null }, snapshot).DueDate);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    private static T Find<T>(Window window, string id) where T : Control => Assert.Single(window.GetVisualDescendants().OfType<T>(),
        item => item.IsEffectivelyVisible && AutomationProperties.GetAutomationId(item) == id);
}

internal sealed class RecurringMemoryWorkspace : MemoryWorkspaceWork, IRecurringTasks, IWorkspaceWork
{
    private readonly List<RecurringTaskDefinition> _definitions = [];
    public bool FailReadAfterMutation { get; set; }
    public bool FailReads { get; set; }
    public bool FailProcess { get; set; }
    public bool FailCreate { get; set; }
    public int CreateCalls { get; private set; }
    public int ProcessCalls { get; private set; }
    WorkspaceWorkSnapshot IWorkspaceWork.Read() => FailReads ? throw new InvalidOperationException("Read unavailable.") : Read();
    public IReadOnlyList<RecurringTaskDefinition> ReadRecurringTasks() => _definitions.ToArray();
    public RecurringTaskDefinition CreateRecurringTask(RecurringTaskDraft draft)
    {
        CreateCalls++;
        if (FailCreate) throw new InvalidOperationException("Persistence unavailable.");
        var result = Definition(Guid.NewGuid().ToString("N"), draft, draft.FirstOccurrenceDate);
        _definitions.Add(result);
        if (FailReadAfterMutation) FailReads = true;
        return result;
    }
    public RecurringTaskDefinition UpdateRecurringTask(string id, RecurringTaskDraft draft)
    {
        var index = _definitions.FindIndex(item => item.Id == id);
        var result = Definition(id, draft, _definitions[index].NextOccurrenceDate);
        _definitions[index] = result;
        if (FailReadAfterMutation) FailReads = true;
        return result;
    }
    public void DeleteRecurringTask(string id) => _definitions.RemoveAll(item => item.Id == id);
    public IReadOnlyList<TaskRecord> ProcessDueRecurringTasks()
    {
        ProcessCalls++;
        if (FailProcess) throw new InvalidOperationException("Persistence unavailable.");
        var generated = new List<TaskRecord>();
        var date = new DateOnly(2026, 11, 1);
        foreach (var definition in _definitions.Where(item => item.IsEnabled && item.NextOccurrenceDate <= date).ToArray())
        {
            generated.Add(CreateTaskDraft(definition.ProjectId, definition.Title, definition.Description,
                definition.ExplicitCategoryId, RecurrenceSchedule.DueDate(date, definition.DueDateOffsetDays),
                new(definition.ParticipantIds, []), null));
            _definitions[_definitions.FindIndex(item => item.Id == definition.Id)] = definition with
            {
                NextOccurrenceDate = RecurrenceSchedule.NextOccurrence(definition.FirstOccurrenceDate,
                    definition.Cadence, RecurrenceSchedule.LatestOccurrence(definition.FirstOccurrenceDate, definition.Cadence, date)),
            };
        }
        if (FailReadAfterMutation) FailReads = true;
        return generated;
    }
    private static RecurringTaskDefinition Definition(string id, RecurringTaskDraft draft, DateOnly? next) => new(
        id, draft.Title, draft.Description, draft.ProjectId, draft.ExplicitCategoryId, draft.ParticipantIds,
        draft.Cadence, draft.FirstOccurrenceDate, draft.DueDateOffsetDays, draft.IsEnabled, next, null);
}
