using DotOrbit.Core.Interchange;
using Microsoft.Data.Sqlite;

namespace DotOrbit.Storage.Sqlite;

internal static class WorkspaceRevisionStorage
{
    internal const string Schema = """
        CREATE TABLE workspace_revision (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
            revision INTEGER NOT NULL CHECK(typeof(revision) = 'integer' AND revision >= 1)
        );
        INSERT INTO workspace_revision (singleton, revision) VALUES (1, 1);
        """;

    private const string TrackingSchema = """
        CREATE TEMP TABLE IF NOT EXISTS workspace_revision_change (
            singleton INTEGER NOT NULL PRIMARY KEY CHECK(singleton = 1),
            changed INTEGER NOT NULL CHECK(changed IN (0, 1))
        );
        INSERT OR IGNORE INTO workspace_revision_change (singleton, changed) VALUES (1, 0);
        """;

    private static readonly (string Table, string UpdateWhen)[] TrackedTables =
    [
        ("categories", "OLD.name IS NOT NEW.name OR OLD.position IS NOT NEW.position"),
        ("projects", "OLD.title IS NOT NEW.title OR OLD.description IS NOT NEW.description OR OLD.category_id IS NOT NEW.category_id OR OLD.target_date IS NOT NEW.target_date OR OLD.position IS NOT NEW.position"),
        ("tasks", "OLD.project_id IS NOT NEW.project_id OR OLD.title IS NOT NEW.title OR OLD.description IS NOT NEW.description OR OLD.explicit_category_id IS NOT NEW.explicit_category_id OR OLD.due_date IS NOT NEW.due_date OR OLD.shared_position IS NOT NEW.shared_position OR OLD.project_position IS NOT NEW.project_position OR OLD.completion_instant IS NOT NEW.completion_instant OR OLD.completion_date IS NOT NEW.completion_date"),
        ("participants", "OLD.label IS NOT NEW.label OR OLD.comparison_key IS NOT NEW.comparison_key"),
        ("task_participants", "OLD.task_id IS NOT NEW.task_id OR OLD.participant_id IS NOT NEW.participant_id OR OLD.position IS NOT NEW.position"),
        ("today_tasks", "OLD.task_id IS NOT NEW.task_id OR OLD.lane IS NOT NEW.lane"),
        ("task_archives", "OLD.task_id IS NOT NEW.task_id OR OLD.archived_instant IS NOT NEW.archived_instant OR OLD.archive_date IS NOT NEW.archive_date"),
        ("project_archives", "OLD.project_id IS NOT NEW.project_id OR OLD.archived_instant IS NOT NEW.archived_instant OR OLD.archive_date IS NOT NEW.archive_date"),
        ("task_bins", "OLD.task_id IS NOT NEW.task_id OR OLD.removed_instant IS NOT NEW.removed_instant OR OLD.today_lane IS NOT NEW.today_lane"),
        ("task_bin_order_anchors", "OLD.task_id IS NOT NEW.task_id OR OLD.scope IS NOT NEW.scope OR OLD.anchor_task_id IS NOT NEW.anchor_task_id OR OLD.relative_position IS NOT NEW.relative_position"),
        ("project_bins", "OLD.project_id IS NOT NEW.project_id OR OLD.removed_instant IS NOT NEW.removed_instant"),
        ("project_bin_tasks", "OLD.project_id IS NOT NEW.project_id OR OLD.task_id IS NOT NEW.task_id"),
        ("project_bin_order_anchors", "OLD.project_id IS NOT NEW.project_id OR OLD.anchor_project_id IS NOT NEW.anchor_project_id OR OLD.relative_position IS NOT NEW.relative_position"),
        ("category_identities", "OLD.category_id IS NOT NEW.category_id OR OLD.colour_key IS NOT NEW.colour_key"),
        ("project_identities", "OLD.project_id IS NOT NEW.project_id OR OLD.colour_key IS NOT NEW.colour_key"),
    ];

    internal static void InstallChangeTracking(SqliteConnection connection)
    {
        Execute(connection, null, TrackingSchema);
        foreach (var (table, updateWhen) in TrackedTables)
        {
            Execute(connection, null, $"""
                CREATE TEMP TRIGGER IF NOT EXISTS revision_{table}_insert
                AFTER INSERT ON main.{table}
                BEGIN UPDATE workspace_revision_change SET changed = 1 WHERE singleton = 1; END;
                CREATE TEMP TRIGGER IF NOT EXISTS revision_{table}_delete
                AFTER DELETE ON main.{table}
                BEGIN UPDATE workspace_revision_change SET changed = 1 WHERE singleton = 1; END;
                CREATE TEMP TRIGGER IF NOT EXISTS revision_{table}_update
                AFTER UPDATE ON main.{table}
                WHEN {updateWhen}
                BEGIN UPDATE workspace_revision_change SET changed = 1 WHERE singleton = 1; END;
                """);
        }
    }

    internal static void ResetChangeTracking(SqliteConnection connection, SqliteTransaction? transaction) =>
        Execute(connection, transaction,
            "UPDATE workspace_revision_change SET changed = 0 WHERE singleton = 1;");

    internal static bool HasTrackedChanges(SqliteConnection connection, SqliteTransaction? transaction) =>
        ReadInt64(connection, transaction,
            "SELECT changed FROM workspace_revision_change WHERE singleton = 1;") == 1;

    internal static WorkspaceRevision Read(SqliteConnection connection, SqliteTransaction? transaction = null) =>
        new(ReadInt64(connection, transaction,
            "SELECT revision FROM workspace_revision WHERE singleton = 1;"));

    internal static WorkspaceRevision? TryRead(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='workspace_revision';";
        if ((long)exists.ExecuteScalar()! == 0)
            return null;
        return Read(connection, transaction);
    }

    internal static WorkspaceRevision AdvanceChecked(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        var current = Read(connection, transaction);
        if (current.Value == long.MaxValue)
            throw new OverflowException("The workspace revision cannot advance.");
        var next = new WorkspaceRevision(checked(current.Value + 1));
        Set(connection, transaction, next);
        return next;
    }

    internal static void Set(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        WorkspaceRevision revision)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE workspace_revision SET revision=$revision WHERE singleton=1;";
        command.Parameters.AddWithValue("$revision", revision.Value);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidDataException();
    }

    internal static WorkspaceRevision RebaseChecked(
        WorkspaceRevision? before,
        WorkspaceRevision? recovered)
    {
        if (before is null && recovered is null)
            return new(1);
        var maximum = Math.Max(before?.Value ?? 0, recovered?.Value ?? 0);
        if (maximum == long.MaxValue)
            throw new OverflowException("The workspace revision cannot be rebased.");
        return new(checked(maximum + 1));
    }

    private static long ReadInt64(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        if (value is null || value is DBNull)
            throw new InvalidDataException();
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
