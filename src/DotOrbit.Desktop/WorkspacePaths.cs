using DotOrbit.Storage.Sqlite;

namespace DotOrbit.Desktop;

internal interface IWorkspacePathProvider
{
    string GetDefaultWorkspacePath();
}

internal sealed class SystemWorkspacePathProvider : IWorkspacePathProvider
{
    public string GetDefaultWorkspacePath() => WorkspacePathDefaults.GetDefaultWorkspacePath();
}
