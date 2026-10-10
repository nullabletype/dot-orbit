using DotOrbit.Core.Workspaces;
using DotOrbit.Markdown;
using DotOrbit.Desktop.ViewModels;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class ProjectCaptureViewModelTests
{
    [Fact]
    public async Task RevertingToTheBaselineWhileAWriteIsHeldPersistsTheCorrectiveRevisionBeforeNavigation()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("A", "", "home", null);
        var writer = new ControlledInspectorSaveWriter(work);
        var model = new ProjectCaptureViewModel(work, null, writer);
        model.SelectProject(project.Id);
        model.Title = "B";
        Assert.True(model.RunScheduledAutosave());
        await writer.WaitForSubmissionCountAsync(1);

        model.Title = "A";
        var navigated = false;
        var navigation = model.NavigateAsync(() => navigated = true);
        Assert.False(navigation.IsCompleted);
        writer.CompleteNext();
        await writer.WaitForSubmissionCountAsync(2);
        Assert.Equal("A", writer.Submissions[1].Title);
        Assert.False(navigated);

        writer.CompleteNext();
        await navigation;
        Assert.True(navigated);
        Assert.Equal("A", work.Read().Projects.Single().Title);
    }

    [Fact]
    public async Task WaitForAutosaveRevisionDoesNotCompleteUntilThatHeldRevisionCommits()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Original", "", "home", null);
        var writer = new ControlledInspectorSaveWriter(work);
        var model = new ProjectCaptureViewModel(work, null, writer);
        model.SelectProject(project.Id);
        model.Title = "Held";
        var revision = model.AutosaveRevision;
        Assert.True(model.RunScheduledAutosave());
        await writer.WaitForSubmissionCountAsync(1);

        var wait = model.WaitForAutosaveRevisionAsync(revision);
        Assert.False(wait.IsCompleted);
        writer.CompleteNext();

        Assert.True(await wait);
        Assert.Equal("Held", work.Read().Projects.Single().Title);
    }

    [Fact]
    public async Task ProjectQuickAddClearsOnlyAfterFlushAndCreateSucceedAndStayRetainsFailure()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Project", "", "home", null);
        var writer = new ControlledInspectorSaveWriter(work);
        var model = new ProjectCaptureViewModel(work, null, writer);
        var row = model.Projects.Single();
        model.SelectProject(project.Id);
        model.Title = "Updated";
        Assert.True(model.RunScheduledAutosave());
        await writer.WaitForSubmissionCountAsync(1);
        row.QuickTitle = "Created after flush";
        var created = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ProjectCaptureViewModel.Message)
                && model.Message == "Task created.") created.TrySetResult();
        };

        Assert.True(row.Submit());
        Assert.Equal("Created after flush", row.QuickTitle);
        Assert.Empty(work.Read().Tasks);
        row.QuickTitle = "Next draft";
        writer.CompleteNext();
        await created.Task;
        Assert.Equal("Created after flush", Assert.Single(work.Read().Tasks).Title);
        Assert.Equal("Next draft", row.QuickTitle);

        model.Title = "Failed flush";
        Assert.True(model.RunScheduledAutosave());
        await writer.WaitForSubmissionCountAsync(2);
        row.QuickTitle = "Retained";
        var decision = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ProjectCaptureViewModel.NeedsDecision) && model.NeedsDecision)
                decision.TrySetResult();
        };
        Assert.True(row.Submit());
        writer.FailNext();
        await decision.Task;
        Assert.Equal("Retained", row.QuickTitle);

        model.StayCommand.Execute(null);
        Assert.Equal("Retained", row.QuickTitle);
        Assert.Single(work.Read().Tasks);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProjectQuickAddClearsAfterFailedFlushIsResolvedAndCreationSucceeds(bool retrySave)
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Project", "", "home", null);
        var writer = new ControlledInspectorSaveWriter(work);
        var model = new ProjectCaptureViewModel(work, null, writer);
        var row = model.Projects.Single();
        model.SelectProject(project.Id);
        model.Title = "Changed";
        Assert.True(model.RunScheduledAutosave());
        await writer.WaitForSubmissionCountAsync(1);
        row.QuickTitle = "Created after decision";
        var cleared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        row.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ProjectRowViewModel.QuickTitle)
                && string.IsNullOrEmpty(row.QuickTitle)) cleared.TrySetResult();
        };
        var decision = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ProjectCaptureViewModel.NeedsDecision) && model.NeedsDecision)
                decision.TrySetResult();
        };

        Assert.True(row.Submit());
        writer.FailNext();
        await decision.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        if (retrySave)
        {
            model.SaveAndLeaveCommand.Execute(null);
            await writer.WaitForSubmissionCountAsync(2);
            writer.CompleteNext();
        }
        else
        {
            model.DiscardAndLeaveCommand.Execute(null);
        }

        await cleared.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("Created after decision", Assert.Single(work.Read().Tasks).Title);
        Assert.Empty(row.QuickTitle);
        Assert.Equal(retrySave ? "Changed" : "Project", model.Title);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public async Task BacklogQuickAddPreservesNewerTypingWhileSubmittedTitleAwaitsFlush()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Project", "", "home", null);
        var writer = new ControlledInspectorSaveWriter(work);
        var model = new ProjectCaptureViewModel(work, null, writer);
        model.SelectProject(project.Id);
        model.Title = "Updated";
        Assert.True(model.RunScheduledAutosave());
        await writer.WaitForSubmissionCountAsync(1);
        model.BacklogQuickCategory = model.BacklogCategories.Single(category => category.Id == "home");
        model.BacklogQuickTitle = "First";

        var submission = model.SubmitBacklogQuickAddAsync();
        Assert.False(submission.IsCompleted);
        model.BacklogQuickTitle = "Second";
        writer.CompleteNext();

        Assert.True(await submission);
        Assert.Equal("First", Assert.Single(work.Read().Tasks).Title);
        Assert.Equal("Second", model.BacklogQuickTitle);
    }

    [Fact]
    public async Task ExpiredTextCoalescingTimerSubmitsLatestRevisionImmediatelyAfterHeldWrite()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Original", "", "home", null);
        var writer = new ControlledInspectorSaveWriter(work);
        var model = new ProjectCaptureViewModel(work, null, writer);
        model.SelectProject(project.Id);
        model.Title = "First";
        Assert.True(model.RunScheduledAutosave());
        await writer.WaitForSubmissionCountAsync(1);
        model.Title = "Latest";
        var latestRevision = model.AutosaveRevision;

        Assert.True(model.RunScheduledAutosave(latestRevision));
        writer.CompleteNext();

        await writer.WaitForSubmissionCountAsync(2);
        Assert.Equal(latestRevision, writer.Submissions[1].Revision);
        Assert.Equal("Latest", writer.Submissions[1].Title);
        writer.CompleteNext();
        Assert.True(await model.WaitForAutosaveRevisionAsync(latestRevision));
    }

    [Fact]
    public async Task EditAfterExpiredTimerReceivesItsOwnFullCoalescingInterval()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Original", "", "home", null);
        var writer = new ControlledInspectorSaveWriter(work);
        var model = new ProjectCaptureViewModel(work, null, writer);
        model.SelectProject(project.Id);
        model.Title = "First";
        Assert.True(model.RunScheduledAutosave());
        await writer.WaitForSubmissionCountAsync(1);
        model.Title = "Expired timer revision";
        Assert.True(model.RunScheduledAutosave(model.AutosaveRevision));
        model.Title = "Latest needs its own timer";
        var latestRevision = model.AutosaveRevision;
        var firstSaveObserved = model.WaitForBackgroundSaveObservationAsync();

        writer.CompleteNext();
        await firstSaveObserved;

        Assert.Single(writer.Submissions);
        Assert.True(model.RunScheduledAutosave(latestRevision));
        await writer.WaitForSubmissionCountAsync(2);
        Assert.Equal("Latest needs its own timer", writer.Submissions[1].Title);
        writer.CompleteNext();
        Assert.True(await model.WaitForAutosaveRevisionAsync(latestRevision));
    }


    [Fact]
    public async Task LatestRevisionAloneUpdatesProjectionAndSavedStatusAfterControlledCompletions()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Original", "", "home", null);
        var writer = new ControlledInspectorSaveWriter(work);
        var model = new ProjectCaptureViewModel(work, null, writer);
        model.SelectProject(project.Id);

        model.Title = "First";
        Assert.True(model.RunScheduledAutosave());
        await writer.WaitForSubmissionCountAsync(1);
        model.Title = "Latest";
        var flush = model.FlushPendingAutosaveAsync();

        writer.CompleteNext();
        await writer.WaitForSubmissionCountAsync(2);
        Assert.Equal("Original", model.Projects.Single().Title);
        Assert.Equal("Saving…", model.AutosaveStatus);
        Assert.False(model.HasAutosaveError);

        writer.CompleteNext();
        Assert.True(await flush);
        Assert.Equal("Latest", model.Projects.Single().Title);
        Assert.Equal("Saved", model.AutosaveStatus);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public async Task StaleFailureIsIgnoredAndRetryTargetsOnlyTheLatestRevision()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Original", "", "home", null);
        var writer = new ControlledInspectorSaveWriter(work);
        var model = new ProjectCaptureViewModel(work, null, writer);
        model.SelectProject(project.Id);

        model.Title = "First";
        Assert.True(model.RunScheduledAutosave());
        await writer.WaitForSubmissionCountAsync(1);
        model.Title = "Latest";
        var flush = model.FlushPendingAutosaveAsync();
        writer.FailNext();

        await writer.WaitForSubmissionCountAsync(2);
        Assert.False(model.HasAutosaveError);
        writer.FailNext();
        Assert.False(await flush);
        Assert.True(model.HasAutosaveError);

        model.RetryAutosaveCommand.Execute(null);
        await writer.WaitForSubmissionCountAsync(3);
        Assert.Equal("Latest", writer.Submissions[2].Title);
        writer.CompleteNext();
        Assert.True(await model.FlushPendingAutosaveAsync());
        Assert.Equal("Latest", work.Read().Projects.Single().Title);
        Assert.False(model.HasAutosaveError);
    }

    [Fact]
    public async Task EditingWhileCreationIsInFlightCreatesOnceThenUpdatesTheCreatedIdentity()
    {
        var work = new MemoryWorkspaceWork();
        var writer = new ControlledInspectorSaveWriter(work);
        var model = new ProjectCaptureViewModel(work, null, writer);
        model.NewProjectCommand.Execute(null);
        model.Title = "First";

        Assert.True(model.RunScheduledAutosave());
        await writer.WaitForSubmissionCountAsync(1);
        model.Title = "Latest";
        var flush = model.FlushPendingAutosaveAsync();
        writer.CompleteNext();

        await writer.WaitForSubmissionCountAsync(2);
        Assert.True(writer.Submissions[0].IsCreating);
        Assert.False(writer.Submissions[1].IsCreating);
        Assert.NotNull(writer.Submissions[1].Id);
        writer.CompleteNext();

        Assert.True(await flush);
        var saved = Assert.Single(work.Read().Projects);
        Assert.Equal("Latest", saved.Title);
        Assert.Equal(saved.Id, writer.Submissions[1].Id);
    }

    [Fact]
    public async Task DiscardAfterSupersedingCreationUpdateFailsUsesCommittedCreationSnapshot()
    {
        var work = new MemoryWorkspaceWork();
        var writer = new ControlledInspectorSaveWriter(work);
        var model = new ProjectCaptureViewModel(work, null, writer);
        model.NewProjectCommand.Execute(null);
        model.Title = "Committed";
        Assert.True(model.RunScheduledAutosave());
        await writer.WaitForSubmissionCountAsync(1);
        model.Title = "Failed update";
        var flush = model.FlushPendingAutosaveAsync();
        writer.CompleteNext();
        await writer.WaitForSubmissionCountAsync(2);
        writer.FailNext();
        Assert.False(await flush);

        model.Cancel();

        Assert.Equal("Committed", model.Title);
        Assert.Equal("Committed", Assert.Single(model.Projects).Title);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public async Task NavigationWaitsForTheLatestRevisionBeforeLeaving()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Original", "", "home", null);
        var writer = new ControlledInspectorSaveWriter(work);
        var model = new ProjectCaptureViewModel(work, null, writer);
        model.SelectProject(project.Id);
        model.Title = "First";
        Assert.True(model.RunScheduledAutosave());
        await writer.WaitForSubmissionCountAsync(1);
        model.Title = "Latest";
        var left = false;

        var navigation = model.NavigateAsync(() => left = true);
        writer.CompleteNext();
        await writer.WaitForSubmissionCountAsync(2);
        Assert.False(left);

        writer.CompleteNext();
        await navigation;
        Assert.True(left);
        Assert.False(model.HasInspector);
        Assert.Equal("Latest", work.Read().Projects.Single().Title);
    }

    [Fact]
    public async Task ImmediateRowActionWaitsForTheLatestRevision()
    {
        var work = new MemoryWorkspaceWork();
        var edited = work.CreateStandaloneTask("Edited", "", "home", null);
        var changed = work.CreateStandaloneTask("Changed", "", "home", null);
        var writer = new ControlledInspectorSaveWriter(work);
        var model = new ProjectCaptureViewModel(work, null, writer);
        model.SelectTask(edited.Id);
        model.Title = "First";
        Assert.True(model.RunScheduledAutosave());
        await writer.WaitForSubmissionCountAsync(1);
        model.Title = "Latest";
        var actionCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ProjectCaptureViewModel.TodayAnnouncement)
                && !string.IsNullOrEmpty(model.TodayAnnouncement)) actionCompleted.TrySetResult();
        };

        model.ToggleToday(changed.Id);
        writer.CompleteNext();
        await writer.WaitForSubmissionCountAsync(2);
        Assert.Null(work.Read().Tasks.Single(task => task.Id == changed.Id).TodayLane);

        writer.CompleteNext();
        await actionCompleted.Task;
        Assert.Equal(TodayLane.Planned, work.Read().Tasks.Single(task => task.Id == changed.Id).TodayLane);
        Assert.Equal("Latest", work.Read().Tasks.Single(task => task.Id == edited.Id).Title);
    }

    [Fact]
    public async Task SupersedingEditReusesParticipantCreatedByInFlightRevision()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Original", "", "home", null);
        var writer = new ControlledInspectorSaveWriter(work);
        var model = new ProjectCaptureViewModel(work, null, writer);
        model.SelectTask(task.Id);
        model.ParticipantToAdd = ParticipantChoice.New;
        model.NewParticipantLabel = "AB";
        model.AddNewParticipantCommand.Execute(null);
        await writer.WaitForSubmissionCountAsync(1);
        model.Title = "Latest";
        var flush = model.FlushPendingAutosaveAsync();

        writer.CompleteNext();
        await writer.WaitForSubmissionCountAsync(2);
        var participant = Assert.Single(work.Read().Participants);
        Assert.Empty(writer.Submissions[1].NewParticipantLabels);
        Assert.Equal([participant.Id], writer.Submissions[1].ParticipantIds);
        writer.CompleteNext();

        Assert.True(await flush);
        Assert.Single(work.Read().Participants);
        Assert.Equal("Latest", work.Read().Tasks.Single().Title);
    }

    [Fact]
    public void MoveToBinFlushesPendingTaskEditsClosesInspectorAndExcludesEveryProjection()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Private note", "Original", "home", new DateOnly(2026, 10, 5));
        work.SetTaskTodayLane(task.Id, TodayLane.Planned);
        var model = new ProjectCaptureViewModel(work,
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero)));
        model.SelectTask(task.Id);
        model.Description = "Saved before removal";

        model.MoveTaskToBinCommand.Execute(null);

        Assert.False(model.HasInspector);
        Assert.Empty(work.Read().Tasks);
        Assert.Empty(model.Backlog);
        Assert.Empty(model.TodayPlanned);
        Assert.Empty(model.UpcomingGroups);
        Assert.Empty(model.Completed);
        Assert.Empty(model.Archived);
        var removed = Assert.Single(model.Bin);
        Assert.Equal("Saved before removal", Assert.IsType<TaskRecord>(removed.Task).Description);
        Assert.Equal("Private note", removed.Title);
        Assert.Contains("restore", model.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidPendingTaskEditPreventsMoveToBinAndRetainsInspector()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Keep me", "", "home", null);
        var model = new ProjectCaptureViewModel(work);
        model.SelectTask(task.Id);
        model.Title = " ";

        model.MoveTaskToBinCommand.Execute(null);

        Assert.True(model.HasInspector);
        Assert.True(model.NeedsDecision);
        Assert.Empty(model.Bin);
        Assert.Single(work.Read().Tasks);
    }

    [Fact]
    public void BinOrdersTasksByRemovalInstantNewestFirstAndRestoresWithFeedback()
    {
        var work = new MemoryWorkspaceWork();
        var first = work.CreateStandaloneTask("First removed", "", "home", null);
        var second = work.CreateStandaloneTask("Second removed", "", "work", null);
        work.CurrentDate = new DateOnly(2026, 10, 4);
        work.MoveTaskToBin(first.Id);
        work.CurrentDate = new DateOnly(2026, 10, 5);
        work.MoveTaskToBin(second.Id);
        var model = new ProjectCaptureViewModel(work,
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));

        Assert.Equal(["Second removed", "First removed"], model.Bin.Select(row => row.Title));
        Assert.Equal("Removed 5 Oct 2026, 14:00", model.Bin[0].RemovedText);
        Assert.Equal("Restore Second removed from Bin", model.Bin[0].RestoreAccessibleName);

        model.Bin[0].RestoreCommand.Execute(null);

        Assert.Equal("Task restored from Bin.", model.Message);
        Assert.Equal("Second removed", Assert.Single(work.Read().Tasks).Title);
    }

    [Fact]
    public void BinnedParentKeepsTaskBinContextAndDisablesIndependentRestore()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTask(project.Id, "Dig");
        work.MoveTaskToBin(task.Id);
        work.MarkProjectBinned(project.Id);

        var model = new ProjectCaptureViewModel(work);

        var row = Assert.Single(model.Bin);
        Assert.Equal("Garden", row.ContextText);
        Assert.False(row.CanRestore);
        Assert.True(row.IsRestoreBlocked);
        Assert.Equal("Restore the parent Project from Bin before restoring this Task.", row.RestoreBlockedText);
        row.RestoreCommand.Execute(null);
        Assert.Equal(task.Id, Assert.IsType<TaskRecord>(Assert.Single(model.Bin).Task).Id);
    }

    [Fact]
    public void BinWriteFailuresPreserveTheTaskAndExplainThatNothingChanged()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Keep me", "", "home", null);
        var model = new ProjectCaptureViewModel(work);
        model.SelectTask(task.Id);
        work.FailWrites = true;

        model.MoveTaskToBinCommand.Execute(null);

        Assert.True(model.HasInspector);
        Assert.Equal(task.Id, Assert.Single(work.Read().Tasks).Id);
        Assert.Empty(model.Bin);
        Assert.Equal("Could not move the Task to Bin. No changes were made.", model.Message);

        work.FailWrites = false;
        work.MoveTaskToBin(task.Id);
        model = new ProjectCaptureViewModel(work);
        work.FailWrites = true;

        model.Bin.Single().RestoreCommand.Execute(null);

        Assert.Empty(work.Read().Tasks);
        Assert.Equal(task.Id, Assert.IsType<TaskRecord>(Assert.Single(model.Bin).Task).Id);
        Assert.Equal("Could not restore the Task from Bin. No changes were made.", model.Message);
    }

    [Fact]
    public void MoveProjectToBinPresentsOneAggregateRowAndRestoresAllTasks()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        work.CreateTask(project.Id, "Dig");
        work.CreateTask(project.Id, "Plant");
        var model = new ProjectCaptureViewModel(work);
        model.SelectProject(project.Id);

        model.MoveProjectToBinCommand.Execute(null);

        Assert.False(model.HasInspector);
        Assert.Empty(model.Projects);
        Assert.Empty(model.Backlog);
        var row = Assert.Single(model.Bin);
        Assert.True(row.IsProject);
        Assert.Equal("Garden", row.Title);
        Assert.Equal("Home · 2 Tasks", row.ContextText);
        Assert.Equal("Restore Garden Project and its Tasks from Bin", row.RestoreAccessibleName);

        row.RestoreCommand.Execute(null);

        Assert.Equal("Project and its Tasks restored from Bin.", model.Message);
        Assert.Equal(2, work.Read().Tasks.Count);
        Assert.Single(work.Read().Projects);
    }

    [Fact]
    public void BinOrdersProjectAggregatesAndIndividualTasksTogetherByRemovalInstant()
    {
        var work = new MemoryWorkspaceWork();
        var standalone = work.CreateStandaloneTask("Old task", "", "home", null);
        var project = work.CreateProject("New project", "", "work", null);
        work.CreateTask(project.Id, "Child");
        work.CurrentDate = new DateOnly(2026, 10, 4);
        work.MoveTaskToBin(standalone.Id);
        work.CurrentDate = new DateOnly(2026, 10, 5);
        work.MoveProjectToBin(project.Id);

        var model = new ProjectCaptureViewModel(work, new FixedTimeProvider(
            new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));

        Assert.Equal(["New project", "Old task"], model.Bin.Select(item => item.Title));
        Assert.True(model.Bin[0].IsProject);
        Assert.True(model.Bin[1].IsTask);
    }

    [Fact]
    public void EmptyBinRequiresExactCountConfirmationExplainsRetentionAndSupportsCancel()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        work.CreateTask(project.Id, "Child");
        var standalone = work.CreateStandaloneTask("Loose", "", "work", null);
        work.MoveProjectToBin(project.Id);
        work.MoveTaskToBin(standalone.Id);
        var model = new ProjectCaptureViewModel(work);

        model.RequestEmptyBinCommand.Execute(null);

        Assert.True(model.NeedsEmptyBinConfirmation);
        Assert.Contains("1 Project and 2 Tasks", model.EmptyBinConfirmationBody, StringComparison.Ordinal);
        Assert.Contains("validated encrypted recovery point", model.EmptyBinConfirmationBody, StringComparison.Ordinal);
        Assert.Contains("retained separately", model.EmptyBinConfirmationBody, StringComparison.Ordinal);
        Assert.Contains("until normal pruning removes them", model.EmptyBinConfirmationBody, StringComparison.Ordinal);
        Assert.Contains("not forensic erasure", model.EmptyBinConfirmationBody, StringComparison.Ordinal);
        model.CancelEmptyBinCommand.Execute(null);
        Assert.False(model.NeedsEmptyBinConfirmation);
        Assert.Equal(2, model.Bin.Count);

        model.RequestEmptyBinCommand.Execute(null);
        model.ConfirmEmptyBinCommand.Execute(null);

        Assert.False(model.NeedsEmptyBinConfirmation);
        Assert.Empty(model.Bin);
        Assert.Contains("validated encrypted recovery point", model.Message, StringComparison.Ordinal);
        Assert.Equal("navigation-bin", model.BinFocusAutomationId);
    }

    [Fact]
    public void EmptyBinFailureClosesConfirmationPreservesRowsAndReportsAccessibleStatus()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Keep", "", "home", null);
        work.MoveTaskToBin(task.Id);
        var model = new ProjectCaptureViewModel(work);
        model.RequestEmptyBinCommand.Execute(null);
        work.FailWrites = true;

        model.ConfirmEmptyBinCommand.Execute(null);

        Assert.False(model.NeedsEmptyBinConfirmation);
        Assert.Equal(task.Id, Assert.IsType<TaskRecord>(Assert.Single(model.Bin).Task).Id);
        Assert.Equal("Could not empty Bin. Nothing was deleted.", model.Message);
        Assert.Equal("empty-bin", model.BinFocusAutomationId);
    }

    [Fact]
    public void TextAutosaveUsesSixHundredMillisecondsAndPersistsOnlyTheLatestRevision()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Original", "", "home", null);
        var model = new ProjectCaptureViewModel(work);
        var requests = 0;
        model.AutosaveRequested += (_, _) => requests++;
        model.SelectProject(project.Id);

        model.Title = "First";
        model.Title = "Second";
        model.Description = "Latest description";

        Assert.Equal(TimeSpan.FromMilliseconds(600), ProjectCaptureViewModel.AutosaveDelay);
        Assert.Equal(3, requests);
        Assert.Equal("Saving soon…", model.AutosaveStatus);
        Assert.Equal("Original", Assert.Single(work.Read().Projects).Title);
        Assert.True(model.RunScheduledAutosave());
        var saved = Assert.Single(work.Read().Projects);
        Assert.Equal("Second", saved.Title);
        Assert.Equal("Latest description", saved.Description);
        Assert.Equal("Saved", model.AutosaveStatus);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void RecordLocalTaskAutosaveUpdatesRowsWithoutResettingUnrelatedProjections()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTask(project.Id, "Dig");
        var unrelatedProject = work.CreateProject("Office", "", "work", null);
        var unrelatedTask = work.CreateTask(unrelatedProject.Id, "File");
        work.SetTaskTodayLane(task.Id, TodayLane.Planned);
        var model = new ProjectCaptureViewModel(work);
        var projectRow = model.Projects.Single(row => row.Id == project.Id);
        var taskRow = model.Backlog.Single(row => row.Id == task.Id);
        var unrelatedProjectRow = model.Projects.Single(row => row.Id == unrelatedProject.Id);
        var unrelatedTaskRow = model.Backlog.Single(row => row.Id == unrelatedTask.Id);
        var homeGroup = model.CategoryGroups.Single(group => group.Id == "home");
        var todayRow = Assert.Single(model.TodayPlanned);
        var todayNotifications = new List<string?>();
        todayRow.PropertyChanged += (_, args) => todayNotifications.Add(args.PropertyName);
        homeGroup.IsExpanded = false;
        var projectsChanged = 0;
        var backlogChanged = 0;
        var categoryGroupsChanged = 0;
        var todayChanged = 0;
        model.Projects.CollectionChanged += (_, _) => projectsChanged++;
        model.Backlog.CollectionChanged += (_, _) => backlogChanged++;
        model.CategoryGroups.CollectionChanged += (_, _) => categoryGroupsChanged++;
        model.TodayPlanned.CollectionChanged += (_, _) => todayChanged++;
        var readsBefore = work.ReadCount;
        var taskBinReadsBefore = work.TaskBinReadCount;
        var projectBinReadsBefore = work.ProjectBinReadCount;

        model.SelectTask(task.Id);
        model.Title = "Dig deeply";
        model.Description = "Keep the edit local.";
        Assert.True(model.RunScheduledAutosave());

        Assert.Same(projectRow, model.Projects.Single(row => row.Id == project.Id));
        Assert.Same(taskRow, model.Backlog.Single(row => row.Id == task.Id));
        Assert.Same(todayRow, model.TodayPlanned.Single());
        Assert.Same(taskRow, todayRow.Task);
        Assert.Equal("Dig deeply", taskRow.Title);
        Assert.Equal("Reorder Dig deeply in Planned", todayRow.ReorderAccessibleName);
        Assert.Contains(nameof(TodayTaskRowViewModel.ReorderAccessibleName), todayNotifications);
        Assert.Same(unrelatedProjectRow, model.Projects.Single(row => row.Id == unrelatedProject.Id));
        Assert.Same(unrelatedTaskRow, model.Backlog.Single(row => row.Id == unrelatedTask.Id));
        Assert.Same(homeGroup, model.CategoryGroups.Single(group => group.Id == "home"));
        Assert.False(homeGroup.IsExpanded);
        Assert.Equal(0, projectsChanged);
        Assert.Equal(0, backlogChanged);
        Assert.Equal(0, categoryGroupsChanged);
        Assert.Equal(0, todayChanged);
        Assert.Equal(readsBefore, work.ReadCount);
        Assert.Equal(taskBinReadsBefore, work.TaskBinReadCount);
        Assert.Equal(projectBinReadsBefore, work.ProjectBinReadCount);
    }

    [Fact]
    public void ProjectCategoryTitleAndColourEditsPreserveRowsGroupsAndDisclosure()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var inherited = work.CreateTask(project.Id, "Inherited");
        var overridden = work.CreateTask(project.Id, "Override");
        work.UpdateTask(overridden.Id, overridden.Title, "", "work", null);
        var unrelated = work.CreateProject("Office", "", "work", null);
        var model = new ProjectCaptureViewModel(work);
        var projectRow = model.Projects.Single(row => row.Id == project.Id);
        var inheritedRow = model.Backlog.Single(row => row.Id == inherited.Id);
        var overriddenRow = model.Backlog.Single(row => row.Id == overridden.Id);
        var unrelatedRow = model.Projects.Single(row => row.Id == unrelated.Id);
        var homeGroup = model.CategoryGroups.Single(group => group.Id == "home");
        var workGroup = model.CategoryGroups.Single(group => group.Id == "work");
        var unrelatedCategoryWrapper = workGroup.Projects.Single(row => row.Project.Id == unrelated.Id);
        homeGroup.IsExpanded = false;
        workGroup.IsExpanded = false;
        var projectsChanged = 0;
        var projectTasksChanged = 0;
        var categoryGroupsChanged = 0;
        model.Projects.CollectionChanged += (_, _) => projectsChanged++;
        projectRow.Tasks.CollectionChanged += (_, _) => projectTasksChanged++;
        model.CategoryGroups.CollectionChanged += (_, _) => categoryGroupsChanged++;

        model.SelectProject(project.Id);
        model.Category = model.Categories.Single(category => category.Id == "work");
        model.ProjectColourKey = "ocean";
        model.Title = "Kitchen garden";
        Assert.True(model.RunScheduledAutosave());

        Assert.Same(projectRow, model.Projects.Single(row => row.Id == project.Id));
        Assert.Same(unrelatedRow, model.Projects.Single(row => row.Id == unrelated.Id));
        Assert.Same(inheritedRow, projectRow.Tasks.Single(row => row.Id == inherited.Id));
        Assert.Same(overriddenRow, projectRow.Tasks.Single(row => row.Id == overridden.Id));
        Assert.Equal("Kitchen garden", projectRow.Title);
        Assert.Equal("Work", inheritedRow.CategoryName);
        Assert.False(inheritedRow.HasCategoryOverride);
        Assert.Equal("Work", overriddenRow.CategoryName);
        Assert.True(overriddenRow.HasCategoryOverride);
        Assert.Equal("ocean", inheritedRow.ProjectColourKey);
        Assert.Same(homeGroup, model.CategoryGroups.Single(group => group.Id == "home"));
        Assert.Same(workGroup, model.CategoryGroups.Single(group => group.Id == "work"));
        Assert.False(homeGroup.IsExpanded);
        Assert.False(workGroup.IsExpanded);
        Assert.DoesNotContain(homeGroup.Projects, row => row.Project.Id == project.Id);
        Assert.Contains(workGroup.Projects, row => ReferenceEquals(row.Project, projectRow));
        Assert.Same(unrelatedCategoryWrapper,
            workGroup.Projects.Single(row => row.Project.Id == unrelated.Id));
        Assert.Equal(0, projectsChanged);
        Assert.Equal(0, projectTasksChanged);
        Assert.Equal(0, categoryGroupsChanged);
    }

    [Fact]
    public void StandaloneCategoryAndDueDateEditsTouchOnlyAffectedStableGroups()
    {
        var work = new MemoryWorkspaceWork();
        var edited = work.CreateStandaloneTask("Edited", "", "home", new DateOnly(2026, 10, 5));
        var other = work.CreateStandaloneTask("Other", "", "work", new DateOnly(2026, 10, 6));
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero));
        var shell = new ShellViewModel(work, time);
        var model = shell.Work!;
        var taskRow = model.Backlog.Single(row => row.Id == edited.Id);
        var otherRow = model.Backlog.Single(row => row.Id == other.Id);
        var homeGroup = model.CategoryGroups.Single(group => group.Id == "home");
        var workGroup = model.CategoryGroups.Single(group => group.Id == "work");
        var unrelatedCategoryWrapper = workGroup.StandaloneTasks.Single(row => row.Task.Id == other.Id);
        var unaffectedUpcoming = model.UpcomingGroups.Single(group => group.Heading == "Tuesday, 6 October 2026");
        var unaffectedUpcomingRow = Assert.Single(unaffectedUpcoming.Rows);
        homeGroup.IsExpanded = false;
        workGroup.IsExpanded = false;
        var backlogChanged = 0;
        var categoryGroupsChanged = 0;
        model.Backlog.CollectionChanged += (_, _) => backlogChanged++;
        model.CategoryGroups.CollectionChanged += (_, _) => categoryGroupsChanged++;

        model.SelectTask(edited.Id);
        model.Category = model.Categories.Single(category => category.Id == "work");
        model.Date = "2026-10-06";
        Assert.True(model.RunScheduledAutosave());

        Assert.Same(taskRow, model.Backlog.Single(row => row.Id == edited.Id));
        Assert.Same(otherRow, model.Backlog.Single(row => row.Id == other.Id));
        Assert.Same(homeGroup, model.CategoryGroups.Single(group => group.Id == "home"));
        Assert.Same(workGroup, model.CategoryGroups.Single(group => group.Id == "work"));
        Assert.False(homeGroup.IsExpanded);
        Assert.False(workGroup.IsExpanded);
        Assert.DoesNotContain(homeGroup.StandaloneTasks, row => row.Task.Id == edited.Id);
        Assert.Contains(workGroup.StandaloneTasks, row => ReferenceEquals(row.Task, taskRow));
        Assert.Same(unrelatedCategoryWrapper,
            workGroup.StandaloneTasks.Single(row => row.Task.Id == other.Id));
        Assert.Same(unaffectedUpcoming,
            model.UpcomingGroups.Single(group => group.Heading == "Tuesday, 6 October 2026"));
        Assert.Same(unaffectedUpcomingRow,
            model.UpcomingGroups.Single(group => group.Heading == "Tuesday, 6 October 2026")
                .Rows.Single(row => row.Task.Id == other.Id));
        Assert.Equal([other.Id, edited.Id],
            unaffectedUpcoming.Rows.Select(row => row.Task.Id));
        Assert.Equal("2", shell.PrimaryNavigation.Single(item => item.Title == "Upcoming").CountText);
        Assert.Equal(0, backlogChanged);
        Assert.Equal(0, categoryGroupsChanged);
    }

    [Fact]
    public void ParticipantRemovalSavesImmediatelyWithoutResettingWorkProjections()
    {
        var work = new MemoryWorkspaceWork();
        var participant = work.CreateParticipant("SD");
        var task = work.CreateStandaloneTask("Call", "", "home", null,
            new ParticipantDraftChange([participant.Id], []));
        var model = new ProjectCaptureViewModel(work);
        var taskRow = model.Backlog.Single(row => row.Id == task.Id);
        var backlogChanged = 0;
        var categoryGroupsChanged = 0;
        model.Backlog.CollectionChanged += (_, _) => backlogChanged++;
        model.CategoryGroups.CollectionChanged += (_, _) => categoryGroupsChanged++;
        model.SelectTask(task.Id);
        var selected = Assert.Single(model.SelectedParticipants);
        var writesBefore = work.WriteCount;

        selected.RemoveCommand.Execute(null);

        Assert.Equal(writesBefore + 1, work.WriteCount);
        Assert.Empty(work.Read().Tasks.Single().Participants);
        Assert.False(model.IsDirty);
        Assert.Same(taskRow, model.Backlog.Single());
        Assert.Equal(0, backlogChanged);
        Assert.Equal(0, categoryGroupsChanged);
    }

    [Fact]
    public void NewParticipantCommitReconcilesPersistedIdentityWithoutResettingChoices()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Call", "", "home", null);
        var existing = work.CreateParticipant("SD");
        var model = new ProjectCaptureViewModel(work);
        model.SelectTask(task.Id);
        var existingChoice = model.AvailableParticipants.Single(choice => choice.Id == existing.Id);
        var choiceActions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        model.AvailableParticipants.CollectionChanged += (_, args) => choiceActions.Add(args.Action);

        model.ParticipantToAdd = model.AvailableParticipants.Single(choice => choice.IsNew);
        model.NewParticipantLabel = "AB";
        model.AddNewParticipantCommand.Execute(null);

        var savedParticipant = work.Read().Participants.Single(participant => participant.Label == "AB");
        var selected = Assert.Single(model.SelectedParticipants);
        Assert.Equal(savedParticipant.Id, selected.Id);
        Assert.Equal("AB", selected.Label);
        Assert.Same(existingChoice,
            model.AvailableParticipants.Single(choice => choice.Id == existing.Id));
        Assert.Contains(model.AvailableParticipants, choice => choice.Id == savedParticipant.Id);
        Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, choiceActions);
        Assert.Equal("participant-picker", model.ParticipantFocusAutomationId);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void ArchivedTaskEditReconcilesActiveSearchWithoutReloadingUnrelatedProjections()
    {
        var work = new MemoryWorkspaceWork();
        var edited = work.CreateStandaloneTask("Tulips", "", "home", null);
        var unrelated = work.CreateStandaloneTask("Tulip notes", "", "home", null);
        work.SetCompletion(edited.Id, new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 29));
        work.SetCompletion(unrelated.Id, new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 29));
        work.ArchiveTask(edited.Id);
        work.ArchiveTask(unrelated.Id);
        var model = new ProjectCaptureViewModel(work);
        model.ArchiveSearchText = "tul";
        var group = Assert.Single(model.ArchiveSearchGroups);
        var unrelatedResult = model.ArchiveSearchResults.Single(row => row.Result.Id == unrelated.Id);
        var archivedTaskRow = model.Archived.Single(row => row.Task.Id == edited.Id).Task;
        var archivedChanged = 0;
        var archiveGroupsChanged = 0;
        var categoryGroupsChanged = 0;
        model.Archived.CollectionChanged += (_, _) => archivedChanged++;
        model.ArchiveGroups.CollectionChanged += (_, _) => archiveGroupsChanged++;
        model.CategoryGroups.CollectionChanged += (_, _) => categoryGroupsChanged++;
        var readsBefore = work.ReadCount;
        var taskBinReadsBefore = work.TaskBinReadCount;
        var projectBinReadsBefore = work.ProjectBinReadCount;

        model.SelectTask(edited.Id);
        model.Title = "Roses";
        Assert.True(model.RunScheduledAutosave());

        Assert.Equal("Roses", archivedTaskRow.Title);
        Assert.Single(model.ArchiveSearchResults);
        Assert.Same(unrelatedResult, Assert.Single(model.ArchiveSearchResults));
        Assert.Same(group, Assert.Single(model.ArchiveSearchGroups));
        Assert.Equal(0, archivedChanged);
        Assert.Equal(0, archiveGroupsChanged);
        Assert.Equal(0, categoryGroupsChanged);
        Assert.Equal(readsBefore, work.ReadCount);
        Assert.Equal(taskBinReadsBefore, work.TaskBinReadCount);
        Assert.Equal(projectBinReadsBefore, work.ProjectBinReadCount);
    }

    [Fact]
    public void FirstValidAutosaveCreatesWorkAndNewParticipantAtomically()
    {
        var work = new MemoryWorkspaceWork();
        var model = new ProjectCaptureViewModel(work);
        model.NewProjectCommand.Execute(null);
        Assert.True(model.RunScheduledAutosave());
        Assert.Empty(work.Read().Projects);
        model.Title = "Garden";
        Assert.True(model.RunScheduledAutosave());
        Assert.Equal("Garden", Assert.Single(work.Read().Projects).Title);
        Assert.False(model.ShowExplicitInspectorActions);

        model.NewTaskCommand.Execute(null);
        model.Title = "Call";
        model.ParticipantToAdd = Assert.IsType<ParticipantChoice>(model.AvailableParticipants.Last());
        Assert.True(model.ShowNewParticipantEntry);
        model.NewParticipantLabel = "SD";
        model.AddNewParticipantCommand.Execute(null);
        Assert.Empty(work.Read().Tasks);
        model.Category = model.Categories.Single(item => item.Id == "home");

        var snapshot = work.Read();
        var task = Assert.Single(snapshot.Tasks);
        var participant = Assert.Single(snapshot.Participants);
        Assert.Equal("SD", participant.Label);
        Assert.Equal([participant.Id], task.Participants);
    }

    [Fact]
    public void CategoryEditingRetainsExplicitSaveAndCancel()
    {
        var work = new MemoryWorkspaceWork();
        var model = new ProjectCaptureViewModel(work);
        model.SelectCategory("home");
        model.Title = "House";
        model.SelectedCategoryColour = model.CategoryColourChoices.Single(choice => choice.Key == "teal");

        Assert.True(model.ShowExplicitInspectorActions);
        Assert.False(model.ShowAutosaveStatus);
        Assert.True(model.IsDirty);
        Assert.Equal("Home", work.Read().Categories.Single(item => item.Id == "home").Name);
        Assert.Equal("orchid", work.Read().Categories.Single(item => item.Id == "home").ColourKey);
        model.Cancel();
        Assert.Equal("Home", model.Title);
        Assert.Equal("orchid", model.CategoryColourKey);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void CategoryColourUsesVisibleDefaultSavesExplicitlyAndRefreshesEveryReference()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        work.CreateTask(project.Id, "Inherited");
        var overridden = work.CreateTask(project.Id, "Override");
        work.UpdateTask(overridden.Id, overridden.Title, "", "home", null);
        work.CreateStandaloneTask("Standalone", "", "home", null);
        var model = new ProjectCaptureViewModel(work);

        model.NewCategoryCommand.Execute(null);
        Assert.Equal("indigo", model.CategoryColourKey);
        Assert.Equal("Category name", model.CategoryPreviewName);
        model.Title = "Errands";
        model.SelectedCategoryColour = model.CategoryColourChoices.Single(choice => choice.Key == "tangerine");
        Assert.Equal("Errands", model.CategoryPreviewName);
        Assert.True(model.Save());
        Assert.Equal("tangerine", work.Read().Categories.Single(category => category.Name == "Errands").ColourKey);

        model.SelectCategory("home");
        model.Title = "House";
        model.SelectedCategoryColour = model.CategoryColourChoices.Single(choice => choice.Key == "ocean");
        Assert.True(model.Save());

        Assert.All(model.Backlog.Where(task => task.CategoryName == "House"), task =>
        {
            Assert.Equal("ocean", task.CategoryColourKey);
            Assert.Contains("Category", task.AccessibleName, StringComparison.Ordinal);
        });
        Assert.Equal("ocean", model.Projects.Single(row => row.Id == project.Id).CategoryColourKey);
        Assert.Equal("ocean", model.CategoryGroups.Single(group => group.Id == "home").ColourKey);
    }

    [Fact]
    public void ProjectColourUsesADistinctDefaultAndPersistsExplicitEdits()
    {
        var work = new MemoryWorkspaceWork();
        var model = new ProjectCaptureViewModel(work);

        model.NewProjectCommand.Execute(null);
        Assert.Equal("lime", model.ProjectColourKey);
        Assert.Equal("Project name", model.ProjectPreviewName);
        model.Title = "Garden";
        model.SelectedProjectColour = model.CategoryColourChoices.Single(choice => choice.Key == "coral");
        Assert.Equal("Garden", model.ProjectPreviewName);
        Assert.True(model.Save());

        var project = Assert.Single(work.Read().Projects);
        Assert.Equal("coral", project.ColourKey);
        Assert.Equal("coral", model.Projects.Single().ColourKey);

        model.SelectProject(project.Id);
        model.SelectedProjectColour = model.CategoryColourChoices.Single(choice => choice.Key == "cyan");
        Assert.Equal("cyan", work.Read().Projects.Single().ColourKey);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void ProjectCategoryChangesFlowToInheritedTasksAndLeaveOverridesStable()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        work.CreateTask(project.Id, "Inherited");
        var overridden = work.CreateTask(project.Id, "Override");
        work.UpdateTask(overridden.Id, overridden.Title, "", "home", null);
        var shell = new ShellViewModel(work);
        var model = shell.Work!;
        model.SelectProject(project.Id);
        model.Category = model.Categories.Single(c => c.Id == "work");
        Assert.True(model.Save());
        Assert.Equal("Work", model.Backlog.Single(t => t.Title == "Inherited").CategoryName);
        Assert.Equal("Home", model.Backlog.Single(t => t.Title == "Override").CategoryName);
        Assert.Equal("1", shell.PrimaryNavigation.Single(n => n.Title == "Projects").CountText);
        Assert.Equal("2", shell.PrimaryNavigation.Single(n => n.Title == "Backlog").CountText);
        model.QuickAdd(project.Id, "Third");
        Assert.Equal("3", shell.PrimaryNavigation.Single(n => n.Title == "Backlog").CountText);
    }

    [Fact]
    public void NavigationFlushesTextWhoseFieldBoundariesCouldOtherwiseCollide()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("a\u001fb", "c", "home", null);
        var model = new ProjectCaptureViewModel(work);
        model.SelectProject(project.Id);
        model.Title = "a";
        model.Description = "b\u001fc";
        Assert.True(model.IsDirty);
        var left = false;
        model.Navigate(() => left = true);
        Assert.True(left);
        Assert.False(model.NeedsDecision);
        Assert.Equal("a", Assert.Single(work.Read().Projects).Title);
        Assert.Equal("b\u001fc", Assert.Single(work.Read().Projects).Description);
    }

    [Fact]
    public void LoadingDraftIgnoresReentrantWritesFromThePreviouslyBoundTitleEditor()
    {
        var work = new MemoryWorkspaceWork();
        var previous = work.CreateProject("Previous", "", "home", null);
        var next = work.CreateProject("Next", "", "work", null);
        var model = new ProjectCaptureViewModel(work);
        model.SelectProject(previous.Id);
        var simulatedStaleWrite = false;
        model.PropertyChanged += (_, change) =>
        {
            if (!simulatedStaleWrite && change.PropertyName == nameof(ProjectCaptureViewModel.Title) && model.Title == "Next")
            {
                simulatedStaleWrite = true;
                model.Title = "Previous";
            }
        };

        model.SelectProject(next.Id);

        Assert.True(simulatedStaleWrite);
        Assert.Equal("Next", model.Title);
        Assert.False(model.IsDirty);
    }

    [Fact]
    public void CreationIsTransientAndCancelLeavesNoProject()
    {
        var work = new MemoryWorkspaceWork();
        var model = new ProjectCaptureViewModel(work);
        model.NewProjectCommand.Execute(null);
        Assert.False(model.IsDirty);
        model.Title = "Draft";
        Assert.True(model.IsDirty);
        model.Title = string.Empty;
        Assert.False(model.IsDirty);
        model.Title = "Draft";
        Assert.Empty(work.Read().Projects);
        Assert.Empty(model.Projects);
        model.CancelCommand.Execute(null);
        Assert.False(model.HasInspector);
        Assert.Empty(work.Read().Projects);

        model.NewTaskCommand.Execute(null);
        Assert.False(model.IsDirty);
        model.CancelCommand.Execute(null);
        model.NewCategoryCommand.Execute(null);
        Assert.False(model.IsDirty);
        model.CancelCommand.Execute(null);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("task")]
    [InlineData("category")]
    public void CreateDraftsGuardNavigationOnlyAfterARealEdit(string kind)
    {
        var model = new ProjectCaptureViewModel(new MemoryWorkspaceWork());
        var command = kind switch
        {
            "project" => model.NewProjectCommand,
            "task" => model.NewTaskCommand,
            _ => model.NewCategoryCommand,
        };

        command.Execute(null);
        Assert.False(model.IsDirty);
        var leftBlankDraft = false;
        model.Navigate(() => leftBlankDraft = true);
        Assert.True(leftBlankDraft);
        Assert.False(model.NeedsDecision);

        command.Execute(null);
        model.Title = "Draft";
        var leftEditedDraft = false;
        model.Navigate(() => leftEditedDraft = true);
        if (kind == "project")
        {
            Assert.True(leftEditedDraft);
            Assert.False(model.NeedsDecision);
        }
        else
        {
            Assert.False(leftEditedDraft);
            Assert.True(model.NeedsDecision);
            Assert.True(model.IsDirty);
        }
    }

    [Fact]
    public void CreateRequiresTitleAndShowsEmptyDerivedSummary()
    {
        var work = new MemoryWorkspaceWork();
        var model = new ProjectCaptureViewModel(work);
        model.NewProjectCommand.Execute(null);
        model.Title = "  ";
        Assert.False(model.Save());
        Assert.Empty(model.Projects);
        model.Title = "Plan garden";
        Assert.True(model.Save());
        var project = Assert.Single(model.Projects);
        Assert.Contains("Not started · 0 of 0 Tasks", project.Summary, StringComparison.Ordinal);
        Assert.Null(Assert.Single(work.Read().Projects).TargetDate);
        Assert.Equal("Save", model.SaveLabel);
    }

    [Fact]
    public void QuickAddFlushesPendingProjectChangesAndCreatesInheritedTasksInIndependentOrders()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var model = new ProjectCaptureViewModel(work);
        model.SelectProject(project.Id);
        model.Title = "Unsaved garden";
        Assert.True(model.QuickAdd(project.Id, " First "));
        Assert.True(model.QuickAdd(project.Id, "Second"));
        Assert.False(model.IsDirty);
        Assert.Equal("Unsaved garden", model.Title);
        Assert.Equal("Unsaved garden", Assert.Single(work.Read().Projects).Title);
        Assert.Equal(["First", "Second"], Assert.Single(model.Projects).Tasks.Select(t => t.Title));
        Assert.Equal(["Second", "First"], model.Backlog.Select(t => t.Title));
        Assert.All(work.Read().Tasks, t => { Assert.Null(t.ExplicitCategoryId); Assert.Null(t.DueDate); Assert.Empty(t.Description); });
        Assert.False(model.QuickAdd(project.Id, " \t "));
        Assert.Equal(2, work.Read().Tasks.Count);
    }

    [Fact]
    public void ExistingProjectAutosavesValidChangesAndRetainsInvalidDateForCorrection()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "Original", "home", null);
        var model = new ProjectCaptureViewModel(work);
        model.SelectProject(project.Id);
        model.Title = "New garden";
        model.Description = "**Plan**";
        model.Date = "2026-10-12";
        model.Category = model.Categories.Single(c => c.Id == "work");
        var automaticallySaved = Assert.Single(work.Read().Projects);
        Assert.Equal("New garden", automaticallySaved.Title);
        Assert.Equal("**Plan**", automaticallySaved.Description);
        Assert.Equal(new DateOnly(2026, 10, 12), automaticallySaved.TargetDate);
        Assert.Equal("work", automaticallySaved.CategoryId);
        model.Title = "Saved garden";
        model.Date = "2026-02-30";
        Assert.False(model.RunScheduledAutosave());
        Assert.True(model.HasDateValidationError);
        Assert.True(model.HasAutosaveError);
        Assert.Equal("Enter a valid date as YYYY-MM-DD, or leave it empty.", model.DateValidationMessage);
        Assert.Equal(model.DateValidationMessage, model.AutosaveStatus);
        Assert.Equal("New garden", Assert.Single(work.Read().Projects).Title);
        model.Date = "2026-10-12";
        Assert.False(model.HasDateValidationError);
        model.Description = "**Plan**";
        model.Category = model.Categories.Single(c => c.Id == "work");
        Assert.True(model.RunScheduledAutosave());
        var saved = Assert.Single(work.Read().Projects);
        Assert.Equal("Saved garden", saved.Title);
        Assert.Equal("**Plan**", saved.Description);
        Assert.Equal(new DateOnly(2026, 10, 12), saved.TargetDate);
        Assert.Equal("work", saved.CategoryId);
    }

    [Fact]
    public void TaskCategoryCanBeOverriddenThenReturnedToInheritance()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTask(project.Id, "Dig");
        var model = new ProjectCaptureViewModel(work);
        model.SelectTask(task.Id);
        Assert.Contains("Inherited", model.CategoryHint, StringComparison.Ordinal);
        model.Category = model.Categories.Single(c => c.Id == "work");
        model.Description = "Dig carefully";
        model.Date = "2026-11-01";
        Assert.True(model.Save());
        Assert.Equal("work", Assert.Single(work.Read().Tasks).ExplicitCategoryId);
        Assert.Equal(new DateOnly(2026, 11, 1), Assert.Single(work.Read().Tasks).DueDate);
        Assert.Equal("Dig carefully", Assert.Single(work.Read().Tasks).Description);
        model.Category = model.Categories.Single(c => c.Id is null);
        Assert.True(model.Save());
        Assert.Null(Assert.Single(work.Read().Tasks).ExplicitCategoryId);
    }

    [Fact]
    public void ParticipantAssociationsAndNewLabelsAutosaveAtomically()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTask(project.Id, "Dig");
        var existing = work.CreateParticipant("SD");
        var model = new ProjectCaptureViewModel(work);
        model.SelectTask(task.Id);

        model.ParticipantToAdd = model.AvailableParticipants.Single(item => item.Id == existing.Id);
        model.AddParticipantCommand.Execute(null);
        model.ParticipantToAdd = ParticipantChoice.New;
        model.NewParticipantLabel = "AB";
        model.AddNewParticipantCommand.Execute(null);

        Assert.False(model.IsDirty);
        Assert.Equal(["SD", "AB"], model.SelectedParticipants.Select(item => item.Label));
        var saved = work.Read();
        Assert.Equal(2, saved.Participants.Count);
        Assert.Equal(saved.Participants.Select(item => item.Id), saved.Tasks.Single().Participants);
    }

    [Fact]
    public void NewParticipantReusesEquivalentExistingIdentityAndRejectsAlreadyAssociatedMatch()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Call", "", "home", null);
        work.CreateParticipant("SD");
        var model = new ProjectCaptureViewModel(work);
        model.SelectTask(task.Id);
        model.NewParticipantLabel = "  ";
        model.AddNewParticipantCommand.Execute(null);
        Assert.Equal("Enter a Participant label.", model.NewParticipantValidationMessage);
        model.NewParticipantLabel = "sd";
        model.AddNewParticipantCommand.Execute(null);
        var selected = Assert.Single(model.SelectedParticipants);
        Assert.Equal("SD", selected.Label);
        Assert.NotNull(selected.Id);
        Assert.Single(work.Read().Participants);

        model.ParticipantToAdd = ParticipantChoice.New;
        model.NewParticipantLabel = " ＳＤ ";
        model.AddNewParticipantCommand.Execute(null);
        Assert.Equal("This Participant is already on this Task.", model.NewParticipantValidationMessage);
        Assert.Equal(" ＳＤ ", model.NewParticipantLabel);
        Assert.Equal("new-participant-label", model.ParticipantFocusAutomationId);
        Assert.Single(model.SelectedParticipants);
    }

    [Fact]
    public void StandaloneTaskDraftRequiresExplicitCategoryAndNeverAppearsUnderAProject()
    {
        var work = new MemoryWorkspaceWork();
        work.CreateProject("Garden", "", "home", null);
        var model = new ProjectCaptureViewModel(work);

        model.NewTaskCommand.Execute(null);
        model.Title = "Buy compost";
        model.Description = "Peat free";
        model.Date = "2026-10-03";
        Assert.False(model.Save());
        Assert.Empty(work.Read().Tasks);

        model.Category = model.Categories.Single(category => category.Id == "work");
        Assert.True(model.Save());

        var task = Assert.Single(work.Read().Tasks);
        Assert.Null(task.ProjectId);
        Assert.Null(task.ProjectPosition);
        Assert.Equal("work", task.ExplicitCategoryId);
        Assert.Equal("Peat free", task.Description);
        Assert.Equal(new DateOnly(2026, 10, 3), task.DueDate);
        Assert.Empty(Assert.Single(model.Projects).Tasks);
        Assert.Equal("Standalone · Work", Assert.Single(model.Backlog).CategoryDisplay);
    }

    [Fact]
    public void WorkIdentityRowsExposeRelationshipFocusedContextAndAccessibleNames()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var inherited = work.CreateTask(project.Id, "Plant bulbs");
        var overridden = work.CreateTask(project.Id, "Submit permit");
        work.UpdateTask(overridden.Id, overridden.Title, "", "work", null);
        var standalone = work.CreateStandaloneTask("File receipt", "", "work", null);
        var model = new ProjectCaptureViewModel(work);

        var inheritedRow = model.Backlog.Single(row => row.Id == inherited.Id);
        Assert.Equal("Garden", inheritedRow.RelationshipText);
        Assert.Equal("Garden", inheritedRow.BroadRelationshipText);
        Assert.False(inheritedRow.HasCategoryOverride);
        Assert.Contains("Task Plant bulbs", inheritedRow.AccessibleName, StringComparison.Ordinal);
        Assert.Contains("Project Garden", inheritedRow.AccessibleName, StringComparison.Ordinal);
        Assert.Contains("Inherited Category Home", inheritedRow.AccessibleName, StringComparison.Ordinal);

        var overrideRow = model.Backlog.Single(row => row.Id == overridden.Id);
        Assert.Equal("Garden", overrideRow.RelationshipText);
        Assert.True(overrideRow.HasCategoryOverride);
        Assert.Equal("Work", overrideRow.CategoryOverrideText);
        Assert.Contains("Category override Work", overrideRow.AccessibleName, StringComparison.Ordinal);

        var standaloneRow = model.Backlog.Single(row => row.Id == standalone.Id);
        Assert.Equal("Standalone · Work", standaloneRow.BroadRelationshipText);
        Assert.Equal("Standalone", standaloneRow.CategoryGroupRelationshipText);
        Assert.Contains("Standalone. Category Work", standaloneRow.AccessibleName, StringComparison.Ordinal);
        Assert.Equal(
            "Standalone",
            model.CategoryGroups.Single(group => group.Name == "Work")
                .StandaloneTasks.Single().Task.CategoryGroupRelationshipText);

        var projectRow = Assert.Single(model.Projects);
        Assert.Equal("Home · 2 Tasks", projectRow.RelationshipText);
        Assert.Contains("Project Garden", projectRow.AccessibleName, StringComparison.Ordinal);
        Assert.Contains("Category Home", projectRow.AccessibleName, StringComparison.Ordinal);
        Assert.Equal("1 Project · 0 standalone Tasks", model.CategoryGroups.Single(group => group.Name == "Home").Summary);
        Assert.Equal("0 Projects · 1 standalone Task", model.CategoryGroups.Single(group => group.Name == "Work").Summary);

        model.SelectTask(overridden.Id);
        Assert.Equal(overrideRow.AccessibleName, model.InspectorIdentityAccessibleText);
        Assert.True(model.ShowTaskIdentityIcon);
    }

    [Fact]
    public void BacklogQuickAddRetainsCategoryOnlyUntilTheEntrySessionEnds()
    {
        var work = new MemoryWorkspaceWork();
        var shell = new ShellViewModel(work);
        var backlog = shell.PrimaryNavigation.Single(item => item.Title == "Backlog");
        backlog.SelectCommand.Execute(null);
        var model = shell.Work!;
        model.BacklogQuickTitle = "Needs category";
        Assert.False(model.SubmitBacklogQuickAdd());
        Assert.Equal("Needs category", model.BacklogQuickTitle);
        Assert.Empty(work.Read().Tasks);
        model.BacklogQuickCategory = model.BacklogCategories.Single(category => category.Id == "home");
        model.BacklogQuickTitle = "First";
        Assert.True(model.SubmitBacklogQuickAdd());
        model.BacklogQuickTitle = "Second";
        Assert.True(model.SubmitBacklogQuickAdd());
        Assert.Equal("home", model.BacklogQuickCategory?.Id);
        Assert.All(work.Read().Tasks, task => Assert.Equal("home", task.ExplicitCategoryId));

        shell.PrimaryNavigation.Single(item => item.Title == "Projects").SelectCommand.Execute(null);
        backlog.SelectCommand.Execute(null);
        Assert.Null(model.BacklogQuickCategory);
        Assert.Empty(model.BacklogQuickTitle);
    }

    [Fact]
    public void CategoryProjectionPreservesGlobalOrdersAndManagementUsesAccessibleCategoryScope()
    {
        var work = new MemoryWorkspaceWork();
        var homeFirst = work.CreateProject("Home first", "", "home", null);
        var workProject = work.CreateProject("Work project", "", "work", null);
        var homeSecond = work.CreateProject("Home second", "", "home", null);
        work.CreateStandaloneTask("Home older", "", "home", null);
        work.CreateStandaloneTask("Work task", "", "work", null);
        work.CreateStandaloneTask("Home newest", "", "home", null);
        var model = new ProjectCaptureViewModel(work);

        Assert.Equal(["Home", "Work"], model.CategoryGroups.Select(category => category.Name));
        var home = model.CategoryGroups.Single(category => category.Id == "home");
        Assert.Equal([homeFirst.Id, homeSecond.Id], home.Projects.Select(row => row.Project.Id));
        Assert.Equal(["Home newest", "Home older"], home.StandaloneTasks.Select(row => row.Task.Title));
        Assert.DoesNotContain(workProject.Id, home.Projects.Select(row => row.Project.Id));

        model.CategoryGroups.Single(category => category.Id == "work").MoveToTopCommand.Execute(null);
        Assert.Equal(["Work", "Home"], model.CategoryGroups.Select(category => category.Name));
        Assert.Equal("Moved Work to position 1 of 2 in Categories.", model.ReorderAnnouncement);
        Assert.Equal("category-reorder-work", model.ReorderFocusAutomationId);

        model.NewCategoryCommand.Execute(null);
        model.Title = " ";
        Assert.False(model.Save());
        Assert.True(model.HasCategoryNameValidationError);
        Assert.Equal("Enter a category name.", model.CategoryNameValidationMessage);
        model.Title = "Personal";
        Assert.False(model.HasCategoryNameValidationError);
        Assert.True(model.Save());
        Assert.Equal("Personal", model.CategoryGroups[^1].Name);
        model.Title = "HOME";
        Assert.False(model.Save());
        Assert.True(model.HasCategoryNameValidationError);
        Assert.Equal("Personal", model.CategoryGroups[^1].Name);
        model.Title = "Someday";
        Assert.False(model.HasCategoryNameValidationError);
        Assert.True(model.Save());
        Assert.Equal("Someday", model.CategoryGroups[^1].Name);

        model.CategoryGroups.Single(category => category.Id == "home").SelectCommand.Execute(null);
        model.DeleteCategoryCommand.Execute(null);
        Assert.True(model.NeedsCategoryReplacement);
        model.CategoryReplacement = model.CategoryReplacementChoices.Single(category => category.Id == "work");
        model.ConfirmDeleteCategoryCommand.Execute(null);
        Assert.False(model.NeedsCategoryReplacement);
        Assert.DoesNotContain(model.CategoryGroups, category => category.Id == "home");
        Assert.All(work.Read().Projects.Where(project => project.Id != workProject.Id), project => Assert.Equal("work", project.CategoryId));
        Assert.All(work.Read().Tasks.Where(task => task.ProjectId is null && task.Title.StartsWith("Home", StringComparison.Ordinal)), task => Assert.Equal("work", task.ExplicitCategoryId));
    }

    [Fact]
    public void CategoriesKeepCompletedActiveStandaloneTasks()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Completed but active", "", "work", null);
        work.CompleteTask(task.Id);

        var model = new ProjectCaptureViewModel(work);

        var categoryTask = Assert.Single(model.CategoryGroups.Single(category => category.Id == "work").StandaloneTasks);
        Assert.Equal(task.Id, categoryTask.Task.Id);
        Assert.True(categoryTask.Task.IsComplete);
    }

    [Theory]
    [InlineData("save", "Renamed", true)]
    [InlineData("discard", "Work", true)]
    [InlineData("stay", "Work", false)]
    public void CategoryDeletionRequiresExplicitDraftResolution(string choice, string persistedName, bool requestsDeletion)
    {
        var work = new MemoryWorkspaceWork();
        work.CreateProject("Referenced project", "", "work", null);
        var model = new ProjectCaptureViewModel(work);
        model.SelectCategory("work");
        model.Title = "Renamed";

        model.DeleteCategoryCommand.Execute(null);

        Assert.True(model.NeedsDecision);
        Assert.False(model.NeedsCategoryReplacement);
        var command = choice switch
        {
            "save" => model.SaveAndLeaveCommand,
            "discard" => model.DiscardAndLeaveCommand,
            _ => model.StayCommand,
        };
        command.Execute(null);

        Assert.False(model.NeedsDecision);
        Assert.Equal(requestsDeletion, model.NeedsCategoryReplacement);
        Assert.Equal(persistedName, work.Read().Categories.Single(category => category.Id == "work").Name);
        Assert.True(model.HasInspector);
        if (!requestsDeletion)
        {
            Assert.True(model.IsDirty);
            Assert.Equal("Renamed", model.Title);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CategoryStorageFailureRetainsTheDraftWithoutShowingANameValidationError(bool creating)
    {
        var work = new MemoryWorkspaceWork();
        var model = new ProjectCaptureViewModel(work);
        if (creating) model.NewCategoryCommand.Execute(null);
        else model.SelectCategory("work");
        model.Title = creating ? "Personal" : "Renamed";
        work.FailWrites = true;

        Assert.False(model.Save());

        Assert.False(model.HasCategoryNameValidationError);
        Assert.Equal("Could not save workspace changes. Your draft is retained. Try again.", model.Message);
        Assert.True(model.IsDirty);
        Assert.Equal(creating ? "Personal" : "Renamed", model.Title);
    }

    [Fact]
    public void TaskContextActionsDetachMatchAutomaticallyAndRequireAnExplicitMismatchChoice()
    {
        var work = new MemoryWorkspaceWork();
        var homeProject = work.CreateProject("Home project", "", "home", null, "cyan");
        var workProject = work.CreateProject("Work project", "", "work", null, "coral");
        var task = work.CreateStandaloneTask("Move me", "", "home", null);
        var model = new ProjectCaptureViewModel(work);

        model.SelectTask(task.Id);
        model.TaskContextTarget = model.TaskContextChoices.Single(choice => choice.ProjectId == homeProject.Id);
        model.ChangeTaskContextCommand.Execute(null);
        var attached = work.Read().Tasks.Single(item => item.Id == task.Id);
        Assert.Equal(homeProject.Id, attached.ProjectId);
        Assert.Null(attached.ExplicitCategoryId);
        var attachedRow = model.Backlog.Single(item => item.Id == task.Id);
        Assert.Equal("Home project", attachedRow.ProjectTitle);
        Assert.Equal("cyan", attachedRow.ProjectColourKey);
        Assert.False(model.NeedsAttachmentChoice);

        model.TaskContextTarget = model.TaskContextChoices.Single(choice => choice.ProjectId is null);
        model.ChangeTaskContextCommand.Execute(null);
        var detached = work.Read().Tasks.Single(item => item.Id == task.Id);
        Assert.Null(detached.ProjectId);
        Assert.Equal("home", detached.ExplicitCategoryId);
        Assert.Null(model.Backlog.Single(item => item.Id == task.Id).ProjectTitle);

        model.TaskContextTarget = model.TaskContextChoices.Single(choice => choice.ProjectId == workProject.Id);
        model.ChangeTaskContextCommand.Execute(null);
        Assert.True(model.NeedsAttachmentChoice);
        Assert.True(model.HasBlockingDialog);
        Assert.Null(work.Read().Tasks.Single(item => item.Id == task.Id).ProjectId);
        Assert.Contains("Keep Home", model.PreserveCategoryLabel, StringComparison.Ordinal);
        Assert.Contains("Adopt Work", model.AdoptCategoryLabel, StringComparison.Ordinal);
        work.FailWrites = true;
        model.PreserveTaskCategoryCommand.Execute(null);
        Assert.True(model.NeedsAttachmentChoice);
        Assert.Null(work.Read().Tasks.Single(item => item.Id == task.Id).ProjectId);
        work.FailWrites = false;
        model.PreserveTaskCategoryCommand.Execute(null);

        var preserved = work.Read().Tasks.Single(item => item.Id == task.Id);
        Assert.Equal(workProject.Id, preserved.ProjectId);
        Assert.Equal("home", preserved.ExplicitCategoryId);
        var preservedRow = model.Backlog.Single(item => item.Id == task.Id);
        Assert.Equal("Work project", preservedRow.ProjectTitle);
        Assert.Equal("coral", preservedRow.ProjectColourKey);
        Assert.True(preservedRow.HasCategoryOverride);

        model.TaskContextTarget = model.TaskContextChoices.Single(choice => choice.ProjectId is null);
        model.ChangeTaskContextCommand.Execute(null);
        model.TaskContextTarget = model.TaskContextChoices.Single(choice => choice.ProjectId == workProject.Id);
        model.ChangeTaskContextCommand.Execute(null);
        Assert.True(model.NeedsAttachmentChoice);
        model.AdoptProjectCategoryCommand.Execute(null);
        var adopted = work.Read().Tasks.Single(item => item.Id == task.Id);
        Assert.Equal(workProject.Id, adopted.ProjectId);
        Assert.Null(adopted.ExplicitCategoryId);
        var adoptedRow = model.Backlog.Single(item => item.Id == task.Id);
        Assert.Equal("Work project", adoptedRow.ProjectTitle);
        Assert.Equal("coral", adoptedRow.ProjectColourKey);
        Assert.False(adoptedRow.HasCategoryOverride);
    }

    [Fact]
    public void TaskContextActionFlushesPendingTaskFieldsWithoutClosingTheInspector()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Work project", "", "work", null);
        var task = work.CreateStandaloneTask("Move me", "", "home", null);
        var model = new ProjectCaptureViewModel(work);

        model.SelectTask(task.Id);
        model.Title = "Unsaved title";
        model.TaskContextTarget = model.TaskContextChoices.Single(choice => choice.ProjectId == project.Id);
        model.ChangeTaskContextCommand.Execute(null);
        Assert.False(model.NeedsDecision);
        Assert.True(model.HasInspector);
        Assert.True(model.NeedsAttachmentChoice);
        Assert.Equal("Unsaved title", model.Title);
        Assert.Equal("Unsaved title", work.Read().Tasks.Single(item => item.Id == task.Id).Title);
        Assert.Null(work.Read().Tasks.Single(item => item.Id == task.Id).ProjectId);

        model.CancelAttachmentCommand.Execute(null);
        Assert.False(model.NeedsAttachmentChoice);
        Assert.True(model.HasInspector);
    }

    [Fact]
    public void AllAccessibleMoveCommandsUseTheSharedOrderAndAnnouncePositionAndScope()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        foreach (var title in new[] { "One", "Two", "Three", "Four" }) work.CreateTask(project.Id, title);
        var model = new ProjectCaptureViewModel(work);
        var one = model.Backlog.Single(task => task.Title == "One");

        one.MoveUpCommand.Execute(null);
        Assert.Equal(["Four", "Three", "One", "Two"], model.Backlog.Select(task => task.Title));
        one.MoveToTopCommand.Execute(null);
        Assert.Equal(["One", "Four", "Three", "Two"], model.Backlog.Select(task => task.Title));
        one.MoveDownCommand.Execute(null);
        Assert.Equal(["Four", "One", "Three", "Two"], model.Backlog.Select(task => task.Title));
        one.MoveToBottomCommand.Execute(null);
        Assert.Equal(["Four", "Three", "Two", "One"], model.Backlog.Select(task => task.Title));
        Assert.Equal("Moved One to position 4 of 4 in Backlog.", model.ReorderAnnouncement);
        Assert.Equal(model.Backlog.Select(task => task.Id), work.Read().Tasks.Select(task => task.Id));
    }

    [Fact]
    public void NavigationFlushesValidProjectChangesWithoutPrompting()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Original", "", "home", null);
        var shell = new ShellViewModel(work);
        shell.Work!.SelectProject(project.Id);
        shell.Work.Title = "Edited";
        shell.PrimaryNavigation.Single(n => n.Title == "Projects").SelectCommand.Execute(null);
        Assert.Equal("Projects", shell.ViewTitle);
        Assert.False(shell.Work.NeedsDecision);
        Assert.Equal("Edited", Assert.Single(work.Read().Projects).Title);
    }

    [Fact]
    public void FailedSaveRetainsDraftPendingDestinationAndAllowsRetry()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Original", "", "home", null);
        var model = new ProjectCaptureViewModel(work);
        model.SelectProject(project.Id);
        model.Title = "Edited";
        bool left = false;
        work.FailWrites = true;
        model.Navigate(() => left = true);
        Assert.False(left);
        Assert.True(model.NeedsDecision);
        Assert.True(model.IsDirty);
        Assert.Equal("Edited", model.Title);
        work.FailWrites = false;
        model.SaveAndLeaveCommand.Execute(null);
        Assert.True(left);
        Assert.Equal("Edited", Assert.Single(work.Read().Projects).Title);
    }

    [Fact]
    public void CompletionUpdatesDerivedProjectStateAndReopenRestoresBacklogWithoutChangingCapturedDate()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", new DateOnly(2026, 9, 28));
        var first = work.CreateTask(project.Id, "First");
        var second = work.CreateTask(project.Id, "Second");
        var model = new ProjectCaptureViewModel(work, new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));

        model.Backlog.Single(row => row.Id == first.Id).ToggleCompletionCommand.Execute(null);
        Assert.Single(model.Backlog);
        Assert.Equal("In progress", Assert.Single(model.Projects).Status);
        Assert.Equal("1/2 tasks", Assert.Single(model.Projects).ProgressText);

        model.Projects.Single().Tasks.Single(row => row.Id == second.Id).ToggleCompletionCommand.Execute(null);
        var complete = Assert.Single(model.Projects);
        Assert.Equal("Complete", complete.Status);
        Assert.Equal("Completed 29 Sep 2026", complete.CompletionDateText);
        Assert.False(complete.IsOverdue);
        Assert.Empty(model.Backlog);

        complete.Tasks.Single(row => row.Id == first.Id).ToggleCompletionCommand.Execute(null);
        Assert.Equal("In progress", Assert.Single(model.Projects).Status);
        Assert.Empty(Assert.Single(model.Projects).CompletionDateText);
        Assert.Contains(model.Backlog, row => row.Id == first.Id);
    }

    [Fact]
    public void CompletedProjectionSurvivesReloadShowsCapturedDateAndReopensStandaloneTask()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Filed receipt", "", "home", null);
        work.CompleteTask(task.Id);

        var model = new ProjectCaptureViewModel(work);
        var completed = Assert.Single(model.Completed);
        Assert.Equal(task.Id, completed.Id);
        Assert.Equal("Completed 29 Sep 2026", completed.CompletionDateText);
        Assert.Empty(model.Backlog);

        var reloaded = new ProjectCaptureViewModel(work);
        reloaded.SetCompletedActive(true);
        Assert.Equal(task.Id, Assert.Single(reloaded.Completed).Id);
        reloaded.Completed.Single().ToggleCompletionCommand.Execute(null);
        Assert.Empty(reloaded.Completed);
        Assert.Equal(task.Id, Assert.Single(reloaded.Backlog).Id);
        Assert.Equal("navigation-completed", reloaded.CompletionFocusAutomationId);
    }

    [Fact]
    public void ProjectArchiveHidesAggregateWorkAndRestoreReturnsEligibleTasksWithoutToday()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var incomplete = work.CreateTask(project.Id, "Plant bulbs");
        var complete = work.CreateTask(project.Id, "Buy compost");
        var individuallyArchived = work.CreateTask(project.Id, "File receipt");
        work.SetTaskTodayLane(incomplete.Id, TodayLane.InProgress);
        work.CompleteTask(complete.Id);
        work.CompleteTask(individuallyArchived.Id);
        work.ArchiveTask(individuallyArchived.Id);
        var model = new ProjectCaptureViewModel(work);
        model.Projects.Single().Tasks.Single(task => task.Id == incomplete.Id).SelectCommand.Execute(null);
        Assert.True(model.HasInspector);

        model.Projects.Single().ArchiveCommand.Execute(null);

        Assert.False(model.HasInspector);
        Assert.Empty(model.Projects);
        var archivedProject = Assert.Single(model.ArchivedProjects).Project;
        Assert.Equal(project.Id, archivedProject.Id);
        Assert.Equal("In progress", archivedProject.Status);
        Assert.Empty(model.Backlog);
        Assert.Empty(model.Completed);
        Assert.Empty(model.Archived);
        Assert.Empty(model.TodayInProgress);
        Assert.Equal("Project archived.", model.Message);
        Assert.Null(work.Read().Tasks.Single(task => task.Id == incomplete.Id).TodayLane);
        Assert.True(work.Read().Tasks.Single(task => task.Id == individuallyArchived.Id).IsArchived);
        var reloaded = new ProjectCaptureViewModel(work);
        reloaded.NewTodayTaskCommand.Execute(null);
        Assert.DoesNotContain(reloaded.TaskContextChoices, choice => choice.ProjectId == project.Id);

        archivedProject.RestoreCommand.Execute(null);

        Assert.Empty(model.ArchivedProjects);
        Assert.Equal(project.Id, Assert.Single(model.Projects).Id);
        Assert.Equal(incomplete.Id, Assert.Single(model.Backlog).Id);
        Assert.Equal(complete.Id, Assert.Single(model.Completed).Id);
        Assert.Equal(individuallyArchived.Id, Assert.Single(model.Archived).Task.Id);
        Assert.Empty(model.TodayPlanned);
        Assert.Empty(model.TodayInProgress);
        Assert.Equal("Project restored.", model.Message);
    }

    [Fact]
    public void BulkArchivePreviewConfirmationCancellationAndExecutionUseSelectedThreshold()
    {
        var work = new MemoryWorkspaceWork();
        var old = work.CreateStandaloneTask("Old", "", "home", null);
        var atCutoff = work.CreateStandaloneTask("At cutoff", "", "home", null);
        work.SetCompletion(old.Id, new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 25));
        work.SetCompletion(atCutoff.Id, new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 26));
        var model = new ProjectCaptureViewModel(work,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero)));

        model.BulkArchiveCompletedAgeDays = 3;

        Assert.Equal(1, model.BulkArchiveAffectedCount);
        Assert.Equal("1 completed Task is older than 3 calendar days.", model.BulkArchiveThresholdSummary);
        model.RequestBulkTaskArchiveCommand.Execute(null);
        Assert.True(model.NeedsBulkTaskArchiveConfirmation);
        Assert.Equal("Archive 1 completed Task?", model.BulkArchiveConfirmationHeading);
        Assert.Contains("Projects will not be archived", model.BulkArchiveConfirmationBody, StringComparison.Ordinal);
        model.CancelBulkTaskArchiveCommand.Execute(null);
        Assert.False(model.NeedsBulkTaskArchiveConfirmation);
        Assert.False(work.Read().Tasks.Single(task => task.Id == old.Id).IsArchived);

        model.RequestBulkTaskArchiveCommand.Execute(null);
        model.ConfirmBulkTaskArchiveCommand.Execute(null);

        Assert.False(model.NeedsBulkTaskArchiveConfirmation);
        Assert.True(work.Read().Tasks.Single(task => task.Id == old.Id).IsArchived);
        Assert.False(work.Read().Tasks.Single(task => task.Id == atCutoff.Id).IsArchived);
        Assert.Equal(0, model.BulkArchiveAffectedCount);
        Assert.Equal("1 completed Task archived.", model.Message);
    }

    [Fact]
    public void BulkArchiveRequiresFreshConfirmationWhenAffectedCountChangesAcrossMidnight()
    {
        var work = new MemoryWorkspaceWork { CurrentDate = new DateOnly(2026, 9, 29) };
        var old = work.CreateStandaloneTask("Old", "", "home", null);
        var boundary = work.CreateStandaloneTask("Boundary", "", "home", null);
        var nextBoundary = work.CreateStandaloneTask("Next boundary", "", "home", null);
        work.SetCompletion(old.Id, new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 25));
        work.SetCompletion(boundary.Id, new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 26));
        work.SetCompletion(nextBoundary.Id, new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 27));
        var model = new ProjectCaptureViewModel(work) { BulkArchiveCompletedAgeDays = 3 };
        model.RequestBulkTaskArchiveCommand.Execute(null);
        Assert.Equal(1, model.BulkArchiveAffectedCount);

        work.CurrentDate = new DateOnly(2026, 9, 30);
        model.ConfirmBulkTaskArchiveCommand.Execute(null);

        Assert.True(model.NeedsBulkTaskArchiveConfirmation);
        Assert.Equal(2, model.BulkArchiveAffectedCount);
        Assert.Equal("The affected count changed. Review it and confirm again.", model.Message);
        Assert.All(work.Read().Tasks, task => Assert.False(task.IsArchived));

        work.BeforeBulkArchive = () => work.CurrentDate = new DateOnly(2026, 10, 1);
        model.ConfirmBulkTaskArchiveCommand.Execute(null);

        Assert.True(model.NeedsBulkTaskArchiveConfirmation);
        Assert.Equal(3, model.BulkArchiveAffectedCount);
        Assert.Equal("The affected Tasks changed. Review them and confirm again.", model.Message);
        Assert.All(work.Read().Tasks, task => Assert.False(task.IsArchived));

        work.BeforeBulkArchive = null;
        model.ConfirmBulkTaskArchiveCommand.Execute(null);

        Assert.False(model.NeedsBulkTaskArchiveConfirmation);
        Assert.All(work.Read().Tasks, task => Assert.True(task.IsArchived));
    }

    [Fact]
    public void BulkArchiveNamesSameCountEligibilityChangesAndRequiresFreshConfirmation()
    {
        var work = new MemoryWorkspaceWork { CurrentDate = new DateOnly(2026, 9, 29) };
        var first = work.CreateStandaloneTask("First", "", "home", null);
        var replacement = work.CreateStandaloneTask("Replacement", "", "home", null);
        work.SetCompletion(first.Id, new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 25));
        work.SetCompletion(replacement.Id, new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 26));
        var model = new ProjectCaptureViewModel(work) { BulkArchiveCompletedAgeDays = 3 };
        model.RequestBulkTaskArchiveCommand.Execute(null);
        Assert.Equal(1, model.BulkArchiveAffectedCount);

        work.ReopenTask(first.Id);
        work.CurrentDate = new DateOnly(2026, 9, 30);
        model.ConfirmBulkTaskArchiveCommand.Execute(null);

        Assert.True(model.NeedsBulkTaskArchiveConfirmation);
        Assert.Equal(1, model.BulkArchiveAffectedCount);
        Assert.Equal("The affected Tasks changed. Review them and confirm again.", model.Message);
        Assert.All(work.Read().Tasks, task => Assert.False(task.IsArchived));

        model.ConfirmBulkTaskArchiveCommand.Execute(null);

        Assert.False(model.NeedsBulkTaskArchiveConfirmation);
        Assert.True(work.Read().Tasks.Single(task => task.Id == replacement.Id).IsArchived);
        Assert.False(work.Read().Tasks.Single(task => task.Id == first.Id).IsArchived);
    }

    [Fact]
    public void CompletedGroupsUseThreeDailyBucketsThenCalendarWeeks()
    {
        var work = new MemoryWorkspaceWork();
        TaskRecord Add(string title, DateOnly date, int hour)
        {
            var task = work.CreateStandaloneTask(title, "", "home", null);
            return work.SetCompletion(task.Id, new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.Zero), date);
        }

        var today = Add("Today", new DateOnly(2026, 9, 29), 12);
        var yesterday = Add("Yesterday", new DateOnly(2026, 9, 28), 12);
        var twoDaysAgo = Add("Two days ago", new DateOnly(2026, 9, 27), 12);
        var weekNewer = Add("Week newer", new DateOnly(2026, 9, 26), 15);
        var weekOlder = Add("Week older", new DateOnly(2026, 9, 24), 9);
        var priorWeek = Add("Prior week", new DateOnly(2026, 9, 20), 12);
        var model = new ProjectCaptureViewModel(
            work,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero)));

        Assert.Equal(
            ["Today", "Yesterday", "Sunday, 27 September 2026", "Week of 21 Sep 2026", "Week of 14 Sep 2026"],
            model.CompletedGroups.Select(group => group.Heading));
        Assert.Equal([today.Id], model.CompletedGroups[0].Tasks.Select(task => task.Id));
        Assert.Equal([yesterday.Id], model.CompletedGroups[1].Tasks.Select(task => task.Id));
        Assert.Equal([twoDaysAgo.Id], model.CompletedGroups[2].Tasks.Select(task => task.Id));
        Assert.Equal([weekNewer.Id, weekOlder.Id], model.CompletedGroups[3].Tasks.Select(task => task.Id));
        Assert.Equal([priorWeek.Id], model.CompletedGroups[4].Tasks.Select(task => task.Id));
    }

    [Fact]
    public void CompletedGroupsUseCapturedCalendarOrderWhenUtcInstantsAreInverted()
    {
        var work = new MemoryWorkspaceWork();
        var todayOlder = work.CreateStandaloneTask("Today older", "", "home", null);
        var todayNewer = work.CreateStandaloneTask("Today newer", "", "home", null);
        var yesterdayLaterInstant = work.CreateStandaloneTask("Yesterday later instant", "", "home", null);
        work.SetCompletion(todayOlder.Id, new DateTimeOffset(2026, 9, 29, 8, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 29));
        work.SetCompletion(todayNewer.Id, new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 29));
        work.SetCompletion(yesterdayLaterInstant.Id, new DateTimeOffset(2026, 9, 29, 20, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 28));

        var model = new ProjectCaptureViewModel(
            work,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 22, 0, 0, TimeSpan.Zero)));

        Assert.Equal(["Today", "Yesterday"], model.CompletedGroups.Select(group => group.Heading));
        Assert.Equal([todayNewer.Id, todayOlder.Id], model.CompletedGroups[0].Tasks.Select(task => task.Id));
        Assert.Equal([yesterdayLaterInstant.Id], model.CompletedGroups[1].Tasks.Select(task => task.Id));
    }

    [Fact]
    public void CompletedGroupsRemainBasedOnCapturedDatesAfterPresentationTimeZoneChange()
    {
        var work = new MemoryWorkspaceWork();
        var capturedToday = work.CreateStandaloneTask("Captured today", "", "home", null);
        var capturedYesterday = work.CreateStandaloneTask("Captured yesterday", "", "home", null);
        work.SetCompletion(capturedToday.Id, new DateTimeOffset(2026, 9, 29, 1, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 29));
        work.SetCompletion(capturedYesterday.Id, new DateTimeOffset(2026, 9, 29, 23, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 28));
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
        var model = new ProjectCaptureViewModel(work, time);

        var before = model.CompletedGroups.Select(group => (group.Heading, Ids: group.Tasks.Select(task => task.Id).ToArray())).ToArray();
        var plusEight = TimeZoneInfo.CreateCustomTimeZone("UTC+08-completed", TimeSpan.FromHours(8), "UTC+08", "UTC+08");
        time.Set(time.GetUtcNow(), plusEight);

        Assert.True(model.RefreshDatePresentation());
        Assert.Equal(before.Select(group => group.Heading), model.CompletedGroups.Select(group => group.Heading));
        Assert.Equal(before.SelectMany(group => group.Ids), model.CompletedGroups.SelectMany(group => group.Tasks).Select(task => task.Id));
    }

    [Fact]
    public void ArchivedTasksLeaveActiveProjectionsButRemainInArchiveAndAttachedProjects()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var attached = work.CreateTask(project.Id, "Plant bulbs");
        var standalone = work.CreateStandaloneTask("File receipt", "", "work", null);
        work.CompleteTask(attached.Id);
        work.CompleteTask(standalone.Id);
        work.ArchiveTask(attached.Id);
        work.ArchiveTask(standalone.Id);

        var model = new ProjectCaptureViewModel(work);

        Assert.Empty(model.Backlog);
        Assert.Empty(model.Completed);
        Assert.Empty(model.CompletedToday);
        Assert.Empty(model.CategoryGroups.Single(group => group.Id == "work").StandaloneTasks);
        Assert.Equal([standalone.Id, attached.Id], model.Archived.Select(row => row.Task.Id));
        var projectRow = Assert.Single(model.Projects);
        var archivedTask = Assert.Single(projectRow.Tasks);
        Assert.Same(archivedTask, model.Archived.Single(row => row.Task.Id == attached.Id).Task);
        Assert.True(archivedTask.IsArchived);
        Assert.Equal("Complete", projectRow.Status);
        Assert.Equal("1/1 tasks", projectRow.ProgressText);
        Assert.Equal("Completed 29 Sep 2026", projectRow.CompletionDateText);
    }

    [Fact]
    public void ArchiveGroupsUseCapturedDatesForThreeDailyBucketsThenCalendarWeeks()
    {
        var work = new MemoryWorkspaceWork();
        TaskRecord Add(string title, DateOnly date, int hour)
        {
            var task = work.CreateStandaloneTask(title, "", "home", null);
            work.SetCompletion(task.Id, new DateTimeOffset(2026, 9, 20, hour, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 20));
            return work.SetArchive(task.Id, new DateTimeOffset(date.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.Zero), date);
        }

        var today = Add("Today", new DateOnly(2026, 9, 29), 12);
        var yesterday = Add("Yesterday", new DateOnly(2026, 9, 28), 12);
        var twoDaysAgo = Add("Two days ago", new DateOnly(2026, 9, 27), 12);
        var weekNewer = Add("Week newer", new DateOnly(2026, 9, 26), 15);
        var weekOlder = Add("Week older", new DateOnly(2026, 9, 24), 9);
        var priorWeek = Add("Prior week", new DateOnly(2026, 9, 20), 12);
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
        var model = new ProjectCaptureViewModel(work, time);

        Assert.Equal(
            ["Today", "Yesterday", "Sunday, 27 September 2026", "Week of 21 Sep 2026", "Week of 14 Sep 2026"],
            model.ArchiveGroups.Select(group => group.Heading));
        Assert.Equal([today.Id], model.ArchiveGroups[0].Rows.Select(row => row.Task!.Id));
        Assert.Equal([yesterday.Id], model.ArchiveGroups[1].Rows.Select(row => row.Task!.Id));
        Assert.Equal([twoDaysAgo.Id], model.ArchiveGroups[2].Rows.Select(row => row.Task!.Id));
        Assert.Equal([weekNewer.Id, weekOlder.Id], model.ArchiveGroups[3].Rows.Select(row => row.Task!.Id));
        Assert.Equal([priorWeek.Id], model.ArchiveGroups[4].Rows.Select(row => row.Task!.Id));

        var before = model.ArchiveGroups.Select(group =>
            (group.Heading, Ids: group.Rows.Select(row => row.Task!.Id).ToArray())).ToArray();
        var plusEight = TimeZoneInfo.CreateCustomTimeZone("UTC+08-archive", TimeSpan.FromHours(8), "UTC+08", "UTC+08");
        time.Set(time.GetUtcNow(), plusEight);

        Assert.True(model.RefreshDatePresentation());
        Assert.Equal(before.Select(group => group.Heading), model.ArchiveGroups.Select(group => group.Heading));
        Assert.Equal(before.SelectMany(group => group.Ids),
            model.ArchiveGroups.SelectMany(group => group.Rows).Select(row => row.Task!.Id));
    }

    [Fact]
    public void MixedArchiveGroupsProjectsAndTasksUnderOneChronologicalHeadingSequence()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        work.ArchiveProject(project.Id);
        var todayTask = work.CreateStandaloneTask("Today Task", "", "home", null);
        var yesterdayTask = work.CreateStandaloneTask("Yesterday Task", "", "home", null);
        work.SetCompletion(todayTask.Id, new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 20));
        work.SetCompletion(yesterdayTask.Id, new DateTimeOffset(2026, 9, 20, 11, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 20));
        work.SetArchive(todayTask.Id, new DateTimeOffset(2026, 9, 29, 14, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 29));
        work.SetArchive(yesterdayTask.Id, new DateTimeOffset(2026, 9, 28, 14, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 28));

        var model = new ProjectCaptureViewModel(work,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 18, 0, 0, TimeSpan.Zero)));

        Assert.Equal(["Today", "Yesterday"], model.ArchiveGroups.Select(group => group.Heading));
        Assert.Equal(2, model.ArchiveGroups[0].Rows.Count);
        Assert.Contains(model.ArchiveGroups[0].Rows, row => row.Task?.Id == todayTask.Id && row.IsTask);
        Assert.Contains(model.ArchiveGroups[0].Rows, row => row.Project?.Id == project.Id && row.IsProject);
        Assert.Equal(yesterdayTask.Id, Assert.Single(model.ArchiveGroups[1].Rows).Task?.Id);
        Assert.All(model.ArchiveGroups, group => Assert.True(group.Rows[^1].IsLast));
    }

    [Fact]
    public void ArchiveSearchPresentsResultContextOpensTheMatchAndReturnsToTheTimelineWhenCleared()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTaskDraft(project.Id, "Plant bulbs", "Blue **tulips** near the gate", null, null);
        work.SetCompletion(task.Id, new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 27));
        work.ArchiveTask(task.Id);
        var model = new ProjectCaptureViewModel(work);

        model.ArchiveSearchText = "tul";

        var result = Assert.Single(model.ArchiveSearchResults);
        Assert.True(model.HasArchiveSearchQuery);
        Assert.True(model.HasArchiveSearchResults);
        Assert.False(model.ShowArchiveTimeline);
        Assert.Equal("1 archived result.", model.ArchiveSearchStatus);
        Assert.Equal("Task", result.TypeLabel);
        Assert.Equal("Project · Garden", result.ParentProjectText);
        Assert.Equal("Completed 27 Sep 2026", result.DateText);
        Assert.Contains("Blue tulips", result.Excerpt, StringComparison.Ordinal);
        Assert.Contains("Task Plant bulbs", result.AccessibleName, StringComparison.Ordinal);

        result.OpenCommand.Execute(null);

        Assert.True(model.HasInspector);
        Assert.Equal("Plant bulbs", model.Title);

        model.ArchiveSearchText = "missing";
        Assert.True(model.ShowArchiveSearchEmpty);
        Assert.Equal("No archived work matches “missing”.", model.ArchiveSearchStatus);

        model.ArchiveSearchText = string.Empty;
        Assert.True(model.ShowArchiveTimeline);
        Assert.False(model.HasArchiveSearchQuery);
        Assert.Empty(model.ArchiveSearchResults);
        Assert.Empty(model.ArchiveSearchStatus);
    }

    [Fact]
    public void ArchiveSearchProjectResultOpensTheArchivedProject()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Portfolio history", "Shipped work", "home", null);
        work.ArchiveProject(project.Id);
        var model = new ProjectCaptureViewModel(work);

        model.ArchiveSearchText = "portfolio";
        var result = Assert.Single(model.ArchiveSearchResults);
        Assert.Equal("Project", result.TypeLabel);
        Assert.False(result.HasParentProject);

        result.OpenCommand.Execute(null);

        Assert.True(model.HasInspector);
        Assert.Equal("Project details", model.InspectorHeading);
        Assert.Equal("Portfolio history", model.Title);
        Assert.Equal("Not started · 0 of 0 Tasks · Home · No date · No completion date", model.ProjectSummary);
        Assert.Equal("Not started · 0/0 tasks", model.InspectorMetaValue);
        Assert.Contains("Project Portfolio history", model.InspectorIdentityAccessibleText, StringComparison.Ordinal);
        Assert.Contains("Archived", model.InspectorIdentityAccessibleText, StringComparison.Ordinal);

        Assert.Equal("Restore Project Portfolio history to Projects", result.RestoreAccessibleName);
        result.RestoreCommand.Execute(null);

        Assert.False(work.Read().Projects.Single().IsArchived);
        Assert.Empty(model.ArchiveSearchResults);
    }

    [Fact]
    public void ArchiveSearchFailureShowsARecoverableNonResultState()
    {
        var work = new MemoryWorkspaceWork { FailArchiveSearch = true };
        var model = new ProjectCaptureViewModel(work);

        model.ArchiveSearchText = "private phrase";

        Assert.True(model.ShowArchiveSearchError);
        Assert.False(model.ShowArchiveSearchEmpty);
        Assert.False(model.HasArchiveSearchResults);
        Assert.Equal("Archive search is unavailable. Try again.", model.ArchiveSearchStatus);

        work.FailArchiveSearch = false;
        model.ArchiveSearchText = "another phrase";

        Assert.False(model.ShowArchiveSearchError);
        Assert.True(model.ShowArchiveSearchEmpty);
    }

    [Fact]
    public void ArchiveRequiresConfirmationFlushesEditsAndCompletionCannotReopenArchivedTask()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var task = work.CreateTask(project.Id, "Plant bulbs");
        work.CompleteTask(task.Id);
        var model = new ProjectCaptureViewModel(work);
        model.SelectTask(task.Id);
        model.Title = "Plant spring bulbs";

        model.Completed.Single().ArchiveCommand.Execute(null);

        Assert.True(model.NeedsArchiveConfirmation);
        Assert.Equal("Archive “Plant spring bulbs”?", model.ArchiveConfirmationHeading);
        Assert.Contains("remain under its Project", model.ArchiveConfirmationBody, StringComparison.Ordinal);
        Assert.False(work.Read().Tasks.Single().IsArchived);
        Assert.Equal("Plant spring bulbs", work.Read().Tasks.Single().Title);

        model.ConfirmArchiveTaskCommand.Execute(null);

        var archived = work.Read().Tasks.Single();
        Assert.True(archived.IsArchived);
        Assert.True(archived.IsComplete);
        Assert.Empty(model.Completed);
        Assert.False(model.HasInspector);
        Assert.Equal("navigation-completed", model.ArchiveFocusAutomationId);

        model.Projects.Single().Tasks.Single().ToggleCompletionCommand.Execute(null);

        Assert.True(work.Read().Tasks.Single().IsComplete);
        Assert.True(work.Read().Tasks.Single().IsArchived);
    }

    [Fact]
    public void RestoringArchivedTaskReturnsItToCapturedCompletedGroupAndNeverToday()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Filed receipt", "", "home", null);
        work.SetCompletion(task.Id, new DateTimeOffset(2026, 9, 28, 18, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 28));
        work.ArchiveTask(task.Id);
        var model = new ProjectCaptureViewModel(
            work,
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));
        model.SetArchiveActive(true);

        model.Archived.Single().Task.RestoreCommand.Execute(null);

        Assert.Empty(model.Archived);
        Assert.Equal(task.Id, Assert.Single(model.Completed).Id);
        Assert.Equal("Yesterday", Assert.Single(model.CompletedGroups).Heading);
        Assert.Empty(model.TodayPlanned);
        Assert.Empty(model.TodayInProgress);
        Assert.Empty(model.CompletedToday);
        Assert.Equal("navigation-archive", model.ArchiveFocusAutomationId);
        Assert.False(work.Read().Tasks.Single().IsArchived);
        Assert.Equal(new DateOnly(2026, 9, 28), work.Read().Tasks.Single().CompletionDate);
    }

    [Fact]
    public void ArchiveAndRestoreFailuresPreserveVisibilityAndExplainThatNothingChanged()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Filed receipt", "", "home", null);
        work.CompleteTask(task.Id);
        var model = new ProjectCaptureViewModel(work);
        model.Completed.Single().ArchiveCommand.Execute(null);
        work.FailWrites = true;

        model.ConfirmArchiveTaskCommand.Execute(null);

        Assert.True(model.NeedsArchiveConfirmation);
        Assert.False(work.Read().Tasks.Single().IsArchived);
        Assert.Equal("Could not archive the Task. No changes were made.", model.Message);

        model.CancelArchiveTaskCommand.Execute(null);
        work.FailWrites = false;
        work.ArchiveTask(task.Id);
        model = new ProjectCaptureViewModel(work);
        work.FailWrites = true;

        model.Archived.Single().Task.RestoreCommand.Execute(null);

        Assert.True(work.Read().Tasks.Single().IsArchived);
        Assert.Empty(model.Completed);
        Assert.Single(model.Archived);
        Assert.Equal("Could not restore the Task. No changes were made.", model.Message);
    }

    [Fact]
    public void ReopeningTaskFlushesPendingFieldsAndMovesItImmediately()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Filed receipt", "", "home", null);
        work.CompleteTask(task.Id);
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Completed").SelectCommand.Execute(null);
        var model = shell.Work!;
        model.SelectTask(task.Id);
        model.Title = "Unsaved receipt";

        model.Completed.Single().ToggleCompletionCommand.Execute(null);
        Assert.False(model.NeedsDecision);
        Assert.False(work.Read().Tasks.Single().IsComplete);
        Assert.Equal("Unsaved receipt", work.Read().Tasks.Single().Title);
        Assert.Empty(model.Completed);
    }

    [Fact]
    public void BacklogReorderUsesVisiblePositionsWhenCompletedTasksAreInterleaved()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var a = work.CreateTask(project.Id, "A");
        var b = work.CreateTask(project.Id, "B");
        var c = work.CreateTask(project.Id, "C");
        var d = work.CreateTask(project.Id, "D");
        var e = work.CreateTask(project.Id, "E");
        var f = work.CreateTask(project.Id, "F");
        work.CompleteTask(e.Id);
        work.CompleteTask(c.Id);
        var model = new ProjectCaptureViewModel(work);

        model.Backlog.Single(task => task.Id == f.Id).MoveDownCommand.Execute(null);
        Assert.Equal([d.Id, e.Id, f.Id, c.Id, b.Id, a.Id], work.Read().Tasks.Select(task => task.Id));
        model.Backlog.Single(task => task.Id == d.Id).MoveToBottomCommand.Execute(null);
        Assert.Equal([f.Id, e.Id, b.Id, c.Id, a.Id, d.Id], work.Read().Tasks.Select(task => task.Id));
        model.Backlog.Single(task => task.Id == d.Id).MoveToTopCommand.Execute(null);
        Assert.Equal([d.Id, e.Id, f.Id, c.Id, b.Id, a.Id], work.Read().Tasks.Select(task => task.Id));
        Assert.Equal("Moved D to position 1 of 4 in Backlog.", model.ReorderAnnouncement);
    }

    [Fact]
    public async Task BoundaryReorderCommandsRemainSafeAndReportTheirPersistedPosition()
    {
        var work = new MemoryWorkspaceWork();
        var first = work.CreateStandaloneTask("First", "", "home", null);
        var last = work.CreateStandaloneTask("Last", "", "home", null);
        work.SetTaskTodayLane(first.Id, TodayLane.Planned);
        work.SetTaskTodayLane(last.Id, TodayLane.Planned);
        var model = new ProjectCaptureViewModel(work);
        var backlogOrder = model.Backlog.Select(row => row.Id).ToArray();
        var todayOrder = model.TodayPlanned.Select(row => row.Task.Id).ToArray();

        model.Backlog[0].MoveUpCommand.Execute(null);
        await model.WaitForWorkspaceActionsAsync();
        model.TodayPlanned[^1].MoveDownCommand.Execute(null);
        await model.WaitForWorkspaceActionsAsync();

        Assert.Equal(backlogOrder, model.Backlog.Select(row => row.Id));
        Assert.Equal(todayOrder, model.TodayPlanned.Select(row => row.Task.Id));
        Assert.Equal("Moved Last to position 1 of 2 in Backlog.", model.ReorderAnnouncement);
        Assert.Equal("Moved First to position 2 of 2 in Planned.", model.TodayAnnouncement);
    }

    [Fact]
    public void CompletingBacklogTaskFlushesPendingFieldsAndMovesFocusToNextRow()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var first = work.CreateTask(project.Id, "First");
        var second = work.CreateTask(project.Id, "Second");
        var shell = new ShellViewModel(work);
        shell.PrimaryNavigation.Single(item => item.Title == "Backlog").SelectCommand.Execute(null);
        var model = shell.Work!;
        model.SelectTask(second.Id);
        model.Title = "Edited second";

        model.Backlog.Single(row => row.Id == second.Id).ToggleCompletionCommand.Execute(null);
        Assert.False(model.NeedsDecision);
        Assert.True(work.Read().Tasks.Single(task => task.Id == second.Id).IsComplete);
        Assert.Equal("Edited second", work.Read().Tasks.Single(task => task.Id == second.Id).Title);
        Assert.Equal("First", work.Read().Tasks.Single(task => task.Id == first.Id).Title);
        Assert.Equal(model.Backlog.Single().CompletionAutomationId, model.CompletionFocusAutomationId);
    }

    [Fact]
    public void CompletionAndIndependentReordersFlushPendingInspectorChanges()
    {
        var work = new MemoryWorkspaceWork();
        var firstProject = work.CreateProject("First", "", "home", null);
        var secondProject = work.CreateProject("Second", "", "home", null);
        var first = work.CreateTask(firstProject.Id, "First task");
        var second = work.CreateTask(firstProject.Id, "Second task");
        var model = new ProjectCaptureViewModel(work);
        model.SelectTask(first.Id);
        model.Title = "Unsaved title";

        model.Projects.Single(project => project.Id == secondProject.Id).MoveToTopCommand.Execute(null);
        model.Projects.Single(project => project.Id == firstProject.Id).Tasks.Single(task => task.Id == second.Id).MoveProjectTaskToTopCommand.Execute(null);
        model.Projects.Single(project => project.Id == firstProject.Id).Tasks.Single(task => task.Id == first.Id).ToggleCompletionCommand.Execute(null);

        Assert.False(model.IsDirty);
        Assert.Equal("Unsaved title", model.Title);
        Assert.Equal("Unsaved title", work.Read().Tasks.Single(task => task.Id == first.Id).Title);
        Assert.Equal([secondProject.Id, firstProject.Id], work.Read().Projects.Select(project => project.Id));
        Assert.Equal([second.Id, first.Id], work.Read().Tasks.Where(task => task.ProjectId == firstProject.Id).OrderBy(task => task.ProjectPosition).Select(task => task.Id));
        Assert.True(work.Read().Tasks.Single(task => task.Id == first.Id).IsComplete);
        Assert.Equal($"task-completion-{first.Id}", model.CompletionFocusAutomationId);
    }

    [Fact]
    public void RowsPresentRelativeFullAndUndatedDatesAndEmptyPastProjectAsOverdue()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", new DateOnly(2026, 9, 28));
        var today = work.CreateStandaloneTask("Today task", "", "home", new DateOnly(2026, 9, 29));
        var undated = work.CreateStandaloneTask("Undated task", "", "home", null);
        var tomorrow = work.CreateTask(project.Id, "Tomorrow task");
        work.UpdateTask(tomorrow.Id, tomorrow.Title, "", null, new DateOnly(2026, 9, 30));
        var model = new ProjectCaptureViewModel(work, new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));

        Assert.Equal("Today", model.Backlog.Single(row => row.Id == today.Id).DateText);
        Assert.Equal("Due Tuesday, 29 September 2026", model.Backlog.Single(row => row.Id == today.Id).DateAccessibleText);
        Assert.Equal("Tomorrow", model.Backlog.Single(row => row.Id == tomorrow.Id).DateText);
        Assert.True(Assert.Single(model.Projects).IsOverdue);

        var empty = work.CreateProject("Empty", "", "home", new DateOnly(2026, 9, 28));
        model = new ProjectCaptureViewModel(work, new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero)));
        Assert.True(model.Projects.Single(row => row.Id == empty.Id).IsOverdue);
        Assert.Equal("No date", model.Backlog.Single(row => row.Id == undated.Id).DateText);
    }

    [Fact]
    public void DatePresentationRefreshesAfterLocalMidnightAndTimeZoneChange()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", new DateOnly(2026, 9, 29));
        var task = work.CreateTask(project.Id, "Water");
        work.UpdateTask(task.Id, task.Title, "", null, new DateOnly(2026, 9, 30));
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 23, 30, 0, TimeSpan.Zero));
        var model = new ProjectCaptureViewModel(work, time);

        Assert.Equal("Tomorrow", model.Backlog.Single().DateText);
        Assert.False(model.Projects.Single().IsOverdue);
        Assert.False(model.RefreshDatePresentation());

        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("UTC+02", TimeSpan.FromHours(2), "UTC+02", "UTC+02");
        time.Set(time.GetUtcNow(), plusTwo);
        Assert.True(model.RefreshDatePresentation());
        Assert.Equal("Today", model.Backlog.Single().DateText);
        Assert.True(model.Projects.Single().IsOverdue);

        time.Set(new DateTimeOffset(2026, 9, 30, 22, 30, 0, TimeSpan.Zero));
        Assert.True(model.RefreshDatePresentation());
        Assert.Equal("30 Sep 2026", model.Backlog.Single().DateText);
    }

    [Fact]
    public void UpcomingGroupsOverdueFirstThenDatesWithSharedOrderTieBreaks()
    {
        var work = new MemoryWorkspaceWork();
        var today = new DateOnly(2026, 10, 4);
        var sameDateLater = work.CreateStandaloneTask("Same date later", "", "home", today.AddDays(2));
        var outside = work.CreateStandaloneTask("Outside", "", "home", today.AddDays(8));
        var sameDateEarlier = work.CreateStandaloneTask("Same date earlier", "", "home", today.AddDays(2));
        var tomorrow = work.CreateStandaloneTask("Tomorrow", "", "home", today.AddDays(1));
        var todayTask = work.CreateStandaloneTask("Today", "", "home", today);
        var recentOverdue = work.CreateStandaloneTask("Recent overdue", "", "home", today.AddDays(-1));
        var oldestOverdue = work.CreateStandaloneTask("Oldest overdue", "", "home", today.AddDays(-5));
        var undated = work.CreateStandaloneTask("Undated", "", "home", null);
        var completed = work.CreateStandaloneTask("Completed", "", "home", today);
        work.SetCompletion(completed.Id, new DateTimeOffset(2026, 10, 4, 9, 0, 0, TimeSpan.Zero), today);
        var project = work.CreateProject("Target only", "", "home", today.AddDays(1));
        work.MoveTaskInSharedOrder(sameDateLater.Id, 0);
        var model = new ProjectCaptureViewModel(work,
            new FixedTimeProvider(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero)));

        Assert.Equal(["Overdue", "Today", "Tomorrow", "Tuesday, 6 October 2026"],
            model.UpcomingGroups.Select(group => group.Heading));
        Assert.Equal([oldestOverdue.Id, recentOverdue.Id], model.UpcomingGroups[0].Rows.Select(row => row.Task.Id));
        Assert.Equal([sameDateLater.Id, sameDateEarlier.Id], model.UpcomingGroups[^1].Rows.Select(row => row.Task.Id));
        Assert.Equal(6, model.UpcomingCount);
        var excludedIds = new[] { outside.Id, undated.Id, completed.Id };
        Assert.DoesNotContain(model.UpcomingGroups.SelectMany(group => group.Rows), row => excludedIds.Contains(row.Task.Id));
        Assert.DoesNotContain(model.UpcomingGroups.SelectMany(group => group.Rows), row => row.Task.Title == project.Title);
    }

    [Fact]
    public void UpcomingActionsAndClockRefreshPreserveSharedOrderAndRegroupImmediately()
    {
        var work = new MemoryWorkspaceWork();
        var due = work.CreateStandaloneTask("Due", "", "home", new DateOnly(2026, 10, 5));
        var other = work.CreateStandaloneTask("Other", "", "home", new DateOnly(2026, 10, 6));
        var enteringBoundary = work.CreateStandaloneTask("Entering boundary", "", "home", new DateOnly(2026, 10, 12));
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 10, 4, 23, 30, 0, TimeSpan.Zero));
        var model = new ProjectCaptureViewModel(work, time);
        var originalOrder = work.Read().Tasks.Select(task => (task.Id, task.SharedPosition)).ToArray();

        model.UpcomingGroups.SelectMany(group => group.Rows).Single(row => row.Task.Id == due.Id)
            .Task.ToggleTodayCommand.Execute(null);
        Assert.Equal(TodayLane.Planned, work.Read().Tasks.Single(task => task.Id == due.Id).TodayLane);
        Assert.Equal(originalOrder, work.Read().Tasks.Select(task => (task.Id, task.SharedPosition)));

        model.SelectTask(due.Id);
        model.Date = "2026-10-20";
        Assert.True(model.RunScheduledAutosave());
        Assert.DoesNotContain(model.UpcomingGroups.SelectMany(group => group.Rows), row => row.Task.Id == due.Id);
        Assert.Equal(originalOrder, work.Read().Tasks.Select(task => (task.Id, task.SharedPosition)));

        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("UTC+02-upcoming", TimeSpan.FromHours(2), "UTC+02", "UTC+02");
        time.Set(time.GetUtcNow(), plusTwo);
        Assert.True(model.RefreshDatePresentation());
        Assert.Equal(["Tomorrow", "Monday, 12 October 2026"], model.UpcomingGroups.Select(group => group.Heading));
        Assert.Equal([other.Id, enteringBoundary.Id],
            model.UpcomingGroups.SelectMany(group => group.Rows).Select(row => row.Task.Id));
        Assert.Equal(2, model.UpcomingCount);
    }

    [Fact]
    public void TodayStarDefaultsToPlannedLaneMovementPreservesOrderAndClearRemovesMembership()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var first = work.CreateTask(project.Id, "First");
        var second = work.CreateTask(project.Id, "Second");
        var shell = new ShellViewModel(work);
        var model = shell.Work!;
        var originalOrder = work.Read().Tasks.Select(task => task.Id).ToArray();

        model.Backlog.Single(task => task.Id == first.Id).ToggleTodayCommand.Execute(null);
        Assert.Equal(TodayLane.Planned, work.Read().Tasks.Single(task => task.Id == first.Id).TodayLane);
        Assert.True(model.Backlog.Single(task => task.Id == first.Id).IsToday);
        Assert.Equal($"Remove First from Today", model.Backlog.Single(task => task.Id == first.Id).TodayAccessibleName);
        Assert.Single(model.TodayPlanned);
        Assert.Equal("1", shell.PrimaryNavigation.Single(item => item.Title == "Today").CountText);

        model.TodayPlanned.Single().MoveToOtherLaneCommand.Execute(null);
        Assert.Empty(model.TodayPlanned);
        Assert.Equal(first.Id, Assert.Single(model.TodayInProgress).Task.Id);
        Assert.Equal(originalOrder, work.Read().Tasks.Select(task => task.Id));

        model.Backlog.Single(task => task.Id == second.Id).ToggleTodayCommand.Execute(null);
        model.ClearTodayCommand.Execute(null);
        Assert.True(model.HasNoTodayTasks);
        Assert.All(work.Read().Tasks, task => Assert.Null(task.TodayLane));
        Assert.Equal("0", shell.PrimaryNavigation.Single(item => item.Title == "Today").CountText);
        Assert.Equal("Cleared 2 Tasks from Today.", model.TodayAnnouncement);
    }

    [Fact]
    public void TodayTaskDraftDefaultsToPlannedAndCanSelectAnOptionalProjectBeforeCreation()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var model = new ProjectCaptureViewModel(work);

        model.NewTodayTaskCommand.Execute(null);

        Assert.True(model.ShowTaskContext);
        Assert.False(model.ShowTaskContextAction);
        Assert.Null(model.TaskContextTarget?.ProjectId);
        model.TaskContextTarget = model.TaskContextChoices.Single(choice => choice.ProjectId == project.Id);
        Assert.Null(model.Category?.Id);
        model.Title = "Plant bulbs";
        model.Description = "Near the fence";
        Assert.True(model.Save());

        var task = work.Read().Tasks.Single();
        Assert.Equal(project.Id, task.ProjectId);
        Assert.Null(task.ExplicitCategoryId);
        Assert.Equal("Near the fence", task.Description);
        Assert.Equal(TodayLane.Planned, task.TodayLane);
    }

    [Fact]
    public void TodayImmediateActionsWaitForTheCategoryDraftDecision()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var first = work.CreateTask(project.Id, "First");
        var second = work.CreateTask(project.Id, "Second");
        work.SetTaskTodayLane(first.Id, TodayLane.Planned);
        work.SetTaskTodayLane(second.Id, TodayLane.Planned);
        var model = new ProjectCaptureViewModel(work);
        var initialOrder = model.TodayPlanned.Select(row => row.Task.Id).ToArray();
        model.SelectCategory("home");
        model.Title = "Renamed home";

        model.TodayPlanned.Single(row => row.Task.Id == initialOrder[1]).MoveToTopCommand.Execute(null);

        Assert.True(model.NeedsDecision);
        Assert.Equal(initialOrder, model.TodayPlanned.Select(row => row.Task.Id));
        model.DiscardAndLeaveCommand.Execute(null);
        Assert.Equal(initialOrder.Reverse(), model.TodayPlanned.Select(row => row.Task.Id));
    }

    [Fact]
    public void TodayLaneReorderChangesOnlyVisibleRelativeSharedPositions()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var hidden = work.CreateTask(project.Id, "Hidden");
        var older = work.CreateTask(project.Id, "Older");
        var otherLane = work.CreateTask(project.Id, "Other lane");
        var newer = work.CreateTask(project.Id, "Newer");
        work.MoveTaskInSharedOrder(hidden.Id, 1);
        work.SetTaskTodayLane(older.Id, TodayLane.Planned);
        work.SetTaskTodayLane(otherLane.Id, TodayLane.InProgress);
        work.SetTaskTodayLane(newer.Id, TodayLane.Planned);
        var model = new ProjectCaptureViewModel(work);

        model.TodayPlanned.Single(row => row.Task.Id == older.Id).MoveToTopCommand.Execute(null);

        Assert.Equal([older.Id, hidden.Id, otherLane.Id, newer.Id], work.Read().Tasks.Select(task => task.Id));
        Assert.Equal([older.Id, newer.Id], model.TodayPlanned.Select(row => row.Task.Id));
        Assert.Equal("Moved Older to position 1 of 2 in Planned.", model.TodayAnnouncement);
    }

    [Fact]
    public void CompletedTodayUsesCapturedDateNewestFirstAndReopenReturnsOnlyToBacklog()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var previouslyToday = work.CreateTask(project.Id, "Previously Today");
        var neverToday = work.CreateTask(project.Id, "Never Today");
        work.SetTaskTodayLane(previouslyToday.Id, TodayLane.Planned);
        work.SetCompletion(previouslyToday.Id, new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 29));
        work.SetTaskTodayLane(previouslyToday.Id, null);
        work.SetCompletion(neverToday.Id, new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero), new DateOnly(2026, 9, 29));
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 29, 14, 0, 0, TimeSpan.Zero));
        var model = new ProjectCaptureViewModel(work, time);

        Assert.Equal([neverToday.Id, previouslyToday.Id], model.CompletedToday.Select(row => row.Task.Id));
        model.CompletedToday[0].Task.ToggleCompletionCommand.Execute(null);
        Assert.Contains(model.Backlog, task => task.Id == neverToday.Id);
        Assert.Null(work.Read().Tasks.Single(task => task.Id == neverToday.Id).TodayLane);

        time.Set(new DateTimeOffset(2026, 9, 30, 0, 1, 0, TimeSpan.Zero));
        Assert.True(model.RefreshDatePresentation());
        Assert.Empty(model.CompletedToday);
    }

    [Fact]
    public async Task ThirtyTwoRapidTodayTogglesStayPendingThenCommitExactlyOnceInOrder()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Burst", "", "home", null);
        var actions = new BlockingWorkspaceActionExecutor(work);
        var model = new ProjectCaptureViewModel(
            work,
            null,
            new InlineWorkspacePersistenceExecutor(new WorkspacePersistenceOperation(work)),
            actions,
            new InlineUiDispatcher());

        for (var index = 0; index < 32; index++) model.ToggleToday(task.Id);

        Assert.Equal(32, model.PendingWorkspaceActionCount);
        Assert.Equal("Saving 32 changes…", model.Message);
        Assert.Null(work.Read().Tasks.Single(item => item.Id == task.Id).TodayLane);
        actions.Release.Set();
        await model.WaitForWorkspaceActionsAsync().WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(32, actions.Requests.Count);
        Assert.All(actions.Requests, request => Assert.Equal(WorkspaceActionKind.ToggleToday, request.Kind));
        Assert.Null(work.Read().Tasks.Single(item => item.Id == task.Id).TodayLane);
        Assert.Equal(0, model.PendingWorkspaceActionCount);
        var timing = Assert.IsType<WorkspaceActionPerformanceTiming>(model.LastWorkspaceActionTiming);
        Assert.True(timing.TotalCompletion >= timing.DispatcherApplication);
        Assert.True(timing.Persistence >= TimeSpan.Zero);
        Assert.True(timing.QueueWait >= TimeSpan.Zero);
    }

    [Fact]
    public async Task QueueWaitStartsAtUiAdmissionBeforeTheViewModelQueue()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Queue timing", "", "home", null);
        var timeProvider = new ManualTimestampProvider();
        var operation = new FirstActionBlockingOperation(work, timeProvider);
        await using var executor = new SerializedWorkspacePersistenceExecutor(operation, timeProvider);
        var model = new ProjectCaptureViewModel(
            work,
            timeProvider,
            executor,
            executor,
            new InlineUiDispatcher());

        model.ToggleToday(task.Id);
        model.ToggleToday(task.Id);
        await operation.FirstStarted.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromMilliseconds(40));
        operation.Release.Set();
        await model.WaitForWorkspaceActionsAsync().WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var timing = Assert.IsType<WorkspaceActionPerformanceTiming>(model.LastWorkspaceActionTiming);
        Assert.Equal(TimeSpan.FromMilliseconds(40), timing.QueueWait);
    }

    [Fact]
    public async Task RepeatedMoveDownIntentsResolveAgainstThePrecedingCommittedOrder()
    {
        var work = new MemoryWorkspaceWork();
        var third = work.CreateStandaloneTask("Third", "", "home", null);
        var second = work.CreateStandaloneTask("Second", "", "home", null);
        var first = work.CreateStandaloneTask("First", "", "home", null);
        var actions = new BlockingWorkspaceActionExecutor(work);
        var model = new ProjectCaptureViewModel(
            work,
            null,
            new InlineWorkspacePersistenceExecutor(new WorkspacePersistenceOperation(work)),
            actions,
            new InlineUiDispatcher());

        model.MoveDown(first.Id);
        model.MoveDown(first.Id);
        actions.Release.Set();
        await model.WaitForWorkspaceActionsAsync().WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal([second.Id, third.Id, first.Id], model.Backlog.Select(row => row.Id));
        Assert.Equal(2, actions.Requests.Count);
        Assert.All(actions.Requests, request => Assert.Equal(WorkspaceMoveKind.Down, request.Move));
    }

    [Fact]
    public async Task FailedDraftFlushBlocksTheQueuedActionUntilTheDecisionIsResolved()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Original", "", "home", null);
        var writer = new ControlledInspectorSaveWriter(work);
        var actions = new RecordingWorkspaceActionExecutor(work);
        var model = new ProjectCaptureViewModel(work, null, writer, actions, new InlineUiDispatcher());
        model.SelectTask(task.Id);
        model.Title = "Unsaved";
        Assert.True(model.RunScheduledAutosave());
        await writer.WaitForSubmissionCountAsync(1);

        model.ToggleToday(task.Id);
        writer.FailNext();
        await WaitUntilAsync(() => model.NeedsDecision);

        Assert.Empty(actions.Requests);
        Assert.Null(work.Read().Tasks.Single(item => item.Id == task.Id).TodayLane);
        Assert.Equal(1, model.PendingWorkspaceActionCount);
        model.StayCommand.Execute(null);
        model.Title = "Corrected";
        Assert.True(model.RunScheduledAutosave());
        await writer.WaitForSubmissionCountAsync(2);
        writer.CompleteNext();
        await model.WaitForWorkspaceActionsAsync().WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal([WorkspaceActionKind.ToggleToday, WorkspaceActionKind.Refresh],
            actions.Requests.Select(request => request.Kind));
        Assert.Equal(TodayLane.Planned, work.Read().Tasks.Single(item => item.Id == task.Id).TodayLane);
    }

    [Fact]
    public async Task SuccessfulCategorySaveResumesAnActionBlockedByItsDraft()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Category guard", "", "home", null);
        var actions = new RecordingWorkspaceActionExecutor(work);
        var model = new ProjectCaptureViewModel(
            work,
            null,
            new InlineWorkspacePersistenceExecutor(new WorkspacePersistenceOperation(work)),
            actions,
            new InlineUiDispatcher());
        model.SelectCategory("home");
        model.Title = "Renamed home";

        model.ToggleToday(task.Id);
        Assert.True(model.NeedsDecision);
        Assert.Empty(actions.Requests);
        model.StayCommand.Execute(null);
        model.SaveCommand.Execute(null);
        await model.WaitForWorkspaceActionsAsync().WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal([WorkspaceActionKind.ToggleToday, WorkspaceActionKind.Refresh],
            actions.Requests.Select(request => request.Kind));
        Assert.Equal(TodayLane.Planned, work.Read().Tasks.Single(item => item.Id == task.Id).TodayLane);
    }

    [Fact]
    public async Task NavigationClosesActionAdmissionBeforeAwaitingDraftFlush()
    {
        var work = new MemoryWorkspaceWork();
        var edited = work.CreateStandaloneTask("Edited", "", "home", null);
        var actionTarget = work.CreateStandaloneTask("Action", "", "home", null);
        var writer = new ControlledInspectorSaveWriter(work);
        var actions = new RecordingWorkspaceActionExecutor(work);
        var model = new ProjectCaptureViewModel(work, null, writer, actions, new InlineUiDispatcher());
        model.SelectTask(edited.Id);
        model.Title = "New title";
        var navigated = false;

        var navigation = model.NavigateAsync(() => navigated = true);
        await writer.WaitForSubmissionCountAsync(1);
        model.ToggleToday(actionTarget.Id);

        Assert.Equal(0, model.PendingWorkspaceActionCount);
        Assert.Empty(actions.Requests);
        writer.CompleteNext();
        await navigation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(navigated);
        Assert.Null(work.Read().Tasks.Single(item => item.Id == actionTarget.Id).TodayLane);
    }

    [Fact]
    public async Task StayingAfterFailedNavigationReopensWorkspaceActionAdmission()
    {
        var work = new MemoryWorkspaceWork();
        var edited = work.CreateStandaloneTask("Edited", "", "home", null);
        var actionTarget = work.CreateStandaloneTask("Action", "", "home", null);
        var writer = new ControlledInspectorSaveWriter(work);
        var actions = new RecordingWorkspaceActionExecutor(work);
        var model = new ProjectCaptureViewModel(work, null, writer, actions, new InlineUiDispatcher());
        model.SelectTask(edited.Id);
        model.Title = "New title";

        _ = model.NavigateAsync(() => { });
        await writer.WaitForSubmissionCountAsync(1);
        writer.FailNext();
        await WaitUntilAsync(() => model.NeedsDecision);
        model.StayCommand.Execute(null);

        model.ToggleToday(actionTarget.Id);
        await writer.WaitForSubmissionCountAsync(2);
        Assert.Equal(1, model.PendingWorkspaceActionCount);
        writer.CompleteNext();
        await model.WaitForWorkspaceActionsAsync().WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(WorkspaceActionKind.ToggleToday, Assert.Single(actions.Requests).Kind);
        Assert.Equal(TodayLane.Planned, work.Read().Tasks.Single(item => item.Id == actionTarget.Id).TodayLane);
    }

    [Fact]
    public async Task LateActionResultRefreshesAfterANewerInspectorRevision()
    {
        var work = new MemoryWorkspaceWork();
        var edited = work.CreateStandaloneTask("Original", "", "home", null);
        var actionTarget = work.CreateStandaloneTask("Action", "", "home", null);
        var actions = new CapturedActionExecutor(work);
        var model = new ProjectCaptureViewModel(
            work,
            null,
            new InlineWorkspacePersistenceExecutor(new WorkspacePersistenceOperation(work)),
            actions,
            new InlineUiDispatcher());
        model.SelectTask(edited.Id);
        model.ToggleToday(actionTarget.Id);
        await actions.ResultCaptured.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        model.Title = "Newer revision";
        Assert.True(model.RunScheduledAutosave());
        actions.Release.Set();
        await model.WaitForWorkspaceActionsAsync().WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("Newer revision", model.Title);
        Assert.Equal("Newer revision", model.Backlog.Single(row => row.Id == edited.Id).Title);
        Assert.Equal([WorkspaceActionKind.ToggleToday, WorkspaceActionKind.Refresh],
            actions.Requests.Select(request => request.Kind));
    }

    [Fact]
    public async Task CommittedRefreshFailureRetriesOnlyTheRefreshAndDoesNotRepeatTheToggle()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Refresh", "", "home", null);
        var actions = new RecordingWorkspaceActionExecutor(work) { FailFirstCommittedRefresh = true };
        var model = new ProjectCaptureViewModel(
            work,
            null,
            new InlineWorkspacePersistenceExecutor(new WorkspacePersistenceOperation(work)),
            actions,
            new InlineUiDispatcher());

        model.ToggleToday(task.Id);
        await model.WaitForWorkspaceActionsAsync().WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(model.CanRetryWorkspaceRefresh);
        Assert.Equal("Change saved, but the view could not refresh. Retry refresh.", model.Message);
        Assert.Equal(TodayLane.Planned, work.Read().Tasks.Single(item => item.Id == task.Id).TodayLane);

        model.RetryWorkspaceRefreshCommand.Execute(null);
        await model.WaitForWorkspaceActionsAsync().WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal([WorkspaceActionKind.ToggleToday, WorkspaceActionKind.Refresh],
            actions.Requests.Select(request => request.Kind));
        Assert.Equal(TodayLane.Planned, work.Read().Tasks.Single(item => item.Id == task.Id).TodayLane);
        Assert.Equal(task.Id, Assert.Single(model.TodayPlanned).Task.Id);
        Assert.False(model.CanRetryWorkspaceRefresh);
    }

    [Fact]
    public async Task FailedDiscreteMutationDoesNotPoisonTheNextAcceptedAction()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Failure", "", "home", null);
        var actions = new RecordingWorkspaceActionExecutor(work) { FailFirstMutation = true };
        var model = new ProjectCaptureViewModel(
            work,
            null,
            new InlineWorkspacePersistenceExecutor(new WorkspacePersistenceOperation(work)),
            actions,
            new InlineUiDispatcher());

        model.ToggleToday(task.Id);
        model.ToggleToday(task.Id);
        await model.WaitForWorkspaceActionsAsync().WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, actions.Requests.Count);
        Assert.Equal(TodayLane.Planned, work.Read().Tasks.Single(item => item.Id == task.Id).TodayLane);
        Assert.Equal("Added Failure to Today in Planned.", model.Message);
    }

    [Fact]
    public async Task FailedQueuedActionRemainsAnnouncedWhileTheNextActionIsPending()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Failure", "", "home", null);
        var actions = new FailureThenBlockingActionExecutor(work);
        var model = new ProjectCaptureViewModel(
            work,
            null,
            new InlineWorkspacePersistenceExecutor(new WorkspacePersistenceOperation(work)),
            actions,
            new InlineUiDispatcher());

        model.ToggleToday(task.Id);
        model.ToggleToday(task.Id);
        actions.FailFirst.Set();
        await actions.SecondSubmitted.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, model.PendingWorkspaceActionCount);
        Assert.Equal(
            "Could not change Today membership. No changes were made. Saving 1 change…",
            model.Message);
        actions.Release.Set();
        await model.WaitForWorkspaceActionsAsync().WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NavigationStopsAdmissionAndWaitsForAcceptedWorkspaceActions()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Navigate", "", "home", null);
        var actions = new BlockingWorkspaceActionExecutor(work);
        var model = new ProjectCaptureViewModel(
            work,
            null,
            new InlineWorkspacePersistenceExecutor(new WorkspacePersistenceOperation(work)),
            actions,
            new InlineUiDispatcher());
        model.ToggleToday(task.Id);
        var navigated = false;

        var navigation = model.NavigateAsync(() => navigated = true);
        model.ToggleToday(task.Id);

        Assert.False(navigation.IsCompleted);
        Assert.False(navigated);
        Assert.Equal(1, model.PendingWorkspaceActionCount);
        Assert.Equal("Finishing 1 change before leaving…", model.Message);
        actions.Release.Set();
        await navigation.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(navigated);
        Assert.Single(actions.Requests);
        Assert.Equal(TodayLane.Planned, work.Read().Tasks.Single(item => item.Id == task.Id).TodayLane);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token, TestContext.Current.CancellationToken);
        while (!condition()) await Task.Delay(1, cancellation.Token);
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset utcNow, TimeZoneInfo? localTimeZone = null) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;
    private TimeZoneInfo _localTimeZone = localTimeZone ?? TimeZoneInfo.Utc;
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public override TimeZoneInfo LocalTimeZone => _localTimeZone;
    public void Set(DateTimeOffset value, TimeZoneInfo? zone = null)
    {
        _utcNow = value;
        if (zone is not null) _localTimeZone = zone;
    }
}

