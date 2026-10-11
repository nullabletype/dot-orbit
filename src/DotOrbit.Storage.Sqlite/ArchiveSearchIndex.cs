using System.Collections.ObjectModel;
using System.Globalization;
using DotOrbit.Core.Diagnostics;
using DotOrbit.Markdown;
using Microsoft.Data.Sqlite;

namespace DotOrbit.Storage.Sqlite;

internal interface IArchiveSearchIndex
{
    void Apply(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ArchiveSearchIndexEffects effects);

    void Rebuild(SqliteConnection connection, SqliteTransaction? transaction = null);
}

internal enum ArchiveSearchSourceType
{
    Task,
    Project,
    Category,
    Participant,
}

internal readonly record struct ArchiveSearchSourceChange(ArchiveSearchSourceType SourceType, string Id);

internal sealed class ArchiveSearchIndexEffects
{
    private static readonly ArchiveSearchIndexEffects Empty = new([]);
    private readonly ReadOnlyCollection<ArchiveSearchSourceChange> _changes;

    private ArchiveSearchIndexEffects(IEnumerable<ArchiveSearchSourceChange> changes)
    {
        _changes = Array.AsReadOnly(changes.Distinct().ToArray());
    }

    internal static ArchiveSearchIndexEffects None => Empty;

    internal IReadOnlyList<ArchiveSearchSourceChange> Changes => _changes;

    internal static ArchiveSearchIndexEffects Task(string id) =>
        Create(ArchiveSearchSourceType.Task, [id]);

    internal static ArchiveSearchIndexEffects Tasks(IEnumerable<string> ids) =>
        Create(ArchiveSearchSourceType.Task, ids);

    internal static ArchiveSearchIndexEffects Project(string id) =>
        Create(ArchiveSearchSourceType.Project, [id]);

    internal static ArchiveSearchIndexEffects Projects(IEnumerable<string> ids) =>
        Create(ArchiveSearchSourceType.Project, ids);

    internal static ArchiveSearchIndexEffects Category(string id) =>
        Create(ArchiveSearchSourceType.Category, [id]);

    internal static ArchiveSearchIndexEffects Participant(string id) =>
        Create(ArchiveSearchSourceType.Participant, [id]);

    internal static ArchiveSearchIndexEffects Combine(params ArchiveSearchIndexEffects[] effects) =>
        new(effects.SelectMany(effect => effect.Changes));

    private static ArchiveSearchIndexEffects Create(ArchiveSearchSourceType sourceType, IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return new(ids.Select(id => new ArchiveSearchSourceChange(
            sourceType,
            id ?? throw new ArgumentNullException(nameof(ids)))));
    }
}

internal sealed class ArchiveSearchIndex : IArchiveSearchIndex
{
    public void Apply(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ArchiveSearchIndexEffects effects)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(effects);

