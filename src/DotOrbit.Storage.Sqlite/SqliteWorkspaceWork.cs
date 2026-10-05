using System.Globalization;
using System.Text;
using DotOrbit.Core.Workspaces;
using DotOrbit.Markdown;
using Microsoft.Data.Sqlite;

namespace DotOrbit.Storage.Sqlite;

internal sealed class SqliteWorkspaceWork(
    EncryptedWorkspaceStore store,
    WorkspaceTransactionCoordinator transactions,
    TimeProvider timeProvider) : IWorkspaceWork
{
    internal const string SchemaThree = """
        CREATE TABLE projects (
            id TEXT NOT NULL PRIMARY KEY,
            title TEXT NOT NULL CHECK(length(trim(title)) > 0),
            description TEXT NOT NULL,
            category_id TEXT NOT NULL REFERENCES categories(id),
            target_date TEXT,
            position INTEGER NOT NULL UNIQUE CHECK(position >= 0)
        );
        CREATE TABLE tasks (
            id TEXT NOT NULL PRIMARY KEY,
            project_id TEXT NOT NULL REFERENCES projects(id),
            title TEXT NOT NULL CHECK(length(trim(title)) > 0),
            description TEXT NOT NULL,
            category_override_id TEXT REFERENCES categories(id),
            due_date TEXT,
            shared_position INTEGER NOT NULL UNIQUE,
            project_position INTEGER NOT NULL CHECK(project_position >= 0),
            UNIQUE(project_id, project_position)
        );
        PRAGMA user_version = 3;
        """;

    internal const string ProjectSchema = """
        CREATE TABLE projects (
            id TEXT NOT NULL PRIMARY KEY,
            title TEXT NOT NULL CHECK(length(trim(title)) > 0),
            description TEXT NOT NULL,
            category_id TEXT NOT NULL REFERENCES categories(id),
            target_date TEXT,
            position INTEGER NOT NULL UNIQUE CHECK(position >= 0)
        );
        """;

    internal const string TaskSchemaFour = """
        CREATE TABLE tasks (
            id TEXT NOT NULL PRIMARY KEY,
            project_id TEXT REFERENCES projects(id),
            title TEXT NOT NULL CHECK(length(trim(title)) > 0),
            description TEXT NOT NULL,
            explicit_category_id TEXT REFERENCES categories(id),
            due_date TEXT,
            shared_position INTEGER NOT NULL UNIQUE CHECK(shared_position >= 0),
            project_position INTEGER CHECK(project_position >= 0),
            CHECK((project_id IS NULL AND explicit_category_id IS NOT NULL AND project_position IS NULL)
                OR (project_id IS NOT NULL AND project_position IS NOT NULL)),
            UNIQUE(project_id, project_position)
        );
        """;

    internal const string SchemaFour = ProjectSchema + TaskSchemaFour + "PRAGMA user_version = 4;";

    internal const string TaskSchema = """
        CREATE TABLE tasks (
            id TEXT NOT NULL PRIMARY KEY,
            project_id TEXT REFERENCES projects(id),
            title TEXT NOT NULL CHECK(length(trim(title)) > 0),
            description TEXT NOT NULL,
            explicit_category_id TEXT REFERENCES categories(id),
            due_date TEXT,
            shared_position INTEGER NOT NULL UNIQUE CHECK(shared_position >= 0),
            project_position INTEGER CHECK(project_position >= 0),
            completion_instant TEXT,
            completion_date TEXT,
            CHECK((project_id IS NULL AND explicit_category_id IS NOT NULL AND project_position IS NULL)
                OR (project_id IS NOT NULL AND project_position IS NOT NULL)),
            CHECK((completion_instant IS NULL AND completion_date IS NULL)
                OR (completion_instant IS NOT NULL AND completion_date IS NOT NULL)),
            UNIQUE(project_id, project_position)
        );
        """;

    internal const string SchemaFive = ProjectSchema + TaskSchema + "PRAGMA user_version = 5;";

    internal const string ParticipantSchema = """
        CREATE TABLE participants (
            id TEXT NOT NULL PRIMARY KEY,
            label TEXT NOT NULL CHECK(length(trim(label)) > 0),
            comparison_key TEXT NOT NULL UNIQUE CHECK(length(comparison_key) > 0)
        );
        """;

    internal const string TaskParticipantSchema = """
        CREATE TABLE task_participants (
            task_id TEXT NOT NULL REFERENCES tasks(id),
            participant_id TEXT NOT NULL REFERENCES participants(id),
            position INTEGER NOT NULL CHECK(position >= 0),
            PRIMARY KEY(task_id, participant_id),
            UNIQUE(task_id, position)
        );
        """;

    internal const string SchemaSix = ProjectSchema + TaskSchema + ParticipantSchema + TaskParticipantSchema
        + "PRAGMA user_version = 6;";

    internal const string TodayTaskSchema = """
        CREATE TABLE today_tasks (
            task_id TEXT NOT NULL PRIMARY KEY REFERENCES tasks(id),
            lane TEXT NOT NULL CHECK(lane IN ('planned', 'in_progress'))
        );
        """;

    internal const string SchemaSeven = ProjectSchema + TaskSchema + ParticipantSchema + TaskParticipantSchema
        + TodayTaskSchema + "PRAGMA user_version = 7;";

    internal const string TaskArchiveSchema = """
        CREATE TABLE task_archives (
            task_id TEXT NOT NULL PRIMARY KEY REFERENCES tasks(id),
            archived_instant TEXT NOT NULL,
            archive_date TEXT NOT NULL
        );
        """;

    internal const string SchemaEight = ProjectSchema + TaskSchema + ParticipantSchema + TaskParticipantSchema
        + TodayTaskSchema + TaskArchiveSchema + "PRAGMA user_version = 8;";

    internal const string ProjectArchiveSchema = """
        CREATE TABLE project_archives (
            project_id TEXT NOT NULL PRIMARY KEY REFERENCES projects(id),
            archived_instant TEXT NOT NULL,
            archive_date TEXT NOT NULL
        );
        """;

    internal const string SchemaNine = ProjectSchema + TaskSchema + ParticipantSchema + TaskParticipantSchema
        + TodayTaskSchema + TaskArchiveSchema + ProjectArchiveSchema + "PRAGMA user_version = 9;";

    internal const string ArchiveSearchSchema = """
        CREATE VIRTUAL TABLE archive_search USING fts5(
            record_type UNINDEXED,
            record_id UNINDEXED,
            title,
            description,
            category,
            participants,
            parent_project UNINDEXED,
            archive_ticks UNINDEXED,
            display_date UNINDEXED,
            date_kind UNINDEXED,
            tokenize = 'unicode61 remove_diacritics 2'
        );
        """;

    internal const string SchemaTen = ProjectSchema + TaskSchema + ParticipantSchema + TaskParticipantSchema
        + TodayTaskSchema + TaskArchiveSchema + ProjectArchiveSchema + ArchiveSearchSchema
        + "PRAGMA user_version = 10;";

    internal const string TaskBinSchema = """
        CREATE TABLE task_bins (
            task_id TEXT NOT NULL PRIMARY KEY REFERENCES tasks(id),
            removed_instant TEXT NOT NULL,
            today_lane TEXT CHECK(today_lane IS NULL OR today_lane IN ('planned', 'in_progress'))
        );
        CREATE TABLE task_bin_order_anchors (
            task_id TEXT NOT NULL REFERENCES task_bins(task_id) ON DELETE CASCADE,
            scope TEXT NOT NULL CHECK(scope IN ('shared', 'project')),
            anchor_task_id TEXT NOT NULL REFERENCES tasks(id),
            relative_position INTEGER NOT NULL CHECK(relative_position <> 0),
            PRIMARY KEY(task_id, scope, anchor_task_id),
            UNIQUE(task_id, scope, relative_position)
        );
        """;

    internal const string ProjectBinSchema = """
        CREATE TABLE project_bins (
            project_id TEXT NOT NULL PRIMARY KEY REFERENCES projects(id),
            removed_instant TEXT NOT NULL
        );
        """;

    internal const string ProjectBinAggregateSchema = """
        CREATE TABLE project_bin_tasks (
            project_id TEXT NOT NULL REFERENCES project_bins(project_id) ON DELETE CASCADE,
            task_id TEXT NOT NULL UNIQUE REFERENCES task_bins(task_id) ON DELETE CASCADE,
            PRIMARY KEY(project_id, task_id)
        );
        CREATE TABLE project_bin_order_anchors (
            project_id TEXT NOT NULL REFERENCES project_bins(project_id) ON DELETE CASCADE,
            anchor_project_id TEXT NOT NULL REFERENCES projects(id),
            relative_position INTEGER NOT NULL CHECK(relative_position <> 0),
            PRIMARY KEY(project_id, anchor_project_id),
            UNIQUE(project_id, relative_position)
        );
        """;

    internal const string SchemaEleven = ProjectSchema + TaskSchema + ParticipantSchema + TaskParticipantSchema
        + TodayTaskSchema + TaskArchiveSchema + ProjectArchiveSchema + ArchiveSearchSchema
        + TaskBinSchema + ProjectBinSchema + "PRAGMA user_version = 11;";

    internal const string Schema = ProjectSchema + TaskSchema + ParticipantSchema + TaskParticipantSchema
        + TodayTaskSchema + TaskArchiveSchema + ProjectArchiveSchema + ArchiveSearchSchema
        + TaskBinSchema + ProjectBinSchema + ProjectBinAggregateSchema + "PRAGMA user_version = 12;";

    internal static void ValidateShape(SqliteConnection connection, SqliteTransaction? transaction, string? schema = null)
    {
        schema ??= Schema;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Each released work schema has one canonical definition shared by creation and migration.
        // Checking it also verifies types, nullability, foreign keys, uniqueness and CHECKs;
        // foreign_key_check alone cannot detect missing foreign-key declarations.
        var definitions = schema.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        ValidateTableDefinition(command, "projects", definitions[0]);
        ValidateTableDefinition(command, "tasks", definitions[1]);
        var hasParticipants = string.Equals(schema, SchemaSix, StringComparison.Ordinal)
            || string.Equals(schema, SchemaSeven, StringComparison.Ordinal)
            || string.Equals(schema, SchemaEight, StringComparison.Ordinal)
            || string.Equals(schema, SchemaNine, StringComparison.Ordinal)
            || string.Equals(schema, SchemaTen, StringComparison.Ordinal)
            || string.Equals(schema, SchemaEleven, StringComparison.Ordinal)
            || string.Equals(schema, Schema, StringComparison.Ordinal);
        if (hasParticipants)
        {
            ValidateTableDefinition(command, "participants", definitions[2]);
            ValidateTableDefinition(command, "task_participants", definitions[3]);
        }
        var hasToday = string.Equals(schema, SchemaSeven, StringComparison.Ordinal)
            || string.Equals(schema, SchemaEight, StringComparison.Ordinal)
            || string.Equals(schema, SchemaNine, StringComparison.Ordinal)
            || string.Equals(schema, SchemaTen, StringComparison.Ordinal)
            || string.Equals(schema, SchemaEleven, StringComparison.Ordinal)
            || string.Equals(schema, Schema, StringComparison.Ordinal);
        if (hasToday)
            ValidateTableDefinition(command, "today_tasks", definitions[4]);
        var hasTaskArchive = string.Equals(schema, SchemaEight, StringComparison.Ordinal)
            || string.Equals(schema, SchemaNine, StringComparison.Ordinal)
            || string.Equals(schema, SchemaTen, StringComparison.Ordinal)
            || string.Equals(schema, SchemaEleven, StringComparison.Ordinal)
            || string.Equals(schema, Schema, StringComparison.Ordinal);
        if (hasTaskArchive)
            ValidateTableDefinition(command, "task_archives", definitions[5]);
        var hasProjectArchive = string.Equals(schema, SchemaNine, StringComparison.Ordinal)
            || string.Equals(schema, SchemaTen, StringComparison.Ordinal)
            || string.Equals(schema, SchemaEleven, StringComparison.Ordinal)
            || string.Equals(schema, Schema, StringComparison.Ordinal);
        if (hasProjectArchive)
            ValidateTableDefinition(command, "project_archives", definitions[6]);
        if (string.Equals(schema, SchemaTen, StringComparison.Ordinal)
            || string.Equals(schema, SchemaEleven, StringComparison.Ordinal)
            || string.Equals(schema, Schema, StringComparison.Ordinal))
            ValidateTableDefinition(command, "archive_search", definitions[7]);
        if (string.Equals(schema, SchemaEleven, StringComparison.Ordinal)
            || string.Equals(schema, Schema, StringComparison.Ordinal))
        {
            ValidateTableDefinition(command, "task_bins", definitions[8]);
            ValidateTableDefinition(command, "task_bin_order_anchors", definitions[9]);
            ValidateTableDefinition(command, "project_bins", definitions[10]);
        }
        if (string.Equals(schema, Schema, StringComparison.Ordinal))
        {
            ValidateTableDefinition(command, "project_bin_tasks", definitions[11]);
            ValidateTableDefinition(command, "project_bin_order_anchors", definitions[12]);
        }
        command.Parameters.Clear();
        command.CommandText = "PRAGMA foreign_key_check;";
        using (var foreignKeys = command.ExecuteReader())
        {
            if (foreignKeys.Read()) throw new InvalidDataException();
        }

        var hasCompletion = string.Equals(schema, SchemaFive, StringComparison.Ordinal)
            || hasParticipants;
        command.CommandText = "SELECT target_date FROM projects UNION ALL SELECT due_date FROM tasks"
            + (hasCompletion
                ? " UNION ALL SELECT completion_date FROM tasks;"
                : ";");
        using (var dates = command.ExecuteReader())
        {
            while (dates.Read())
            {
                if (!dates.IsDBNull(0)
                    && !DateOnly.TryParseExact(dates.GetString(0), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    throw new InvalidDataException();
                }
            }
        }
        if (hasCompletion)
        {
            command.CommandText = "SELECT completion_instant FROM tasks WHERE completion_instant IS NOT NULL;";
            using var instants = command.ExecuteReader();
            while (instants.Read())
            {
                if (!DateTimeOffset.TryParseExact(instants.GetString(0), "O", CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out _))
                    throw new InvalidDataException();
            }
        }
        if (hasParticipants)
        {
            command.CommandText = "SELECT label, comparison_key FROM participants;";
            using var participants = command.ExecuteReader();
            while (participants.Read())
                if (!string.Equals(
                        ParticipantLabel.ComparisonKey(participants.GetString(0)),
                        participants.GetString(1),
                        StringComparison.Ordinal))
                    throw new InvalidDataException();
        }
        if (hasToday)
        {
            command.CommandText = """
                SELECT COUNT(*)
                FROM today_tasks tt
                JOIN tasks t ON t.id = tt.task_id
                WHERE t.completion_instant IS NOT NULL OR t.completion_date IS NOT NULL;
                """;
            if ((long)command.ExecuteScalar()! != 0) throw new InvalidDataException();
        }
        if (hasTaskArchive)
        {
            command.CommandText = "SELECT archived_instant,archive_date FROM task_archives;";
            using (var archives = command.ExecuteReader())
            {
                while (archives.Read())
                    if (!DateTimeOffset.TryParseExact(archives.GetString(0), "O", CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out _)
                        || !DateOnly.TryParseExact(archives.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out _))
                        throw new InvalidDataException();
            }
            command.CommandText = """
                SELECT COUNT(*)
                FROM task_archives a
                JOIN tasks t ON t.id = a.task_id
                WHERE t.completion_instant IS NULL OR t.completion_date IS NULL;
                """;
            if ((long)command.ExecuteScalar()! != 0) throw new InvalidDataException();
        }
        if (hasProjectArchive)
        {
            command.CommandText = "SELECT archived_instant,archive_date FROM project_archives;";
            using (var archives = command.ExecuteReader())
            {
                while (archives.Read())
                    if (!DateTimeOffset.TryParseExact(archives.GetString(0), "O", CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out _)
                        || !DateOnly.TryParseExact(archives.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out _))
                        throw new InvalidDataException();
            }
            command.CommandText = """
                SELECT COUNT(*)
                FROM today_tasks tt
                JOIN tasks t ON t.id = tt.task_id
                JOIN project_archives a ON a.project_id = t.project_id;
                """;
            if ((long)command.ExecuteScalar()! != 0) throw new InvalidDataException();
        }
        if (string.Equals(schema, SchemaEleven, StringComparison.Ordinal)
            || string.Equals(schema, Schema, StringComparison.Ordinal))
        {
            command.CommandText = "SELECT removed_instant FROM task_bins UNION ALL SELECT removed_instant FROM project_bins;";
            using (var removals = command.ExecuteReader())
            {
                while (removals.Read())
                    if (!DateTimeOffset.TryParseExact(removals.GetString(0), "O", CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind, out _))
                        throw new InvalidDataException();
            }
            command.CommandText = "SELECT COUNT(*) FROM today_tasks WHERE task_id IN (SELECT task_id FROM task_bins);";
            if ((long)command.ExecuteScalar()! != 0) throw new InvalidDataException();
        }
    }

    private static void ValidateTableDefinition(SqliteCommand command, string table, string expected)
    {
        command.Parameters.Clear();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table;";
        command.Parameters.AddWithValue("$table", table);
        var actual = command.ExecuteScalar() as string;
        if (actual is null || !string.Equals(NormalizeDefinition(actual), NormalizeDefinition(expected), StringComparison.Ordinal))
        {
            throw new InvalidDataException();
        }
    }

    private static string NormalizeDefinition(string definition) =>
        string.Join(' ', definition.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public WorkspaceWorkSnapshot Read() => Guard(() => transactions.Read(ReadSnapshot));

    public IReadOnlyList<TaskBinRecord> ReadTaskBin() => Guard(() => transactions.Read((connection, transaction) =>
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT task_id,removed_instant,today_lane
            FROM task_bins
            WHERE task_id NOT IN (SELECT task_id FROM project_bin_tasks)
            ORDER BY removed_instant DESC,task_id;
            """;
        using var reader = command.ExecuteReader();
        var metadata = new List<(string Id, DateTimeOffset RemovedAt, TodayLane? TodayLane)>();
        while (reader.Read())
            metadata.Add((reader.GetString(0), ReadInstant(reader, 1)!.Value,
                reader.IsDBNull(2) ? null : ParseTodayLane(reader.GetString(2))));
        reader.Close();
        return (IReadOnlyList<TaskBinRecord>)metadata
            .Select(item => ReadTaskBinRecord(connection, transaction, item.Id, item.RemovedAt, item.TodayLane))
            .ToArray();
    }));

    public IReadOnlyList<ProjectBinRecord> ReadProjectBin() => Guard(() => transactions.Read((connection, transaction) =>
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT project_id,removed_instant
            FROM project_bins
            ORDER BY removed_instant DESC,project_id;
            """;
        using var reader = command.ExecuteReader();
        var metadata = new List<(string Id, DateTimeOffset RemovedAt)>();
        while (reader.Read()) metadata.Add((reader.GetString(0), ReadInstant(reader, 1)!.Value));
        reader.Close();
        return (IReadOnlyList<ProjectBinRecord>)metadata
            .Select(item => ReadProjectBinRecord(connection, transaction, item.Id, item.RemovedAt))
            .ToArray();
    }));

    public EmptyBinPreview PreviewEmptyBin() => Guard(() => transactions.Read(ReadEmptyBinPreview));

    public EmptyBinResult EmptyBin(EmptyBinPreview confirmedPreview)
    {
        ArgumentNullException.ThrowIfNull(confirmedPreview);
        return Guard(() => transactions.ExecuteEmptyBin(
            confirmedPreview,
            ReadEmptyBinPreview,
            (connection, transaction) =>
            {
                Execute(connection, transaction, """
                    CREATE TEMP TABLE empty_bin_task_ids (id TEXT NOT NULL PRIMARY KEY);
                    CREATE TEMP TABLE empty_bin_project_ids (id TEXT NOT NULL PRIMARY KEY);
                    INSERT INTO empty_bin_task_ids SELECT task_id FROM task_bins;
                    INSERT INTO empty_bin_project_ids SELECT project_id FROM project_bins;
                    DELETE FROM task_participants WHERE task_id IN (SELECT id FROM empty_bin_task_ids);
                    DELETE FROM today_tasks WHERE task_id IN (SELECT id FROM empty_bin_task_ids);
                    DELETE FROM task_archives WHERE task_id IN (SELECT id FROM empty_bin_task_ids);
                    DELETE FROM task_bin_order_anchors;
                    DELETE FROM project_bin_order_anchors;
                    DELETE FROM project_bin_tasks;
                    DELETE FROM task_bins;
                    DELETE FROM tasks WHERE id IN (SELECT id FROM empty_bin_task_ids);
                    DELETE FROM project_archives WHERE project_id IN (SELECT id FROM empty_bin_project_ids);
                    DELETE FROM project_bins;
                    DELETE FROM projects WHERE id IN (SELECT id FROM empty_bin_project_ids);
                    DROP TABLE empty_bin_task_ids;
                    DROP TABLE empty_bin_project_ids;
                    """);
            }));
    }

    public IReadOnlyList<ArchiveSearchResult> SearchArchive(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var match = BuildArchiveMatchQuery(query);
        if (match.Length == 0) return [];
        return Guard(() => transactions.Read((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT record_type, record_id, title, NULLIF(parent_project, ''),
                       date_kind, display_date,
                       snippet(archive_search, -1, '', '', ' … ', 12)
                FROM archive_search
                WHERE archive_search MATCH $query
                ORDER BY CAST(archive_ticks AS INTEGER) DESC, record_type, record_id;
                """;
            command.Parameters.AddWithValue("$query", match);
            using var reader = command.ExecuteReader();
            var results = new List<ArchiveSearchResult>();
            while (reader.Read())
            {
                results.Add(new(
                    reader.GetString(0) switch
                    {
                        "project" => ArchiveSearchRecordType.Project,
                        "task" => ArchiveSearchRecordType.Task,
                        _ => throw new InvalidDataException(),
                    },
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4) switch
                    {
                        "archived" => ArchiveSearchDateKind.Archived,
                        "completed" => ArchiveSearchDateKind.Completed,
                        _ => throw new InvalidDataException(),
                    },
                    DateOnly.ParseExact(reader.GetString(5), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    reader.GetString(6).Trim()));
            }
            return (IReadOnlyList<ArchiveSearchResult>)results.AsReadOnly();
        }));
    }

    public WorkspaceCategory CreateCategory(string name)
    {
        name = NormalizeCategoryName(name);
        WorkspaceCategory? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            EnsureCategoryNameAvailable(connection, transaction, name);
            var position = EncryptedWorkspaceStore.ExecuteScalar<long>(connection,
                "SELECT COALESCE(MAX(position), -1) + 1 FROM categories;", transaction);
            result = new WorkspaceCategory(store.GetIdentifier(), name, position);
            Execute(connection, transaction,
                "INSERT INTO categories (id,name,position) VALUES ($id,$name,$position);",
                ("$id", result.Id), ("$name", result.Name), ("$position", result.Position));
        }));
        return result!;
    }

    public WorkspaceCategory RenameCategory(string id, string name)
    {
        name = NormalizeCategoryName(name);
        WorkspaceCategory? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            Require(connection, transaction, "categories", id);
            EnsureCategoryNameAvailable(connection, transaction, name, id);
            Execute(connection, transaction, "UPDATE categories SET name=$name WHERE id=$id;",
                ("$id", id), ("$name", name));
            result = ReadSnapshot(connection, transaction).Categories.Single(category => category.Id == id);
        }));
        return result!;
    }

    public ParticipantRecord CreateParticipant(string label)
    {
        label = ParticipantLabel.Normalize(label);
        ParticipantRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            EnsureParticipantLabelAvailable(connection, transaction, label);
            result = new(store.GetIdentifier(), label);
            Execute(connection, transaction,
                "INSERT INTO participants (id,label,comparison_key) VALUES ($id,$label,$key);",
                ("$id", result.Id), ("$label", result.Label),
                ("$key", ParticipantLabel.ComparisonKey(result.Label)));
        }));
        return result!;
    }

    public ParticipantRecord RenameParticipant(string id, string label)
    {
        label = ParticipantLabel.Normalize(label);
        ParticipantRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            Require(connection, transaction, "participants", id);
            EnsureParticipantLabelAvailable(connection, transaction, label, id);
            Execute(connection, transaction,
                "UPDATE participants SET label=$label, comparison_key=$key WHERE id=$id;",
                ("$id", id), ("$label", label), ("$key", ParticipantLabel.ComparisonKey(label)));
            result = new(id, label);
        }));
        return result!;
    }

    public void DeleteParticipant(string id)
    {
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            Require(connection, transaction, "participants", id);
            using var referenceCommand = connection.CreateCommand();
            referenceCommand.Transaction = transaction;
            referenceCommand.CommandText = "SELECT COUNT(*) FROM task_participants WHERE participant_id=$id;";
            referenceCommand.Parameters.AddWithValue("$id", id);
            var references = (long)referenceCommand.ExecuteScalar()!;
            if (references > 0) throw new InvalidOperationException("The Participant is referenced by a Task.");
            Execute(connection, transaction, "DELETE FROM participants WHERE id=$id;", ("$id", id));
        }));
    }

    public void DeleteCategory(string id, string? replacementCategoryId = null)
    {
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            Require(connection, transaction, "categories", id);
            var categoryIds = ReadIds(connection, transaction, "SELECT id FROM categories ORDER BY position,id;");
            if (categoryIds.Count == 1) throw new InvalidOperationException("The final Category cannot be deleted.");

            using var referenceCommand = connection.CreateCommand();
            referenceCommand.Transaction = transaction;
            referenceCommand.CommandText = "SELECT (SELECT COUNT(*) FROM projects WHERE category_id=$id)"
                + " + (SELECT COUNT(*) FROM tasks WHERE explicit_category_id=$id);";
            referenceCommand.Parameters.AddWithValue("$id", id);
            var referenceCount = (long)referenceCommand.ExecuteScalar()!;
            if (referenceCount > 0)
            {
                if (replacementCategoryId is null || string.Equals(id, replacementCategoryId, StringComparison.Ordinal))
                    throw new ArgumentException("A distinct replacement Category is required.", nameof(replacementCategoryId));
                Require(connection, transaction, "categories", replacementCategoryId);
                Execute(connection, transaction, "UPDATE projects SET category_id=$replacement WHERE category_id=$id;",
                    ("$replacement", replacementCategoryId), ("$id", id));
                Execute(connection, transaction, "UPDATE tasks SET explicit_category_id=$replacement WHERE explicit_category_id=$id;",
                    ("$replacement", replacementCategoryId), ("$id", id));
            }
            else if (replacementCategoryId is not null)
            {
                throw new ArgumentException("An unreferenced Category does not need a replacement.", nameof(replacementCategoryId));
            }

            Execute(connection, transaction, "DELETE FROM categories WHERE id=$id;", ("$id", id));
            categoryIds.Remove(id);
            RewriteOrder(connection, transaction, "categories", "position", categoryIds);
        }));
    }

    public CategoryOrderChange MoveCategory(string id, int targetPosition)
    {
        CategoryOrderChange? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            Require(connection, transaction, "categories", id);
            var orderedIds = ReadIds(connection, transaction, "SELECT id FROM categories ORDER BY position,id;");
            Move(orderedIds, id, targetPosition);
            RewriteOrder(connection, transaction, "categories", "position", orderedIds);
            result = new(id, targetPosition + 1, orderedIds.Count);
        }));
        return result!;
    }

    public ProjectRecord CreateProject(string title, string description, string categoryId, DateOnly? targetDate)
    {
        title = WorkTitle.Normalize(title);
        ArgumentNullException.ThrowIfNull(description);
        ProjectRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            Require(connection, transaction, "categories", categoryId);
            var position = EncryptedWorkspaceStore.ExecuteScalar<long>(connection,
                "SELECT COALESCE(MAX(position), -1) + 1 FROM projects;", transaction);
            result = new ProjectRecord(store.GetIdentifier(), title, description, categoryId, targetDate, position);
            Execute(connection, transaction,
                "INSERT INTO projects VALUES ($id, $title, $description, $category, $date, $position);",
                ("$id", result.Id), ("$title", title), ("$description", description),
                ("$category", categoryId), ("$date", Date(targetDate)), ("$position", position));
        }));
        return result!;
    }

    public TaskRecord CreateTask(string projectId, string title)
    {
        title = WorkTitle.Normalize(title);
        TaskRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            RequireActiveProject(connection, transaction, projectId);
            ShiftSharedOrderForNewTask(connection, transaction);
            using var order = connection.CreateCommand();
            order.Transaction = transaction;
            order.CommandText = "SELECT COALESCE(MAX(project_position), -1) + 1 FROM tasks WHERE project_id=$project;";
            order.Parameters.AddWithValue("$project", projectId);
            var position = (long)order.ExecuteScalar()!;
            result = new TaskRecord(store.GetIdentifier(), projectId, title, string.Empty, null, null, 0, position);
            Execute(connection, transaction,
                "INSERT INTO tasks VALUES ($id, $project, $title, '', NULL, NULL, 0, $position, NULL, NULL);",
                ("$id", result.Id), ("$project", projectId), ("$title", title), ("$position", position));
        }));
        return result!;
    }

    public TaskRecord CreateTaskDraft(string? projectId, string title, string description, string? categoryId,
        DateOnly? dueDate, ParticipantDraftChange? participantChange = null, TodayLane? todayLane = null)
    {
        title = WorkTitle.Normalize(title);
        ArgumentNullException.ThrowIfNull(description);
        if (projectId is null && categoryId is null)
            throw new ArgumentException("A standalone Task requires a Category.", nameof(categoryId));
        TaskRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            if (projectId is not null) RequireActiveProject(connection, transaction, projectId);
            if (categoryId is not null) Require(connection, transaction, "categories", categoryId);
            ShiftSharedOrderForNewTask(connection, transaction);
            long? projectPosition = null;
            if (projectId is not null)
            {
                using var order = connection.CreateCommand();
                order.Transaction = transaction;
                order.CommandText = "SELECT COALESCE(MAX(project_position), -1) + 1 FROM tasks WHERE project_id=$project;";
                order.Parameters.AddWithValue("$project", projectId);
                projectPosition = (long)order.ExecuteScalar()!;
            }
            var id = store.GetIdentifier();
            Execute(connection, transaction,
                "INSERT INTO tasks VALUES ($id, $project, $title, $description, $category, $date, 0, $position, NULL, NULL);",
                ("$id", id), ("$project", projectId), ("$title", title), ("$description", description),
                ("$category", categoryId), ("$date", Date(dueDate)), ("$position", projectPosition));
            ApplyParticipantChanges(connection, transaction, id, participantChange ?? new([], []));
            if (todayLane is not null)
                Execute(connection, transaction, "INSERT INTO today_tasks (task_id, lane) VALUES ($id, $lane);",
                    ("$id", id), ("$lane", TodayLaneValue(todayLane.Value)));
            result = ReadSnapshot(connection, transaction).Tasks.Single(task => task.Id == id);
        }));
        return result!;
    }

    public TaskRecord CreateStandaloneTask(string title, string description, string categoryId, DateOnly? dueDate,
        ParticipantDraftChange? participantChange = null)
    {
        title = WorkTitle.Normalize(title);
        ArgumentNullException.ThrowIfNull(description);
        TaskRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            Require(connection, transaction, "categories", categoryId);
            ShiftSharedOrderForNewTask(connection, transaction);
            result = new TaskRecord(store.GetIdentifier(), null, title, description, categoryId, dueDate, 0, null);
            Execute(connection, transaction,
                "INSERT INTO tasks VALUES ($id, NULL, $title, $description, $category, $date, 0, NULL, NULL, NULL);",
                ("$id", result.Id), ("$title", title), ("$description", description),
                ("$category", categoryId), ("$date", Date(dueDate)));
            ApplyParticipantChanges(connection, transaction, result.Id,
                participantChange ?? new([], []));
            result = ReadSnapshot(connection, transaction).Tasks.Single(task => task.Id == result.Id);
        }));
        return result!;
    }

    public ProjectRecord UpdateProject(string id, string title, string description, string categoryId, DateOnly? targetDate)
    {
        title = WorkTitle.Normalize(title);
        ArgumentNullException.ThrowIfNull(description);
        ProjectRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            Require(connection, transaction, "projects", id);
            Require(connection, transaction, "categories", categoryId);
            Execute(connection, transaction,
                "UPDATE projects SET title=$title, description=$description, category_id=$category, target_date=$date WHERE id=$id;",
                ("$id", id), ("$title", title), ("$description", description), ("$category", categoryId), ("$date", Date(targetDate)));
            result = ReadSnapshot(connection, transaction).Projects.Single(project => project.Id == id);
        }));
        return result!;
    }

    public TaskRecord UpdateTask(string id, string title, string description, string? explicitCategoryId, DateOnly? dueDate,
        ParticipantDraftChange? participantChange = null)
    {
        title = WorkTitle.Normalize(title);
        ArgumentNullException.ThrowIfNull(description);
        TaskRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            RequireActiveTask(connection, transaction, id);
            var existing = ReadSnapshot(connection, transaction).Tasks.Single(task => task.Id == id);
            if (existing.ProjectId is null && explicitCategoryId is null)
                throw new ArgumentException("A standalone Task requires an explicit Category.", nameof(explicitCategoryId));
            if (explicitCategoryId is not null) Require(connection, transaction, "categories", explicitCategoryId);
            Execute(connection, transaction,
                "UPDATE tasks SET title=$title, description=$description, explicit_category_id=$category, due_date=$date WHERE id=$id;",
                ("$id", id), ("$title", title), ("$description", description), ("$category", explicitCategoryId), ("$date", Date(dueDate)));
            if (participantChange is not null)
                ApplyParticipantChanges(connection, transaction, id, participantChange);
            result = ReadSnapshot(connection, transaction).Tasks.Single(task => task.Id == id);
        }));
        return result!;
    }

    public TaskRecord CompleteTask(string id)
    {
        TaskRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            RequireActiveTask(connection, transaction, id);
            var task = ReadSnapshot(connection, transaction).Tasks.Single(item => item.Id == id);
            if (task.IsArchived)
                throw new InvalidOperationException("An archived Task must be restored before it can be completed.");
            var instant = timeProvider.GetUtcNow();
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeProvider.LocalTimeZone).DateTime);
            Execute(connection, transaction, "DELETE FROM today_tasks WHERE task_id=$id;", ("$id", id));
            Execute(connection, transaction,
                "UPDATE tasks SET completion_instant=$instant, completion_date=$date WHERE id=$id;",
                ("$id", id), ("$instant", instant.ToString("O", CultureInfo.InvariantCulture)), ("$date", Date(localDate)));
            result = ReadSnapshot(connection, transaction).Tasks.Single(task => task.Id == id);
        }));
        return result!;
    }

    public TaskRecord ReopenTask(string id)
    {
        TaskRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            RequireActiveTask(connection, transaction, id);
            var snapshot = ReadSnapshot(connection, transaction);
            var task = snapshot.Tasks.Single(item => item.Id == id);
            if (task.IsArchived)
                throw new InvalidOperationException("An archived Task must be restored before it can be reopened.");
            Execute(connection, transaction,
                "UPDATE tasks SET completion_instant=NULL, completion_date=NULL WHERE id=$id;", ("$id", id));
            result = ReadSnapshot(connection, transaction).Tasks.Single(task => task.Id == id);
        }));
        return result!;
    }

    public TaskRecord ArchiveTask(string id)
    {
        TaskRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            RequireActiveTask(connection, transaction, id);
            var task = ReadSnapshot(connection, transaction).Tasks.Single(item => item.Id == id);
            if (!task.IsComplete)
                throw new InvalidOperationException("Only a complete Task can be archived.");
            if (task.IsArchived)
                throw new InvalidOperationException("The Task is already archived.");
            var instant = timeProvider.GetUtcNow();
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeProvider.LocalTimeZone).DateTime);
            Execute(connection, transaction, "DELETE FROM today_tasks WHERE task_id=$id;", ("$id", id));
            Execute(connection, transaction,
                "INSERT INTO task_archives (task_id,archived_instant,archive_date) VALUES ($id,$instant,$date);",
                ("$id", id), ("$instant", instant.ToString("O", CultureInfo.InvariantCulture)), ("$date", Date(localDate)));
            result = ReadSnapshot(connection, transaction).Tasks.Single(item => item.Id == id);
        }));
        return result!;
    }

    public TaskRecord RestoreTask(string id)
    {
        TaskRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            RequireActiveTask(connection, transaction, id);
            var task = ReadSnapshot(connection, transaction).Tasks.Single(item => item.Id == id);
            if (!task.IsArchived)
                throw new InvalidOperationException("The Task is not archived.");
            Execute(connection, transaction, "DELETE FROM task_archives WHERE task_id=$id;", ("$id", id));
            result = ReadSnapshot(connection, transaction).Tasks.Single(item => item.Id == id);
        }));
        return result!;
    }

    public TaskBinRecord MoveTaskToBin(string id)
    {
        TaskBinRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            result = MoveTaskToBinCore(connection, transaction, id, timeProvider.GetUtcNow(), null);
        }));
        return result!;
    }

    public TaskRecord RestoreTaskFromBin(string id)
    {
        TaskRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            result = RestoreTaskFromBinCore(connection, transaction, id);
        }));
        return result!;
    }

    public ProjectBinRecord MoveProjectToBin(string id)
    {
        ProjectBinRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            RequireProjectNotBinned(connection, transaction, id);
            var snapshot = ReadSnapshot(connection, transaction);
            var project = snapshot.Projects.Single(item => item.Id == id);
            var projectIds = snapshot.Projects.OrderBy(item => item.Position).Select(item => item.Id).ToList();
            var projectIndex = projectIds.IndexOf(id);
            var removedAt = timeProvider.GetUtcNow();
            Execute(connection, transaction,
                "INSERT INTO project_bins (project_id,removed_instant) VALUES ($id,$removed);",
                ("$id", id), ("$removed", removedAt.ToString("O", CultureInfo.InvariantCulture)));
            WriteProjectBinOrderAnchors(connection, transaction, id, projectIds, projectIndex);

            var childIds = ReadActiveProjectTaskIds(connection, transaction, id).ToArray();
            foreach (var taskId in childIds)
                MoveTaskToBinCore(connection, transaction, taskId, removedAt, id);

            projectIds.Remove(id);
            RewriteProjectOrder(connection, transaction, projectIds);
            result = ReadProjectBinRecord(connection, transaction, id, removedAt);
        }));
        return result!;
    }

    public ProjectRecord RestoreProjectFromBin(string id)
    {
        ProjectRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            Require(connection, transaction, "projects", id);
            if (!Exists(connection, transaction, "project_bins", "project_id", id))
                throw new InvalidOperationException("The Project is not in Bin.");
            var anchors = ReadProjectBinOrderAnchors(connection, transaction, id);
            var aggregateTaskIds = ReadIds(connection, transaction, """
                SELECT pbt.task_id
                FROM project_bin_tasks pbt
                JOIN tasks t ON t.id=pbt.task_id
                WHERE pbt.project_id=$project
                ORDER BY t.shared_position,t.id;
                """, ("$project", id));
            var projectIds = ReadActiveProjectIds(connection, transaction);
            projectIds.Insert(ProjectBinRestorePolicy.RestoreIndex(projectIds, anchors), id);
            Execute(connection, transaction, "DELETE FROM project_bins WHERE project_id=$id;", ("$id", id));
            RewriteProjectOrder(connection, transaction, projectIds);
            RestoreProjectTaskAggregate(connection, transaction, aggregateTaskIds);
            result = ReadSnapshot(connection, transaction).Projects.Single(item => item.Id == id);
        }));
        return result!;
    }

    public ProjectRecord ArchiveProject(string id)
    {
        ProjectRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            Require(connection, transaction, "projects", id);
            var project = ReadSnapshot(connection, transaction).Projects.Single(item => item.Id == id);
            if (project.IsArchived)
                throw new InvalidOperationException("The Project is already archived.");
            var (instant, localDate) = CurrentArchiveMoment();
            Execute(connection, transaction,
                "DELETE FROM today_tasks WHERE task_id IN (SELECT id FROM tasks WHERE project_id=$id);",
                ("$id", id));
            Execute(connection, transaction,
                "INSERT INTO project_archives (project_id,archived_instant,archive_date) VALUES ($id,$instant,$date);",
                ("$id", id), ("$instant", instant.ToString("O", CultureInfo.InvariantCulture)),
                ("$date", Date(localDate)));
            result = ReadSnapshot(connection, transaction).Projects.Single(item => item.Id == id);
        }));
        return result!;
    }

    public ProjectRecord RestoreProject(string id)
    {
        ProjectRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            Require(connection, transaction, "projects", id);
            var project = ReadSnapshot(connection, transaction).Projects.Single(item => item.Id == id);
            if (!project.IsArchived)
                throw new InvalidOperationException("The Project is not archived.");
            Execute(connection, transaction, "DELETE FROM project_archives WHERE project_id=$id;", ("$id", id));
            result = ReadSnapshot(connection, transaction).Projects.Single(item => item.Id == id);
        }));
        return result!;
    }

    public BulkTaskArchivePreview PreviewBulkTaskArchive(int completedAgeDays)
    {
        var threshold = BulkTaskArchiveThreshold.Validate(completedAgeDays);
        return Guard(() => transactions.Read((connection, transaction) =>
        {
            var evaluatedOn = CurrentLocalDate();
            return CreateBulkTaskArchivePreview(ReadSnapshot(connection, transaction), evaluatedOn, threshold);
        }));
    }

    public BulkTaskArchiveResult BulkArchiveTasks(BulkTaskArchivePreview confirmedPreview)
    {
        ArgumentNullException.ThrowIfNull(confirmedPreview);
        var threshold = BulkTaskArchiveThreshold.Validate(confirmedPreview.CompletedAgeDays);
        BulkTaskArchiveResult? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            var snapshot = ReadSnapshot(connection, transaction);
            var currentPreview = CreateBulkTaskArchivePreview(snapshot, CurrentLocalDate(), threshold);
            if (!confirmedPreview.Matches(currentPreview))
            {
                result = new(false, currentPreview, 0);
                return;
            }
            var eligible = snapshot.Tasks
                .Where(task => currentPreview.EligibleTaskIds.Contains(task.Id, StringComparer.Ordinal))
                .ToArray();
            if (eligible.Length == 0)
            {
                result = new(true, confirmedPreview, 0);
                return;
            }
            var (instant, localDate) = CurrentArchiveMoment();
            foreach (var task in eligible)
            {
                Execute(connection, transaction,
                    "INSERT INTO task_archives (task_id,archived_instant,archive_date) VALUES ($id,$instant,$date);",
                    ("$id", task.Id), ("$instant", instant.ToString("O", CultureInfo.InvariantCulture)),
                    ("$date", Date(localDate)));
            }
            result = new(true, confirmedPreview, eligible.Length);
        }));
        return result!;
    }

    public TaskRecord SetTaskTodayLane(string id, TodayLane? lane)
    {
        if (lane is not null && !Enum.IsDefined(lane.Value)) throw new ArgumentOutOfRangeException(nameof(lane));
        TaskRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            RequireActiveTask(connection, transaction, id);
            var snapshot = ReadSnapshot(connection, transaction);
            var task = snapshot.Tasks.Single(item => item.Id == id);
            if (task.IsComplete && lane is not null)
                throw new InvalidOperationException("A completed Task cannot belong to Today.");
            if (lane is not null && task.ProjectId is { } projectId
                && snapshot.Projects.Single(project => project.Id == projectId).IsArchived)
                throw new InvalidOperationException("A Task in an archived Project cannot belong to Today.");
            if (lane is null)
            {
                Execute(connection, transaction, "DELETE FROM today_tasks WHERE task_id=$id;", ("$id", id));
            }
            else
            {
                Execute(connection, transaction, """
                    INSERT INTO today_tasks (task_id,lane) VALUES ($id,$lane)
                    ON CONFLICT(task_id) DO UPDATE SET lane=excluded.lane;
                    """, ("$id", id), ("$lane", TodayLaneValue(lane.Value)));
            }
            result = ReadSnapshot(connection, transaction).Tasks.Single(item => item.Id == id);
        }));
        return result!;
    }

    public int ClearToday()
    {
        var cleared = 0;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM today_tasks;";
            cleared = command.ExecuteNonQuery();
        }));
        return cleared;
    }

    public TodayLaneOrderChange MoveTaskInTodayLane(string id, int targetPosition)
    {
        TodayLaneOrderChange? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            RequireActiveTask(connection, transaction, id);
            var orderedTasks = ReadSnapshot(connection, transaction).Tasks.OrderBy(task => task.SharedPosition).ToArray();
            var task = orderedTasks.Single(item => item.Id == id);
            if (task.IsComplete || task.TodayLane is null)
                throw new ArgumentException("The Task does not belong to an incomplete Today lane.", nameof(id));
            var visibleIds = orderedTasks
                .Where(item => !item.IsComplete && item.TodayLane == task.TodayLane)
                .Select(item => item.Id)
                .ToList();
            Move(visibleIds, id, targetPosition);
            var visibleSet = visibleIds.ToHashSet(StringComparer.Ordinal);
            var visibleIndex = 0;
            var mergedIds = orderedTasks
                .Select(item => visibleSet.Contains(item.Id) ? visibleIds[visibleIndex++] : item.Id)
                .ToList();
            RewriteSharedOrder(connection, transaction, mergedIds);
            result = new(id, task.TodayLane.Value, targetPosition + 1, visibleIds.Count);
        }));
        return result!;
    }

    public SharedTaskOrderChange MoveTaskInSharedOrder(string id, int targetPosition)
    {
        SharedTaskOrderChange? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            RequireActiveTask(connection, transaction, id);
            var orderedTasks = ReadSnapshot(connection, transaction).Tasks
                .OrderBy(task => task.SharedPosition)
                .ToArray();
            var snapshot = ReadSnapshot(connection, transaction);
            var archivedProjectIds = snapshot.Projects.Where(project => project.IsArchived)
                .Select(project => project.Id).ToHashSet(StringComparer.Ordinal);
            var visibleIds = orderedTasks.Where(task => !task.IsComplete && !task.IsArchived
                    && (task.ProjectId is null || !archivedProjectIds.Contains(task.ProjectId)))
                .Select(task => task.Id).ToList();
            if ((uint)targetPosition >= (uint)visibleIds.Count)
                throw new ArgumentOutOfRangeException(nameof(targetPosition));
            var currentPosition = visibleIds.IndexOf(id);
            if (currentPosition < 0) throw new ArgumentException("The Task does not exist.", nameof(id));
            if (currentPosition != targetPosition)
            {
                visibleIds.RemoveAt(currentPosition);
                visibleIds.Insert(targetPosition, id);
                var visibleSet = visibleIds.ToHashSet(StringComparer.Ordinal);
                var visibleIndex = 0;
                var mergedIds = orderedTasks
                    .Select(task => visibleSet.Contains(task.Id) ? visibleIds[visibleIndex++] : task.Id)
                    .ToList();
                RewriteSharedOrder(connection, transaction, mergedIds);
            }
            result = new(id, targetPosition + 1, visibleIds.Count);
        }));
        return result!;
    }

    public ProjectOrderChange MoveProject(string id, int targetPosition)
    {
        ProjectOrderChange? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            Require(connection, transaction, "projects", id);
            var orderedProjects = ReadSnapshot(connection, transaction).Projects.OrderBy(project => project.Position).ToArray();
            var visibleIds = orderedProjects.Where(project => !project.IsArchived).Select(project => project.Id).ToList();
            Move(visibleIds, id, targetPosition);
            var visibleSet = visibleIds.ToHashSet(StringComparer.Ordinal);
            var visibleIndex = 0;
            var mergedIds = orderedProjects
                .Select(project => visibleSet.Contains(project.Id) ? visibleIds[visibleIndex++] : project.Id)
                .ToList();
            RewriteOrder(connection, transaction, "projects", "position", mergedIds);
            result = new(id, targetPosition + 1, visibleIds.Count);
        }));
        return result!;
    }

    public ProjectTaskOrderChange MoveTaskInProject(string projectId, string taskId, int targetPosition)
    {
        ProjectTaskOrderChange? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            Require(connection, transaction, "projects", projectId);
            var orderedIds = ReadIds(connection, transaction,
                "SELECT id FROM tasks WHERE project_id=$project AND id NOT IN (SELECT task_id FROM task_bins) ORDER BY project_position,id;", ("$project", projectId));
            Move(orderedIds, taskId, targetPosition);
            RewriteProjectTaskOrder(connection, transaction, projectId, orderedIds);
            result = new(projectId, taskId, targetPosition + 1, orderedIds.Count);
        }));
        return result!;
    }

    public TaskRecord DetachTask(string id)
    {
        TaskRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            RequireActiveTask(connection, transaction, id);
            var snapshot = ReadSnapshot(connection, transaction);
            var task = snapshot.Tasks.Single(item => item.Id == id);
            if (task.ProjectId is null) throw new ArgumentException("The Task is already standalone.", nameof(id));
            var effectiveCategoryId = EffectiveCategoryId(snapshot, task);
            var sourceProjectId = task.ProjectId;
            Execute(connection, transaction,
                "UPDATE tasks SET project_id=NULL, project_position=NULL, explicit_category_id=$category WHERE id=$id;",
                ("$category", effectiveCategoryId), ("$id", id));
            var sourceIds = ReadIds(connection, transaction,
                "SELECT id FROM tasks WHERE project_id=$project AND id NOT IN (SELECT task_id FROM task_bins) ORDER BY project_position,id;", ("$project", sourceProjectId));
            RewriteProjectTaskOrder(connection, transaction, sourceProjectId, sourceIds);
            result = ReadSnapshot(connection, transaction).Tasks.Single(item => item.Id == id);
        }));
        return result!;
    }

    public TaskRecord AttachTask(string id, string projectId, TaskAttachmentCategoryChoice? categoryChoice = null)
    {
        TaskRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            RequireActiveTask(connection, transaction, id);
            RequireActiveProject(connection, transaction, projectId);
            var snapshot = ReadSnapshot(connection, transaction);
            var task = snapshot.Tasks.Single(item => item.Id == id);
            if (string.Equals(task.ProjectId, projectId, StringComparison.Ordinal))
                throw new ArgumentException("The Task already belongs to this Project.", nameof(projectId));
            var effectiveCategoryId = EffectiveCategoryId(snapshot, task);
            var targetCategoryId = snapshot.Projects.Single(project => project.Id == projectId).CategoryId;
            var categoriesMatch = string.Equals(effectiveCategoryId, targetCategoryId, StringComparison.Ordinal);
            if (!categoriesMatch && categoryChoice is null)
                throw new ArgumentException("Choose whether to preserve or adopt the Project Category.", nameof(categoryChoice));
            if (categoryChoice is not null && !Enum.IsDefined(categoryChoice.Value))
                throw new ArgumentOutOfRangeException(nameof(categoryChoice));

            var sourceProjectId = task.ProjectId;
            Execute(connection, transaction,
                "UPDATE tasks SET project_id=NULL, project_position=NULL, explicit_category_id=$category WHERE id=$id;",
                ("$category", effectiveCategoryId), ("$id", id));
            if (sourceProjectId is not null)
            {
                var sourceIds = ReadIds(connection, transaction,
                    "SELECT id FROM tasks WHERE project_id=$project AND id NOT IN (SELECT task_id FROM task_bins) ORDER BY project_position,id;", ("$project", sourceProjectId));
                RewriteProjectTaskOrder(connection, transaction, sourceProjectId, sourceIds);
            }

            using var destinationPositionCommand = connection.CreateCommand();
            destinationPositionCommand.Transaction = transaction;
            destinationPositionCommand.CommandText = "SELECT COALESCE(MAX(project_position), -1) + 1 FROM tasks WHERE project_id=$project;";
            destinationPositionCommand.Parameters.AddWithValue("$project", projectId);
            var destinationPosition = (long)destinationPositionCommand.ExecuteScalar()!;
            var explicitCategoryId = categoriesMatch || categoryChoice == TaskAttachmentCategoryChoice.AdoptProjectCategory
                ? null
                : effectiveCategoryId;
            Execute(connection, transaction,
                "UPDATE tasks SET project_id=$project, project_position=$position, explicit_category_id=$category WHERE id=$id;",
                ("$project", projectId), ("$position", destinationPosition), ("$category", explicitCategoryId), ("$id", id));
            result = ReadSnapshot(connection, transaction).Tasks.Single(item => item.Id == id);
        }));
        return result!;
    }

    private static WorkspaceWorkSnapshot ReadSnapshot(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        var hasTaskBins = SchemaObjectExists(connection, transaction, "table", "task_bins");
        var hasProjectBins = SchemaObjectExists(connection, transaction, "table", "project_bins");
        var categories = new List<WorkspaceCategory>();
        var projects = new List<ProjectRecord>();
        var tasks = new List<TaskRecord>();
        var participants = new List<ParticipantRecord>();
        var participantIdsByTask = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var todayLaneByTask = new Dictionary<string, TodayLane>(StringComparer.Ordinal);
        var archiveByTask = new Dictionary<string, (DateTimeOffset Instant, DateOnly Date)>(StringComparer.Ordinal);
        var archiveByProject = new Dictionary<string, (DateTimeOffset Instant, DateOnly Date)>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,name,position FROM categories ORDER BY position,id;";
        using (var reader = command.ExecuteReader())
            while (reader.Read()) categories.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        command.CommandText = "SELECT project_id,archived_instant,archive_date FROM project_archives;";
        using (var reader = command.ExecuteReader())
            while (reader.Read()) archiveByProject.Add(reader.GetString(0), (ReadInstant(reader, 1)!.Value, ReadDate(reader, 2)!.Value));
        command.CommandText = hasProjectBins ? """
            SELECT id,title,description,category_id,target_date,position
            FROM projects
            WHERE id NOT IN (SELECT project_id FROM project_bins)
            ORDER BY position,id;
            """ : "SELECT id,title,description,category_id,target_date,position FROM projects ORDER BY position,id;";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var projectId = reader.GetString(0);
                var hasArchive = archiveByProject.TryGetValue(projectId, out var archive);
                projects.Add(new(projectId, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    ReadDate(reader, 4), reader.GetInt64(5),
                    hasArchive ? archive.Instant : null, hasArchive ? archive.Date : null));
            }
        command.CommandText = "SELECT id,label FROM participants ORDER BY label COLLATE NOCASE,id;";
        using (var reader = command.ExecuteReader())
            while (reader.Read()) participants.Add(new(reader.GetString(0), reader.GetString(1)));
        command.CommandText = "SELECT task_id,participant_id FROM task_participants ORDER BY task_id,position;";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var taskId = reader.GetString(0);
                if (!participantIdsByTask.TryGetValue(taskId, out var ids))
                {
                    ids = [];
                    participantIdsByTask.Add(taskId, ids);
                }
                ids.Add(reader.GetString(1));
            }
        command.CommandText = "SELECT task_id,lane FROM today_tasks;";
        using (var reader = command.ExecuteReader())
            while (reader.Read()) todayLaneByTask.Add(reader.GetString(0), ParseTodayLane(reader.GetString(1)));
        command.CommandText = "SELECT task_id,archived_instant,archive_date FROM task_archives;";
        using (var reader = command.ExecuteReader())
            while (reader.Read()) archiveByTask.Add(reader.GetString(0), (ReadInstant(reader, 1)!.Value, ReadDate(reader, 2)!.Value));
        command.CommandText = hasTaskBins ? """
            SELECT id,project_id,title,description,explicit_category_id,due_date,shared_position,project_position,
                   completion_instant,completion_date
            FROM tasks
            WHERE id NOT IN (SELECT task_id FROM task_bins)
            ORDER BY shared_position,id;
            """ : "SELECT id,project_id,title,description,explicit_category_id,due_date,shared_position,project_position,completion_instant,completion_date FROM tasks ORDER BY shared_position,id;";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var taskId = reader.GetString(0);
                TodayLane? todayLane = todayLaneByTask.TryGetValue(taskId, out var lane) ? lane : null;
                var hasArchive = archiveByTask.TryGetValue(taskId, out var archive);
                tasks.Add(new(taskId, reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), ReadDate(reader, 5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetInt64(7), ReadInstant(reader, 8), ReadDate(reader, 9), participantIdsByTask.GetValueOrDefault(taskId)?.AsReadOnly() ?? [], todayLane, hasArchive ? archive.Instant : null, hasArchive ? archive.Date : null));
            }
        if (tasks.Any(task => (task.ArchivedAt is null) != (task.ArchiveDate is null)
                || task.IsArchived && !task.IsComplete))
            throw new InvalidDataException();
        if (projects.Any(project => (project.ArchivedAt is null) != (project.ArchiveDate is null)))
            throw new InvalidDataException();
        var archivedProjectIds = projects.Where(project => project.IsArchived)
            .Select(project => project.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (tasks.Any(task => task.TodayLane is not null && task.ProjectId is { } projectId
                && archivedProjectIds.Contains(projectId)))
            throw new InvalidDataException();
        return new(categories.AsReadOnly(), projects.AsReadOnly(), tasks.AsReadOnly(), participants.AsReadOnly());
    }

    private static TaskRecord ReadStoredTask(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string id,
        TodayLane? todayLane)
    {
        var participants = new List<string>();
        using (var participantCommand = connection.CreateCommand())
        {
            participantCommand.Transaction = transaction;
            participantCommand.CommandText = """
                SELECT participant_id FROM task_participants
                WHERE task_id=$id ORDER BY position;
                """;
            participantCommand.Parameters.AddWithValue("$id", id);
            using var participantReader = participantCommand.ExecuteReader();
            while (participantReader.Read()) participants.Add(participantReader.GetString(0));
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT t.id,t.project_id,t.title,t.description,t.explicit_category_id,t.due_date,
                   t.shared_position,t.project_position,t.completion_instant,t.completion_date,
                   a.archived_instant,a.archive_date
            FROM tasks t
            LEFT JOIN task_archives a ON a.task_id=t.id
            WHERE t.id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException();
        return new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), ReadDate(reader, 5),
            reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetInt64(7), ReadInstant(reader, 8),
            ReadDate(reader, 9), participants.AsReadOnly(), todayLane, ReadInstant(reader, 10), ReadDate(reader, 11));
    }

    private static TaskBinRecord ReadTaskBinRecord(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string id,
        DateTimeOffset removedAt,
        TodayLane? todayLane)
    {
        var task = ReadStoredTask(connection, transaction, id, todayLane);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.title,c.name,CASE WHEN pb.project_id IS NULL THEN 0 ELSE 1 END
            FROM tasks t
            LEFT JOIN projects p ON p.id=t.project_id
            JOIN categories c ON c.id=COALESCE(t.explicit_category_id,p.category_id)
            LEFT JOIN project_bins pb ON pb.project_id=t.project_id
            WHERE t.id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException();
        var parentIsBinned = reader.GetInt64(2) != 0;
        return new(task, removedAt, reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetString(1),
            !parentIsBinned,
            parentIsBinned ? "Restore the parent Project from Bin before restoring this Task." : null);
    }

    private static TaskBinRecord MoveTaskToBinCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        DateTimeOffset removedAt,
        string? aggregateProjectId)
    {
        RequireActiveTask(connection, transaction, id);
        var task = ReadSnapshot(connection, transaction).Tasks.Single(item => item.Id == id);
        var sharedIds = ReadTaskIdsInSharedOrder(connection, transaction);
        var sharedIndex = sharedIds.IndexOf(id);
        var projectIds = task.ProjectId is null
            ? []
            : ReadActiveProjectTaskIds(connection, transaction, task.ProjectId);
        var projectIndex = projectIds.IndexOf(id);
        Execute(connection, transaction, """
            INSERT INTO task_bins (task_id,removed_instant,today_lane)
            VALUES ($id,$removed,$lane);
            """,
            ("$id", id), ("$removed", removedAt.ToString("O", CultureInfo.InvariantCulture)),
            ("$lane", task.TodayLane is null ? null : TodayLaneValue(task.TodayLane.Value)));
        if (aggregateProjectId is not null)
            Execute(connection, transaction,
                "INSERT INTO project_bin_tasks (project_id,task_id) VALUES ($project,$task);",
                ("$project", aggregateProjectId), ("$task", id));
        WriteTaskBinOrderAnchors(connection, transaction, id, "shared", sharedIds, sharedIndex);
        if (task.ProjectId is not null)
            WriteTaskBinOrderAnchors(connection, transaction, id, "project", projectIds, projectIndex);
        Execute(connection, transaction, "DELETE FROM today_tasks WHERE task_id=$id;", ("$id", id));
        sharedIds.Remove(id);
        RewriteSharedOrder(connection, transaction, sharedIds);
        if (task.ProjectId is not null)
        {
            projectIds.Remove(id);
            RewriteProjectTaskOrder(connection, transaction, task.ProjectId, projectIds);
        }
        return ReadTaskBinRecord(connection, transaction, id, removedAt, task.TodayLane);
    }

    private static TaskRecord RestoreTaskFromBinCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id)
    {
        Require(connection, transaction, "tasks", id);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT today_lane FROM task_bins WHERE task_id=$id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("The Task is not in Bin.");
        TodayLane? todayLane = reader.IsDBNull(0) ? null : ParseTodayLane(reader.GetString(0));
        reader.Close();
        var sharedAnchors = ReadTaskBinOrderAnchors(connection, transaction, id, "shared");
        var projectAnchors = ReadTaskBinOrderAnchors(connection, transaction, id, "project");
        var stored = ReadStoredTask(connection, transaction, id, todayLane);
        var parentIsBinned = stored.ProjectId is not null
            && Exists(connection, transaction, "project_bins", "project_id", stored.ProjectId);
        TaskBinRestorePolicy.EnsureParentAllowsRestore(parentIsBinned);
        var sharedIds = ReadTaskIdsInSharedOrder(connection, transaction);
        sharedIds.Insert(TaskBinRestorePolicy.RestoreIndex(sharedIds, sharedAnchors), id);
        List<string>? projectIds = null;
        if (stored.ProjectId is not null)
        {
            projectIds = ReadActiveProjectTaskIds(connection, transaction, stored.ProjectId);
            projectIds.Insert(TaskBinRestorePolicy.RestoreIndex(projectIds, projectAnchors), id);
        }
        Execute(connection, transaction, "DELETE FROM task_bins WHERE task_id=$id;", ("$id", id));
        RewriteSharedOrder(connection, transaction, sharedIds);
        if (stored.ProjectId is not null)
            RewriteProjectTaskOrder(connection, transaction, stored.ProjectId, projectIds!);
        if (todayLane is not null && !stored.IsComplete
            && (stored.ProjectId is null
                || !Exists(connection, transaction, "project_archives", "project_id", stored.ProjectId)))
            Execute(connection, transaction, "INSERT INTO today_tasks (task_id,lane) VALUES ($id,$lane);",
                ("$id", id), ("$lane", TodayLaneValue(todayLane.Value)));
        return ReadSnapshot(connection, transaction).Tasks.Single(item => item.Id == id);
    }

    private static void RestoreProjectTaskAggregate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> aggregateTaskIds)
    {
        var remaining = new HashSet<string>(aggregateTaskIds, StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var next = aggregateTaskIds.FirstOrDefault(taskId => remaining.Contains(taskId)
                && !ReadTaskBinOrderAnchors(connection, transaction, taskId, "shared")
                    .Concat(ReadTaskBinOrderAnchors(connection, transaction, taskId, "project"))
                    .Any(anchor => remaining.Contains(anchor.TaskId)));
            next ??= remaining.Order(StringComparer.Ordinal).First();
            RestoreTaskFromBinCore(connection, transaction, next);
            remaining.Remove(next);
        }
    }

    private static ProjectBinRecord ReadProjectBinRecord(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        DateTimeOffset removedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.title,p.description,p.category_id,p.target_date,p.position,
                   pa.archived_instant,pa.archive_date,c.name,
                   (SELECT COUNT(*) FROM tasks t WHERE t.project_id=p.id)
            FROM projects p
            JOIN categories c ON c.id=p.category_id
            LEFT JOIN project_archives pa ON pa.project_id=p.id
            WHERE p.id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException();
        var project = new ProjectRecord(id, reader.GetString(0), reader.GetString(1), reader.GetString(2),
            ReadDate(reader, 3), reader.GetInt64(4), ReadInstant(reader, 5), ReadDate(reader, 6));
        return new(project, removedAt, reader.GetString(7), reader.GetInt32(8));
    }

    private static EmptyBinPreview ReadEmptyBinPreview(
        SqliteConnection connection,
        SqliteTransaction? transaction) => new(
        ReadIds(connection, transaction, "SELECT project_id FROM project_bins ORDER BY project_id;"),
        ReadIds(connection, transaction, "SELECT task_id FROM task_bins ORDER BY task_id;"));

    private static void WriteTaskBinOrderAnchors(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        string scope,
        List<string> orderedIds,
        int taskIndex)
    {
        for (var index = 0; index < orderedIds.Count; index++)
        {
            if (index == taskIndex) continue;
            Execute(connection, transaction, """
                INSERT INTO task_bin_order_anchors (task_id,scope,anchor_task_id,relative_position)
                VALUES ($task,$scope,$anchor,$relativePosition);
                """, ("$task", taskId), ("$scope", scope), ("$anchor", orderedIds[index]),
                ("$relativePosition", index - taskIndex));
        }
    }

    private static List<TaskBinOrderAnchor> ReadTaskBinOrderAnchors(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        string scope)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT anchor_task_id,relative_position
            FROM task_bin_order_anchors
            WHERE task_id=$task AND scope=$scope
            ORDER BY ABS(relative_position),relative_position;
            """;
        command.Parameters.AddWithValue("$task", taskId);
        command.Parameters.AddWithValue("$scope", scope);
        using var reader = command.ExecuteReader();
        var anchors = new List<TaskBinOrderAnchor>();
        while (reader.Read()) anchors.Add(new(reader.GetString(0), reader.GetInt32(1)));
        return anchors;
    }

    private static void WriteProjectBinOrderAnchors(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId,
        List<string> orderedIds,
        int projectIndex)
    {
        for (var index = 0; index < orderedIds.Count; index++)
        {
            if (index == projectIndex) continue;
            Execute(connection, transaction, """
                INSERT INTO project_bin_order_anchors (project_id,anchor_project_id,relative_position)
                VALUES ($project,$anchor,$relativePosition);
                """, ("$project", projectId), ("$anchor", orderedIds[index]),
                ("$relativePosition", index - projectIndex));
        }
    }

    private static List<TaskBinOrderAnchor> ReadProjectBinOrderAnchors(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT anchor_project_id,relative_position
            FROM project_bin_order_anchors
            WHERE project_id=$project
            ORDER BY ABS(relative_position),relative_position;
            """;
        command.Parameters.AddWithValue("$project", projectId);
        using var reader = command.ExecuteReader();
        var anchors = new List<TaskBinOrderAnchor>();
        while (reader.Read()) anchors.Add(new(reader.GetString(0), reader.GetInt32(1)));
        return anchors;
    }

    private static void ShiftSharedOrderForNewTask(SqliteConnection connection, SqliteTransaction transaction)
    {
        var orderedIds = ReadTaskIdsInSharedOrder(connection, transaction);
        RewriteSharedOrder(connection, transaction, orderedIds, 1);
    }

    private static List<string> ReadTaskIdsInSharedOrder(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id FROM tasks
            WHERE id NOT IN (SELECT task_id FROM task_bins)
            ORDER BY shared_position,id;
            """;
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    private static void RewriteSharedOrder(
        SqliteConnection connection,
        SqliteTransaction transaction,
        List<string> orderedIds,
        int startingPosition = 0)
    {
        var binnedIds = ReadIds(connection, transaction,
            "SELECT task_id FROM task_bins ORDER BY removed_instant,task_id;");
        var allIds = orderedIds.Concat(binnedIds.Where(id => !orderedIds.Contains(id, StringComparer.Ordinal))).ToArray();
        var offset = allIds.Length + startingPosition + 1;
        Execute(connection, transaction, "UPDATE tasks SET shared_position = shared_position + $offset;", ("$offset", offset));
        for (var position = 0; position < allIds.Length; position++)
            Execute(connection, transaction, "UPDATE tasks SET shared_position=$position WHERE id=$id;",
                ("$position", position + startingPosition), ("$id", allIds[position]));
    }

    private static List<string> ReadIds(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    private static void Move(List<string> orderedIds, string id, int targetPosition)
    {
        if ((uint)targetPosition >= (uint)orderedIds.Count) throw new ArgumentOutOfRangeException(nameof(targetPosition));
        var currentPosition = orderedIds.IndexOf(id);
        if (currentPosition < 0) throw new ArgumentException("The item does not belong to this order.", nameof(id));
        if (currentPosition == targetPosition) return;
        orderedIds.RemoveAt(currentPosition);
        orderedIds.Insert(targetPosition, id);
    }

    private static void RewriteOrder(SqliteConnection connection, SqliteTransaction transaction, string table, string column, List<string> orderedIds)
    {
        using var offsetCommand = connection.CreateCommand();
        offsetCommand.Transaction = transaction;
        offsetCommand.CommandText = $"SELECT COALESCE(MAX({column}), -1) + $count + 1 FROM {table};";
        offsetCommand.Parameters.AddWithValue("$count", orderedIds.Count);
        var offset = (long)offsetCommand.ExecuteScalar()!;
        Execute(connection, transaction, $"UPDATE {table} SET {column} = {column} + $offset;", ("$offset", offset));
        for (var position = 0; position < orderedIds.Count; position++)
            Execute(connection, transaction, $"UPDATE {table} SET {column}=$position WHERE id=$id;", ("$position", position), ("$id", orderedIds[position]));
    }

    private static List<string> ReadActiveProjectIds(
        SqliteConnection connection,
        SqliteTransaction transaction) => ReadIds(connection, transaction, """
            SELECT id FROM projects
            WHERE id NOT IN (SELECT project_id FROM project_bins)
            ORDER BY position,id;
            """);

    private static void RewriteProjectOrder(
        SqliteConnection connection,
        SqliteTransaction transaction,
        List<string> orderedIds)
    {
        var binnedIds = ReadIds(connection, transaction,
            "SELECT project_id FROM project_bins ORDER BY removed_instant,project_id;");
        var allIds = orderedIds.Concat(binnedIds.Where(id => !orderedIds.Contains(id, StringComparer.Ordinal))).ToList();
        RewriteOrder(connection, transaction, "projects", "position", allIds);
    }

    private static void RewriteProjectTaskOrder(SqliteConnection connection, SqliteTransaction transaction, string projectId, List<string> orderedIds)
    {
        var binnedIds = ReadIds(connection, transaction, """
            SELECT t.id FROM tasks t
            JOIN task_bins b ON b.task_id=t.id
            WHERE t.project_id=$project
            ORDER BY b.removed_instant,b.task_id;
            """, ("$project", projectId));
        var allIds = orderedIds.Concat(binnedIds.Where(id => !orderedIds.Contains(id, StringComparer.Ordinal))).ToArray();
        using var offsetCommand = connection.CreateCommand();
        offsetCommand.Transaction = transaction;
        offsetCommand.CommandText = "SELECT COALESCE(MAX(project_position), -1) + $count + 1 FROM tasks WHERE project_id=$project;";
        offsetCommand.Parameters.AddWithValue("$count", allIds.Length);
        offsetCommand.Parameters.AddWithValue("$project", projectId);
        var offset = (long)offsetCommand.ExecuteScalar()!;
        Execute(connection, transaction,
            "UPDATE tasks SET project_position = project_position + $offset WHERE project_id=$project;",
            ("$offset", offset), ("$project", projectId));
        for (var position = 0; position < allIds.Length; position++)
            Execute(connection, transaction, "UPDATE tasks SET project_position=$position WHERE id=$id;", ("$position", position), ("$id", allIds[position]));
    }

    private static List<string> ReadActiveProjectTaskIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId) => ReadIds(connection, transaction, """
            SELECT id FROM tasks
            WHERE project_id=$project AND id NOT IN (SELECT task_id FROM task_bins)
            ORDER BY project_position,id;
            """, ("$project", projectId));

    private static string EffectiveCategoryId(WorkspaceWorkSnapshot snapshot, TaskRecord task) =>
        task.ExplicitCategoryId
        ?? snapshot.Projects.Single(project => project.Id == task.ProjectId).CategoryId;

    private static string NormalizeCategoryName(string name)
    {
        var validation = CategoryName.Create(name);
        if (!validation.IsValid) throw new ArgumentException("A Category name is required.", nameof(name));
        return validation.CategoryName!.Value;
    }

    private static void EnsureCategoryNameAvailable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string name,
        string? exceptId = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, name FROM categories;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!string.Equals(reader.GetString(0), exceptId, StringComparison.Ordinal)
                && StringComparer.OrdinalIgnoreCase.Equals(reader.GetString(1), name))
                throw new ArgumentException("Category names must be unique case-insensitively.", nameof(name));
        }
    }

    private void ApplyParticipantChanges(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        ParticipantDraftChange change)
    {
        var associations = change.ParticipantIds.Distinct(StringComparer.Ordinal).ToList();
        foreach (var participantId in associations) Require(connection, transaction, "participants", participantId);
        foreach (var newLabel in change.NewParticipantLabels)
        {
            var label = ParticipantLabel.Normalize(newLabel);
            EnsureParticipantLabelAvailable(connection, transaction, label);
            var participant = new ParticipantRecord(store.GetIdentifier(), label);
            Execute(connection, transaction,
                "INSERT INTO participants (id,label,comparison_key) VALUES ($id,$label,$key);",
                ("$id", participant.Id), ("$label", participant.Label),
                ("$key", ParticipantLabel.ComparisonKey(participant.Label)));
            associations.Add(participant.Id);
        }

        Execute(connection, transaction, "DELETE FROM task_participants WHERE task_id=$task;", ("$task", taskId));
        for (var position = 0; position < associations.Count; position++)
            Execute(connection, transaction,
                "INSERT INTO task_participants (task_id,participant_id,position) VALUES ($task,$participant,$position);",
                ("$task", taskId), ("$participant", associations[position]), ("$position", position));

    }

    internal static void RebuildArchiveSearchIndex(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        var snapshot = ReadSnapshot(connection, transaction);
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM archive_search;";
            clear.ExecuteNonQuery();
        }

        var categories = snapshot.Categories.ToDictionary(category => category.Id, StringComparer.Ordinal);
        var projects = snapshot.Projects.ToDictionary(project => project.Id, StringComparer.Ordinal);
        var participants = snapshot.Participants.ToDictionary(participant => participant.Id, StringComparer.Ordinal);

        foreach (var project in snapshot.Projects.Where(project => project.IsArchived))
        {
            InsertArchiveSearchDocument(
                connection,
                transaction,
                "project",
                project.Id,
                project.Title,
                SearchableMarkdownText(project.Description),
                categories[project.CategoryId].Name,
                string.Empty,
                string.Empty,
                project.ArchivedAt!.Value.UtcTicks,
                project.ArchiveDate!.Value,
                "archived");
        }

        foreach (var task in snapshot.Tasks.Where(task => task.IsArchived))
        {
            var parent = task.ProjectId is null ? null : projects[task.ProjectId];
            var categoryId = task.ExplicitCategoryId ?? parent!.CategoryId;
            InsertArchiveSearchDocument(
                connection,
                transaction,
                "task",
                task.Id,
                task.Title,
                SearchableMarkdownText(task.Description),
                categories[categoryId].Name,
                string.Join(' ', task.Participants.Select(id => participants[id].Label)),
                parent?.Title ?? string.Empty,
                task.ArchivedAt!.Value.UtcTicks,
                task.CompletionDate!.Value,
                "completed");
        }
    }

    private static void InsertArchiveSearchDocument(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string recordType,
        string recordId,
        string title,
        string description,
        string category,
        string participants,
        string parentProject,
        long archiveTicks,
        DateOnly displayDate,
        string dateKind)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO archive_search
                (record_type, record_id, title, description, category, participants,
                 parent_project, archive_ticks, display_date, date_kind)
            VALUES
                ($recordType, $recordId, $title, $description, $category, $participants,
                 $parentProject, $archiveTicks, $displayDate, $dateKind);
            """;
        command.Parameters.AddWithValue("$recordType", recordType);
        command.Parameters.AddWithValue("$recordId", recordId);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$participants", participants);
        command.Parameters.AddWithValue("$parentProject", parentProject);
        command.Parameters.AddWithValue("$archiveTicks", archiveTicks);
        command.Parameters.AddWithValue("$displayDate", Date(displayDate));
        command.Parameters.AddWithValue("$dateKind", dateKind);
        command.ExecuteNonQuery();
    }

    private static string SearchableMarkdownText(string markdown)
    {
        var plainText = SanitisedMarkdownRenderer.Render(markdown).ToPlainText();
        return string.Join(' ', plainText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string BuildArchiveMatchQuery(string query)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();
        foreach (var rune in query.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (Rune.IsLetterOrDigit(rune)
                || category is UnicodeCategory.NonSpacingMark
                    or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.EnclosingMark)
            {
                token.Append(rune.ToString());
            }
            else if (token.Length > 0)
            {
                tokens.Add(token.ToString());
                token.Clear();
            }
        }
        if (token.Length > 0) tokens.Add(token.ToString());
        return string.Join(" AND ", tokens
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(value => $"\"{value}\"*"));
    }

    private static string? Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private DateOnly CurrentLocalDate() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), timeProvider.LocalTimeZone).DateTime);

    private static BulkTaskArchivePreview CreateBulkTaskArchivePreview(
        WorkspaceWorkSnapshot snapshot,
        DateOnly evaluatedOn,
        int completedAgeDays) =>
        new(completedAgeDays, evaluatedOn,
            BulkTaskArchivePolicy.EligibleTasks(snapshot, evaluatedOn, completedAgeDays)
                .Select(task => task.Id)
                .ToArray());

    private (DateTimeOffset Instant, DateOnly LocalDate) CurrentArchiveMoment()
    {
        var instant = timeProvider.GetUtcNow();
        return (instant, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeProvider.LocalTimeZone).DateTime));
    }

    private static void EnsureParticipantLabelAvailable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string label,
        string? exceptId = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM participants WHERE comparison_key=$key;";
        command.Parameters.AddWithValue("$key", ParticipantLabel.ComparisonKey(label));
        var existingId = command.ExecuteScalar() as string;
        if (existingId is not null && !string.Equals(existingId, exceptId, StringComparison.Ordinal))
            throw new ArgumentException("Participant labels must be unique after normalization.", nameof(label));
    }

    private static void RequireActiveProject(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId)
    {
        Require(connection, transaction, "projects", projectId);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM project_archives WHERE project_id=$id;";
        command.Parameters.AddWithValue("$id", projectId);
        if ((long)command.ExecuteScalar()! != 0)
            throw new InvalidOperationException("Restore the Project before adding or moving Tasks into it.");
        if (Exists(connection, transaction, "project_bins", "project_id", projectId))
            throw new InvalidOperationException("Restore the Project from Bin before adding or moving Tasks into it.");
    }

    private static void RequireProjectNotBinned(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string projectId)
    {
        Require(connection, transaction, "projects", projectId);
        if (Exists(connection, transaction, "project_bins", "project_id", projectId))
            throw new InvalidOperationException("The Project is already in Bin.");
    }

    private static void RequireActiveTask(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId)
    {
        Require(connection, transaction, "tasks", taskId);
        if (Exists(connection, transaction, "task_bins", "task_id", taskId))
            throw new InvalidOperationException("Restore the Task from Bin before changing it.");
    }

    private static bool Exists(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        string idColumn,
        string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {idColumn}=$id;";
        command.Parameters.AddWithValue("$id", id);
        return (long)command.ExecuteScalar()! != 0;
    }

    private static bool SchemaObjectExists(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string type,
        string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type=$type AND name=$name;";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return (long)command.ExecuteScalar()! == 1;
    }
    private static DateOnly? ReadDate(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null : DateOnly.ParseExact(reader.GetString(ordinal), "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateTimeOffset? ReadInstant(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null : DateTimeOffset.ParseExact(reader.GetString(ordinal), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string TodayLaneValue(TodayLane lane) => lane switch
    {
        TodayLane.Planned => "planned",
        TodayLane.InProgress => "in_progress",
        _ => throw new ArgumentOutOfRangeException(nameof(lane)),
    };
    private static TodayLane ParseTodayLane(string value) => value switch
    {
        "planned" => TodayLane.Planned,
        "in_progress" => TodayLane.InProgress,
        _ => throw new InvalidDataException(),
    };

    private static void Require(SqliteConnection connection, SqliteTransaction transaction, string table, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        if ((long)command.ExecuteScalar()! != 1) throw new ArgumentException("The referenced item does not exist.", nameof(id));
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static T Guard<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (SqliteException) { throw new WorkspaceWorkException(); }
        catch (IOException) { throw new WorkspaceWorkException(); }
        catch (UnauthorizedAccessException) { throw new WorkspaceWorkException(); }
        catch (FormatException) { throw new WorkspaceWorkException(); }
        catch (InvalidDataException) { throw new WorkspaceWorkException(); }
    }
}
