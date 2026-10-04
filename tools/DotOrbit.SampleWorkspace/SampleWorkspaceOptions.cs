using System.Globalization;
using DotOrbit.Storage.Sqlite;

namespace DotOrbit.SampleWorkspace;

internal sealed record SampleWorkspaceOptions(string OutputPath, DateOnly AnchorDate)
{
    public static SampleWorkspaceOptions Parse(
        string[] args,
        string currentDirectory,
        DateOnly currentDate)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);

        string? outputPath = null;
        DateOnly? anchorDate = null;
        var useDefaultWorkspace = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--output" when outputPath is null && index + 1 < args.Length:
                    outputPath = args[++index];
                    break;
                case "--anchor-date" when anchorDate is null && index + 1 < args.Length:
                    if (!DateOnly.TryParseExact(
                            args[++index],
                            "yyyy-MM-dd",
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out var parsedDate))
                    {
                        throw new SampleWorkspaceException("invalid-anchor-date");
                    }
                    anchorDate = parsedDate;
                    break;
                case "--default-workspace" when !useDefaultWorkspace:
                    useDefaultWorkspace = true;
                    break;
                default:
                    throw new SampleWorkspaceException("invalid-arguments");
            }
        }

        if (outputPath is not null && string.IsNullOrWhiteSpace(outputPath))
        {
            throw new SampleWorkspaceException("invalid-output-path");
        }
        if (useDefaultWorkspace && outputPath is not null)
        {
            throw new SampleWorkspaceException("conflicting-output-options");
        }

        string resolvedOutput;
        if (useDefaultWorkspace)
        {
            resolvedOutput = WorkspacePathDefaults.GetDefaultWorkspacePath();
        }
        else
        {
            var repositoryRoot = FindRepositoryRoot(currentDirectory);
            resolvedOutput = outputPath is null
                ? Path.Combine(repositoryRoot, "artifacts", "sample-workspace", "workspace.db")
                : Path.GetFullPath(outputPath, currentDirectory);
        }
        return new(resolvedOutput, anchorDate ?? currentDate);
    }

    private static string FindRepositoryRoot(string currentDirectory)
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

        throw new SampleWorkspaceException("repository-root-not-found");
    }
}

internal sealed class SampleWorkspaceException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}
