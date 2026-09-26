namespace DotOrbit.Desktop;

public static class WorkspacePaths
{
    public static string DefaultWorkspacePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dot-orbit",
        "workspace.db");
}
