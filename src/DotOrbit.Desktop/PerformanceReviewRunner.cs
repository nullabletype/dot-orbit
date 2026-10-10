using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using DotOrbit.Core.Diagnostics;
using DotOrbit.Core.Workspaces;
using DotOrbit.Desktop.ViewModels;
using DotOrbit.Desktop.Views;
using DotOrbit.Storage.Sqlite;

namespace DotOrbit.Desktop;

internal readonly record struct PerformanceReviewScenario(
    bool IsEnabled,
    bool IsValid,
    int TaskCount,
    int Iterations,
    int BudgetMilliseconds)
{
    public int RecoveryPointCount { get; init; }

    private const string RecoveryPointsPrefix = "--performance-recovery-points=";
    private const string ReviewArgument = "--performance-review";
    private const string TaskCountPrefix = "--performance-tasks=";
    private const string IterationsPrefix = "--performance-iterations=";
    private const string BudgetPrefix = "--performance-budget-ms=";

    public static PerformanceReviewScenario FromArguments(string[]? arguments)
    {
        if (arguments?.Contains(ReviewArgument, StringComparer.Ordinal) != true)
            return new(false, true, 250, 20, 100);

        var taskCount = ParseSingle(arguments, TaskCountPrefix, 250, 2, 5_000, out var tasksValid);
        var iterations = ParseSingle(arguments, IterationsPrefix, 20, 5, 100, out var iterationsValid);
        var budget = ParseSingle(arguments, BudgetPrefix, 100, 1, 5_000, out var budgetValid);
        var recoveryPoints = ParseSingle(arguments, RecoveryPointsPrefix, 0, 0, 16, out var recoveryValid);
        var traceModeDisabled = !arguments.Contains("--performance-trace", StringComparer.Ordinal);
        return new(
            true,
            tasksValid && iterationsValid && budgetValid && recoveryValid && traceModeDisabled,
            taskCount,
            iterations,
            budget)
        {
            RecoveryPointCount = recoveryPoints,
        };
    }

    private static int ParseSingle(
        IEnumerable<string> arguments,
        string prefix,
        int defaultValue,
        int minimum,
        int maximum,
        out bool isValid)
    {
        var values = arguments
            .Where(argument => argument.StartsWith(prefix, StringComparison.Ordinal))
            .Select(argument => argument[prefix.Length..])
            .ToArray();
        isValid = values.Length == 0
            || (values.Length == 1
                && int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                && parsed >= minimum
                && parsed <= maximum);
        return values.Length == 1
            && int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : defaultValue;
    }
}

internal sealed class PerformanceReviewWorkspace : IDisposable
{
    private const string SyntheticPassphrase = "synthetic performance review only";
    private static readonly DateTimeOffset FixedNow =
        new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory;

    private PerformanceReviewWorkspace(
        string directory,
        IWorkspaceSession session,
        TimeProvider timeProvider,
        TimeSpan recoverySetupDuration,
        TimeSpan unlockDuration)
    {
        _directory = directory;
        Session = session;
        TimeProvider = timeProvider;
        RecoverySetupDuration = recoverySetupDuration;
        UnlockDuration = unlockDuration;
    }

    public IWorkspaceSession Session { get; }
    public TimeProvider TimeProvider { get; }
    public TimeSpan RecoverySetupDuration { get; }
    public TimeSpan UnlockDuration { get; }
    internal string DirectoryPath => _directory;

