using System.Globalization;
using DotOrbit.Core.Workspaces;
using Microsoft.Data.Sqlite;

namespace DotOrbit.Storage.Sqlite;

internal sealed partial class SqliteWorkspaceWork
{
    internal const string RecurringSchema = """
        CREATE TABLE recurring_tasks (
            id TEXT NOT NULL PRIMARY KEY,
            title TEXT NOT NULL CHECK(length(trim(title)) > 0),
            description TEXT NOT NULL,
            project_id TEXT REFERENCES projects(id),
            explicit_category_id TEXT REFERENCES categories(id),
            cadence INTEGER NOT NULL CHECK(cadence IN (0,1,2)),
            first_date TEXT NOT NULL,
            next_date TEXT,
            last_date TEXT,
            due_offset INTEGER CHECK(due_offset IS NULL OR (typeof(due_offset)='integer' AND due_offset >= 0)),
            enabled INTEGER NOT NULL CHECK(enabled IN (0,1)),
            pause_reason TEXT,
            CHECK(project_id IS NOT NULL OR explicit_category_id IS NOT NULL)
        );
        CREATE TABLE recurring_task_participants (
            recurring_id TEXT NOT NULL REFERENCES recurring_tasks(id) ON DELETE CASCADE,
            participant_id TEXT NOT NULL REFERENCES participants(id),
            position INTEGER NOT NULL CHECK(position >= 0),
            PRIMARY KEY(recurring_id,participant_id),
            UNIQUE(recurring_id,position)
        );
        """;

    internal static void ValidateRecurringShape(SqliteConnection connection, SqliteTransaction? transaction)
    {
        try { ValidateRecurringShapeCore(connection, transaction); }
        catch (FormatException) { throw new InvalidDataException(); }
        catch (ArgumentException) { throw new InvalidDataException(); }
        catch (OverflowException) { throw new InvalidDataException(); }
    }

    private static void ValidateRecurringShapeCore(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var definitions = RecurringSchema.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        ValidateTableDefinition(command, "recurring_tasks", definitions[0]);
        ValidateTableDefinition(command, "recurring_task_participants", definitions[1]);
        foreach (var item in ReadRecurringTasksCore(connection, transaction))
        {
            if (WorkTitle.Normalize(item.Title) != item.Title || !Enum.IsDefined(item.Cadence)
                || item.DueDateOffsetDays < 0 || item.NextOccurrenceDate < item.FirstOccurrenceDate)
                throw new InvalidDataException();
            if (item.NextOccurrenceDate is { } next
                && RecurrenceSchedule.LatestOccurrence(item.FirstOccurrenceDate, item.Cadence, next) != next)
                throw new InvalidDataException();
        }
        command.CommandText = "SELECT first_date,next_date,last_date,cadence FROM recurring_tasks;";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                for (var index = 0; index < 3; index++)
                    if (!reader.IsDBNull(index) && !DateOnly.TryParseExact(reader.GetString(index), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        throw new InvalidDataException();
                var first = ReadDate(reader, 0)!.Value;
                var last = ReadDate(reader, 2);
                var cadence = (RecurrenceCadence)reader.GetInt32(3);
                // Edits may place the first occurrence after the retained high-water mark,
                // or change cadence so that the high-water date is no longer on that cadence.
                DateOnly? expectedNext = last is { } consumed && consumed >= first
                    ? RecurrenceSchedule.NextOccurrence(first, cadence,
                        RecurrenceSchedule.LatestOccurrence(first, cadence, consumed))
                    : first;
                if (ReadDate(reader, 1) != expectedNext) throw new InvalidDataException();
            }
        }
        command.CommandText = """
            SELECT COUNT(*) FROM recurring_tasks r WHERE enabled=1 AND
                (pause_reason IS NOT NULL OR project_id IN (SELECT project_id FROM project_archives)
                    OR project_id IN (SELECT project_id FROM project_bins));
            """;
        if ((long)command.ExecuteScalar()! != 0) throw new InvalidDataException();
        command.CommandText = "PRAGMA foreign_key_check;";
        using var foreignKeys = command.ExecuteReader();
        if (foreignKeys.Read()) throw new InvalidDataException();
    }

    public IReadOnlyList<RecurringTaskDefinition> ReadRecurringTasks() =>
        Guard(() => transactions.Read(ReadRecurringTasksCore));

