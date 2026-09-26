namespace DotOrbit.Desktop;

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
