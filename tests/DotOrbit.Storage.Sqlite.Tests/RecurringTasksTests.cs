using DotOrbit.Core.Workspaces;
using DotOrbit.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DotOrbit.Storage.Sqlite.Tests;

public sealed class RecurringTasksTests : IDisposable
{
    private const string Secret = "synthetic recurring password";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "orbit-recurring-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _clock = new(new(2026, 1, 31, 12, 0, 0, TimeSpan.Zero));
    private readonly WorkspacePassphrase _passphrase = WorkspacePassphrase.Create(Secret, Secret).Passphrase!;
    private string WorkspacePath => Path.Combine(_directory, "workspace.db");
    private EncryptedWorkspaceStore Store => new(new SystemIdentifierGenerator(), _clock);
    private IWorkspaceSession Create() => Store.Create(WorkspacePath, _passphrase, CategoryName.Create("Synthetic").CategoryName!).Session!;
    private static IRecurringTasks Recurring(IWorkspaceSession session) => Assert.IsAssignableFrom<IRecurringTasks>(session.Work);
    private static RecurringTaskDraft Draft(IWorkspaceSession session) => new("Recurring synthetic task", "**description**", null,
        session.Work.Read().Categories[0].Id, [], RecurrenceCadence.Daily, new(2026, 1, 31), 5, true);
    private static long Revision(IWorkspaceSession session) => session.Planning.Read().WorkspaceRevision.Value;

