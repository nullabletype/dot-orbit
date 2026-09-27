using System.Globalization;
using DotOrbit.Core.Workspaces;
using Microsoft.Data.Sqlite;

namespace DotOrbit.Storage.Sqlite;

internal sealed class SqliteWorkspaceWork(
    EncryptedWorkspaceStore store,
    WorkspaceTransactionCoordinator transactions) : IWorkspaceWork
{
    internal const string Schema = """
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

    internal static void ValidateShape(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, title, description, category_id, target_date, position FROM projects LIMIT 0;
            SELECT id, project_id, title, description, category_override_id, due_date, shared_position, project_position FROM tasks LIMIT 0;
            """;
        using (var reader = command.ExecuteReader())
        {
            while (reader.NextResult()) { }
        }

        command.CommandText = "PRAGMA foreign_key_check;";
        using var foreignKeys = command.ExecuteReader();
        if (foreignKeys.Read())
        {
            throw new InvalidDataException();
        }
    }

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
            var shared = EncryptedWorkspaceStore.ExecuteScalar<long>(connection,
                "SELECT COALESCE(MIN(shared_position), 1) - 1 FROM tasks;", transaction);
            using var order = connection.CreateCommand();
            order.Transaction = transaction;
            order.CommandText = "SELECT COALESCE(MAX(project_position), -1) + 1 FROM tasks WHERE project_id = $project;";
            order.Parameters.AddWithValue("$project", projectId);
            var position = (long)order.ExecuteScalar()!;
            result = new TaskRecord(store.GetIdentifier(), projectId, title, string.Empty, null, null, shared, position);
            Execute(connection, transaction,
                "INSERT INTO tasks VALUES ($id, $project, $title, '', NULL, NULL, $shared, $position);",
                ("$id", result.Id), ("$project", projectId), ("$title", title), ("$shared", shared), ("$position", position));
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

    public TaskRecord UpdateTask(string id, string title, string description, string? categoryOverrideId, DateOnly? dueDate)
    {
        title = WorkTitle.Normalize(title);
        ArgumentNullException.ThrowIfNull(description);
        TaskRecord? result = null;
        Guard(() => transactions.Execute((connection, transaction) =>
        {
            Require(connection, transaction, "tasks", id);
            if (categoryOverrideId is not null) Require(connection, transaction, "categories", categoryOverrideId);
            Execute(connection, transaction,
                "UPDATE tasks SET title=$title, description=$description, category_override_id=$category, due_date=$date WHERE id=$id;",
                ("$id", id), ("$title", title), ("$description", description), ("$category", categoryOverrideId), ("$date", Date(dueDate)));
            result = ReadSnapshot(connection, transaction).Tasks.Single(task => task.Id == id);
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
        command.CommandText = "SELECT id,project_id,title,description,category_override_id,due_date,shared_position,project_position FROM tasks ORDER BY shared_position,id;";
        using (var reader = command.ExecuteReader())
            while (reader.Read()) tasks.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), ReadDate(reader, 5), reader.GetInt64(6), reader.GetInt64(7)));
        return new(categories.AsReadOnly(), projects.AsReadOnly(), tasks.AsReadOnly());
    }

    private static string? Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly? ReadDate(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null : DateOnly.ParseExact(reader.GetString(ordinal), "yyyy-MM-dd", CultureInfo.InvariantCulture);

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
    }
}
