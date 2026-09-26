namespace DotOrbit.Desktop;

public static class WorkspacePaths
{
    public static string DefaultWorkspacePath =>
        new SystemWorkspacePathProvider().GetDefaultWorkspacePath();
}

internal interface IWorkspacePathProvider
{
    string GetDefaultWorkspacePath();
}

internal sealed class SystemWorkspacePathProvider : IWorkspacePathProvider
{
    public string GetDefaultWorkspacePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dot-orbit",
        "workspace.db");
}
