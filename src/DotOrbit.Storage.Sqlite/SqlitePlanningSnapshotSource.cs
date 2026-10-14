using DotOrbit.Core.Interchange;

namespace DotOrbit.Storage.Sqlite;

internal sealed class SqlitePlanningSnapshotSource(
    WorkspaceTransactionCoordinator transactions) : IPlanningSnapshotSource
{
    public PlanningSnapshotContent Read() => transactions.Read((connection, transaction) =>
        PlanningSnapshotProjection.Create(
            WorkspaceRevisionStorage.Read(connection, transaction),
            SqliteWorkspaceWork.ReadSnapshot(connection, transaction)));
}
