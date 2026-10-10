using Microsoft.Data.Sqlite;
using DotOrbit.Core.Workspaces;

namespace DotOrbit.Storage.Sqlite;

internal sealed class WorkspaceConnectionFactory
{
    private readonly Func<string, WorkspacePassphrase, SqliteOpenMode, SqliteConnection> _open;

    internal WorkspaceConnectionFactory(
        Func<string, WorkspacePassphrase, SqliteOpenMode, SqliteConnection>? open = null)
    {
        _open = open ?? ((path, passphrase, mode) =>
            EncryptedWorkspaceStore.OpenConnection(path, passphrase, mode));
    }

    internal SqliteConnection OpenConfigured(
        string path,
        WorkspacePassphrase passphrase,
        SqliteOpenMode mode)
    {
        SqliteConnection? connection = null;
        try
        {
            connection = _open(path, passphrase, mode);
            EncryptedWorkspaceStore.ConfigureConnection(connection);
            return connection;
        }
        catch
        {
            connection?.Dispose();
            throw;
        }
    }
}
