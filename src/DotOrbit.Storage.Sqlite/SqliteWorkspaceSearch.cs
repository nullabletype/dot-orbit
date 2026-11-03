using System.Collections.ObjectModel;
using DotOrbit.Core.Workspaces;
using Microsoft.Data.Sqlite;

namespace DotOrbit.Storage.Sqlite;

internal sealed partial class SqliteWorkspaceWork
{
    internal const string SearchDocumentSchema = """
        CREATE TABLE search_documents(
            document_id INTEGER PRIMARY KEY,
            record_type TEXT NOT NULL CHECK(record_type IN ('project','task')),
            record_id TEXT NOT NULL,
            UNIQUE(record_type,record_id)
        );
        """;

    internal const string WorkspaceSearchSchema = """
        CREATE VIRTUAL TABLE workspace_search USING fts5(
            title,
            description,
            category,
            participants,
            tokenize = 'unicode61 remove_diacritics 2'
        );
        """;

    public IReadOnlyList<WorkspaceSearchResult> SearchWorkspace(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var match = BuildSearchMatchQuery(query);
        if (match.Length == 0) return [];
        return Guard(() => transactions.Read((connection, transaction) =>
            ReadWorkspaceMatches(connection, transaction, match)));
    }

    private static ReadOnlyCollection<WorkspaceSearchResult> ReadWorkspaceMatches(
        SqliteConnection connection, SqliteTransaction? transaction, string match)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.record_type, s.record_id,
                   CASE WHEN tb.task_id IS NOT NULL OR pb.project_id IS NOT NULL THEN $bin
                        WHEN ta.task_id IS NOT NULL OR pa.project_id IS NOT NULL THEN $archive
                        WHEN p.id IS NOT NULL THEN $projects
                        WHEN t.completion_date IS NOT NULL THEN $completed ELSE $backlog END AS destination,
                   CASE WHEN aggregate.task_id IS NOT NULL THEN tb.removed_instant END,
                   tb.today_lane
            FROM workspace_search
            JOIN search_documents s ON s.document_id=workspace_search.rowid
            LEFT JOIN tasks t ON s.record_type='task' AND t.id=s.record_id
            LEFT JOIN projects p ON s.record_type='project' AND p.id=s.record_id
            LEFT JOIN project_bins pb ON pb.project_id=p.id
            LEFT JOIN task_bins tb ON tb.task_id=t.id
            LEFT JOIN project_bin_tasks aggregate ON aggregate.task_id=t.id
            LEFT JOIN task_archives ta ON ta.task_id=t.id
            LEFT JOIN project_archives pa ON pa.project_id=COALESCE(p.id,t.project_id)
            WHERE workspace_search MATCH $query
            ORDER BY destination,
                     julianday(COALESCE(tb.removed_instant,pb.removed_instant,
                         ta.archived_instant,pa.archived_instant,t.completion_instant)) DESC,
                     COALESCE(p.position,t.shared_position),s.record_type,s.record_id;
            """;
        command.Parameters.AddWithValue("$query", match);
        command.Parameters.AddWithValue("$bin", (int)WorkspaceSearchGroup.Bin);
        command.Parameters.AddWithValue("$archive", (int)WorkspaceSearchGroup.Archive);
        command.Parameters.AddWithValue("$projects", (int)WorkspaceSearchGroup.Projects);
        command.Parameters.AddWithValue("$completed", (int)WorkspaceSearchGroup.Completed);
        command.Parameters.AddWithValue("$backlog", (int)WorkspaceSearchGroup.Backlog);

        var results = new List<WorkspaceSearchResult>();
        var aggregates = new List<(int Index, DateTimeOffset RemovedAt, TodayLane? Lane)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!reader.IsDBNull(3))
                    aggregates.Add((results.Count, ReadInstant(reader, 3)!.Value,
                        reader.IsDBNull(4) ? null : ParseTodayLane(reader.GetString(4))));
                results.Add(new((WorkspaceSearchGroup)reader.GetInt32(2),
                    reader.GetString(0) == "project" ? ArchiveSearchRecordType.Project : ArchiveSearchRecordType.Task,
                    reader.GetString(1)));
            }
        }
        foreach (var (index, removedAt, lane) in aggregates)
        {
            var result = results[index];
            results[index] = result with
            {
                AggregateBinTask = ReadTaskBinRecord(connection, transaction, result.Id, removedAt, lane),
            };
        }
        return results.AsReadOnly();
    }
}
