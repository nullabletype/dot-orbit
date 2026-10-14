using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using Microsoft.Data.Sqlite;

namespace DotOrbit.Storage.Sqlite;

internal sealed class SqliteProposalWorkspaceSource(
    WorkspaceTransactionCoordinator transactions) : IProposalWorkspaceSource
{
    public ProposalWorkspaceState Read()
    {
        try
        {
            return transactions.Read(Read);
        }
        catch (SqliteException) { throw new WorkspaceWorkException(); }
        catch (IOException) { throw new WorkspaceWorkException(); }
        catch (UnauthorizedAccessException) { throw new WorkspaceWorkException(); }
        catch (FormatException) { throw new WorkspaceWorkException(); }
        catch (OverflowException) { throw new WorkspaceWorkException(); }
    }

    internal static ProposalWorkspaceState Read(
        SqliteConnection connection,
        SqliteTransaction? transaction) => new(
        WorkspaceRevisionStorage.Read(connection, transaction),
        SqliteWorkspaceWork.ReadSnapshot(connection, transaction),
        SqliteWorkspaceWork.ReadTaskBin(connection, transaction),
        SqliteWorkspaceWork.ReadProjectBin(connection, transaction));
}