    public static PerformanceReviewWorkspace Create(int taskCount, int recoveryPointCount = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recoveryPointCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(recoveryPointCount, 16);
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"dot-orbit-performance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var passphrase = WorkspacePassphrase.Create(SyntheticPassphrase, SyntheticPassphrase).Passphrase
                ?? throw new InvalidOperationException();
            var category = CategoryName.Create("Synthetic category 01").CategoryName
                ?? throw new InvalidOperationException();
            var timeProvider = new FixedPerformanceTimeProvider(FixedNow.AddHours(-Math.Max(0, recoveryPointCount - 1)));
            var created = new EncryptedWorkspaceStore(
                new SystemIdentifierGenerator(),
                timeProvider).Create(
                Path.Combine(directory, "workspace.orb"),
                passphrase,
                category);
            if (created is not { Status: WorkspaceCreationStatus.Created, Session: { } session })
                throw new WorkspaceWorkException();

            try
            {
                Populate(session.Work, taskCount);
                var recoveryStarted = Stopwatch.GetTimestamp();
                if (recoveryPointCount > 0)
                {
                    var configured = session.Recovery.ConfigureAutomaticRecoveryDirectory(
                        Path.Combine(directory, "recovery"));
                    if (configured.Status != RecoveryDirectoryConfigurationStatus.Configured)
                        throw new InvalidOperationException();
                    var taskId = session.Work.Read().Tasks.First(task => !task.IsArchived).Id;
                    for (var index = 1; index < recoveryPointCount; index++)
                    {
                        timeProvider.Advance(TimeSpan.FromHours(1));
                        session.Work.SetTaskTodayLane(taskId,
                            index % 2 == 0 ? TodayLane.Planned : TodayLane.InProgress);
                    }
                    if (Directory.GetFiles(configured.DirectoryPath!, "*.dotorbit-recovery").Length != recoveryPointCount)
                        throw new InvalidOperationException();
                }
                var recoveryDuration = Stopwatch.GetElapsedTime(recoveryStarted);
                session.Dispose();
                var unlockStarted = Stopwatch.GetTimestamp();
                var opened = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), timeProvider)
                    .Open(Path.Combine(directory, "workspace.orb"), passphrase);
                session = opened.Session ?? throw new InvalidOperationException();
                return new(directory, session, timeProvider, recoveryDuration, Stopwatch.GetElapsedTime(unlockStarted));
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }
        catch
        {
            DeleteDirectory(directory);
            throw;
        }
    }

    public void Dispose()
    {
        Session.Dispose();
        DeleteDirectory(_directory);
    }

    private static void Populate(IWorkspaceWork work, int taskCount)
    {
        var categoryIds = new List<string> { work.Read().Categories.Single().Id };
        for (var index = 2; index <= 8; index++)
            categoryIds.Add(work.CreateCategory($"Synthetic category {index:00}").Id);

        var participantIds = Enumerable.Range(1, 8)
            .Select(index => work.CreateParticipant($"P{index:00}").Id)
            .ToArray();
        var projectCount = Math.Clamp(taskCount / 20, 1, 100);
        var projectIds = Enumerable.Range(1, projectCount)
            .Select(index => work.CreateProject(
                $"Synthetic project {index:000}",
                "Synthetic performance data.",
                categoryIds[(index - 1) % categoryIds.Count],
                null).Id)
            .ToArray();
        var createdTaskIds = new List<string>(taskCount);
        for (var index = taskCount; index >= 1; index--)
        {
            var projectId = index % 3 == 0 ? null : projectIds[index % projectIds.Length];
            var categoryId = projectId is null ? categoryIds[index % categoryIds.Count] : null;
            var participantChange = index % 5 == 0
                ? new ParticipantDraftChange([participantIds[index % participantIds.Length]], [])
                : null;
            var task = work.CreateTaskDraft(
                projectId,
                $"Synthetic task {index:00000}",
                "Synthetic performance data with a short Markdown description.",
                categoryId,
                index % 4 == 0 ? new DateOnly(2026, 10, 8).AddDays(index % 14) : null,
                participantChange,
                index % 7 == 0 ? TodayLane.Planned : null);
            createdTaskIds.Add(task.Id);
        }

        for (var index = 19; index < createdTaskIds.Count; index += 20)
        {
            work.CompleteTask(createdTaskIds[index]);
            work.ArchiveTask(createdTaskIds[index]);
        }
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class FixedPerformanceTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;
    public void Advance(TimeSpan duration) => _utcNow += duration;
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

internal static class PerformanceReviewRunner
{
    internal const int InvalidArgumentsExitCode = 24;
    internal const int SetupFailureExitCode = 25;
    internal const int BudgetExceededExitCode = 26;
    internal const int RunnerFailureExitCode = 27;

    public static async Task<int> RunAsync(
        MainWindow window,
        PerformanceReviewWorkspace workspace,
        PerformanceReviewScenario scenario,
        TimeSpan startupDuration)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(workspace);
        var store = workspace.Session.Work;
        await IdleAsync();

        var shell = (ShellViewModel?)window.DataContext ?? throw new InvalidOperationException();
        var work = shell.Work ?? throw new InvalidOperationException();
        var activeTasks = store.Read().Tasks.Where(task => !task.IsArchived).Take(2).ToArray();
        if (activeTasks.Length < 2) throw new InvalidOperationException();

        Console.WriteLine(
            $"performance-review: environment commit={PerformanceReviewBuild.Commit()} configuration={Configuration()} "
            + $"os={Environment.OSVersion.Platform} framework={Environment.Version} tasks={scenario.TaskCount} "
            + $"iterations={scenario.Iterations} budget_ms={scenario.BudgetMilliseconds} recovery_points={scenario.RecoveryPointCount}");
        WriteTimingObservation("recovery-setup", workspace.RecoverySetupDuration);
        WriteTimingObservation("workspace-unlock", workspace.UnlockDuration);
        Console.WriteLine(
            $"performance-review: operation=startup-to-open iterations=1 p50_ms={Format(startupDuration.TotalMilliseconds)} "
            + $"p95_ms={Format(startupDuration.TotalMilliseconds)} max_ms={Format(startupDuration.TotalMilliseconds)} result=observed");

        work.SelectTask(activeTasks[0].Id);
        await IdleAsync();
        Task<bool>? pendingStableSave = null;
        var coldSaveResults = await MeasureWithIdleAsync(
            "task-title-save-cold",
            1,
            scenario.BudgetMilliseconds,
            _ =>
            {
                work.Title = "Synthetic cold title save";
                var revision = work.AutosaveRevision;
                if (!work.RunScheduledAutosave(force: true)) throw new InvalidOperationException();
                pendingStableSave = work.WaitForAutosaveRevisionAsync(revision);
            },
            async () =>
            {
                if (pendingStableSave is null || !await pendingStableSave) throw new InvalidOperationException();
            },
            actionBudgetMilliseconds: 50);
        foreach (var result in coldSaveResults) WriteResult(result, observedOnly: true);

        for (var warmup = 0; warmup < 3; warmup++)
        {
            work.SelectTask(activeTasks[warmup % 2].Id);
            shell.PrimaryNavigation.Single(item => item.Title == (warmup % 2 == 0 ? "Projects" : "Backlog"))
                .SelectCommand.Execute(null);
            work.SelectTask(activeTasks[0].Id);
            work.Title = $"Synthetic warm-up title {warmup % 2}";
            var revision = work.AutosaveRevision;
            if (!work.RunScheduledAutosave(force: true)
                || !await work.WaitForAutosaveRevisionAsync(revision)) throw new InvalidOperationException();
            await IdleAsync();
        }

        using var connectionRecording = PerformanceTrace.Start(maximumSamples: 100_000);
        var results = new List<PerformanceOperationResult>();
        results.AddRange(await MeasureWithIdleAsync(
            "task-select",
            scenario.Iterations,
            scenario.BudgetMilliseconds,
            iteration => work.SelectTask(activeTasks[iteration % 2].Id)));
        results.AddRange(await MeasureWithIdleAsync(
            "view-switch",
            scenario.Iterations,
            scenario.BudgetMilliseconds,
            iteration =>
            {
                var title = iteration % 2 == 0 ? "Projects" : "Backlog";
                shell.PrimaryNavigation.Single(item => item.Title == title).SelectCommand.Execute(null);
            }));
        results.Add(MeasureSync(
            "snapshot-read",
            scenario.Iterations,
            scenario.BudgetMilliseconds,
            iteration =>
            {
                _ = iteration;
                _ = store.Read();
            }));
        results.Add(MeasureSync(
            "task-bin-read",
            scenario.Iterations,
            scenario.BudgetMilliseconds,
            iteration =>
            {
                _ = iteration;
                _ = store.ReadTaskBin();
            }));
        results.Add(MeasureSync(
            "project-bin-read",
            scenario.Iterations,
            scenario.BudgetMilliseconds,
            iteration =>
            {
                _ = iteration;
                _ = store.ReadProjectBin();
            }));
        results.Add(MeasureSync(
            "storage-update",
            scenario.Iterations,
            scenario.BudgetMilliseconds,
            iteration =>
            {
                var task = activeTasks[1];
                _ = store.UpdateTask(
                    task.Id,
                    $"Synthetic storage update {iteration % 2}",
                    task.Description,
                    task.ExplicitCategoryId,
                    task.DueDate,
                    new ParticipantDraftChange(task.Participants, []));
            }));
        var observations = new List<PerformanceOperationResult>();
        observations.AddRange(await MeasureWithIdleAsync(
            "full-reload",
            scenario.Iterations,
            scenario.BudgetMilliseconds,
            _ => work.RefreshFromStore()));
        results.Add(MeasureSyncWithSetup(
            "incremental-task-projection",
            scenario.Iterations,
            Math.Min(scenario.BudgetMilliseconds, 50),
            iteration =>
            {
                var task = activeTasks[1];
                return store.UpdateTask(
                    task.Id,
                    $"Synthetic projection update {iteration % 2}",
                    task.Description,
                    task.ExplicitCategoryId,
                    task.DueDate,
                    new ParticipantDraftChange(task.Participants, []));
            },
            work.ApplyCommittedTaskEdit));
        work.SelectTask(activeTasks[0].Id);
        await IdleAsync();
        results.AddRange(await MeasureWithIdleAsync(
            "task-title-save",
            scenario.Iterations,
            scenario.BudgetMilliseconds,
            iteration =>
            {
                work.Title = $"Synthetic task 00001 revision {iteration % 2}";
                var revision = work.AutosaveRevision;
                if (!work.RunScheduledAutosave(force: true)) throw new InvalidOperationException();
                pendingStableSave = work.WaitForAutosaveRevisionAsync(revision);
            },
            async () =>
            {
                if (pendingStableSave is null || !await pendingStableSave) throw new InvalidOperationException();
            },
            actionBudgetMilliseconds: 50));

        // These row actions retain their full refresh; measure their complete command-to-idle boundary.
        var actionTaskId = activeTasks[1].Id;
        work.RefreshFromStore();
        var initiallyOnToday = work.TodayPlanned.Concat(work.TodayInProgress).Any(row => row.Task.Id == actionTaskId);
        observations.AddRange(await MeasureWithIdleAsync(
            "today-membership", scenario.Iterations, scenario.BudgetMilliseconds,
            _ => work.ToggleToday(actionTaskId),
            waitForStable: work.WaitForWorkspaceActionsAsync,
            workspaceActionTiming: () => work.LastWorkspaceActionTiming,
            validateAfterIteration: iteration => Require(
                work.TodayPlanned.Concat(work.TodayInProgress).Any(row => row.Task.Id == actionTaskId)
                    == (iteration % 2 == 0 ? !initiallyOnToday : initiallyOnToday)
                && !work.NeedsDecision)));
        if (!work.TodayPlanned.Concat(work.TodayInProgress).Any(row => row.Task.Id == actionTaskId))
        {
            work.ToggleToday(actionTaskId);
            await work.WaitForWorkspaceActionsAsync();
        }
        var initiallyPlanned = work.TodayPlanned.Any(row => row.Task.Id == actionTaskId);
        observations.AddRange(await MeasureWithIdleAsync(
            "today-start-stop", scenario.Iterations, scenario.BudgetMilliseconds,
            _ => work.MoveToOtherTodayLane(actionTaskId),
            waitForStable: work.WaitForWorkspaceActionsAsync,
            workspaceActionTiming: () => work.LastWorkspaceActionTiming,
            validateAfterIteration: iteration => Require(
                (iteration % 2 == 0 ? !initiallyPlanned : initiallyPlanned)
                    ? work.TodayPlanned.Any(row => row.Task.Id == actionTaskId)
                    : work.TodayInProgress.Any(row => row.Task.Id == actionTaskId))));
        observations.AddRange(await MeasureWithIdleAsync(
            "task-complete-reopen", scenario.Iterations, scenario.BudgetMilliseconds,
            _ => work.ToggleCompletion(actionTaskId),
            waitForStable: work.WaitForWorkspaceActionsAsync,
            workspaceActionTiming: () => work.LastWorkspaceActionTiming,
            validateAfterIteration: iteration => Require(
                work.Completed.Any(row => row.Id == actionTaskId) == (iteration % 2 == 0))));
        // An odd iteration count leaves the Task completed; restore it before the reorder observation.
        if (store.Read().Tasks.Single(task => task.Id == actionTaskId).IsComplete)
        {
            work.ToggleCompletion(actionTaskId);
            await work.WaitForWorkspaceActionsAsync();
        }
        work.MoveToBottom(actionTaskId);
        await work.WaitForWorkspaceActionsAsync();
        observations.AddRange(await MeasureWithIdleAsync(
            "task-move", scenario.Iterations, scenario.BudgetMilliseconds,
            iteration =>
            {
                if (iteration % 2 == 0) work.MoveToTop(actionTaskId);
                else work.MoveToBottom(actionTaskId);
            },
            waitForStable: work.WaitForWorkspaceActionsAsync,
            workspaceActionTiming: () => work.LastWorkspaceActionTiming,
            validateAfterIteration: iteration => Require(
                work.Backlog[iteration % 2 == 0 ? 0 : work.Backlog.Count - 1].Id == actionTaskId)));
        connectionRecording.Dispose();
        var trace = connectionRecording.Snapshot();
        var connectionOpenCount = trace.Count(sample => sample.Stage == PerformanceStage.ConnectionOpen);
        var connectionConfigureCount = trace.Count(sample => sample.Stage == PerformanceStage.ConnectionConfigure);
        var connectionReusePassed = ConnectionReuseConstraintsPassed(
            connectionOpenCount,
            connectionConfigureCount,
            connectionRecording.DroppedSamples);
        if (scenario.RecoveryPointCount > 0)
        {
            var recoveryDirectory = workspace.Session.Recovery.AutomaticRecoveryDirectoryPath!;
            var priorPoints = Directory.GetFiles(recoveryDirectory, "*.dotorbit-recovery").ToHashSet(StringComparer.Ordinal);
            ((FixedPerformanceTimeProvider)workspace.TimeProvider).Advance(TimeSpan.FromHours(1));
            observations.Add(MeasureSync("due-recovery-commit", 1, scenario.BudgetMilliseconds,
                _ => store.CompleteTask(actionTaskId)));
            Require(Directory.GetFiles(recoveryDirectory, "*.dotorbit-recovery").Any(path => !priorPoints.Contains(path)));
        }

        foreach (var result in results) WriteResult(result);
        foreach (var observation in observations) WriteResult(observation, observedOnly: true);
        var titleSaveStable = results.Single(result => result.Operation == "task-title-save-stable");
        var titleSaveAllocationPassed = titleSaveStable.P50AllocatedBytes < 4_000_000;
        var titleSaveMaximumPassed = titleSaveStable.MaximumMilliseconds <= scenario.BudgetMilliseconds * 2;
        Console.WriteLine(
            $"performance-review: constraint=task-title-save allocation_budget_bytes=4000000 "
            + $"maximum_budget_ms={scenario.BudgetMilliseconds * 2} "
            + $"result={(titleSaveAllocationPassed && titleSaveMaximumPassed ? "passed" : "failed")}");
        Console.WriteLine(
            $"performance-review: constraint=scoped-warm-connection-reuse connection_open_count={connectionOpenCount} "
            + $"connection_configure_count={connectionConfigureCount} "
            + $"dropped_samples={connectionRecording.DroppedSamples} "
            + $"result={(connectionReusePassed ? "passed" : "failed")}");
        var passed = ReviewConstraintsPassed(results, scenario.BudgetMilliseconds)
            && connectionReusePassed;
        Console.WriteLine(
            $"performance-review: result={(passed ? "passed" : "failed")} code={(passed ? 0 : BudgetExceededExitCode)}");
        return passed ? 0 : BudgetExceededExitCode;
    }

    internal static bool TitleSaveStableConstraintsPassed(
        PerformanceOperationResult stable,
        int budgetMilliseconds) =>
        stable.P50AllocatedBytes < 4_000_000
        && stable.MaximumMilliseconds <= budgetMilliseconds * 2;

    internal static bool ReviewConstraintsPassed(
        IReadOnlyCollection<PerformanceOperationResult> results,
        int budgetMilliseconds)
    {
        var titleSaveStable = results.Single(result => result.Operation == "task-title-save-stable");
        return results.All(result => result.P95Milliseconds <= result.BudgetMilliseconds)
            && TitleSaveStableConstraintsPassed(titleSaveStable, budgetMilliseconds);
    }

    internal static bool ConnectionReuseConstraintsPassed(
        int connectionOpenCount,
        int connectionConfigureCount,
        long droppedSamples) =>
        connectionOpenCount == 0
        && connectionConfigureCount == 0
        && droppedSamples == 0;

    private static async Task<IReadOnlyList<PerformanceOperationResult>> MeasureWithIdleAsync(
        string operation,
        int iterations,
        int budgetMilliseconds,
        Action<int> action,
        Func<Task>? waitForStable = null,
        int? actionBudgetMilliseconds = null,
        Action<int>? validateAfterIteration = null,
        Func<WorkspaceActionPerformanceTiming?>? workspaceActionTiming = null)
    {
        var actionElapsed = new double[iterations];
        var actionAllocations = new long[iterations];
        var stableElapsed = new double[iterations];
        var stableAllocations = new long[iterations];
        var admissionElapsed = workspaceActionTiming is null ? null : new double[iterations];
        var queueWaitElapsed = workspaceActionTiming is null ? null : new double[iterations];
        var persistenceElapsed = workspaceActionTiming is null ? null : new double[iterations];
        var dispatcherElapsed = workspaceActionTiming is null ? null : new double[iterations];
        var completionElapsed = workspaceActionTiming is null ? null : new double[iterations];
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var totalAllocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            var started = Stopwatch.GetTimestamp();
            action(iteration);
            actionElapsed[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            actionAllocations[iteration] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            if (waitForStable is not null) await waitForStable();
            await IdleAsync();
            stableElapsed[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            stableAllocations[iteration] = GC.GetTotalAllocatedBytes(precise: false) - totalAllocatedBefore;
            if (workspaceActionTiming?.Invoke() is { } phases)
            {
                admissionElapsed![iteration] = phases.Admission.TotalMilliseconds;
                queueWaitElapsed![iteration] = phases.QueueWait.TotalMilliseconds;
                persistenceElapsed![iteration] = phases.Persistence.TotalMilliseconds;
                dispatcherElapsed![iteration] = phases.DispatcherApplication.TotalMilliseconds;
                completionElapsed![iteration] = phases.TotalCompletion.TotalMilliseconds;
            }
            validateAfterIteration?.Invoke(iteration);
        }

        var gen0Collections = GC.CollectionCount(0) - gen0Before;
        var gen1Collections = GC.CollectionCount(1) - gen1Before;
        var gen2Collections = GC.CollectionCount(2) - gen2Before;
        var results = new List<PerformanceOperationResult>
        {
            PerformanceOperationResult.Create(
                $"{operation}-action",
                actionElapsed,
                actionAllocations,
                actionBudgetMilliseconds ?? budgetMilliseconds) with
            {
                Gen0Collections = gen0Collections,
                Gen1Collections = gen1Collections,
                Gen2Collections = gen2Collections,
            },
            PerformanceOperationResult.Create(
                $"{operation}-stable",
                stableElapsed,
                stableAllocations,
                budgetMilliseconds) with
            {
                Gen0Collections = gen0Collections,
                Gen1Collections = gen1Collections,
                Gen2Collections = gen2Collections,
            },
        };
        if (workspaceActionTiming is not null)
        {
            var zeroAllocations = new long[iterations];
            results.Add(PerformanceOperationResult.Create(
                $"{operation}-admission", admissionElapsed!, zeroAllocations, 50));
            results.Add(PerformanceOperationResult.Create(
                $"{operation}-queue-wait", queueWaitElapsed!, zeroAllocations, budgetMilliseconds));
            results.Add(PerformanceOperationResult.Create(
                $"{operation}-persistence", persistenceElapsed!, zeroAllocations, budgetMilliseconds));
            results.Add(PerformanceOperationResult.Create(
                $"{operation}-dispatcher-application", dispatcherElapsed!, zeroAllocations, budgetMilliseconds));
            results.Add(PerformanceOperationResult.Create(
                $"{operation}-total-completion", completionElapsed!, zeroAllocations, budgetMilliseconds));
        }
        return results;
    }

    private static PerformanceOperationResult MeasureSync(
        string operation,
        int iterations,
        int budgetMilliseconds,
        Action<int> action) =>
        MeasureSyncWithSetup(operation, iterations, budgetMilliseconds, iteration => iteration, action);

    private static PerformanceOperationResult MeasureSyncWithSetup<T>(
        string operation,
        int iterations,
        int budgetMilliseconds,
        Func<int, T> setup,
        Action<T> action)
    {
        var elapsed = new double[iterations];
        var allocations = new long[iterations];
        var gen0Collections = 0;
        var gen1Collections = 0;
        var gen2Collections = 0;
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var input = setup(iteration);
            var gen0Before = GC.CollectionCount(0);
            var gen1Before = GC.CollectionCount(1);
            var gen2Before = GC.CollectionCount(2);
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            action(input);
            elapsed[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocations[iteration] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            gen0Collections += GC.CollectionCount(0) - gen0Before;
            gen1Collections += GC.CollectionCount(1) - gen1Before;
            gen2Collections += GC.CollectionCount(2) - gen2Before;
        }

        return PerformanceOperationResult.Create(operation, elapsed, allocations, budgetMilliseconds) with
        {
            Gen0Collections = gen0Collections,
            Gen1Collections = gen1Collections,
            Gen2Collections = gen2Collections,
        };
    }

    private static Task IdleAsync() =>
        Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).GetTask();

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Synthetic performance action did not reach its expected state.");
    }

    private static void WriteTimingObservation(string operation, TimeSpan duration) =>
        Console.WriteLine(
            $"performance-review: operation={operation} iterations=1 p50_ms={Format(duration.TotalMilliseconds)} "
            + $"p95_ms={Format(duration.TotalMilliseconds)} max_ms={Format(duration.TotalMilliseconds)} result=observed");

    private static void WriteResult(
        PerformanceOperationResult result,
        bool observedOnly = false)
    {
        Console.WriteLine(
            $"performance-review: operation={result.Operation} iterations={result.Iterations} "
            + $"p50_ms={Format(result.P50Milliseconds)} p95_ms={Format(result.P95Milliseconds)} "
            + $"max_ms={Format(result.MaximumMilliseconds)} alloc_p50_bytes={result.P50AllocatedBytes} "
            + $"gen0={result.Gen0Collections} gen1={result.Gen1Collections} gen2={result.Gen2Collections} "
            + $"budget_ms={result.BudgetMilliseconds} "
            + $"result={(observedOnly ? "observed" : result.P95Milliseconds <= result.BudgetMilliseconds ? "passed" : "failed")}");
    }

    private static string Configuration()
    {
#if DEBUG
        return "Debug";
#else
        return "Release";
#endif
    }

    private static string Format(double milliseconds) =>
        milliseconds.ToString("0.000", CultureInfo.InvariantCulture);
}