internal sealed class ManualTimestampProvider : TimeProvider
{
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

    public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
}

internal sealed class ControlledInspectorSaveWriter(IWorkspaceWork work)
    : IInspectorSaveWriter, IWorkspaceActionExecutor
{
    private readonly object _gate = new();
    private readonly Queue<(InspectorSaveRequest Request, TaskCompletionSource<InspectorSaveResult> Completion)> _pending = [];
    private readonly WorkspacePersistenceOperation _operation = new(work);
    private TaskCompletionSource _submissionChanged = NewSignal();

    public List<InspectorSaveRequest> Submissions { get; } = [];

    public Task<InspectorSaveResult> SubmitAsync(InspectorSaveRequest request)
    {
        lock (_gate)
        {
            var completion = new TaskCompletionSource<InspectorSaveResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Submissions.Add(request);
            _pending.Enqueue((request, completion));
            _submissionChanged.TrySetResult();
            _submissionChanged = NewSignal();
            return completion.Task;
        }
    }

    public async Task WaitForSubmissionCountAsync(int count)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (Submissions.Count >= count) return;
                wait = _submissionChanged.Task;
            }
            await wait.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    public void CompleteNext()
    {
        var pending = Next();
        pending.Completion.SetResult(_operation.Execute(pending.Request));
    }

    public void FailNext()
    {
        var pending = Next();
        pending.Completion.SetResult(InspectorSaveResult.Failed(pending.Request));
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            while (_pending.Count > 0)
            {
                var pending = _pending.Dequeue();
                pending.Completion.TrySetResult(InspectorSaveResult.Superseded(pending.Request));
            }
        }
        return ValueTask.CompletedTask;
    }

    public Task<WorkspaceActionResult> SubmitActionAsync(WorkspaceActionRequest request) =>
        Task.FromResult(_operation.ExecuteAction(request));

    private (InspectorSaveRequest Request, TaskCompletionSource<InspectorSaveResult> Completion) Next()
    {
        lock (_gate) return _pending.Dequeue();
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class BlockingWorkspaceActionExecutor(IWorkspaceWork work) : IWorkspaceActionExecutor
{
    private readonly WorkspacePersistenceOperation _operation = new(work);
    private readonly object _gate = new();

    public ManualResetEventSlim Release { get; } = new();
    public List<WorkspaceActionRequest> Requests { get; } = [];

    public Task<WorkspaceActionResult> SubmitActionAsync(WorkspaceActionRequest request)
    {
        lock (_gate) Requests.Add(request);
        return Task.Run(() =>
        {
            Release.Wait();
            return _operation.ExecuteAction(request);
        });
    }

}

internal sealed class RecordingWorkspaceActionExecutor(IWorkspaceWork work) : IWorkspaceActionExecutor
{
    private readonly WorkspacePersistenceOperation _operation = new(work);

    public bool FailFirstCommittedRefresh { get; set; }
    public bool FailFirstMutation { get; set; }
    public List<WorkspaceActionRequest> Requests { get; } = [];

    public Task<WorkspaceActionResult> SubmitActionAsync(WorkspaceActionRequest request)
    {
        Requests.Add(request);
        if (FailFirstMutation)
        {
            FailFirstMutation = false;
            return Task.FromResult(new WorkspaceActionResult(request, WorkspaceActionOutcome.MutationFailed));
        }
        var result = _operation.ExecuteAction(request);
        if (FailFirstCommittedRefresh && request.Kind != WorkspaceActionKind.Refresh)
        {
            FailFirstCommittedRefresh = false;
            result = result with { Outcome = WorkspaceActionOutcome.CommittedRefreshFailed, Reload = null };
        }
        return Task.FromResult(result);
    }

}

internal sealed class CapturedActionExecutor(IWorkspaceWork work) : IWorkspaceActionExecutor
{
    private readonly WorkspacePersistenceOperation _operation = new(work);
    private readonly TaskCompletionSource _resultCaptured =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ManualResetEventSlim Release { get; } = new();
    public Task ResultCaptured => _resultCaptured.Task;
    public List<WorkspaceActionRequest> Requests { get; } = [];

    public Task<WorkspaceActionResult> SubmitActionAsync(WorkspaceActionRequest request)
    {
        Requests.Add(request);
        if (request.Kind == WorkspaceActionKind.Refresh)
            return Task.FromResult(_operation.ExecuteAction(request));
        return Task.Run(() =>
        {
            var result = _operation.ExecuteAction(request);
            _resultCaptured.TrySetResult();
            Release.Wait(TestContext.Current.CancellationToken);
            return result;
        }, TestContext.Current.CancellationToken);
    }
}

internal sealed class FailureThenBlockingActionExecutor(IWorkspaceWork work) : IWorkspaceActionExecutor
{
    private readonly WorkspacePersistenceOperation _operation = new(work);
    private readonly TaskCompletionSource _secondSubmitted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _submissionCount;

    public ManualResetEventSlim Release { get; } = new();
    public ManualResetEventSlim FailFirst { get; } = new();
    public Task SecondSubmitted => _secondSubmitted.Task;

    public Task<WorkspaceActionResult> SubmitActionAsync(WorkspaceActionRequest request)
    {
        if (Interlocked.Increment(ref _submissionCount) == 1)
            return Task.Run(() =>
            {
                FailFirst.Wait(TestContext.Current.CancellationToken);
                return new WorkspaceActionResult(request, WorkspaceActionOutcome.MutationFailed);
            }, TestContext.Current.CancellationToken);
        _secondSubmitted.TrySetResult();
        return Task.Run(() =>
        {
            Release.Wait(TestContext.Current.CancellationToken);
            return _operation.ExecuteAction(request);
        }, TestContext.Current.CancellationToken);
    }
}

internal sealed class FirstActionBlockingOperation(
    IWorkspaceWork work,
    TimeProvider timeProvider) : IWorkspacePersistenceOperation
{
    private readonly WorkspacePersistenceOperation _operation = new(work, timeProvider);
    private readonly TaskCompletionSource _firstStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _actionCount;

    public Task FirstStarted => _firstStarted.Task;
    public ManualResetEventSlim Release { get; } = new();

    public InspectorSaveResult Execute(InspectorSaveRequest request) => _operation.Execute(request);

    public WorkspaceActionResult ExecuteAction(WorkspaceActionRequest request)
    {
        if (Interlocked.Increment(ref _actionCount) == 1)
        {
            _firstStarted.TrySetResult();
            Release.Wait(TestContext.Current.CancellationToken);
        }
        return _operation.ExecuteAction(request);
    }
}

internal sealed class MemoryWorkspaceWork : IWorkspaceWork
{
    private readonly List<WorkspaceCategory> _categories = [new("home", "Home", 0, "orchid"), new("work", "Work", 1, "violet")];
    private readonly List<ProjectRecord> _projects = [];
    private readonly HashSet<string> _binnedProjectIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _aggregateTaskIds = new(StringComparer.Ordinal);
    private readonly List<ProjectBinRecord> _projectBin = [];
    private readonly List<TaskRecord> _tasks = [];
    private readonly List<TaskBinRecord> _bin = [];
    private readonly List<ParticipantRecord> _participants = [];
    public bool FailWrites { get; set; }
    public bool FailArchiveSearch { get; set; }
    public DateOnly CurrentDate { get; set; } = new(2026, 9, 29);
    public Action? BeforeBulkArchive { get; set; }
    public int WriteCount { get; private set; }
    public int ReadCount { get; private set; }
    public int TaskBinReadCount { get; private set; }
    public int ProjectBinReadCount { get; private set; }
    public WorkspaceWorkSnapshot Read()
    {
        ReadCount++;
        return new(_categories.OrderBy(category => category.Position)
            .ToArray(), _projects.Where(project => !_binnedProjectIds.Contains(project.Id))
            .OrderBy(project => project.Position).ToArray(), _tasks.Where(task => _bin.All(item => item.Task.Id != task.Id)
                && (task.ProjectId is null || !_binnedProjectIds.Contains(task.ProjectId)))
            .OrderBy(t => t.SharedPosition).ToArray(), _participants.ToArray());
    }
    public IReadOnlyList<TaskBinRecord> ReadTaskBin()
    {
        TaskBinReadCount++;
        return _bin
            .Where(item => !_aggregateTaskIds.Contains(item.Task.Id))
            .Select(item => item.Task.ProjectId is { } projectId && _binnedProjectIds.Contains(projectId)
                ? item with
                {
                    CanRestore = false,
                    RestoreBlockedReason = "Restore the parent Project from Bin before restoring this Task.",
                }
                : item)
            .OrderByDescending(item => item.RemovedAt).ToArray();
    }
    public IReadOnlyList<ProjectBinRecord> ReadProjectBin()
    {
        ProjectBinReadCount++;
        return _projectBin.OrderByDescending(item => item.RemovedAt).ToArray();
    }
    public EmptyBinPreview PreviewEmptyBin() => new(
        _projectBin.Select(item => item.Project.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
        _bin.Select(item => item.Task.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray());
    public void MarkProjectBinned(string projectId) => _binnedProjectIds.Add(projectId);
    public IReadOnlyList<ArchiveSearchResult> SearchArchive(string query)
    {
        if (FailArchiveSearch) throw new WorkspaceWorkException();
        var queryTokens = SearchTokens(query);
        if (queryTokens.Length == 0) return [];
        var results = new List<(DateTimeOffset ArchivedAt, ArchiveSearchResult Result)>();
        foreach (var project in _projects.Where(project => project.IsArchived))
        {
            var description = SanitisedMarkdownRenderer.Render(project.Description).ToPlainText().ReplaceLineEndings(" ");
            var category = _categories.Single(item => item.Id == project.CategoryId).Name;
            if (Matches(queryTokens, project.Title, description, category))
                results.Add((project.ArchivedAt!.Value, new(
                    ArchiveSearchRecordType.Project, project.Id, project.Title, null,
                    ArchiveSearchDateKind.Archived, project.ArchiveDate!.Value,
                    string.IsNullOrWhiteSpace(description) ? project.Title : description)));
        }
        foreach (var task in _tasks.Where(task => task.IsArchived && _bin.All(item => item.Task.Id != task.Id)))
        {
            var parent = task.ProjectId is null ? null : _projects.Single(project => project.Id == task.ProjectId);
            var categoryId = task.ExplicitCategoryId ?? parent!.CategoryId;
            var category = _categories.Single(item => item.Id == categoryId).Name;
            var participants = string.Join(' ', task.Participants.Select(id => _participants.Single(item => item.Id == id).Label));
            var description = SanitisedMarkdownRenderer.Render(task.Description).ToPlainText().ReplaceLineEndings(" ");
            if (Matches(queryTokens, task.Title, description, category, participants, parent?.Title ?? string.Empty))
                results.Add((task.ArchivedAt!.Value, new(
                    ArchiveSearchRecordType.Task, task.Id, task.Title, parent?.Title,
                    ArchiveSearchDateKind.Completed, task.CompletionDate!.Value,
                    string.IsNullOrWhiteSpace(description) ? task.Title : description)));
        }
        return results.OrderByDescending(item => item.ArchivedAt)
            .ThenBy(item => item.Result.RecordType)
            .ThenBy(item => item.Result.Id, StringComparer.Ordinal)
            .Select(item => item.Result)
            .ToArray();
    }

    private static bool Matches(string[] queryTokens, params string[] fields)
    {
        var words = fields.SelectMany(SearchTokens).ToArray();
        return queryTokens.All(token => words.Any(word => word.StartsWith(token, StringComparison.OrdinalIgnoreCase)));
    }

    private static string[] SearchTokens(string value) => value
        .Split(value.Where(character => !char.IsLetterOrDigit(character)).Distinct().ToArray(),
            StringSplitOptions.RemoveEmptyEntries)
        .Select(token => token.Normalize().ToUpperInvariant())
        .ToArray();
    public ParticipantRecord CreateParticipant(string label)
    {
        Check();
        label = ParticipantLabel.Normalize(label);
        if (_participants.Any(item => ParticipantLabel.ComparisonKey(item.Label) == ParticipantLabel.ComparisonKey(label)))
            throw new ArgumentException("Participant label unavailable.", nameof(label));
        var participant = new ParticipantRecord($"participant-{_participants.Count}", label);
        _participants.Add(participant);
        return participant;
    }
    public ParticipantRecord RenameParticipant(string id, string label)
    {
        Check();
        label = ParticipantLabel.Normalize(label);
        if (_participants.Any(item => item.Id != id
            && ParticipantLabel.ComparisonKey(item.Label) == ParticipantLabel.ComparisonKey(label)))
            throw new ArgumentException("Participant label unavailable.", nameof(label));
        var index = _participants.FindIndex(item => item.Id == id);
        return _participants[index] = _participants[index] with { Label = label };
    }
    public void DeleteParticipant(string id)
    {
        Check();
        if (_tasks.Any(task => task.Participants.Contains(id))) throw new InvalidOperationException();
        _participants.RemoveAll(item => item.Id == id);
    }
    public WorkspaceCategory CreateCategory(string name, string? colourKey = null)
    {
        Check();
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name) || _categories.Any(category => string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Category name unavailable.", nameof(name));
        var identity = CategoryIdentity.Create(colourKey ?? IdentityColourPalette.KeyForPosition(_categories.Count));
        var category = new WorkspaceCategory($"category-{_categories.Count}", name, _categories.Count, identity.ColourKey);
        _categories.Add(category);
        return category;
    }
    public WorkspaceCategory RenameCategory(string id, string name)
    {
        Check();
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name) || _categories.Any(category => category.Id != id && string.Equals(category.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Category name unavailable.", nameof(name));
        var index = _categories.FindIndex(category => category.Id == id);
        return _categories[index] = _categories[index] with { Name = name };
    }
    public WorkspaceCategory UpdateCategory(string id, string name, string colourKey)
    {
        var identity = CategoryIdentity.Create(colourKey);
        var renamed = RenameCategory(id, name);
        var index = _categories.FindIndex(category => category.Id == id);
        return _categories[index] = renamed with { ColourKey = identity.ColourKey };
    }
    public void DeleteCategory(string id, string? replacementCategoryId = null)
    {
        Check();
        if (_categories.Count == 1) throw new InvalidOperationException();
        var referenced = _projects.Any(project => project.CategoryId == id) || _tasks.Any(task => task.ExplicitCategoryId == id);
        if (referenced && (replacementCategoryId is null || replacementCategoryId == id))
            throw new ArgumentException("Replacement required.", nameof(replacementCategoryId));
        if (replacementCategoryId is not null && _categories.All(category => category.Id != replacementCategoryId))
            throw new ArgumentException("Replacement missing.", nameof(replacementCategoryId));
        for (var index = 0; index < _projects.Count; index++)
            if (_projects[index].CategoryId == id) _projects[index] = _projects[index] with { CategoryId = replacementCategoryId! };
        for (var index = 0; index < _tasks.Count; index++)
            if (_tasks[index].ExplicitCategoryId == id) _tasks[index] = _tasks[index] with { ExplicitCategoryId = replacementCategoryId };
        _categories.RemoveAll(category => category.Id == id);
        RewriteCategoryPositions();
    }
    public CategoryOrderChange MoveCategory(string id, int targetPosition)
    {
        Check();
        if ((uint)targetPosition >= (uint)_categories.Count) throw new ArgumentOutOfRangeException(nameof(targetPosition));
        var category = _categories.Single(item => item.Id == id);
        _categories.Remove(category);
        _categories.Insert(targetPosition, category);
        RewriteCategoryPositions();
        return new(id, targetPosition + 1, _categories.Count);
    }
    public ProjectRecord CreateProject(string title, string description, string categoryId, DateOnly? targetDate,
        string? colourKey = null)
    {
        Check();
        var identity = ProjectIdentity.Create(colourKey ?? IdentityColourPalette.KeyForProjectPosition(_projects.Count));
        var project = new ProjectRecord($"project-{_projects.Count}", title.Trim(), description, categoryId, targetDate,
            _projects.Count, ColourKey: identity.ColourKey);
        _projects.Add(project); return project;
    }
    public TaskRecord CreateTask(string projectId, string title)
    {
        Check();
        if (_projects.Single(project => project.Id == projectId).IsArchived) throw new InvalidOperationException();
        ShiftForNewTask();
        var task = new TaskRecord($"task-{_tasks.Count}", projectId, title.Trim(), "", null, null, 0, _tasks.Count(t => t.ProjectId == projectId));
        _tasks.Add(task); return task;
    }
    public TaskRecord CreateTaskDraft(string? projectId, string title, string description, string? categoryId,
        DateOnly? dueDate, ParticipantDraftChange? participantChange = null, TodayLane? todayLane = null)
    {
        Check();
        if (projectId is null && categoryId is null) throw new ArgumentException("Category required.", nameof(categoryId));
        if (projectId is not null && _projects.All(project => project.Id != projectId)) throw new ArgumentException("Project missing.", nameof(projectId));
        if (projectId is not null && _projects.Single(project => project.Id == projectId).IsArchived) throw new InvalidOperationException();
        var taskBackup = _tasks.ToList();
        var participantBackup = _participants.ToList();
        ShiftForNewTask();
        try
        {
            var associations = ApplyParticipantChanges(participantChange ?? new([], []));
            var task = new TaskRecord($"task-{_tasks.Count}", projectId, title.Trim(), description, categoryId, dueDate, 0,
                projectId is null ? null : _tasks.Count(item => item.ProjectId == projectId),
                ParticipantIds: associations, TodayLane: todayLane);
            _tasks.Add(task);
            return task;
        }
        catch
        {
            _tasks.Clear();
            _tasks.AddRange(taskBackup);
            _participants.Clear();
            _participants.AddRange(participantBackup);
            throw;
        }
    }
    public TaskRecord CreateStandaloneTask(string title, string description, string categoryId, DateOnly? dueDate,
        ParticipantDraftChange? participantChange = null)
    {
        Check();
        if (string.IsNullOrWhiteSpace(categoryId)) throw new ArgumentException("Category required.", nameof(categoryId));
        var taskBackup = _tasks.ToList();
        var participantBackup = _participants.ToList();
        ShiftForNewTask();
        try
        {
            var taskId = $"task-{_tasks.Count}";
            var associations = ApplyParticipantChanges(participantChange ?? new([], []));
            var task = new TaskRecord(taskId, null, title.Trim(), description, categoryId, dueDate, 0, null, ParticipantIds: associations);
            _tasks.Add(task); return task;
        }
        catch
        {
            _tasks.Clear();
            _tasks.AddRange(taskBackup);
            _participants.Clear();
            _participants.AddRange(participantBackup);
            throw;
        }
    }
    public ProjectRecord UpdateProject(string id, string title, string description, string categoryId, DateOnly? targetDate,
        string? colourKey = null)
    {
        Check();
        int index = _projects.FindIndex(p => p.Id == id);
        var identity = colourKey is null ? _projects[index].ColourKey : ProjectIdentity.Create(colourKey).ColourKey;
        return _projects[index] = _projects[index] with
        {
            Title = title.Trim(),
            Description = description,
            CategoryId = categoryId,
            TargetDate = targetDate,
            ColourKey = identity,
        };
    }
    public TaskRecord UpdateTask(string id, string title, string description, string? explicitCategoryId, DateOnly? dueDate,
        ParticipantDraftChange? participantChange = null)
    {
        Check();
        int index = _tasks.FindIndex(t => t.Id == id);
        if (_tasks[index].ProjectId is null && explicitCategoryId is null) throw new ArgumentException("Category required.", nameof(explicitCategoryId));
        var taskBackup = _tasks.ToList();
        var participantBackup = _participants.ToList();
        try
        {
            var associations = participantChange is null
                ? _tasks[index].Participants.ToList()
                : ApplyParticipantChanges(participantChange);
            return _tasks[index] = _tasks[index] with { Title = title.Trim(), Description = description, ExplicitCategoryId = explicitCategoryId, DueDate = dueDate, ParticipantIds = associations };
        }
        catch
        {
            _tasks.Clear();
            _tasks.AddRange(taskBackup);
            _participants.Clear();
            _participants.AddRange(participantBackup);
            throw;
        }
    }
    public TaskRecord CompleteTask(string id)
    {
        Check();
        int index = _tasks.FindIndex(task => task.Id == id);
        if (_tasks[index].IsArchived) throw new InvalidOperationException();
        return _tasks[index] = _tasks[index] with
        {
            CompletedAt = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero),
            CompletionDate = new DateOnly(2026, 9, 29),
            TodayLane = null,
        };
    }
    public TaskRecord ReopenTask(string id)
    {
        Check();
        int index = _tasks.FindIndex(task => task.Id == id);
        if (_tasks[index].IsArchived) throw new InvalidOperationException();
        return _tasks[index] = _tasks[index] with { CompletedAt = null, CompletionDate = null };
    }
    public TaskRecord ArchiveTask(string id)
    {
        Check();
        var index = _tasks.FindIndex(task => task.Id == id);
        if (!_tasks[index].IsComplete || _tasks[index].IsArchived) throw new InvalidOperationException();
        return _tasks[index] = _tasks[index] with
        {
            TodayLane = null,
            ArchivedAt = new DateTimeOffset(2026, 9, 29, 13, 0, 0, TimeSpan.Zero),
            ArchiveDate = new DateOnly(2026, 9, 29),
        };
    }
    public TaskRecord RestoreTask(string id)
    {
        Check();
        var index = _tasks.FindIndex(task => task.Id == id);
        if (!_tasks[index].IsArchived) throw new InvalidOperationException();
        return _tasks[index] = _tasks[index] with { ArchivedAt = null, ArchiveDate = null };
    }
    public TaskBinRecord MoveTaskToBin(string id)
    {
        Check();
        if (_bin.Any(item => item.Task.Id == id)) throw new InvalidOperationException();
        var index = _tasks.FindIndex(task => task.Id == id);
        var prior = _tasks[index];
        var project = prior.ProjectId is null ? null : _projects.Single(item => item.Id == prior.ProjectId);
        var categoryId = prior.ExplicitCategoryId ?? project!.CategoryId;
        var removed = new TaskBinRecord(prior, new DateTimeOffset(CurrentDate, new TimeOnly(14, 0), TimeSpan.Zero),
            project?.Title, _categories.Single(item => item.Id == categoryId).Name, true, null);
        _tasks[index] = prior with { TodayLane = null };
        _bin.Add(removed);
        return removed;
    }
    public TaskRecord RestoreTaskFromBin(string id)
    {
        Check();
        var removed = _bin.Single(item => item.Task.Id == id);
        _bin.Remove(removed);
        var index = _tasks.FindIndex(task => task.Id == id);
        return _tasks[index] = removed.Task;
    }
    public ProjectBinRecord MoveProjectToBin(string id)
    {
        Check();
        if (_binnedProjectIds.Contains(id)) throw new InvalidOperationException();
        var project = _projects.Single(item => item.Id == id);
        var removedAt = new DateTimeOffset(CurrentDate, new TimeOnly(14, 0), TimeSpan.Zero);
        _binnedProjectIds.Add(id);
        foreach (var task in _tasks.Where(item => item.ProjectId == id).ToArray())
        {
            if (_bin.Any(item => item.Task.Id == task.Id)) continue;
            MoveTaskToBin(task.Id);
            _aggregateTaskIds.Add(task.Id);
        }
        var record = new ProjectBinRecord(project, removedAt,
            _categories.Single(item => item.Id == project.CategoryId).Name,
            _tasks.Count(item => item.ProjectId == id));
        _projectBin.Add(record);
        return record;
    }
    public ProjectRecord RestoreProjectFromBin(string id)
    {
        Check();
        var removed = _projectBin.Single(item => item.Project.Id == id);
        foreach (var taskId in _aggregateTaskIds.Where(taskId =>
                     _tasks.Single(item => item.Id == taskId).ProjectId == id).ToArray())
        {
            _aggregateTaskIds.Remove(taskId);
            RestoreTaskFromBin(taskId);
        }
        _projectBin.Remove(removed);
        _binnedProjectIds.Remove(id);
        return removed.Project;
    }
    public EmptyBinResult EmptyBin(EmptyBinPreview confirmedPreview)
    {
        Check();
        var current = PreviewEmptyBin();
        if (!confirmedPreview.Matches(current)) return new(EmptyBinStatus.PreviewChanged, current);
        _tasks.RemoveAll(task => confirmedPreview.TaskIds.Contains(task.Id, StringComparer.Ordinal));
        _projects.RemoveAll(project => confirmedPreview.ProjectIds.Contains(project.Id, StringComparer.Ordinal));
        _bin.Clear();
        _projectBin.Clear();
        _aggregateTaskIds.Clear();
        _binnedProjectIds.Clear();
        return new(EmptyBinStatus.Emptied, confirmedPreview);
    }
    public ProjectRecord ArchiveProject(string id)
    {
        Check();
        var index = _projects.FindIndex(project => project.Id == id);
        if (_projects[index].IsArchived) throw new InvalidOperationException();
        for (var taskIndex = 0; taskIndex < _tasks.Count; taskIndex++)
            if (_tasks[taskIndex].ProjectId == id)
                _tasks[taskIndex] = _tasks[taskIndex] with { TodayLane = null };
        return _projects[index] = _projects[index] with
        {
            ArchivedAt = new DateTimeOffset(2026, 9, 29, 13, 0, 0, TimeSpan.Zero),
            ArchiveDate = new DateOnly(2026, 9, 29),
        };
    }
    public ProjectRecord RestoreProject(string id)
    {
        Check();
        var index = _projects.FindIndex(project => project.Id == id);
        if (!_projects[index].IsArchived) throw new InvalidOperationException();
        return _projects[index] = _projects[index] with { ArchivedAt = null, ArchiveDate = null };
    }
    public BulkTaskArchivePreview PreviewBulkTaskArchive(int completedAgeDays)
    {
        var tasks = BulkTaskArchivePolicy.EligibleTasks(Read(), CurrentDate, completedAgeDays);
        return new(completedAgeDays, CurrentDate, tasks.Select(task => task.Id).ToArray());
    }
    public BulkTaskArchiveResult BulkArchiveTasks(BulkTaskArchivePreview confirmedPreview)
    {
        Check();
        BeforeBulkArchive?.Invoke();
        var currentPreview = PreviewBulkTaskArchive(confirmedPreview.CompletedAgeDays);
        if (!confirmedPreview.Matches(currentPreview))
            return new(false, currentPreview, 0);
        var tasks = _tasks.Where(task => currentPreview.EligibleTaskIds.Contains(task.Id, StringComparer.Ordinal)).ToArray();
        foreach (var task in tasks)
        {
            var index = _tasks.FindIndex(item => item.Id == task.Id);
            _tasks[index] = _tasks[index] with
            {
                ArchivedAt = new DateTimeOffset(2026, 9, 29, 13, 0, 0, TimeSpan.Zero),
                ArchiveDate = new DateOnly(2026, 9, 29),
            };
        }
        return new(true, confirmedPreview, tasks.Length);
    }
    public TaskRecord SetArchive(string id, DateTimeOffset instant, DateOnly date)
    {
        int index = _tasks.FindIndex(task => task.Id == id);
        if (!_tasks[index].IsComplete) throw new InvalidOperationException();
        return _tasks[index] = _tasks[index] with { ArchivedAt = instant, ArchiveDate = date, TodayLane = null };
    }
    public TaskRecord SetCompletion(string id, DateTimeOffset instant, DateOnly date)
    {
        int index = _tasks.FindIndex(task => task.Id == id);
        return _tasks[index] = _tasks[index] with { CompletedAt = instant, CompletionDate = date };
    }
    public TaskRecord SetTaskTodayLane(string id, TodayLane? lane)
    {
        Check();
        var index = _tasks.FindIndex(task => task.Id == id);
        if (_tasks[index].IsComplete && lane is not null) throw new InvalidOperationException();
        return _tasks[index] = _tasks[index] with { TodayLane = lane };
    }
    public int ClearToday()
    {
        Check();
        var cleared = 0;
        for (var index = 0; index < _tasks.Count; index++)
            if (_tasks[index].TodayLane is not null)
            {
                _tasks[index] = _tasks[index] with { TodayLane = null };
                cleared++;
            }
        return cleared;
    }
    public TodayLaneOrderChange MoveTaskInTodayLane(string id, int targetPosition)
    {
        Check();
        var ordered = _tasks.OrderBy(task => task.SharedPosition).ToList();
        var task = ordered.Single(item => item.Id == id);
        if (task.IsComplete || task.TodayLane is null) throw new ArgumentException("Task is not in Today.", nameof(id));
        var visible = ordered.Where(item => !item.IsComplete && item.TodayLane == task.TodayLane).ToList();
        if ((uint)targetPosition >= (uint)visible.Count) throw new ArgumentOutOfRangeException(nameof(targetPosition));
        visible.Remove(task);
        visible.Insert(targetPosition, task);
        var visibleIds = visible.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var visibleIndex = 0;
        ordered = ordered.Select(item => visibleIds.Contains(item.Id) ? visible[visibleIndex++] : item).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var storedIndex = _tasks.FindIndex(candidate => candidate.Id == ordered[index].Id);
            _tasks[storedIndex] = _tasks[storedIndex] with { SharedPosition = index };
        }
        return new(id, task.TodayLane.Value, targetPosition + 1, visible.Count);
    }
    public SharedTaskOrderChange MoveTaskInSharedOrder(string id, int targetPosition)
    {
        Check();
        var ordered = _tasks.OrderBy(task => task.SharedPosition).ToList();
        var archivedProjectIds = _projects.Where(project => project.IsArchived)
            .Select(project => project.Id).ToHashSet(StringComparer.Ordinal);
        var visible = ordered.Where(task => !task.IsComplete && !task.IsArchived
            && (task.ProjectId is null || !archivedProjectIds.Contains(task.ProjectId))).ToList();
        if ((uint)targetPosition >= (uint)visible.Count) throw new ArgumentOutOfRangeException(nameof(targetPosition));
        var task = visible.Single(task => task.Id == id);
        visible.Remove(task);
        visible.Insert(targetPosition, task);
        var visibleIndex = 0;
        var visibleIds = visible.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        ordered = ordered.Select(item => visibleIds.Contains(item.Id) ? visible[visibleIndex++] : item).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var storedIndex = _tasks.FindIndex(candidate => candidate.Id == ordered[index].Id);
            _tasks[storedIndex] = _tasks[storedIndex] with { SharedPosition = index };
        }
        return new(id, targetPosition + 1, visible.Count);
    }
    public ProjectOrderChange MoveProject(string id, int targetPosition)
    {
        Check();
        var ordered = _projects.OrderBy(project => project.Position).ToList();
        var visible = ordered.Where(project => !project.IsArchived).ToList();
        if ((uint)targetPosition >= (uint)visible.Count) throw new ArgumentOutOfRangeException(nameof(targetPosition));
        var project = visible.Single(project => project.Id == id);
        visible.Remove(project);
        visible.Insert(targetPosition, project);
        var visibleIds = visible.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var visibleIndex = 0;
        ordered = ordered.Select(item => visibleIds.Contains(item.Id) ? visible[visibleIndex++] : item).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var storedIndex = _projects.FindIndex(candidate => candidate.Id == ordered[index].Id);
            _projects[storedIndex] = _projects[storedIndex] with { Position = index };
        }
        return new(id, targetPosition + 1, visible.Count);
    }
    public ProjectTaskOrderChange MoveTaskInProject(string projectId, string taskId, int targetPosition)
    {
        Check();
        var ordered = _tasks.Where(task => task.ProjectId == projectId).OrderBy(task => task.ProjectPosition).ToList();
        if ((uint)targetPosition >= (uint)ordered.Count) throw new ArgumentOutOfRangeException(nameof(targetPosition));
        var task = ordered.Single(task => task.Id == taskId);
        ordered.Remove(task);
        ordered.Insert(targetPosition, task);
        for (var index = 0; index < ordered.Count; index++)
        {
            var storedIndex = _tasks.FindIndex(candidate => candidate.Id == ordered[index].Id);
            _tasks[storedIndex] = _tasks[storedIndex] with { ProjectPosition = index };
        }
        return new(projectId, taskId, targetPosition + 1, ordered.Count);
    }
    public TaskRecord DetachTask(string id)
    {
        Check();
        var index = _tasks.FindIndex(task => task.Id == id);
        var task = _tasks[index];
        if (task.ProjectId is null) throw new ArgumentException("Task is standalone.", nameof(id));
        var effectiveCategoryId = EffectiveCategoryId(task);
        var sourceProjectId = task.ProjectId;
        _tasks[index] = task with { ProjectId = null, ProjectPosition = null, ExplicitCategoryId = effectiveCategoryId };
        RewriteProjectPositions(sourceProjectId);
        return _tasks[index];
    }
    public TaskRecord AttachTask(string id, string projectId, TaskAttachmentCategoryChoice? categoryChoice = null)
    {
        Check();
        if (_projects.Single(project => project.Id == projectId).IsArchived) throw new InvalidOperationException();
        var index = _tasks.FindIndex(task => task.Id == id);
        var task = _tasks[index];
        var effectiveCategoryId = EffectiveCategoryId(task);
        var targetCategoryId = _projects.Single(project => project.Id == projectId).CategoryId;
        if (effectiveCategoryId != targetCategoryId && categoryChoice is null)
            throw new ArgumentException("Category choice required.", nameof(categoryChoice));
        var sourceProjectId = task.ProjectId;
        _tasks[index] = task with
        {
            ProjectId = projectId,
            ProjectPosition = _tasks.Count(candidate => candidate.ProjectId == projectId),
            ExplicitCategoryId = effectiveCategoryId == targetCategoryId || categoryChoice == TaskAttachmentCategoryChoice.AdoptProjectCategory
                ? null
                : effectiveCategoryId,
        };
        if (sourceProjectId is not null && sourceProjectId != projectId) RewriteProjectPositions(sourceProjectId);
        return _tasks[index];
    }
    private void ShiftForNewTask()
    {
        for (var index = 0; index < _tasks.Count; index++) _tasks[index] = _tasks[index] with { SharedPosition = _tasks[index].SharedPosition + 1 };
    }
    private List<string> ApplyParticipantChanges(ParticipantDraftChange change)
    {
        var associations = change.ParticipantIds.Distinct(StringComparer.Ordinal).ToList();
        foreach (var label in change.NewParticipantLabels) associations.Add(CreateParticipant(label).Id);
        return associations;
    }
    private string EffectiveCategoryId(TaskRecord task) => task.ExplicitCategoryId
        ?? _projects.Single(project => project.Id == task.ProjectId).CategoryId;
    private void RewriteProjectPositions(string projectId)
    {
        var ordered = _tasks.Where(task => task.ProjectId == projectId).OrderBy(task => task.ProjectPosition).ToArray();
        for (var position = 0; position < ordered.Length; position++)
        {
            var index = _tasks.FindIndex(task => task.Id == ordered[position].Id);
            _tasks[index] = _tasks[index] with { ProjectPosition = position };
        }
    }
    private void RewriteCategoryPositions()
    {
        for (var position = 0; position < _categories.Count; position++)
            _categories[position] = _categories[position] with { Position = position };
    }
    private void Check()
    {
        if (FailWrites) throw new WorkspaceWorkException();
        WriteCount++;
    }
}