    private static IReadOnlyList<RecurringTaskDefinition> ReadRecurringTasksCore(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT recurring_id,participant_id FROM recurring_task_participants ORDER BY recurring_id,position;";
        var participants = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                if (!participants.TryGetValue(reader.GetString(0), out var ids)) participants.Add(reader.GetString(0), ids = []);
                ids.Add(reader.GetString(1));
            }
        command.CommandText = "SELECT id,title,description,project_id,explicit_category_id,cadence,first_date,due_offset,enabled,next_date,pause_reason FROM recurring_tasks ORDER BY title,id;";
        var result = new List<RecurringTaskDefinition>();
        using var rows = command.ExecuteReader();
        while (rows.Read())
            result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetString(2),
                rows.IsDBNull(3) ? null : rows.GetString(3), rows.IsDBNull(4) ? null : rows.GetString(4),
                participants.TryGetValue(rows.GetString(0), out var ids) ? ids.AsReadOnly() : [],
                (RecurrenceCadence)rows.GetInt32(5), ReadDate(rows, 6)!.Value,
                rows.IsDBNull(7) ? null : rows.GetInt32(7), rows.GetInt64(8) == 1,
                ReadDate(rows, 9), rows.IsDBNull(10) ? null : rows.GetString(10)));
        return result.AsReadOnly();
    }

    public RecurringTaskDefinition CreateRecurringTask(RecurringTaskDraft draft) => SaveRecurringTask(null, draft);
    public RecurringTaskDefinition UpdateRecurringTask(string id, RecurringTaskDraft draft) => SaveRecurringTask(id, draft);

    private RecurringTaskDefinition SaveRecurringTask(string? id, RecurringTaskDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var title = WorkTitle.Normalize(draft.Title);
        ArgumentNullException.ThrowIfNull(draft.Description);
        ArgumentNullException.ThrowIfNull(draft.ParticipantIds);
        if (!Enum.IsDefined(draft.Cadence)) throw new ArgumentOutOfRangeException(nameof(draft));
        var participantIds = draft.ParticipantIds.ToArray();
        var newParticipantLabels = draft.NewParticipantLabels?.ToArray() ?? [];
        if (participantIds.Distinct(StringComparer.Ordinal).Count() != participantIds.Length)
            throw new ArgumentException("Participants must be unique.", nameof(draft));
        RecurringTaskDefinition? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            var today = CurrentLocalDate();
            var previous = id is null ? null : ReadRecurringTasksCore(connection, transaction).SingleOrDefault(item => item.Id == id)
                ?? throw new ArgumentException("The Recurring Task definition does not exist.", nameof(id));
            if (draft.FirstOccurrenceDate < today && (previous is null || previous.FirstOccurrenceDate != draft.FirstOccurrenceDate))
                throw new ArgumentException("The first occurrence must be today or later.", nameof(draft));
            ValidateRecurringReferences(connection, transaction, draft.ProjectId, draft.ExplicitCategoryId, participantIds);
            var recordId = id ?? store.GetIdentifier();
            DateOnly? next = draft.FirstOccurrenceDate;
            DateOnly? last = null;
            if (previous is not null)
            {
                using var lastCommand = connection.CreateCommand();
                lastCommand.Transaction = transaction;
                lastCommand.CommandText = "SELECT last_date FROM recurring_tasks WHERE id=$id;";
                lastCommand.Parameters.AddWithValue("$id", recordId);
                var lastValue = lastCommand.ExecuteScalar();
                if (lastValue is string date) last = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (previous.FirstOccurrenceDate == draft.FirstOccurrenceDate && previous.Cadence == draft.Cadence)
                    next = previous.NextOccurrenceDate;
                else if (last is { } consumed && consumed >= draft.FirstOccurrenceDate)
                    next = RecurrenceSchedule.NextOccurrence(draft.FirstOccurrenceDate, draft.Cadence,
                        RecurrenceSchedule.LatestOccurrence(draft.FirstOccurrenceDate, draft.Cadence, consumed));
            }
            RecurrenceSchedule.DueDate(next is { } addition && addition > today ? addition : today, draft.DueDateOffsetDays);
            var associations = ResolveRecurringParticipantChanges(connection, transaction,
                new ParticipantDraftChange(participantIds, newParticipantLabels));
            Execute(connection, transaction, """
                INSERT INTO recurring_tasks (id,title,description,project_id,explicit_category_id,cadence,first_date,next_date,last_date,due_offset,enabled,pause_reason)
                VALUES ($id,$title,$description,$project,$category,$cadence,$first,$next,$last,$offset,$enabled,NULL)
                ON CONFLICT(id) DO UPDATE SET title=excluded.title,description=excluded.description,
                    project_id=excluded.project_id,explicit_category_id=excluded.explicit_category_id,cadence=excluded.cadence,
                    first_date=excluded.first_date,next_date=excluded.next_date,due_offset=excluded.due_offset,
                    enabled=excluded.enabled,pause_reason=NULL;
                """, ("$id", recordId), ("$title", title), ("$description", draft.Description), ("$project", draft.ProjectId),
                ("$category", draft.ExplicitCategoryId), ("$cadence", (int)draft.Cadence), ("$first", Date(draft.FirstOccurrenceDate)),
                ("$next", Date(next)), ("$last", Date(last)), ("$offset", draft.DueDateOffsetDays), ("$enabled", draft.IsEnabled ? 1 : 0));
            var oldParticipants = previous?.ParticipantIds ?? [];
            if (!oldParticipants.SequenceEqual(associations))
            {
                Execute(connection, transaction, "DELETE FROM recurring_task_participants WHERE recurring_id=$id;", ("$id", recordId));
                for (var index = 0; index < associations.Count; index++)
                    Execute(connection, transaction, "INSERT INTO recurring_task_participants VALUES ($id,$participant,$position);",
                        ("$id", recordId), ("$participant", associations[index]), ("$position", index));
            }
            result = ReadRecurringTasksCore(connection, transaction).Single(item => item.Id == recordId);
            return ArchiveSearchIndexEffects.None;
        }));
        return result!;
    }

    private List<string> ResolveRecurringParticipantChanges(
        SqliteConnection connection, SqliteTransaction transaction, ParticipantDraftChange change)
    {
        var associations = change.ParticipantIds.ToList();
        var labels = change.NewParticipantLabels.Select(ParticipantLabel.Normalize).ToArray();
        foreach (var label in labels)
        {
            var comparisonKey = ParticipantLabel.ComparisonKey(label);
            using var existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = "SELECT id FROM participants WHERE comparison_key=$key;";
            existing.Parameters.AddWithValue("$key", comparisonKey);
            var participantId = existing.ExecuteScalar() as string;
            if (participantId is null)
            {
                participantId = store.GetIdentifier();
                Execute(connection, transaction,
                    "INSERT INTO participants (id,label,comparison_key) VALUES ($id,$label,$key);",
                    ("$id", participantId), ("$label", label), ("$key", comparisonKey));
            }
            if (!associations.Contains(participantId, StringComparer.Ordinal)) associations.Add(participantId);
        }
        return associations;
    }

    private static void ValidateRecurringReferences(SqliteConnection connection, SqliteTransaction transaction,
        string? projectId, string? categoryId, IReadOnlyList<string> participantIds)
    {
        if (projectId is null && categoryId is null) throw new ArgumentException("A standalone definition requires a Category.");
        if (projectId is not null) RequireActiveProject(connection, transaction, projectId);
        if (categoryId is not null) Require(connection, transaction, "categories", categoryId);
        foreach (var participantId in participantIds) Require(connection, transaction, "participants", participantId);
    }

    public void DeleteRecurringTask(string id) => Guard(() => transactions.Execute((connection, transaction) =>
    {
        Require(connection, transaction, "recurring_tasks", id);
        Execute(connection, transaction, "DELETE FROM recurring_task_participants WHERE recurring_id=$id; DELETE FROM recurring_tasks WHERE id=$id;", ("$id", id));
        return ArchiveSearchIndexEffects.None;
    }));

    public IReadOnlyList<TaskRecord> ProcessDueRecurringTasks()
    {
        var result = new List<TaskRecord>();
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            var today = CurrentLocalDate();
            foreach (var definition in ReadRecurringTasksCore(connection, transaction))
            {
                if (!definition.IsEnabled || definition.NextOccurrenceDate is not { } pending || pending > today) continue;
                ValidateRecurringReferences(connection, transaction, definition.ProjectId, definition.ExplicitCategoryId, definition.ParticipantIds);
                var dueDate = RecurrenceSchedule.DueDate(today, definition.DueDateOffsetDays);
                var latest = RecurrenceSchedule.LatestOccurrence(definition.FirstOccurrenceDate, definition.Cadence, today);
                var next = RecurrenceSchedule.NextOccurrence(definition.FirstOccurrenceDate, definition.Cadence, latest);
                ShiftSharedOrderForNewTask(connection, transaction);
                long? projectPosition = null;
                if (definition.ProjectId is not null)
                {
                    using var order = connection.CreateCommand();
                    order.Transaction = transaction;
                    order.CommandText = "SELECT COALESCE(MAX(project_position),-1)+1 FROM tasks WHERE project_id=$project;";
                    order.Parameters.AddWithValue("$project", definition.ProjectId);
                    projectPosition = (long)order.ExecuteScalar()!;
                }
                var taskId = store.GetIdentifier();
                Execute(connection, transaction,
                    "INSERT INTO tasks VALUES ($id,$project,$title,$description,$category,$due,0,$position,NULL,NULL);",
                    ("$id", taskId), ("$project", definition.ProjectId), ("$title", definition.Title),
                    ("$description", definition.Description), ("$category", definition.ExplicitCategoryId),
                    ("$due", Date(dueDate)), ("$position", projectPosition));
                ApplyParticipantChanges(connection, transaction, taskId, new(definition.ParticipantIds, []));
                Execute(connection, transaction, "UPDATE recurring_tasks SET last_date=$last,next_date=$next WHERE id=$id;",
                    ("$last", Date(latest)), ("$next", Date(next)), ("$id", definition.Id));
                result.Add(ReadStoredTask(connection, transaction, taskId, null));
            }
            if (result.Count > 1)
            {
                var committed = ReadSnapshot(connection, transaction).Tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
                for (var index = 0; index < result.Count; index++) result[index] = committed[result[index].Id];
            }
            return ArchiveSearchIndexEffects.None;
        }));
        return result.AsReadOnly();
    }

    private static void PauseRecurringTasksForProject(SqliteConnection connection, SqliteTransaction transaction, string projectId) =>
        Execute(connection, transaction,
            "UPDATE recurring_tasks SET enabled=0,pause_reason='Restore or reassign the Project, then enable this recurring Task to resume.' WHERE project_id=$id;", ("$id", projectId));
}
