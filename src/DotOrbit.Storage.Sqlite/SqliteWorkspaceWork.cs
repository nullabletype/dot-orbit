using System.Globalization;
using DotOrbit.Core.Workspaces;
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

    internal const string Schema = ProjectSchema + TaskSchema + "PRAGMA user_version = 5;";

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
        command.Parameters.Clear();
        command.CommandText = "PRAGMA foreign_key_check;";
        using (var foreignKeys = command.ExecuteReader())
        {
            if (foreignKeys.Read()) throw new InvalidDataException();
        }

        command.CommandText = "SELECT target_date FROM projects UNION ALL SELECT due_date FROM tasks"
            + (string.Equals(schema, Schema, StringComparison.Ordinal)
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
        if (string.Equals(schema, Schema, StringComparison.Ordinal))
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
            Require(connection, transaction, "projects", projectId);
            ShiftSharedOrderForNewTask(connection, transaction);
            using var order = connection.CreateCommand();
            order.Transaction = transaction;
            order.CommandText = "SELECT COALESCE(MAX(project_position), -1) + 1 FROM tasks WHERE project_id = $project;";
            order.Parameters.AddWithValue("$project", projectId);
            var position = (long)order.ExecuteScalar()!;
            result = new TaskRecord(store.GetIdentifier(), projectId, title, string.Empty, null, null, 0, position);
            Execute(connection, transaction,
                "INSERT INTO tasks VALUES ($id, $project, $title, '', NULL, NULL, 0, $position, NULL, NULL);",
                ("$id", result.Id), ("$project", projectId), ("$title", title), ("$position", position));
        }));
        return result!;
    }

    public TaskRecord CreateStandaloneTask(string title, string description, string categoryId, DateOnly? dueDate)
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

    public TaskRecord UpdateTask(string id, string title, string description, string? explicitCategoryId, DateOnly? dueDate)
    {
        title = WorkTitle.Normalize(title);
        ArgumentNullException.ThrowIfNull(description);
        TaskRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            Require(connection, transaction, "tasks", id);
            var existing = ReadSnapshot(connection, transaction).Tasks.Single(task => task.Id == id);
            if (existing.ProjectId is null && explicitCategoryId is null)
                throw new ArgumentException("A standalone Task requires an explicit Category.", nameof(explicitCategoryId));
            if (explicitCategoryId is not null) Require(connection, transaction, "categories", explicitCategoryId);
            Execute(connection, transaction,
                "UPDATE tasks SET title=$title, description=$description, explicit_category_id=$category, due_date=$date WHERE id=$id;",
                ("$id", id), ("$title", title), ("$description", description), ("$category", explicitCategoryId), ("$date", Date(dueDate)));
            result = ReadSnapshot(connection, transaction).Tasks.Single(task => task.Id == id);
        }));
        return result!;
    }

    public TaskRecord CompleteTask(string id)
    {
        TaskRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            Require(connection, transaction, "tasks", id);
            var instant = timeProvider.GetUtcNow();
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeProvider.LocalTimeZone).DateTime);
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
            Require(connection, transaction, "tasks", id);
            Execute(connection, transaction,
                "UPDATE tasks SET completion_instant=NULL, completion_date=NULL WHERE id=$id;", ("$id", id));
            result = ReadSnapshot(connection, transaction).Tasks.Single(task => task.Id == id);
        }));
        return result!;
    }

    public SharedTaskOrderChange MoveTaskInSharedOrder(string id, int targetPosition)
    {
        SharedTaskOrderChange? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            Require(connection, transaction, "tasks", id);
            var orderedTasks = ReadSnapshot(connection, transaction).Tasks
                .OrderBy(task => task.SharedPosition)
                .ToArray();
            var visibleIds = orderedTasks.Where(task => !task.IsComplete).Select(task => task.Id).ToList();
            if ((uint)targetPosition >= (uint)visibleIds.Count)
                throw new ArgumentOutOfRangeException(nameof(targetPosition));
            var currentPosition = visibleIds.IndexOf(id);
            if (currentPosition < 0) throw new ArgumentException("The Task does not exist.", nameof(id));
            if (currentPosition != targetPosition)
            {
                visibleIds.RemoveAt(currentPosition);
                visibleIds.Insert(targetPosition, id);
                var visibleIndex = 0;
                var mergedIds = orderedTasks
                    .Select(task => task.IsComplete ? task.Id : visibleIds[visibleIndex++])
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
            var orderedIds = ReadIds(connection, transaction, "SELECT id FROM projects ORDER BY position,id;");
            Move(orderedIds, id, targetPosition);
            RewriteOrder(connection, transaction, "projects", "position", orderedIds);
            result = new(id, targetPosition + 1, orderedIds.Count);
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
                "SELECT id FROM tasks WHERE project_id=$project ORDER BY project_position,id;", ("$project", projectId));
            Move(orderedIds, taskId, targetPosition);
            RewriteProjectTaskOrder(connection, transaction, projectId, orderedIds);
            result = new(projectId, taskId, targetPosition + 1, orderedIds.Count);
        }));
        return result!;
    }

    private static WorkspaceWorkSnapshot ReadSnapshot(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        var categories = new List<WorkspaceCategory>();
        var projects = new List<ProjectRecord>();
        var tasks = new List<TaskRecord>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,name,position FROM categories ORDER BY position,id;";
        using (var reader = command.ExecuteReader())
            while (reader.Read()) categories.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        command.CommandText = "SELECT id,title,description,category_id,target_date,position FROM projects ORDER BY position,id;";
        using (var reader = command.ExecuteReader())
            while (reader.Read()) projects.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), ReadDate(reader, 4), reader.GetInt64(5)));
        command.CommandText = "SELECT id,project_id,title,description,explicit_category_id,due_date,shared_position,project_position,completion_instant,completion_date FROM tasks ORDER BY shared_position,id;";
        using (var reader = command.ExecuteReader())
            while (reader.Read()) tasks.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), ReadDate(reader, 5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetInt64(7), ReadInstant(reader, 8), ReadDate(reader, 9)));
        return new(categories.AsReadOnly(), projects.AsReadOnly(), tasks.AsReadOnly());
    }

    private static void ShiftSharedOrderForNewTask(SqliteConnection connection, SqliteTransaction transaction)
    {
        var orderedIds = ReadTaskIdsInSharedOrder(connection, transaction);
        if (orderedIds.Count == 0) return;
        Execute(connection, transaction, "UPDATE tasks SET shared_position = shared_position + $offset;", ("$offset", orderedIds.Count));
        for (var position = 0; position < orderedIds.Count; position++)
            Execute(connection, transaction, "UPDATE tasks SET shared_position=$position WHERE id=$id;", ("$position", position + 1), ("$id", orderedIds[position]));
    }

    private static List<string> ReadTaskIdsInSharedOrder(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM tasks ORDER BY shared_position,id;";
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    private static void RewriteSharedOrder(SqliteConnection connection, SqliteTransaction transaction, List<string> orderedIds)
    {
        var offset = orderedIds.Count;
        Execute(connection, transaction, "UPDATE tasks SET shared_position = shared_position + $offset;", ("$offset", offset));
        for (var position = 0; position < orderedIds.Count; position++)
            Execute(connection, transaction, "UPDATE tasks SET shared_position=$position WHERE id=$id;", ("$position", position), ("$id", orderedIds[position]));
    }

    private static List<string> ReadIds(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
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
        Execute(connection, transaction, $"UPDATE {table} SET {column} = {column} + $offset;", ("$offset", orderedIds.Count));
        for (var position = 0; position < orderedIds.Count; position++)
            Execute(connection, transaction, $"UPDATE {table} SET {column}=$position WHERE id=$id;", ("$position", position), ("$id", orderedIds[position]));
    }

    private static void RewriteProjectTaskOrder(SqliteConnection connection, SqliteTransaction transaction, string projectId, List<string> orderedIds)
    {
        Execute(connection, transaction,
            "UPDATE tasks SET project_position = project_position + $offset WHERE project_id=$project;",
            ("$offset", orderedIds.Count), ("$project", projectId));
        for (var position = 0; position < orderedIds.Count; position++)
            Execute(connection, transaction, "UPDATE tasks SET project_position=$position WHERE id=$id;", ("$position", position), ("$id", orderedIds[position]));
    }

    private static string? Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly? ReadDate(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null : DateOnly.ParseExact(reader.GetString(ordinal), "yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateTimeOffset? ReadInstant(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null : DateTimeOffset.ParseExact(reader.GetString(ordinal), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void Require(SqliteConnection connection, SqliteTransaction transaction, string table, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        if ((long)command.ExecuteScalar()! != 1) throw new ArgumentException("The referenced item does not exist.", nameof(id));
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
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
    }
}
