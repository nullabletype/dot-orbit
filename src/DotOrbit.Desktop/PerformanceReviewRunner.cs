using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
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
        return new(true, tasksValid && iterationsValid && budgetValid, taskCount, iterations, budget);
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
        TimeProvider timeProvider)
    {
        _directory = directory;
        Session = session;
        TimeProvider = timeProvider;
    }

    public IWorkspaceSession Session { get; }
    public TimeProvider TimeProvider { get; }
    internal string DirectoryPath => _directory;

    public static PerformanceReviewWorkspace Create(int taskCount)
    {
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
            var timeProvider = new FixedPerformanceTimeProvider(FixedNow);
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
                return new(directory, session, timeProvider);
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
    public override DateTimeOffset GetUtcNow() => utcNow;
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
        IWorkspaceWork store,
        PerformanceReviewScenario scenario,
        TimeSpan startupDuration)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(store);
        await IdleAsync();

        var shell = (ShellViewModel?)window.DataContext ?? throw new InvalidOperationException();
        var work = shell.Work ?? throw new InvalidOperationException();
        var activeTasks = store.Read().Tasks.Where(task => !task.IsArchived).Take(2).ToArray();
        if (activeTasks.Length < 2) throw new InvalidOperationException();

        Console.WriteLine(
            $"performance-review: environment commit={PerformanceReviewBuild.Commit()} configuration={Configuration()} "
            + $"os={Environment.OSVersion.Platform} framework={Environment.Version} tasks={scenario.TaskCount} "
            + $"iterations={scenario.Iterations} budget_ms={scenario.BudgetMilliseconds}");
        Console.WriteLine(
            $"performance-review: operation=startup-to-open iterations=1 p50_ms={Format(startupDuration.TotalMilliseconds)} "
            + $"p95_ms={Format(startupDuration.TotalMilliseconds)} max_ms={Format(startupDuration.TotalMilliseconds)} result=observed");

        work.SelectTask(activeTasks[0].Id);
        await IdleAsync();
        var results = new List<PerformanceOperationResult>();
        results.AddRange(await MeasureWithIdleAsync(
            "task-title-save-cold",
            1,
            scenario.BudgetMilliseconds,
            _ =>
            {
                work.Title = "Synthetic cold title save";
                if (!work.FlushPendingAutosave()) throw new InvalidOperationException();
            }));

        for (var warmup = 0; warmup < 3; warmup++)
        {
            work.SelectTask(activeTasks[warmup % 2].Id);
            shell.PrimaryNavigation.Single(item => item.Title == (warmup % 2 == 0 ? "Projects" : "Backlog"))
                .SelectCommand.Execute(null);
            work.SelectTask(activeTasks[0].Id);
            work.Title = $"Synthetic warm-up title {warmup % 2}";
            if (!work.FlushPendingAutosave()) throw new InvalidOperationException();
            await IdleAsync();
        }

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
        results.AddRange(await MeasureWithIdleAsync(
            "full-reload",
            scenario.Iterations,
            scenario.BudgetMilliseconds,
            _ => work.RefreshFromStore()));
        work.SelectTask(activeTasks[0].Id);
        await IdleAsync();
        results.AddRange(await MeasureWithIdleAsync(
            "task-title-save",
            scenario.Iterations,
            scenario.BudgetMilliseconds,
            iteration =>
            {
                work.Title = $"Synthetic task 00001 revision {iteration % 2}";
                if (!work.FlushPendingAutosave()) throw new InvalidOperationException();
            }));

        foreach (var result in results) WriteResult(result);
        var passed = results.All(result => result.P95Milliseconds <= scenario.BudgetMilliseconds);
        Console.WriteLine(
            $"performance-review: result={(passed ? "passed" : "failed")} code={(passed ? 0 : BudgetExceededExitCode)}");
        return passed ? 0 : BudgetExceededExitCode;
    }

    private static async Task<IReadOnlyList<PerformanceOperationResult>> MeasureWithIdleAsync(
        string operation,
        int iterations,
        int budgetMilliseconds,
        Action<int> action)
    {
        var actionElapsed = new double[iterations];
        var actionAllocations = new long[iterations];
        var stableElapsed = new double[iterations];
        var stableAllocations = new long[iterations];
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            action(iteration);
            actionElapsed[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            actionAllocations[iteration] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            await IdleAsync();
            stableElapsed[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            stableAllocations[iteration] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        }

        var gen0Collections = GC.CollectionCount(0) - gen0Before;
        var gen1Collections = GC.CollectionCount(1) - gen1Before;
        var gen2Collections = GC.CollectionCount(2) - gen2Before;
        return
        [
            PerformanceOperationResult.Create(
                $"{operation}-action",
                actionElapsed,
                actionAllocations,
                budgetMilliseconds) with
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
        ];
    }

    private static PerformanceOperationResult MeasureSync(
        string operation,
        int iterations,
        int budgetMilliseconds,
        Action<int> action)
    {
        var elapsed = new double[iterations];
        var allocations = new long[iterations];
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var started = Stopwatch.GetTimestamp();
            action(iteration);
            elapsed[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocations[iteration] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        }

        return PerformanceOperationResult.Create(operation, elapsed, allocations, budgetMilliseconds) with
        {
            Gen0Collections = GC.CollectionCount(0) - gen0Before,
            Gen1Collections = GC.CollectionCount(1) - gen1Before,
            Gen2Collections = GC.CollectionCount(2) - gen2Before,
        };
    }

    private static Task IdleAsync() =>
        Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).GetTask();

    private static void WriteResult(PerformanceOperationResult result)
    {
        Console.WriteLine(
            $"performance-review: operation={result.Operation} iterations={result.Iterations} "
            + $"p50_ms={Format(result.P50Milliseconds)} p95_ms={Format(result.P95Milliseconds)} "
            + $"max_ms={Format(result.MaximumMilliseconds)} alloc_p50_bytes={result.P50AllocatedBytes} "
            + $"gen0={result.Gen0Collections} gen1={result.Gen1Collections} gen2={result.Gen2Collections} "
            + $"budget_ms={result.BudgetMilliseconds} "
            + $"result={(result.P95Milliseconds <= result.BudgetMilliseconds ? "passed" : "failed")}");
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
