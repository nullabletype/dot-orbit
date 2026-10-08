using DotOrbit.Core.Workspaces;
using DotOrbit.Storage.Sqlite;

namespace DotOrbit.SampleWorkspace;

internal static class SampleWorkspaceGenerator
{
    internal const string SamplePassphrase = "dot-orbit sample only";

    private const int ExpectedCategoryCount = 6;
    private const int ExpectedProjectCount = 6;
    private const int ExpectedTaskCount = 33;
    private const int ExpectedParticipantCount = 7;
    private const int ExpectedArchivedTaskCount = 6;
    private const int ExpectedArchivedProjectCount = 1;

    public static SampleWorkspaceSummary Generate(string outputPath, DateOnly anchorDate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        outputPath = Path.GetFullPath(outputPath);
        ValidateAnchorDate(anchorDate);
        if (File.Exists(outputPath))
        {
            throw new SampleWorkspaceException("output-already-exists");
        }
        if (File.Exists(RecoveryStatePath(outputPath)))
        {
            throw new SampleWorkspaceException("output-companion-exists");
        }

        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new SampleWorkspaceException("invalid-output-path");
        var stagingDirectory = Path.Combine(
            outputDirectory,
            $".dot-orbit-sample-{Guid.NewGuid():N}");
        var stagedPath = Path.Combine(stagingDirectory, Path.GetFileName(outputPath));
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            var summary = GenerateAndValidate(stagedPath, anchorDate);
            File.Move(stagedPath, outputPath, overwrite: false);
            return summary with { OutputPath = outputPath };
        }
        catch (SampleWorkspaceException)
        {
            throw;
        }
        catch (IOException)
        {
            throw new SampleWorkspaceException("workspace-publish-failed");
        }
        catch (UnauthorizedAccessException)
        {
            throw new SampleWorkspaceException("workspace-publish-failed");
        }
        finally
        {
            DeleteStagingDirectory(stagingDirectory);
        }
    }

    private static SampleWorkspaceSummary GenerateAndValidate(string stagedPath, DateOnly anchorDate)
    {
        var timeProvider = new AdjustableTimeProvider(AtUtc(anchorDate));
        var store = new EncryptedWorkspaceStore(new DeterministicIdentifierGenerator(), timeProvider);
        var passphrase = CreatePassphrase();
        var category = CategoryName.Create("Work").CategoryName
            ?? throw new SampleWorkspaceException("invalid-scenario");
        var creation = store.Create(stagedPath, passphrase, category);
        if (creation.Status != WorkspaceCreationStatus.Created || creation.Session is null)
        {
            throw new SampleWorkspaceException(creation.Status == WorkspaceCreationStatus.AlreadyExists
                ? "output-already-exists"
                : "workspace-create-failed");
        }

        SampleWorkspaceSummary summary;
        using (var session = creation.Session)
        {
            Populate(session.Work, timeProvider, anchorDate);
            summary = Validate(stagedPath, session, anchorDate);
        }

        var reopened = new EncryptedWorkspaceStore().Open(stagedPath, UnlockPassphrase());
        if (reopened.Status != WorkspaceOpenStatus.Opened || reopened.Session is null)
        {
            throw new SampleWorkspaceException("workspace-reopen-failed");
        }
        using (reopened.Session)
        {
            if (reopened.Session.SchemaVersion != EncryptedWorkspaceStore.CurrentSchemaVersion
                || reopened.Session.Work.Read().Tasks.Count != ExpectedTaskCount)
            {
                throw new SampleWorkspaceException("workspace-validation-failed");
            }
        }

        return summary;
    }

    private static void ValidateAnchorDate(DateOnly anchorDate)
    {
        try
        {
            foreach (var offset in Projects.Select(project => project.TargetDateOffset)
                         .Concat(Tasks.Select(task => task.DueDateOffset))
                         .Concat(Tasks.Select(task => task.CompletionDateOffset))
                         .OfType<int>())
            {
                anchorDate.AddDays(offset);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new SampleWorkspaceException("anchor-date-out-of-range");
        }
    }

    private static string RecoveryStatePath(string workspacePath) =>
        workspacePath + ".recovery-state.json";

    private static void DeleteStagingDirectory(string stagingDirectory)
    {
        try
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Populate(
        IWorkspaceWork work,
        AdjustableTimeProvider timeProvider,
        DateOnly anchorDate)
    {
        var categoryIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["work"] = work.Read().Categories.Single().Id,
        };
        var firstCategory = work.Read().Categories.Single();
        work.UpdateCategory(firstCategory.Id, firstCategory.Name, Categories[0].ColourKey);
        foreach (var definition in Categories.Skip(1))
        {
            categoryIds.Add(definition.Key, work.CreateCategory(definition.Name, definition.ColourKey).Id);
        }

        var participantIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in Participants)
        {
            participantIds.Add(definition.Key, work.CreateParticipant(definition.Label).Id);
        }

        var projectIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in Projects)
        {
            var project = work.CreateProject(
                definition.Title,
                definition.Description,
                categoryIds[definition.CategoryKey],
                Offset(anchorDate, definition.TargetDateOffset),
                definition.ColourKey);
            projectIds.Add(definition.Key, project.Id);
        }

        var taskIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var definition in Tasks.Reverse())
        {
            var participantChange = new ParticipantDraftChange(
                definition.ParticipantKeys.Select(key => participantIds[key]).ToArray(),
                []);
            var task = work.CreateTaskDraft(
                definition.ProjectKey is null ? null : projectIds[definition.ProjectKey],
                definition.Title,
                definition.Description,
                definition.CategoryKey is null ? null : categoryIds[definition.CategoryKey],
                Offset(anchorDate, definition.DueDateOffset),
                participantChange,
                definition.TodayLane);
            taskIds.Add(definition.Key, task.Id);
        }

        foreach (var project in Projects)
        {
            var projectTasks = Tasks
                .Where(task => task.ProjectKey == project.Key)
                .OrderBy(task => task.ProjectPosition)
                .ToArray();
            for (var position = 0; position < projectTasks.Length; position++)
            {
                work.MoveTaskInProject(projectIds[project.Key], taskIds[projectTasks[position].Key], position);
            }
        }

        var completionMinute = 0;
        foreach (var task in Tasks
                     .Where(task => task.CompletionDateOffset is not null)
                     .OrderBy(task => task.CompletionDateOffset)
                     .ThenBy(task => task.Key, StringComparer.Ordinal))
        {
            timeProvider.SetDate(anchorDate.AddDays(task.CompletionDateOffset!.Value), completionMinute++);
            work.CompleteTask(taskIds[task.Key]);
        }

        foreach (var archived in ArchivedTasks)
        {
            timeProvider.SetDate(anchorDate.AddDays(archived.ArchiveDateOffset), completionMinute++);
            work.ArchiveTask(taskIds[archived.TaskKey]);
        }
        foreach (var archived in ArchivedProjects)
        {
            timeProvider.SetDate(anchorDate.AddDays(archived.ArchiveDateOffset), completionMinute++);
            work.ArchiveProject(projectIds[archived.ProjectKey]);
        }
    }

    private static SampleWorkspaceSummary Validate(
        string outputPath,
        IWorkspaceSession session,
        DateOnly anchorDate)
    {
        var snapshot = session.Work.Read();
        var incomplete = snapshot.Tasks.Count(task => !task.IsComplete);
        var completed = snapshot.Tasks.Count(task => task.IsComplete);
        var archived = snapshot.Tasks.Count(task => task.IsArchived);
        var archivedProjects = snapshot.Projects.Count(project => project.IsArchived);
        var archivedProjectIds = snapshot.Projects.Where(project => project.IsArchived)
            .Select(project => project.Id).ToHashSet(StringComparer.Ordinal);
        var today = snapshot.Tasks.Count(task => task.IsInToday);
        var upcoming = snapshot.Tasks.Count(task =>
            !task.IsComplete
            && (task.ProjectId is null || !archivedProjectIds.Contains(task.ProjectId))
            && task.DueDate is { } dueDate
            && dueDate <= anchorDate.AddDays(7));
        var isValid = session.SchemaVersion == EncryptedWorkspaceStore.CurrentSchemaVersion
            && session.Recovery.AutomaticRecoveryDirectoryPath is null
            && snapshot.Categories.Count == ExpectedCategoryCount
            && snapshot.Projects.Count == ExpectedProjectCount
            && snapshot.Tasks.Count == ExpectedTaskCount
            && snapshot.Participants.Count == ExpectedParticipantCount
            && incomplete == 21
            && completed == 12
            && archived == ExpectedArchivedTaskCount
            && archivedProjects == ExpectedArchivedProjectCount
            && today == 8
            && upcoming == 11;
        if (!isValid)
        {
            throw new SampleWorkspaceException("invalid-scenario");
        }

        return new(
            outputPath,
            session.SchemaVersion,
            snapshot.Categories.Count,
            snapshot.Projects.Count,
            snapshot.Tasks.Count,
            snapshot.Participants.Count,
            incomplete,
            completed,
            archived,
            archivedProjects,
            today,
            upcoming);
    }

    private static WorkspacePassphrase CreatePassphrase() =>
        WorkspacePassphrase.Create(SamplePassphrase, SamplePassphrase).Passphrase
        ?? throw new SampleWorkspaceException("invalid-sample-passphrase");

    private static WorkspacePassphrase UnlockPassphrase() =>
        WorkspacePassphrase.ForUnlock(SamplePassphrase)
        ?? throw new SampleWorkspaceException("invalid-sample-passphrase");

    private static DateOnly? Offset(DateOnly anchorDate, int? offset) =>
        offset is null ? null : anchorDate.AddDays(offset.Value);

    private static DateTimeOffset AtUtc(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 9, 0, 0, TimeSpan.Zero);

    private static readonly CategoryDefinition[] Categories =
    [
        new("work", "Work", "indigo"),
        new("home", "Home", "teal"),
        new("health", "Health", "lime"),
        new("learning", "Learning & creative practice", "violet"),
        new("errands", "Errands", "tangerine"),
        new("someday", "Someday", "rose"),
    ];

    private static readonly ParticipantDefinition[] Participants =
    [
        new("sd", "SD"),
        new("ab", "AB"),
        new("mk", "MK"),
        new("garden", "Garden pal"),
        new("coach", "Coach"),
        new("zoe", "Zoë"),
        new("unused", "Unused"),
    ];

    private static readonly ProjectDefinition[] Projects =
    [
        new("launch", "Launch the dot-orbit sample workspace",
            "A representative release-shaped Project with due-date boundaries and both Today lanes.",
            "work", 7, "cyan"),
        new("garden", "Autumn garden",
            "Prepare the garden for winter while keeping errands distinct from work done at home.",
            "home", -3, "coral"),
        new("welsh", "Welsh foundations",
            "Build a small completed learning Project with Unicode examples such as café and Cymru.",
            "learning", -1, "gold"),
        new("kitchen", "Kitchen refresh",
            "An intentionally empty Project proving that no Tasks still means Not started.",
            "home", null, "cobalt"),
        new("health", "Health reset",
            "A partly complete Project targeted for today without becoming overdue.",
            "health", 0, "magenta"),
        new("accounts", "Prepare quarterly household accounts and paperwork",
            "A deliberately long title for truncation, ordering, and mixed-date checks.",
            "work", 30, "emerald"),
    ];

    private static readonly TaskDefinition[] Tasks =
    [
        Attached("polish-keyboard", "launch", 1, "Polish keyboard journey",
            "Check logical focus order and visible focus at the minimum window size.", null, 0, TodayLane.InProgress, null, "ab"),
        Standalone("pick-up-parcel", "Pick up parcel", "Collect it before the depot closes.", "errands", -7, TodayLane.InProgress, null, "ab"),
        Standalone("publish-changelog", "Publish sample changelog", "Record the generated scenario for reviewers.", "work", 0, null, 0, "sd"),
        Standalone("replace-bulb", "Replace hallway bulb", "Undated household maintenance.", "home", null, null, null),
        Attached("write-checklist", "launch", 0, "Write release checklist",
            "- Build\n- Test\n- Review", null, -1, TodayLane.Planned, null, "sd", "ab"),
        Attached("clear-pots", "garden", 0, "Clear summer pots", "Compost spent plants.", null, -8, null, -8, "garden"),
        Attached("evening-walk", "health", 2, "Evening walk", "Keep the routine gentle and repeatable.", null, null, TodayLane.InProgress, null, "coach"),
        Standalone("renew-library", "Renew library books", "Renew the Welsh grammar guide.", "learning", 0, TodayLane.Planned, null),
        Attached("greetings", "welsh", 0, "Practise greetings", "Bore da, prynhawn da, and noswaith dda.", null, -10, null, -10, "zoe"),
        Attached("order-bulbs", "garden", 1, "Order spring bulbs", "Use the local garden centre.", "errands", -5, null, null, "garden"),
        Attached("verify-recovery", "launch", 2, "Verify recovery wording", "Keep diagnostics operational and non-sensitive.", null, 1, TodayLane.Planned, null, "sd"),
        Attached("gather-statements", "accounts", 0, "Gather statements", "Collect the synthetic quarterly statements.", null, 6, null, null, "mk"),
        Attached("plant-bulbs", "garden", 2, "Plant bulbs", "Plant in two drifts near the fence.", null, 2, TodayLane.Planned, null, "garden", "zoe"),
        Attached("first-conversation", "welsh", 1, "Record first conversation", "Capture a short dialogue without personal data.", null, -2, null, -2, "zoe"),
        Standalone("buy-tickets", "Buy train tickets", "Choose the flexible fare.", "errands", 1, null, null, "sd"),
        Attached("prep-questions", "health", 1, "Prep questions", "Write questions for the next appointment.", null, 0, TodayLane.Planned, null, "coach"),
        Attached("baseline", "health", 0, "Run 5 km baseline", "Record only a synthetic result.", null, -2, null, -2, "coach"),
        Standalone("read-adr", "Read migration ADR", "Review the forward-only migration boundary.", "learning", 8, null, null),
        Attached("repair-bed", "garden", 3, "Repair raised bed", "Replace the cracked corner brace.", null, -1, null, -1, "zoe"),
        Standalone("dentist", "Schedule dentist", "Book a routine check-up.", "health", 7, TodayLane.Planned, null, "coach"),
        Attached("pronunciation", "welsh", 2, "Finish pronunciation review", "Review ŵ, ŷ, and ll.", null, 0, null, 0, "zoe"),
        Attached("winter-layout", "garden", 4, "Sketch winter layout", "No due date; keep it in shared order.", null, null, null, null),
        Attached("platform-smoke", "launch", 3, "Run platform smoke checks", "macOS, Windows, and Linux.", null, 7, null, null, "mk"),
        Attached("match-receipts", "accounts", 1, "Match receipts", "Match synthetic receipts to statement rows.", null, 7, null, null, "mk"),
        Attached("refill-vitamins", "health", 3, "Refill vitamins", "Just outside the Upcoming window.", null, 8, null, null),
        Standalone("weekly-update", "Send weekly update", "A later Work task.", "work", 30, null, null, "ab"),
        Standalone("retrospective", "Write short retrospective", "Capture what worked and what changed.", "work", -2, null, -2, "sd", "mk"),
        Attached("write-summary", "accounts", 2, "Write summary", "Summarise the quarter in plain language.", null, 30, null, null, "sd", "mk"),
        Standalone("archive-notes", "Consolidate workshop notes", "A completion from an earlier week.", "learning", -9, null, -9, "zoe"),
        Attached("markdown-showcase", "launch", 4, "Review Markdown showcase",
            "# Sample notes\n\n- Unicode: café, Zoë, Cymru\n- Safe link: https://example.com\n- Unsafe text: javascript:alert(1)\n\n`dotnet test`\n\n<strong>Raw HTML stays data.</strong>",
            null, null, null, null, "sd", "mk"),
        Standalone("tune-bike", "Tune the exercise bike", "A completion from two weeks ago.", "health", -15, null, -15),
        Standalone("file-warranty", "File appliance warranty", "A completion exactly thirty days ago.", "home", -30, null, -30),
        Standalone("close-loop", "Close an old household loop", "A completion just beyond thirty days.", "home", -31, null, -31),
    ];

    private static readonly ArchivedTaskDefinition[] ArchivedTasks =
    [
        new("clear-pots", 0),
        new("first-conversation", -1),
        new("retrospective", -2),
        new("archive-notes", -8),
        new("tune-bike", -15),
        new("file-warranty", -30),
    ];

    private static readonly ArchivedProjectDefinition[] ArchivedProjects =
    [
        new("garden", 0),
    ];

    private static TaskDefinition Attached(
        string key,
        string projectKey,
        int projectPosition,
        string title,
        string description,
        string? categoryKey,
        int? dueDateOffset,
        TodayLane? todayLane,
        int? completionDateOffset,
        params string[] participantKeys) =>
        new(key, projectKey, projectPosition, title, description, categoryKey, dueDateOffset, todayLane,
            completionDateOffset, participantKeys);

    private static TaskDefinition Standalone(
        string key,
        string title,
        string description,
        string categoryKey,
        int? dueDateOffset,
        TodayLane? todayLane,
        int? completionDateOffset,
        params string[] participantKeys) =>
        new(key, null, null, title, description, categoryKey, dueDateOffset, todayLane,
            completionDateOffset, participantKeys);

    private sealed record CategoryDefinition(string Key, string Name, string ColourKey);
    private sealed record ParticipantDefinition(string Key, string Label);
    private sealed record ArchivedTaskDefinition(string TaskKey, int ArchiveDateOffset);
    private sealed record ArchivedProjectDefinition(string ProjectKey, int ArchiveDateOffset);
    private sealed record ProjectDefinition(
        string Key,
        string Title,
        string Description,
        string CategoryKey,
        int? TargetDateOffset,
        string ColourKey);
    private sealed record TaskDefinition(
        string Key,
        string? ProjectKey,
        int? ProjectPosition,
        string Title,
        string Description,
        string? CategoryKey,
        int? DueDateOffset,
        TodayLane? TodayLane,
        int? CompletionDateOffset,
        IReadOnlyList<string> ParticipantKeys);
}

internal sealed record SampleWorkspaceSummary(
    string OutputPath,
    int SchemaVersion,
    int CategoryCount,
    int ProjectCount,
    int TaskCount,
    int ParticipantCount,
    int IncompleteTaskCount,
    int CompletedTaskCount,
    int ArchivedTaskCount,
    int ArchivedProjectCount,
    int TodayTaskCount,
    int UpcomingTaskCount);