internal static class PerformanceReviewBuild
{
    public static string Commit()
    {
        var informationalVersion = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return Commit(informationalVersion);
    }

    internal static string Commit(string? informationalVersion)
    {
        var separator = informationalVersion?.LastIndexOf('+') ?? -1;
        var commit = separator >= 0 ? informationalVersion![(separator + 1)..] : string.Empty;
        return commit.Length == 40 && commit.All(Uri.IsHexDigit)
            ? commit.ToLowerInvariant()
            : "unrecorded";
    }
}

internal readonly record struct PerformanceOperationResult(
    string Operation,
    int Iterations,
    double P50Milliseconds,
    double P95Milliseconds,
    double MaximumMilliseconds,
    long P50AllocatedBytes,
    int BudgetMilliseconds)
{
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }

    public static PerformanceOperationResult Create(
        string operation,
        IReadOnlyCollection<double> elapsedMilliseconds,
        IReadOnlyCollection<long> allocatedBytes,
        int budgetMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetMilliseconds);
        if (elapsedMilliseconds.Count == 0 || elapsedMilliseconds.Count != allocatedBytes.Count)
            throw new ArgumentException("Measurement collections must be non-empty and have matching lengths.");

        var elapsed = elapsedMilliseconds.Order().ToArray();
        var allocations = allocatedBytes.Order().ToArray();
        return new(
            operation,
            elapsed.Length,
            Percentile(elapsed, 0.50),
            Percentile(elapsed, 0.95),
            elapsed[^1],
            Percentile(allocations, 0.50),
            budgetMilliseconds);
    }

    private static double Percentile(double[] sorted, double percentile) =>
        sorted[(int)Math.Ceiling(percentile * sorted.Length) - 1];

    private static long Percentile(long[] sorted, double percentile) =>
        sorted[(int)Math.Ceiling(percentile * sorted.Length) - 1];
}