    [Fact]
    public void DefinitionsPersistEncryptedAndAreAbsentFromPlanningWhileGeneratedTasksAreOrdinary()
    {
        string definitionId;
        string generatedId;
        using (var session = Create())
        {
            var participant = session.Work.CreateParticipant("AB");
            var project = session.Work.CreateProject("Synthetic project", "", session.Work.Read().Categories[0].Id, null);
            session.Work.CreateTask(project.Id, "Existing task");
            var api = Recurring(session);
            var definition = api.CreateRecurringTask(Draft(session) with { ProjectId = project.Id, ExplicitCategoryId = null, ParticipantIds = [participant.Id] });
            definitionId = definition.Id;
            Assert.Single(session.Planning.Read().Tasks);
            var before = Revision(session);
            var task = Assert.Single(api.ProcessDueRecurringTasks());
            generatedId = task.Id;
            Assert.NotEqual(definitionId, generatedId);
            Assert.Equal(before + 1, Revision(session));
            Assert.Equal(definition.Title, task.Title);
            Assert.Equal("**description**", task.Description);
            Assert.Equal(project.Id, task.ProjectId);
            Assert.Null(task.ExplicitCategoryId);
            Assert.Equal([participant.Id], task.ParticipantIds);
            Assert.Equal(new(2026, 2, 5), task.DueDate);
            Assert.False(task.IsComplete);
            Assert.False(task.IsArchived);
            Assert.Null(task.TodayLane);
            Assert.Equal(0, task.SharedPosition);
            Assert.Equal(1, task.ProjectPosition);
            Assert.Equal(generatedId, session.Work.Read().Tasks.OrderBy(item => item.SharedPosition).First().Id);
            Assert.Equal(2, session.Planning.Read().Tasks.Count);
            Assert.Empty(api.ProcessDueRecurringTasks());
            Assert.Equal(before + 1, Revision(session));
        }
        Assert.DoesNotContain("Recurring synthetic task", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(WorkspacePath)), StringComparison.Ordinal);
        using var reopened = Store.Open(WorkspacePath, _passphrase).Session!;
        Assert.Equal(definitionId, Assert.Single(Recurring(reopened).ReadRecurringTasks()).Id);
        Assert.Contains(reopened.Work.Read().Tasks, task => task.Id == generatedId);
        Assert.Empty(Recurring(reopened).ProcessDueRecurringTasks());
    }

    [Fact]
    public async Task LatestOnlyCatchUpUsesActualAdditionDateAndConcurrentRetryAndClockRollbackCannotDuplicate()
    {
        using var session = Create();
        var api = Recurring(session);
        api.CreateRecurringTask(Draft(session) with { Cadence = RecurrenceCadence.Monthly });
        _clock.SetUtcNow(new(2026, 5, 15, 12, 0, 0, TimeSpan.Zero));
        var before = Revision(session);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(api.ProcessDueRecurringTasks)));
        var task = Assert.Single(results.SelectMany(result => result));
        Assert.Equal(new(2026, 5, 20), task.DueDate);
        Assert.Equal(new(2026, 5, 31), Assert.Single(api.ReadRecurringTasks()).NextOccurrenceDate);
        Assert.Equal(before + 1, Revision(session));
        _clock.SetUtcNow(new(2026, 2, 28, 12, 0, 0, TimeSpan.Zero));
        Assert.Empty(api.ProcessDueRecurringTasks());
        _clock.SetUtcNow(new(2026, 5, 31, 12, 0, 0, TimeSpan.Zero));
        Assert.Single(api.ProcessDueRecurringTasks());
        Assert.Equal(new(2026, 6, 30), Assert.Single(api.ReadRecurringTasks()).NextOccurrenceDate);
    }

    [Fact]
    public void CrudValidationAndPauseResumeAffectOnlyFutureMaterialisation()
    {
        using var session = Create();
        var api = Recurring(session);
        var draft = Draft(session);
        Assert.Throws<ArgumentException>(() => api.CreateRecurringTask(draft with { Title = " " }));
        Assert.Throws<ArgumentException>(() => api.CreateRecurringTask(draft with { FirstOccurrenceDate = new(2026, 1, 30) }));
        Assert.Throws<ArgumentException>(() => api.CreateRecurringTask(draft with { ExplicitCategoryId = null }));
        Assert.Throws<ArgumentException>(() => api.CreateRecurringTask(draft with { ExplicitCategoryId = "absent" }));
        Assert.Throws<ArgumentException>(() => api.CreateRecurringTask(draft with { ParticipantIds = ["absent"] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => api.CreateRecurringTask(draft with { DueDateOffsetDays = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => api.CreateRecurringTask(draft with { DueDateOffsetDays = int.MaxValue }));
        Assert.Throws<ArgumentOutOfRangeException>(() => api.CreateRecurringTask(draft with { Cadence = (RecurrenceCadence)8 }));
        Assert.Empty(api.ReadRecurringTasks());
        var definition = api.CreateRecurringTask(draft);
        var task = Assert.Single(api.ProcessDueRecurringTasks());
        _clock.Advance(TimeSpan.FromDays(4));
        var before = Revision(session);
        api.UpdateRecurringTask(definition.Id, draft with { IsEnabled = false, Title = "Changed future" });
        Assert.Equal(before + 1, Revision(session));
        Assert.Empty(api.ProcessDueRecurringTasks());
        api.UpdateRecurringTask(definition.Id, draft with { IsEnabled = true, Title = "Changed future", DueDateOffsetDays = null });
        var second = Assert.Single(api.ProcessDueRecurringTasks());
        Assert.Equal("Changed future", second.Title);
        Assert.Null(second.DueDate);
        Assert.Equal(draft.Title, session.Work.Read().Tasks.Single(item => item.Id == task.Id).Title);
        api.DeleteRecurringTask(definition.Id);
        Assert.Empty(api.ReadRecurringTasks());
        Assert.Equal(2, session.Work.Read().Tasks.Count);
        Assert.Throws<ArgumentException>(() => api.UpdateRecurringTask(definition.Id, draft));
    }

    [Fact]
    public void DueOverflowRollsBackAllTasksAndCursorsAndLeavesPendingForRetry()
    {
        using var session = Create();
        var api = Recurring(session);
        _clock.SetUtcNow(new(9999, 12, 30, 12, 0, 0, TimeSpan.Zero));
        var draft = Draft(session) with { FirstOccurrenceDate = new(9999, 12, 30), DueDateOffsetDays = 0 };
        var first = api.CreateRecurringTask(draft with { Title = "A succeeds before rollback" });
        var second = api.CreateRecurringTask(draft with { Title = "Z overflows", DueDateOffsetDays = 1 });
        _clock.Advance(TimeSpan.FromDays(1));
        var before = Revision(session);
        Assert.Throws<ArgumentOutOfRangeException>(() => api.ProcessDueRecurringTasks());
        Assert.Empty(session.Work.Read().Tasks);
        Assert.Equal(before, Revision(session));
        Assert.All(api.ReadRecurringTasks(), item => Assert.Equal(new(9999, 12, 30), item.NextOccurrenceDate));
        api.UpdateRecurringTask(second.Id, draft with { Title = "Z corrected" });
        Assert.Equal(2, api.ProcessDueRecurringTasks().Count);
        Assert.All(api.ReadRecurringTasks(), item => Assert.Null(item.NextOccurrenceDate));
        Assert.Empty(api.ProcessDueRecurringTasks());
        Assert.Contains(api.ReadRecurringTasks(), item => item.Id == first.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProjectLifecyclePausesAndRestoreRequiresExplicitResume(bool bin)
    {
        using var session = Create();
        var api = Recurring(session);
        var category = session.Work.Read().Categories[0].Id;
        var project = session.Work.CreateProject("Project", "", category, null);
        var draft = Draft(session) with { ProjectId = project.Id, ExplicitCategoryId = null };
        var definition = api.CreateRecurringTask(draft);
        var before = Revision(session);
        if (bin) session.Work.MoveProjectToBin(project.Id); else session.Work.ArchiveProject(project.Id);
        Assert.Equal(before + 1, Revision(session));
        var paused = Assert.Single(api.ReadRecurringTasks());
        Assert.False(paused.IsEnabled);
        Assert.NotNull(paused.PauseReason);
        Assert.Empty(api.ProcessDueRecurringTasks());
        Assert.Throws<InvalidOperationException>(() => api.UpdateRecurringTask(definition.Id, draft));
        if (bin) session.Work.RestoreProjectFromBin(project.Id); else session.Work.RestoreProject(project.Id);
        Assert.False(Assert.Single(api.ReadRecurringTasks()).IsEnabled);
        Assert.Empty(api.ProcessDueRecurringTasks());
        api.UpdateRecurringTask(definition.Id, draft);
        Assert.Null(Assert.Single(api.ReadRecurringTasks()).PauseReason);
        Assert.Single(api.ProcessDueRecurringTasks());
    }

    [Fact]
    public void CategoryAndParticipantLifecyclesIncludeDefinitionsAndPermanentProjectDeletionPreservesPausedDefinition()
    {
        using var session = Create();
        var api = Recurring(session);
        var category = session.Work.Read().Categories[0];
        var replacement = session.Work.CreateCategory("Replacement");
        var participant = session.Work.CreateParticipant("AB");
        var project = session.Work.CreateProject("Project", "", category.Id, null);
        var draft = Draft(session) with { ProjectId = project.Id, ExplicitCategoryId = null, ParticipantIds = [participant.Id] };
        var definition = api.CreateRecurringTask(draft);
        Assert.Throws<InvalidOperationException>(() => session.Work.DeleteParticipant(participant.Id));
        session.Work.RenameParticipant(participant.Id, "CD");
        Assert.Equal([participant.Id], Assert.Single(api.ReadRecurringTasks()).ParticipantIds);
        session.Work.DeleteCategory(category.Id, replacement.Id);
        session.Work.MoveProjectToBin(project.Id);
        session.Work.EmptyBin(session.Work.PreviewEmptyBin());
        var preserved = Assert.Single(api.ReadRecurringTasks());
        Assert.Equal(definition.Id, preserved.Id);
        Assert.Null(preserved.ProjectId);
        Assert.Equal(replacement.Id, preserved.ExplicitCategoryId);
        Assert.False(preserved.IsEnabled);
        Assert.Contains("deleted", preserved.PauseReason, StringComparison.Ordinal);
        Assert.Empty(api.ProcessDueRecurringTasks());
        api.UpdateRecurringTask(definition.Id, draft with { ProjectId = null, ExplicitCategoryId = replacement.Id, ParticipantIds = [] });
        session.Work.DeleteParticipant(participant.Id);
        Assert.Single(api.ProcessDueRecurringTasks());
        var other = session.Work.CreateCategory("Other");
        session.Work.DeleteCategory(replacement.Id, other.Id);
        Assert.Equal(other.Id, Assert.Single(api.ReadRecurringTasks()).ExplicitCategoryId);
    }

    [Fact]
    public void NoOpReadsSavesAndProcessingDoNotAdvanceRevision()
    {
        using var session = Create();
        var api = Recurring(session);
        var draft = Draft(session) with { IsEnabled = false };
        var definition = api.CreateRecurringTask(draft);
        var before = Revision(session);
        api.UpdateRecurringTask(definition.Id, draft);
        Assert.Single(api.ReadRecurringTasks());
        Assert.Empty(api.ProcessDueRecurringTasks());
        Assert.Equal(before, Revision(session));
    }

    [Fact]
    public void SchemaEighteenMigratesWithoutAdvancingRevisionAndNewDefinitionsSurviveRecoveryRestore()
    {
        long revision;
        using (var session = Create()) revision = Revision(session);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = WorkspacePath, Password = Secret, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE recurring_task_participants; DROP TABLE recurring_tasks; PRAGMA user_version=18;";
            command.ExecuteNonQuery();
        }
        var opened = Store.Open(WorkspacePath, _passphrase);
        using var migrated = opened.Session!;
        Assert.Equal(WorkspaceOpenStatus.Opened, opened.Status);
        Assert.Equal(19, migrated.SchemaVersion);
        Assert.Equal(revision, Revision(migrated));
        Assert.Empty(Recurring(migrated).ReadRecurringTasks());
        Recurring(migrated).CreateRecurringTask(Draft(migrated));
        var recovery = migrated.Recovery.CreateRecoveryPoint(Path.Combine(_directory, "recovery"));
        Assert.Equal(RecoveryPointCreationStatus.Created, recovery.Status);
        Assert.NotNull(recovery.RecoveryPointPath);
        Assert.True(File.Exists(recovery.RecoveryPointPath));
        var definition = Assert.Single(Recurring(migrated).ReadRecurringTasks());
        Recurring(migrated).ProcessDueRecurringTasks();
        Recurring(migrated).DeleteRecurringTask(definition.Id);
        var beforeRestore = Revision(migrated);
        var restore = migrated.Recovery.Restore(recovery.RecoveryPointPath!, Path.Combine(_directory, "pre-restore"));
        using var restored = restore.Session!;
        Assert.Equal(WorkspaceRestoreStatus.Restored, restore.Status);
        Assert.Equal(definition.Id, Assert.Single(Recurring(restored).ReadRecurringTasks()).Id);
        Assert.Empty(restored.Work.Read().Tasks);
        Assert.True(Revision(restored) > beforeRestore);
        Assert.Single(Recurring(restored).ProcessDueRecurringTasks());
        Assert.Empty(Recurring(restored).ProcessDueRecurringTasks());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    public void EverySupportedReleasedSchemaMigratesToUsableRecurringStorage(int version)
    {
        Create().Dispose();
        File.Delete(WorkspacePath);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = WorkspacePath, Password = Secret, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE categories (id TEXT NOT NULL PRIMARY KEY,name TEXT NOT NULL COLLATE NOCASE UNIQUE,position INTEGER NOT NULL CHECK (position >= 0));
                INSERT INTO categories VALUES ('synthetic-category','Synthetic',0);
                """;
            command.ExecuteNonQuery();
            var workSchema = version switch
            {
                1 or 2 => "",
                3 => SqliteWorkspaceWork.SchemaThree,
                4 => SqliteWorkspaceWork.SchemaFour,
                5 => SqliteWorkspaceWork.SchemaFive,
                6 => SqliteWorkspaceWork.SchemaSix,
                7 => SqliteWorkspaceWork.SchemaSeven,
                8 => SqliteWorkspaceWork.SchemaEight,
                9 => SqliteWorkspaceWork.SchemaNine,
                10 => SqliteWorkspaceWork.SchemaTen,
                11 => SqliteWorkspaceWork.SchemaEleven,
                12 => SqliteWorkspaceWork.SchemaTwelve,
                13 => SqliteWorkspaceWork.SchemaThirteen,
                14 => SqliteWorkspaceWork.SchemaFourteen,
                _ => SqliteWorkspaceWork.Schema,
            };
            command.CommandText = (version >= 2 ? "CREATE INDEX ix_categories_position ON categories(position);" : "") + workSchema
                + (version >= 13 ? "INSERT INTO category_identities VALUES ('synthetic-category','orchid');" : "")
                + (version switch { 16 => SqliteAgentIntegrationStore.SchemaSixteen, 17 => SqliteAgentIntegrationStore.SchemaSeventeen, 18 => SqliteAgentIntegrationStore.Schema, _ => "" })
                + "PRAGMA user_version=" + version.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";";
            command.ExecuteNonQuery();
        }
        var opened = Store.Open(WorkspacePath, _passphrase);
        using var migrated = opened.Session!;
        Assert.Equal(WorkspaceOpenStatus.Opened, opened.Status);
        Assert.Equal(19, migrated.SchemaVersion);
        Assert.Equal(1, Revision(migrated));
        Assert.Empty(Recurring(migrated).ReadRecurringTasks());
        Recurring(migrated).CreateRecurringTask(Draft(migrated));
        Assert.Single(Recurring(migrated).ProcessDueRecurringTasks());
        Assert.Equal(3, Revision(migrated));
        Assert.Single(Directory.GetFiles(_directory, "dot-orbit-pre-migration-v" + version.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-*.dotorbit-recovery"));
    }

    [Fact]
    public void DefinitionAndMaterialisationScheduleEncryptedAutomaticRecovery()
    {
        using var session = Create();
        var directory = Path.Combine(_directory, "automatic");
        session.Recovery.ConfigureAutomaticRecoveryDirectory(directory);
        var api = Recurring(session);
        var definition = api.CreateRecurringTask(Draft(session));
        _clock.Advance(TimeSpan.FromHours(1));
        var first = Directory.GetFiles(directory, "*.dotorbit-recovery");
        Assert.NotEmpty(first);
        using (var saved = Store.Open(first.Order(StringComparer.Ordinal).Last(), _passphrase).Session!)
            Assert.Equal(definition.Id, Assert.Single(Recurring(saved).ReadRecurringTasks()).Id);
        var task = Assert.Single(api.ProcessDueRecurringTasks());
        _clock.Advance(TimeSpan.FromHours(1));
        var points = Directory.GetFiles(directory, "*.dotorbit-recovery");
        Assert.True(points.Length > first.Length);
        using var latest = Store.Open(points.Order(StringComparer.Ordinal).Last(), _passphrase).Session!;
        Assert.Equal(task.Id, Assert.Single(latest.Work.Read().Tasks).Id);
        Assert.Empty(Recurring(latest).ProcessDueRecurringTasks());
    }

    [Theory]
    [InlineData(RecurrenceCadence.Daily, 1)]
    [InlineData(RecurrenceCadence.Weekly, 7)]
    public void FutureScheduleWaitsThenGeneratesOneTaskAtCadenceBoundary(RecurrenceCadence cadence, int interval)
    {
        using var session = Create();
        var api = Recurring(session);
        var first = new DateOnly(2026, 2, 1);
        api.CreateRecurringTask(Draft(session) with { Cadence = cadence, FirstOccurrenceDate = first, DueDateOffsetDays = 0 });
        Assert.Empty(api.ProcessDueRecurringTasks());
        _clock.Advance(TimeSpan.FromDays(1));
        var task = Assert.Single(api.ProcessDueRecurringTasks());
        Assert.Equal(first, task.DueDate);
        Assert.Equal(first.AddDays(interval), Assert.Single(api.ReadRecurringTasks()).NextOccurrenceDate);
        Assert.Equal(task.Id, Assert.Single(UpcomingTaskProjection.Create(session.Work.Read().Tasks, first)).Id);
        Assert.DoesNotContain(session.Work.Read().Tasks, item => item.TodayLane is not null);
    }

    [Fact]
    public void TimeZoneChangesUseLocalAdditionDateWithoutRepeatingConsumedOccurrence()
    {
        _clock.SetUtcNow(new(2026, 1, 31, 23, 30, 0, TimeSpan.Zero));
        var time = new MutableZoneTimeProvider(_clock);
        var store = new EncryptedWorkspaceStore(new SystemIdentifierGenerator(), time);
        using var session = store.Create(WorkspacePath, _passphrase, CategoryName.Create("Synthetic").CategoryName!).Session!;
        var api = Recurring(session);
        api.CreateRecurringTask(Draft(session) with { FirstOccurrenceDate = new(2026, 2, 1), DueDateOffsetDays = 0 });
        Assert.Empty(api.ProcessDueRecurringTasks());
        time.Zone = TimeZoneInfo.CreateCustomTimeZone("Synthetic east", TimeSpan.FromHours(2), "Synthetic east", "Synthetic east");
        Assert.Equal(new(2026, 2, 1), Assert.Single(api.ProcessDueRecurringTasks()).DueDate);
        time.Zone = TimeZoneInfo.CreateCustomTimeZone("Synthetic west", TimeSpan.FromHours(-2), "Synthetic west", "Synthetic west");
        Assert.Empty(api.ProcessDueRecurringTasks());
        time.Zone = TimeZoneInfo.Utc;
        Assert.Empty(api.ProcessDueRecurringTasks());
        Assert.Single(session.Work.Read().Tasks);
        Assert.Equal(new(2026, 2, 2), Assert.Single(api.ReadRecurringTasks()).NextOccurrenceDate);
    }

    [Fact]
    public void EditingConsumedScheduleRetainsHighWaterMarkAndDoesNotRegenerateEarlierOccurrence()
    {
        using var session = Create();
        var api = Recurring(session);
        var draft = Draft(session);
        var definition = api.CreateRecurringTask(draft);
        Assert.Single(api.ProcessDueRecurringTasks());
        _clock.Advance(TimeSpan.FromDays(3));
        Assert.Single(api.ProcessDueRecurringTasks());
        api.UpdateRecurringTask(definition.Id, draft with { Cadence = RecurrenceCadence.Weekly });
        Assert.Equal(new(2026, 2, 7), Assert.Single(api.ReadRecurringTasks()).NextOccurrenceDate);
        Assert.Empty(api.ProcessDueRecurringTasks());
        _clock.SetUtcNow(new(2026, 1, 31, 12, 0, 0, TimeSpan.Zero));
        Assert.Empty(api.ProcessDueRecurringTasks());
        Assert.Equal(2, session.Work.Read().Tasks.Count);
    }

    [Fact]
    public void MultipleGeneratedResultsContainCommittedSharedPositionsAndRemainIndependentOfDefinition()
    {
        using var session = Create();
        var api = Recurring(session);
        var first = api.CreateRecurringTask(Draft(session) with { Title = "A" });
        api.CreateRecurringTask(Draft(session) with { Title = "B" });
        var before = Revision(session);
        var generated = api.ProcessDueRecurringTasks();
        Assert.Equal(before + 1, Revision(session));
        Assert.Equal([1L, 0L], generated.Select(task => task.SharedPosition));
        session.Work.UpdateTask(generated[0].Id, "Independent edit", "", generated[0].ExplicitCategoryId, null);
        api.DeleteRecurringTask(first.Id);
        Assert.Equal("Independent edit", session.Work.Read().Tasks.Single(task => task.Id == generated[0].Id).Title);
        Assert.Equal(2, session.Work.Read().Tasks.Count);
    }

    private sealed class MutableZoneTimeProvider(ManualTimeProvider clock) : TimeProvider
    {
        public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();
        public override TimeZoneInfo LocalTimeZone => Zone;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            clock.CreateTimer(callback, state, dueTime, period);
    }

    [Theory]
    [InlineData("date")]
    [InlineData("cursor")]
    [InlineData("reference")]
    [InlineData("shape")]
    [InlineData("false-exhaustion")]
    [InlineData("skipped-pending")]
    [InlineData("skipped-first")]
    public void MalformedRecurrenceRecordsFailClosedAtOpen(string corruption)
    {
        using (var session = Create())
        {
            Recurring(session).CreateRecurringTask(Draft(session));
            if (corruption is "false-exhaustion" or "skipped-pending")
                Assert.Single(Recurring(session).ProcessDueRecurringTasks());
        }
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = WorkspacePath, Password = Secret, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = corruption switch
            {
                "date" => "UPDATE recurring_tasks SET first_date='invalid';",
                "cursor" => "UPDATE recurring_tasks SET next_date='2026-01-30';",
                "reference" => "PRAGMA foreign_keys=OFF; UPDATE recurring_tasks SET project_id='missing';",
                "false-exhaustion" => "UPDATE recurring_tasks SET next_date=NULL;",
                "skipped-pending" => "UPDATE recurring_tasks SET next_date='2026-02-02';",
                "skipped-first" => "UPDATE recurring_tasks SET next_date='2026-02-01';",
                _ => "ALTER TABLE recurring_tasks ADD COLUMN unexpected TEXT;",
            };
            command.ExecuteNonQuery();
        }
        var opened = Store.Open(WorkspacePath, _passphrase);
        using var unexpected = opened.Session;
        Assert.Equal(WorkspaceOpenStatus.InvalidPassphraseOrStore, opened.Status);
        Assert.Null(unexpected);
    }

    [Fact]
    public void UpdatingOffsetValidatesFutureCursorRatherThanTodayOrHistoricalFirstDate()
    {
        using var session = Create();
        var api = Recurring(session);
        _clock.SetUtcNow(new(9999, 12, 24, 12, 0, 0, TimeSpan.Zero));
        var draft = Draft(session) with { FirstOccurrenceDate = new(9999, 12, 24), Cadence = RecurrenceCadence.Weekly, DueDateOffsetDays = 0 };
        var definition = api.CreateRecurringTask(draft);
        Assert.Single(api.ProcessDueRecurringTasks());
        _clock.Advance(TimeSpan.FromDays(1));
        var before = Revision(session);
        Assert.Throws<ArgumentOutOfRangeException>(() => api.UpdateRecurringTask(definition.Id, draft with { DueDateOffsetDays = 1 }));
        Assert.Equal(before, Revision(session));
        var unchanged = Assert.Single(api.ReadRecurringTasks());
        Assert.Equal(0, unchanged.DueDateOffsetDays);
        Assert.Equal(new(9999, 12, 31), unchanged.NextOccurrenceDate);
        _clock.Advance(TimeSpan.FromDays(6));
        Assert.Single(api.ProcessDueRecurringTasks());
        var exhausted = api.UpdateRecurringTask(definition.Id, draft with { Title = "Exhausted metadata" });
        Assert.Null(exhausted.NextOccurrenceDate);
        Assert.Equal("Exhausted metadata", exhausted.Title);
    }

    [Theory]
    [InlineData("future-first")]
    [InlineData("changed-cadence")]
    [InlineData("exhausted")]
    public void CursorValidationPreservesLegitimateEditedSchedulesAndTrueExhaustionAcrossRestart(string scenario)
    {
        DateOnly? expectedNext;
        using (var session = Create())
        {
            if (scenario == "exhausted") _clock.SetUtcNow(new(9999, 12, 31, 12, 0, 0, TimeSpan.Zero));
            var api = Recurring(session);
            var draft = Draft(session) with { FirstOccurrenceDate = DateOnly.FromDateTime(_clock.GetLocalNow().DateTime), DueDateOffsetDays = 0 };
            var definition = api.CreateRecurringTask(draft);
            Assert.Single(api.ProcessDueRecurringTasks());
            if (scenario == "future-first")
            {
                api.UpdateRecurringTask(definition.Id, draft with { FirstOccurrenceDate = new(2026, 2, 5) });
                expectedNext = new(2026, 2, 5);
            }
            else if (scenario == "changed-cadence")
            {
                _clock.Advance(TimeSpan.FromDays(3));
                Assert.Single(api.ProcessDueRecurringTasks());
                api.UpdateRecurringTask(definition.Id, draft with { Cadence = RecurrenceCadence.Weekly });
                expectedNext = new(2026, 2, 7);
            }
            else expectedNext = null;
        }
        var reopened = Store.Open(WorkspacePath, _passphrase);
        using var restored = reopened.Session!;
        Assert.Equal(WorkspaceOpenStatus.Opened, reopened.Status);
        Assert.Equal(expectedNext, Assert.Single(Recurring(restored).ReadRecurringTasks()).NextOccurrenceDate);
        Assert.Empty(Recurring(restored).ProcessDueRecurringTasks());
        Assert.Equal(scenario == "changed-cadence" ? 2 : 1, restored.Work.Read().Tasks.Count);
    }

    [Fact]
    public void CreateAtomicallyNormalizesAndReusesParticipantLabelsWithOneRevision()
    {
        using var session = Create();
        var existing = session.Work.CreateParticipant("ab");
        var before = Revision(session);
        var definition = Recurring(session).CreateRecurringTask(Draft(session) with
        {
            ParticipantIds = [existing.Id],
            NewParticipantLabels = [" ＡＢ ", "  CD  ", "cd"],
        });
        Assert.Equal(before + 1, Revision(session));
        var catalogue = session.Work.Read().Participants;
        Assert.Equal(2, catalogue.Count);
        Assert.Equal("ab", catalogue.Single(item => item.Id == existing.Id).Label);
        var created = Assert.Single(catalogue, item => item.Id != existing.Id);
        Assert.Equal("CD", created.Label);
        Assert.Equal([existing.Id, created.Id], definition.ParticipantIds);
        var task = Assert.Single(Recurring(session).ProcessDueRecurringTasks());
        Assert.Equal(definition.ParticipantIds, task.ParticipantIds);
    }

    [Fact]
    public void UpdateAtomicallyAddsAndReusesLabelsWithoutRewritingEarlierTasksOrAdvancingNoOpRevision()
    {
        using var session = Create();
        var api = Recurring(session);
        var draft = Draft(session) with { NewParticipantLabels = new[] { "AB" } };
        var definition = api.CreateRecurringTask(draft);
        var original = Assert.Single(api.ProcessDueRecurringTasks());
        var existing = session.Work.CreateParticipant("CD");
        var before = Revision(session);
        var update = draft with { NewParticipantLabels = [" cd ", "EF", "ｅｆ"] };
        var updated = api.UpdateRecurringTask(definition.Id, update);
        Assert.Equal(before + 1, Revision(session));
        var created = session.Work.Read().Participants.Single(item => item.Label == "EF");
        Assert.Equal([existing.Id, created.Id], updated.ParticipantIds);
        Assert.Equal(3, session.Work.Read().Participants.Count);
        Assert.Equal(original.ParticipantIds, session.Work.Read().Tasks.Single(item => item.Id == original.Id).ParticipantIds);
        var after = Revision(session);
        api.UpdateRecurringTask(definition.Id, update);
        Assert.Equal(after, Revision(session));
        Assert.Equal(3, session.Work.Read().Participants.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidStagedParticipantLabelRollsBackCreatedLabelsAndDefinitionTogether(bool update)
    {
        using var session = Create();
        var api = Recurring(session);
        var draft = Draft(session);
        var existing = update ? api.CreateRecurringTask(draft) : null;
        var before = Revision(session);
        var invalid = draft with { Title = "Unsaved content", NewParticipantLabels = ["Would be orphaned", " "] };
        Assert.Throws<ArgumentException>(() => update
            ? api.UpdateRecurringTask(existing!.Id, invalid)
            : api.CreateRecurringTask(invalid));
        Assert.Equal(before, Revision(session));
        Assert.Empty(session.Work.Read().Participants);
        if (existing is null) Assert.Empty(api.ReadRecurringTasks());
        else
        {
            var retained = Assert.Single(api.ReadRecurringTasks());
            Assert.Equal(existing.Title, retained.Title);
            Assert.Empty(retained.ParticipantIds);
        }
    }

    [Theory]
    [InlineData("title")]
    [InlineData("category")]
    [InlineData("due-offset")]
    [InlineData("participant-reference")]
    public void InvalidDefinitionDoesNotLeaveOrphanedStagedParticipants(string invalidField)
    {
        using var session = Create();
        var api = Recurring(session);
        var draft = Draft(session) with { NewParticipantLabels = ["Would be orphaned"] };
        var invalid = invalidField switch
        {
            "title" => draft with { Title = " " },
            "category" => draft with { ExplicitCategoryId = "missing" },
            "due-offset" => draft with { DueDateOffsetDays = -1 },
            _ => draft with { ParticipantIds = ["missing"] },
        };
        var before = Revision(session);
        Assert.ThrowsAny<ArgumentException>(() => api.CreateRecurringTask(invalid));
        Assert.Equal(before, Revision(session));
        Assert.Empty(api.ReadRecurringTasks());
        Assert.Empty(session.Work.Read().Participants);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
