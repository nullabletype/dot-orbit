using DotOrbit.Core.Workspaces;
using DotOrbit.Storage.Sqlite;

namespace DotOrbit.Desktop;

internal interface IWorkspacePathProvider
{
    bool UsesRememberedWorkspace => false;

    string GetDefaultWorkspacePath();

    DefaultWorkspaceResolution ResolveDefaultWorkspace();
}

internal interface IWorkspacePathAvailability
{
    bool IsAvailableRegularFile(string path);
}

internal sealed class StoreWorkspacePathAvailability(IWorkspaceStore store)
    : IWorkspacePathAvailability
{
    public bool IsAvailableRegularFile(string path) => store.Exists(path);
}

internal sealed class SystemWorkspacePathAvailability : IWorkspacePathAvailability
{
    public bool IsAvailableRegularFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists
                || (file.Attributes & FileAttributes.Directory) != 0
                || file.Length == 0)
            {
                return false;
            }

            using var handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.RandomAccess);
            return RandomAccess.GetLength(handle) > 0;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or NotSupportedException)
        {
            return false;
        }
    }
}

internal enum WorkspacePathIntent
{
    Default,
    Existing,
    New,
    Development,
}

internal sealed record WorkspacePathSelection(
    string StoragePath,
    string RememberedPath,
    WorkspacePathIntent Intent,
    DefaultWorkspaceResolutionStatus ResolutionStatus = DefaultWorkspaceResolutionStatus.Ready,
    bool WasRemembered = false)
{
    public static WorkspacePathSelection Default(
        DefaultWorkspaceResolution resolution,
        string defaultWorkspacePath)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        return new(
            WorkspacePathPolicy.Canonicalize(resolution.WorkspacePath),
            WorkspacePathPolicy.Canonicalize(defaultWorkspacePath),
            WorkspacePathIntent.Default,
            resolution.Status);
    }

    public static WorkspacePathSelection Existing(string path, bool wasRemembered = false)
    {
        var canonicalPath = WorkspacePathPolicy.Canonicalize(path);
        return new(
            canonicalPath,
            canonicalPath,
            WorkspacePathIntent.Existing,
            WasRemembered: wasRemembered);
    }

    public static WorkspacePathSelection New(string path)
    {
        var canonicalPath = WorkspacePathPolicy.Canonicalize(path);
        return new(canonicalPath, canonicalPath, WorkspacePathIntent.New);
    }

    public static WorkspacePathSelection Development(DefaultWorkspaceResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var canonicalPath = WorkspacePathPolicy.Canonicalize(resolution.WorkspacePath);
        return new(
            canonicalPath,
            canonicalPath,
            WorkspacePathIntent.Development,
            resolution.Status);
    }
}

internal static class WorkspacePathPolicy
{
    public static bool TryCanonicalize(string? path, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return false;
        }

        try
        {
            canonicalPath = Path.GetFullPath(path);
            return Path.IsPathFullyQualified(canonicalPath);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }

    public static string Canonicalize(string path) =>
        TryCanonicalize(path, out var canonicalPath)
            ? canonicalPath
            : throw new ArgumentException("The workspace path must be an absolute local path.", nameof(path));
}

internal static class WorkspacePathSelectionFactory
{
    public static WorkspacePathSelection CreateInitial(
        IWorkspacePathProvider pathProvider,
        IWorkspacePathPreferenceStore preferenceStore)
    {
        ArgumentNullException.ThrowIfNull(pathProvider);
        ArgumentNullException.ThrowIfNull(preferenceStore);
        if (pathProvider.UsesRememberedWorkspace
            && preferenceStore.LoadWorkspacePath() is { } rememberedPath)
        {
            return WorkspacePathSelection.Existing(rememberedPath, wasRemembered: true);
        }

        return CreateDefault(pathProvider);
    }

    public static WorkspacePathSelection CreateDefault(IWorkspacePathProvider pathProvider)
    {
        ArgumentNullException.ThrowIfNull(pathProvider);
        var resolution = pathProvider.ResolveDefaultWorkspace();
        return pathProvider.UsesRememberedWorkspace
            ? WorkspacePathSelection.Default(resolution, pathProvider.GetDefaultWorkspacePath())
            : WorkspacePathSelection.Development(resolution);
    }
}

internal sealed class SystemWorkspacePathProvider : IWorkspacePathProvider
{
    private const string SampleWorkspaceArgument = "--sample-workspace";
    private const string DefaultWorkspaceArgument = "--default-workspace";

    private readonly string? _workspacePath;
    private readonly bool _usesRememberedWorkspace;

    public SystemWorkspacePathProvider()
    {
        _usesRememberedWorkspace = true;
    }

    private SystemWorkspacePathProvider(
        string? workspacePath,
        bool usesRememberedWorkspace)
    {
        _workspacePath = workspacePath;
        _usesRememberedWorkspace = usesRememberedWorkspace;
    }

    public bool UsesRememberedWorkspace => _usesRememberedWorkspace;

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
            return new SystemWorkspacePathProvider(
                workspacePath: null,
                usesRememberedWorkspace: false);
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

        return new SystemWorkspacePathProvider(
            Path.Combine(
                repositoryRoot,
                "artifacts",
                "sample-workspace",
                "workspace.orb"),
            usesRememberedWorkspace: false);
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
