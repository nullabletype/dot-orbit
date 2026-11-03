using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class InspectorDatePlacementTests
{
    private static readonly DateTime Day = new(2026, 11, 4);

    [Fact]
    public void ProjectDraftBeforeFirstSaveCreatesOnePlannedTaskAndClearingUndoesPlacement()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var model = new ProjectCaptureViewModel(work, Clock());
        model.NewTaskCommand.Execute(null);
        Assert.True(model.ShowTaskContext);
        model.TaskContextTarget = model.TaskContextChoices.Single(choice => choice.ProjectId == project.Id);
        model.CommitCalendarDate(Day);
        Assert.Empty(work.Read().Tasks);
        Assert.Empty(model.TodayPlanned);

        model.Title = "Plant bulbs";
        Assert.True(model.Save());
        var saved = Assert.Single(work.Read().Tasks);
        Assert.Equal(project.Id, saved.ProjectId);
        Assert.Equal(DateOnly.FromDateTime(Day), saved.DueDate);
        Assert.Equal(TodayLane.Planned, saved.TodayLane);
        Assert.Equal(saved.Id, Assert.Single(model.TodayPlanned).Task.Id);

        model.CommitCalendarDate(null);
        var cleared = Assert.Single(work.Read().Tasks);
        Assert.Equal(saved.Id, cleared.Id);
        Assert.Null(cleared.DueDate);
        Assert.Null(cleared.TodayLane);
        Assert.Empty(model.TodayPlanned);
    }

    [Fact]
    public void QuickAddedTaskJoinsPlannedOnceAndLeavesOnAChoiceOfAnotherDate()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Garden", "", "home", null);
        var model = new ProjectCaptureViewModel(work, Clock());
        Assert.True(model.QuickAdd(project.Id, "Buy bulbs"));
        var saved = Assert.Single(work.Read().Tasks);
        model.SelectTask(saved.Id);
        model.CommitCalendarDate(Day);
        model.CommitCalendarDate(Day);
        model.Description = "Purple";
        Assert.True(model.Save());
        Assert.Equal(saved.Id, Assert.Single(model.TodayPlanned).Task.Id);
        Assert.Empty(model.TodayInProgress);
        Assert.True(Assert.Single(model.Backlog).IsToday);

        model.CommitCalendarDate(Day.AddDays(1));
        var moved = Assert.Single(work.Read().Tasks);
        Assert.Equal(DateOnly.FromDateTime(Day.AddDays(1)), moved.DueDate);
        Assert.Null(moved.TodayLane);
        Assert.False(Assert.Single(model.Backlog).IsToday);
        Assert.Equal(saved.ProjectPosition, moved.ProjectPosition);
        Assert.False(moved.IsComplete);
    }

    [Theory]
    [InlineData(TodayLane.Planned)]
    [InlineData(TodayLane.InProgress)]
    public void ExistingMembershipSurvivesChangingAndClearingTheDate(TodayLane lane)
    {
        var work = new MemoryWorkspaceWork();
        var older = work.CreateStandaloneTask("Older", "", "home", null);
        work.CreateStandaloneTask("Newer", "", "work", null);
        work.SetTaskTodayLane(older.Id, lane);
        var originalOrder = work.Read().Tasks.Select(task => (task.Id, task.SharedPosition)).ToArray();
        var model = new ProjectCaptureViewModel(work, Clock());
        model.SelectTask(older.Id);
        model.CommitCalendarDate(Day);
        model.CommitCalendarDate(null);

        var saved = work.Read().Tasks.Single(task => task.Id == older.Id);
        Assert.Null(saved.DueDate);
        Assert.Equal(lane, saved.TodayLane);
        Assert.Equal(older.Id, Assert.Single(model.TodayPlanned.Concat(model.TodayInProgress)).Task.Id);
        Assert.Equal(originalOrder, work.Read().Tasks.Select(task => (task.Id, task.SharedPosition)));
    }

    [Fact]
    public void ExplicitRemovalPersistsThroughAutosaveAndRefreshAndExplicitAddOwnsMembership()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Older", "", "home", null);
        var model = new ProjectCaptureViewModel(work, Clock());
        model.SelectTask(task.Id);
        model.CommitCalendarDate(Day);
        Assert.Equal(task.Id, Assert.Single(model.TodayPlanned).Task.Id);
        model.Backlog.Single().ToggleTodayCommand.Execute(null);
        model.Description = "Revised";
        Assert.True(model.Save());
        model.RetryWorkspaceRefreshCommand.Execute(null);
        var removed = Assert.Single(work.Read().Tasks);
        Assert.Null(removed.TodayLane);
        Assert.Equal(DateOnly.FromDateTime(Day), removed.DueDate);
        Assert.False(Assert.Single(model.Backlog).IsToday);

        model.Backlog.Single().ToggleTodayCommand.Execute(null);
        model.CommitCalendarDate(null);
        var kept = Assert.Single(work.Read().Tasks);
        Assert.Equal(TodayLane.Planned, kept.TodayLane);
        Assert.Null(kept.DueDate);
        Assert.Equal(task.Id, Assert.Single(model.TodayPlanned).Task.Id);
    }

    [Fact]
    public void ExplicitLaneChangeSurvivesDateUndoAndReturningToPlanned()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Older", "", "home", null);
        var model = new ProjectCaptureViewModel(work, Clock());
        model.SelectTask(task.Id);
        model.CommitCalendarDate(Day);
        Assert.Single(model.TodayPlanned).MoveToOtherLaneCommand.Execute(null);
        model.CommitCalendarDate(Day.AddDays(1));
        Assert.Equal(task.Id, Assert.Single(model.TodayInProgress).Task.Id);
        model.CommitCalendarDate(Day);
        Assert.Single(model.TodayInProgress).MoveToOtherLaneCommand.Execute(null);
        model.CommitCalendarDate(null);
        Assert.Equal(task.Id, Assert.Single(model.TodayPlanned).Task.Id);
    }

    [Fact]
    public void TodayCreationDefaultAndPlacementFromAnEarlierInspectorSurviveClearing()
    {
        var work = new MemoryWorkspaceWork();
        var model = new ProjectCaptureViewModel(work, Clock());
        model.NewTodayTaskCommand.Execute(null);
        model.Category = model.Categories.Single(item => item.Id == "home");
        model.CommitCalendarDate(Day);
        model.Title = "Today's task";
        Assert.True(model.Save());
        model.CommitCalendarDate(null);
        var defaultMember = Assert.Single(work.Read().Tasks);
        Assert.Equal(TodayLane.Planned, defaultMember.TodayLane);

        model.NewTaskCommand.Execute(null);
        model.Category = model.Categories.Single(item => item.Id == "work");
        model.Title = "Other task";
        model.CommitCalendarDate(Day);
        var other = work.Read().Tasks.Single(task => task.Id != defaultMember.Id);
        Assert.Equal(TodayLane.Planned, other.TodayLane);
        model.SelectTask(defaultMember.Id);
        model.SelectTask(other.Id);
        model.CommitCalendarDate(null);
        Assert.Equal(TodayLane.Planned, work.Read().Tasks.Single(task => task.Id == other.Id).TodayLane);
    }

    [Fact]
    public void RolloverAndRestartLeaveStoredDueDateAloneUntilReselected()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Scheduled", "", "home", DateOnly.FromDateTime(Day));
        var clock = Clock();
        clock.Set(new DateTimeOffset(2026, 11, 3, 23, 59, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
        var model = new ProjectCaptureViewModel(work, clock);
        model.SelectTask(task.Id);
        clock.Set(new DateTimeOffset(2026, 11, 4, 0, 1, 0, TimeSpan.Zero));
        Assert.True(model.RefreshDatePresentation());
        model.Description = "Still scheduled";
        Assert.True(model.Save());
        Assert.Null(Assert.Single(work.Read().Tasks).TodayLane);

        var restarted = new ProjectCaptureViewModel(work, clock);
        restarted.SelectTask(task.Id);
        Assert.Empty(restarted.TodayPlanned);
        restarted.CommitCalendarDate(Day);
        Assert.Equal(task.Id, Assert.Single(restarted.TodayPlanned).Task.Id);
        var reopened = new ProjectCaptureViewModel(work, clock);
        Assert.Equal(task.Id, Assert.Single(reopened.TodayPlanned).Task.Id);
        reopened.SelectTask(task.Id);
        reopened.CommitCalendarDate(null);
        Assert.Equal(TodayLane.Planned, Assert.Single(work.Read().Tasks).TodayLane);
    }

    [Fact]
    public void CompletedTaskKeepsItsCompletionAndStaysOutsideToday()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Finished", "", "home", null);
        var completed = work.CompleteTask(task.Id);
        var model = new ProjectCaptureViewModel(work, Clock());
        model.SelectTask(task.Id);
        model.CommitCalendarDate(Day);

        var saved = Assert.Single(work.Read().Tasks);
        Assert.Equal(DateOnly.FromDateTime(Day), saved.DueDate);
        Assert.Null(saved.TodayLane);
        Assert.Equal(completed.CompletedAt, saved.CompletedAt);
        Assert.Equal(completed.CompletionDate, saved.CompletionDate);
        Assert.Empty(model.TodayPlanned);
    }

    [Fact]
    public void DiscardingAnInvalidDatedDraftCreatesNoTaskOrTodayEntry()
    {
        var work = new MemoryWorkspaceWork();
        var model = new ProjectCaptureViewModel(work, Clock());
        model.NewTaskCommand.Execute(null);
        model.CommitCalendarDate(Day);
        model.Cancel();

        Assert.False(model.HasInspector);
        Assert.NotEmpty(model.InspectorMetaValue);
        Assert.Empty(work.Read().Tasks);
        Assert.Empty(model.TodayPlanned);
    }

    [Fact]
    public void FailedSaveLeavesDateAndLaneUnchangedAndRetryCommitsBoth()
    {
        var work = new MemoryWorkspaceWork();
        var older = work.CreateStandaloneTask("Older", "", "home", null);
        var model = new ProjectCaptureViewModel(work, Clock());
        model.SelectTask(older.Id);
        work.FailWrites = true;
        model.CommitCalendarDate(Day);
        Assert.True(model.HasAutosaveError);
        var unchanged = Assert.Single(work.Read().Tasks);
        Assert.Null(unchanged.DueDate);
        Assert.Null(unchanged.TodayLane);
        Assert.Empty(model.TodayPlanned);
        work.FailWrites = false;
        model.RetryAutosaveCommand.Execute(null);
        var saved = Assert.Single(work.Read().Tasks);
        Assert.False(model.HasAutosaveError);
        Assert.Equal(DateOnly.FromDateTime(Day), saved.DueDate);
        Assert.Equal(TodayLane.Planned, saved.TodayLane);
        Assert.Equal(older.Id, Assert.Single(model.TodayPlanned).Task.Id);
    }

    [Fact]
    public async Task ClearingWhileFirstSaveIsInFlightUpdatesTheCreatedIdentity()
    {
        var work = new MemoryWorkspaceWork();
        var writer = new ControlledInspectorSaveWriter(work);
        var model = new ProjectCaptureViewModel(work, Clock(), writer);
        model.NewTaskCommand.Execute(null);
        model.Category = model.Categories.Single(item => item.Id == "home");
        model.Title = "Held";
        model.CommitCalendarDate(Day);
        await writer.WaitForSubmissionCountAsync(1);
        model.CommitCalendarDate(null);
        var flush = model.FlushPendingAutosaveAsync();
        writer.CompleteNext();
        await writer.WaitForSubmissionCountAsync(2);
        var first = Assert.Single(work.Read().Tasks);
        Assert.Equal(DateOnly.FromDateTime(Day), first.DueDate);
        Assert.Equal(TodayLane.Planned, first.TodayLane);
        writer.CompleteNext();
        Assert.True(await flush);
        var latest = Assert.Single(work.Read().Tasks);
        Assert.Equal(first.Id, latest.Id);
        Assert.Null(latest.TodayLane);
        Assert.Null(latest.DueDate);
        Assert.Empty(model.TodayPlanned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetryingACommittedCreationRefreshPreservesItsIdentityAndLatestDate(bool clearBeforeRetry)
    {
        var work = new FailedRefreshWorkspace();
        var model = new ProjectCaptureViewModel(work, Clock());
        model.NewTaskCommand.Execute(null);
        model.Category = model.Categories.Single(item => item.Id == "home");
        model.Title = "First task";
        work.FailRefresh = true;
        model.CommitCalendarDate(Day);

        var committed = Assert.Single(work.Read().Tasks);
        Assert.Equal(TodayLane.Planned, committed.TodayLane);
        Assert.Equal("Task saved, but the view could not refresh. Retry.", model.AutosaveStatus);
        Assert.Empty(model.TodayPlanned);
        var writes = work.WriteCount;
        work.FailRefresh = false;
        if (clearBeforeRetry) model.CommitCalendarDate(null);
        else model.RetryAutosaveCommand.Execute(null);

        var saved = Assert.Single(work.Read().Tasks);
        Assert.Equal(committed.Id, saved.Id);
        Assert.Equal(clearBeforeRetry ? writes + 1 : writes, work.WriteCount);
        Assert.Equal(clearBeforeRetry ? null : TodayLane.Planned, saved.TodayLane);
        Assert.Equal(clearBeforeRetry ? null : DateOnly.FromDateTime(Day), saved.DueDate);
        Assert.Equal(!clearBeforeRetry, model.Backlog.Single().IsToday);
        Assert.False(model.HasAutosaveError);
    }

    [Fact]
    public async Task LaterDateChoiceWaitsForExplicitRemovalBeforeSavingDateAndNewPlacement()
    {
        var work = new MemoryWorkspaceWork();
        var task = work.CreateStandaloneTask("Older", "", "home", null);
        work.SetTaskTodayLane(task.Id, TodayLane.Planned);
        var actions = new CapturedActionExecutor(work);
        var model = new ProjectCaptureViewModel(work, Clock(),
            new InlineWorkspacePersistenceExecutor(new WorkspacePersistenceOperation(work)), actions);
        model.SelectTask(task.Id);
        model.Backlog.Single().ToggleTodayCommand.Execute(null);
        await actions.ResultCaptured;
        model.CommitCalendarDate(Day);
        var flush = model.FlushPendingAutosaveAsync();
        Assert.False(flush.IsCompleted);
        Assert.Null(work.Read().Tasks.Single().DueDate);
        actions.ReleaseResult();
        Assert.True(await flush);
        var saved = Assert.Single(work.Read().Tasks);
        Assert.Equal(DateOnly.FromDateTime(Day), saved.DueDate);
        Assert.Equal(TodayLane.Planned, saved.TodayLane);
        model.CommitCalendarDate(null);
        Assert.Null(work.Read().Tasks.Single().TodayLane);
    }

    [Fact]
    public async Task ProjectContextCannotChangeDuringTheFirstValidTaskSave()
    {
        var work = new MemoryWorkspaceWork();
        var project = work.CreateProject("Project", "", "home", null);
        var writer = new ControlledInspectorSaveWriter(work);
        var model = new ProjectCaptureViewModel(work, Clock(), writer);
        model.NewTaskCommand.Execute(null);
        model.Category = model.Categories.Single(item => item.Id == "home");
        model.Title = "First task";
        model.CommitCalendarDate(Day);
        await writer.WaitForSubmissionCountAsync(1);
        Assert.False(model.TaskContextEnabled);
        model.TaskContextTarget = model.TaskContextChoices.Single(item => item.ProjectId == project.Id);
        Assert.Null(model.TaskContextTarget?.ProjectId);
        writer.CompleteNext();
        await model.WaitForBackgroundSaveObservationAsync();
        Assert.True(model.TaskContextEnabled);
        Assert.Null(work.Read().Tasks.Single().ProjectId);
    }

    [Fact]
    public async Task DiscardAfterACommittedCreationWithFailedRefreshKeepsRefreshAvailable()
    {
        var work = new FailedRefreshWorkspace();
        var model = new ProjectCaptureViewModel(work, Clock());
        model.NewTaskCommand.Execute(null);
        model.Category = model.Categories.Single(item => item.Id == "home");
        model.Title = "First task";
        work.FailRefresh = true;
        model.CommitCalendarDate(Day);
        var writes = work.WriteCount;
        var navigated = false;
        await model.NavigateAsync(() => navigated = true);
        Assert.True(model.NeedsDecision);
        model.DiscardAndLeaveCommand.Execute(null);
        Assert.True(navigated);
        Assert.True(model.CanRetryWorkspaceRefresh);
        work.FailRefresh = false;
        model.RetryWorkspaceRefreshCommand.Execute(null);
        Assert.Equal(writes, work.WriteCount);
        Assert.Single(model.TodayPlanned);
    }

    private sealed class FailedRefreshWorkspace : MemoryWorkspaceWork, IWorkspaceWork
    {
        public bool FailRefresh { get; set; }

        WorkspaceWorkSnapshot IWorkspaceWork.Read() =>
            FailRefresh ? throw new WorkspaceWorkException() : Read();
    }

    private static FixedTimeProvider Clock() => new(new DateTimeOffset(2026, 11, 3, 23, 30, 0, TimeSpan.Zero),
        TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2"));
}
