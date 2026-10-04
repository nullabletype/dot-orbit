namespace DotOrbit.Storage.Sqlite;

public static class WorkspacePathDefaults
{
    public static string GetDefaultWorkspacePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dot-orbit",
        "workspace.db");
}