        var projectIds = new HashSet<string>(StringComparer.Ordinal);
        var taskIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in effects.Changes)
        {
            switch (change.SourceType)
            {
                case ArchiveSearchSourceType.Task:
                    taskIds.Add(change.Id);
                    break;
                case ArchiveSearchSourceType.Project:
                    projectIds.Add(change.Id);
                    AddIds(connection, transaction, taskIds,
                        """
                        SELECT t.id
                        FROM tasks t
                        JOIN task_archives ta ON ta.task_id=t.id
                        WHERE t.project_id=$id;
                        """, change.Id);
                    break;
                case ArchiveSearchSourceType.Category:
                    AddIds(connection, transaction, projectIds,
                        """
                        SELECT p.id
                        FROM projects p
                        JOIN project_archives pa ON pa.project_id=p.id
                        WHERE p.category_id=$id;
                        """, change.Id);
                    AddIds(connection, transaction, taskIds, """
                        SELECT t.id
                        FROM tasks t
                        JOIN task_archives ta ON ta.task_id=t.id
                        LEFT JOIN projects p ON p.id=t.project_id
                        WHERE COALESCE(t.explicit_category_id,p.category_id)=$id;
                        """, change.Id);
                    break;
                case ArchiveSearchSourceType.Participant:
                    AddIds(connection, transaction, taskIds,
                        """
                        SELECT tp.task_id
                        FROM task_participants tp
                        JOIN task_archives ta ON ta.task_id=tp.task_id
                        WHERE tp.participant_id=$id;
                        """, change.Id);
                    break;
                default:
                    throw new InvalidOperationException("The archive-search effect is not supported.");
            }
        }

        foreach (var id in projectIds.Order(StringComparer.Ordinal))
            RefreshProject(connection, transaction, id);
        foreach (var id in taskIds.Order(StringComparer.Ordinal))
            RefreshTask(connection, transaction, id);
    }

    public void Rebuild(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var timing = PerformanceTrace.MeasureAllocationStage(PerformanceStage.ArchiveIndexRebuild);
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM archive_search;";
            clear.ExecuteNonQuery();
        }

        foreach (var id in ReadIds(connection, transaction,
                     "SELECT project_id FROM project_archives ORDER BY project_id;"))
            RefreshProject(connection, transaction, id);
        foreach (var id in ReadIds(connection, transaction,
                     "SELECT task_id FROM task_archives ORDER BY task_id;"))
            RefreshTask(connection, transaction, id);
    }

    private static void RefreshProject(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string id)
    {
        DeleteDocument(connection, transaction, "project", id);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.title,p.description,c.name,pa.archived_instant,pa.archive_date
            FROM projects p
            JOIN categories c ON c.id=p.category_id
            JOIN project_archives pa ON pa.project_id=p.id
            WHERE p.id=$id
            """ + (TableExists(connection, transaction, "project_bins")
                ? " AND NOT EXISTS (SELECT 1 FROM project_bins pb WHERE pb.project_id=p.id);"
                : ";");
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return;
        var title = reader.GetString(0);
        var description = reader.GetString(1);
        var category = reader.GetString(2);
        var archivedAt = ReadInstant(reader, 3);
        var archivedOn = ReadDate(reader, 4);
        reader.Close();
        InsertDocument(
            connection,
            transaction,
            "project",
            id,
            title,
            SearchableMarkdownText(description),
            category,
            string.Empty,
            string.Empty,
            archivedAt.UtcTicks,
            archivedOn,
            "archived");
    }

    private static void RefreshTask(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string id)
    {
        DeleteDocument(connection, transaction, "task", id);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT t.title,t.description,c.name,p.title,ta.archived_instant,t.completion_date
            FROM tasks t
            LEFT JOIN projects p ON p.id=t.project_id
            JOIN categories c ON c.id=COALESCE(t.explicit_category_id,p.category_id)
            JOIN task_archives ta ON ta.task_id=t.id
            WHERE t.id=$id
            """ + (TableExists(connection, transaction, "task_bins")
                ? " AND NOT EXISTS (SELECT 1 FROM task_bins tb WHERE tb.task_id=t.id);"
                : ";");
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return;
        var title = reader.GetString(0);
        var description = reader.GetString(1);
        var category = reader.GetString(2);
        var parentProject = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
        var archivedAt = ReadInstant(reader, 4);
        var completedOn = ReadDate(reader, 5);
        reader.Close();

        var participants = ReadParticipantLabels(connection, transaction, id);
        InsertDocument(
            connection,
            transaction,
            "task",
            id,
            title,
            SearchableMarkdownText(description),
            category,
            string.Join(' ', participants),
            parentProject,
            archivedAt.UtcTicks,
            completedOn,
            "completed");
    }

    private static void DeleteDocument(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string recordType,
        string recordId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM archive_search WHERE record_type=$recordType AND record_id=$recordId;";
        command.Parameters.AddWithValue("$recordType", recordType);
        command.Parameters.AddWithValue("$recordId", recordId);
        command.ExecuteNonQuery();
    }

    private static void InsertDocument(
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
        command.Parameters.AddWithValue("$displayDate", displayDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$dateKind", dateKind);
        command.ExecuteNonQuery();
    }

    private static string[] ReadParticipantLabels(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string taskId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT p.label
            FROM task_participants tp
            JOIN participants p ON p.id=tp.participant_id
            WHERE tp.task_id=$id
            ORDER BY tp.position;
            """;
        command.Parameters.AddWithValue("$id", taskId);
        using var reader = command.ExecuteReader();
        var labels = new List<string>();
        while (reader.Read()) labels.Add(reader.GetString(0));
        return labels.ToArray();
    }

    private static void AddIds(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        HashSet<string> destination,
        string sql,
        string id)
    {
        foreach (var relatedId in ReadIds(connection, transaction, sql, id)) destination.Add(relatedId);
    }

    private static string[] ReadIds(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        string? id = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (id is not null) command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids.ToArray();
    }

    private static bool TableExists(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return (long)command.ExecuteScalar()! == 1;
    }

    private static DateTimeOffset ReadInstant(SqliteDataReader reader, int ordinal) =>
        DateTimeOffset.ParseExact(
            reader.GetString(ordinal),
            "O",
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static DateOnly ReadDate(SqliteDataReader reader, int ordinal) =>
        DateOnly.ParseExact(reader.GetString(ordinal), "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string SearchableMarkdownText(string markdown)
    {
        var plainText = SanitisedMarkdownRenderer.Render(markdown).ToPlainText();
        return string.Join(' ', plainText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
