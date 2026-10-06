using DotOrbit.Storage.Sqlite;

namespace DotOrbit.Desktop;

internal interface IWorkspacePathProvider
{
    string GetDefaultWorkspacePath();

    DefaultWorkspaceResolution ResolveDefaultWorkspace();
}

internal sealed class SystemWorkspacePathProvider : IWorkspacePathProvider
{
    private const string SampleWorkspaceArgument = "--sample-workspace";
    private const string DefaultWorkspaceArgument = "--default-workspace";

    private readonly string? _workspacePath;

    public SystemWorkspacePathProvider()
    {
    }

    private SystemWorkspacePathProvider(string workspacePath)
    {
        _workspacePath = workspacePath;
    }

    public static SystemWorkspacePathProvider FromArguments(
        string[]? arguments,
        string currentDirectory,
        string? applicationDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        var useSampleWorkspace = arguments?.Contains(SampleWorkspaceArgument, StringComparer.Ordinal) == true;
        var useDefaultWorkspace = arguments?.Contains(DefaultWorkspaceArgument, StringComparer.Ordinal) == true;
        if (useSampleWorkspace && useDefaultWorkspace)
        {
            throw new InvalidOperationException(
                $"{SampleWorkspaceArgument} and {DefaultWorkspaceArgument} cannot be used together.");
        }
        if (useDefaultWorkspace)
        {
            return new SystemWorkspacePathProvider();
        }

        var repositoryRoot = useSampleWorkspace
            ? FindRepositoryRoot(currentDirectory)
                ?? FindRepositoryRoot(applicationDirectory ?? AppContext.BaseDirectory)
            : FindRepositoryRoot(applicationDirectory ?? AppContext.BaseDirectory);
        if (repositoryRoot is null)
        {
            if (useSampleWorkspace)
            {
                throw new InvalidOperationException(
                    $"{SampleWorkspaceArgument} must launch a dot-orbit build from its repository checkout.");
            }

            return new SystemWorkspacePathProvider();
        }

        return new SystemWorkspacePathProvider(Path.Combine(
            repositoryRoot,
            "artifacts",
            "sample-workspace",
            "workspace.orb"));
    }

    public string GetDefaultWorkspacePath() =>
        _workspacePath ?? WorkspacePathDefaults.GetDefaultWorkspacePath();

    public DefaultWorkspaceResolution ResolveDefaultWorkspace() =>
        _workspacePath is null
            ? WorkspacePathDefaults.ResolveDefaultWorkspace()
            : DefaultWorkspaceResolution.Ready(_workspacePath);

    private static string? FindRepositoryRoot(string currentDirectory)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(currentDirectory));
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DotOrbit.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }
}
