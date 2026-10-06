using System.Text.Json;
using DotOrbit.Core.Workspaces;

namespace DotOrbit.Storage.Sqlite;

public enum DefaultWorkspaceResolutionStatus
{
    Ready,
    Conflict,
    Failed,
}

public sealed record DefaultWorkspaceResolution(
    DefaultWorkspaceResolutionStatus Status,
    string WorkspacePath)
{
    public static DefaultWorkspaceResolution Ready(string workspacePath) =>
        new(DefaultWorkspaceResolutionStatus.Ready, workspacePath);
}

public static class WorkspacePathDefaults
{
    private const int AdoptionStateVersion = 1;
    internal const string CurrentWorkspaceFileName = "workspace.orb";
    internal const string LegacyWorkspaceFileName = "workspace.db";
    internal const string AdoptionStateFileName = "workspace.orb.adoption-state.json";
    internal const string PreservedRecoveryStateFileName =
        "workspace.db.recovery-state.json.adoption-preserved";

    private static readonly string[] CompanionSuffixes =
    [
        "-journal",
        "-wal",
        "-shm",
        ".recovery-state.json",
    ];

    private static readonly string[] WorkspaceSuffixes = ["", .. CompanionSuffixes];

    public static string GetDefaultWorkspacePath() => Path.Combine(
        GetDefaultWorkspaceDirectory(),
        CurrentWorkspaceFileName);

    public static DefaultWorkspaceResolution ResolveDefaultWorkspace() =>
        ResolveDefaultWorkspace(
            GetDefaultWorkspaceDirectory(),
            new WorkspaceFileOperations());

    internal static DefaultWorkspaceResolution ResolveDefaultWorkspace(
        string workspaceDirectory,
        IWorkspaceFileOperations fileOperations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        ArgumentNullException.ThrowIfNull(fileOperations);

        var directory = fileOperations.ResolvePath(workspaceDirectory);
        var legacyPath = Path.Combine(directory, LegacyWorkspaceFileName);
        var currentPath = Path.Combine(directory, CurrentWorkspaceFileName);
        var statePath = Path.Combine(directory, AdoptionStateFileName);
        var preservedRecoveryStatePath = Path.Combine(
            directory,
            PreservedRecoveryStateFileName);

        try
        {
            if (fileOperations.Exists(statePath))
            {
                if (!TryReadAdoptionState(fileOperations, statePath, out var state)
                    || !MatchesAdoptionState(fileOperations, state!, legacyPath, currentPath))
                {
                    return new(DefaultWorkspaceResolutionStatus.Conflict, currentPath);
                }

                return fileOperations.Exists(legacyPath)
                    ? DefaultWorkspaceResolution.Ready(legacyPath)
                    : fileOperations.Exists(currentPath)
                        ? DefaultWorkspaceResolution.Ready(currentPath)
                        : new(DefaultWorkspaceResolutionStatus.Conflict, currentPath);
            }

            if (fileOperations.Exists(preservedRecoveryStatePath))
            {
                return new(DefaultWorkspaceResolutionStatus.Conflict, currentPath);
            }

            var hasLegacy = fileOperations.Exists(legacyPath);
            var hasCurrent = fileOperations.Exists(currentPath);
            var companionStates = GetCompanionStates(fileOperations, legacyPath, currentPath);

            if (hasLegacy && hasCurrent)
            {
                return new(DefaultWorkspaceResolutionStatus.Conflict, currentPath);
            }

            if (hasCurrent)
            {
                return companionStates.Any(state => state.HasLegacy)
                    ? new(DefaultWorkspaceResolutionStatus.Conflict, currentPath)
                    : DefaultWorkspaceResolution.Ready(currentPath);
            }

            if (!hasLegacy)
            {
                return companionStates.Any(state => state.HasLegacy || state.HasCurrent)
                    ? new(DefaultWorkspaceResolutionStatus.Conflict, currentPath)
                    : DefaultWorkspaceResolution.Ready(currentPath);
            }

            return companionStates.Any(state => state.HasCurrent)
                ? new(DefaultWorkspaceResolutionStatus.Conflict, currentPath)
                : DefaultWorkspaceResolution.Ready(legacyPath);
        }
        catch (IOException)
        {
            return new(DefaultWorkspaceResolutionStatus.Failed, currentPath);
        }
        catch (UnauthorizedAccessException)
        {
            return new(DefaultWorkspaceResolutionStatus.Failed, currentPath);
        }
        catch (InvalidDataException)
        {
            return new(DefaultWorkspaceResolutionStatus.Failed, currentPath);
        }
    }

    internal static string GetDefaultWorkspaceDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dot-orbit");

    internal static DefaultWorkspaceResolutionStatus LinkValidatedWorkspace(
        string workspacePath,
        IWorkspaceFileOperations fileOperations,
        IIdentifierGenerator identifierGenerator,
        bool removeLegacyNames)
    {
        var directory = Path.GetDirectoryName(workspacePath);
        if (string.IsNullOrEmpty(directory))
        {
            return DefaultWorkspaceResolutionStatus.Failed;
        }

        var legacyPath = Path.Combine(directory, LegacyWorkspaceFileName);
        var currentPath = Path.Combine(directory, CurrentWorkspaceFileName);
        var statePath = Path.Combine(directory, AdoptionStateFileName);
        try
        {
            AdoptionStateDocument state;
            if (fileOperations.Exists(statePath))
            {
                if (!TryReadAdoptionState(fileOperations, statePath, out var existingState)
                    || !MatchesAdoptionState(
                        fileOperations,
                        existingState!,
                        legacyPath,
                        currentPath))
                {
                    return DefaultWorkspaceResolutionStatus.Conflict;
                }

                state = existingState!;
            }
            else
            {
                if (!fileOperations.Exists(legacyPath)
                    || WorkspaceSuffixes.Any(suffix => fileOperations.Exists(currentPath + suffix))
                    || fileOperations.Exists(Path.Combine(
                        directory,
                        PreservedRecoveryStateFileName)))
                {
                    return DefaultWorkspaceResolutionStatus.Conflict;
                }

                state = CaptureAdoptionState(fileOperations, legacyPath);
                PublishAdoptionState(fileOperations, identifierGenerator, statePath, state);
            }

            foreach (var file in state.Files)
            {
                var legacyFile = legacyPath + file.Suffix;
                var currentFile = currentPath + file.Suffix;
                if (!fileOperations.Exists(currentFile))
                {
                    if (!MatchesHash(fileOperations, legacyFile, file.Sha256))
                    {
                        return DefaultWorkspaceResolutionStatus.Conflict;
                    }

                    fileOperations.CreateHardLink(legacyFile, currentFile);
                }

                if (!MatchesHash(fileOperations, currentFile, file.Sha256))
                {
                    return DefaultWorkspaceResolutionStatus.Conflict;
                }
            }

            if (removeLegacyNames)
            {
                if (!DeleteLegacyNames(fileOperations, legacyPath, state))
                {
                    return DefaultWorkspaceResolutionStatus.Conflict;
                }
            }

            return DefaultWorkspaceResolutionStatus.Ready;
        }
        catch (IOException)
        {
            return DefaultWorkspaceResolutionStatus.Failed;
        }
        catch (UnauthorizedAccessException)
        {
            return DefaultWorkspaceResolutionStatus.Failed;
        }
        catch (InvalidDataException)
        {
            return DefaultWorkspaceResolutionStatus.Failed;
        }
    }

    internal static DefaultWorkspaceResolutionStatus CompleteAdoption(
        string workspaceDirectory,
        IWorkspaceFileOperations fileOperations)
    {
        var directory = fileOperations.ResolvePath(workspaceDirectory);
        var currentPath = Path.Combine(directory, CurrentWorkspaceFileName);
        var statePath = Path.Combine(directory, AdoptionStateFileName);
        try
        {
            var removalStatus = RemoveLegacyNames(workspaceDirectory, fileOperations);
            if (removalStatus != DefaultWorkspaceResolutionStatus.Ready
                || !TryReadAdoptionState(fileOperations, statePath, out var state)
                || state!.Files.Any(file =>
                    !MatchesHash(fileOperations, currentPath + file.Suffix, file.Sha256)))
            {
                return removalStatus == DefaultWorkspaceResolutionStatus.Failed
                    ? DefaultWorkspaceResolutionStatus.Failed
                    : DefaultWorkspaceResolutionStatus.Conflict;
            }

            fileOperations.DeleteFile(statePath);
            return DefaultWorkspaceResolutionStatus.Ready;
        }
        catch (IOException)
        {
            return DefaultWorkspaceResolutionStatus.Failed;
        }
        catch (UnauthorizedAccessException)
        {
            return DefaultWorkspaceResolutionStatus.Failed;
        }
        catch (InvalidDataException)
        {
            return DefaultWorkspaceResolutionStatus.Failed;
        }
    }

    internal static DefaultWorkspaceResolutionStatus RemoveLegacyNames(
        string workspaceDirectory,
        IWorkspaceFileOperations fileOperations)
    {
        var directory = fileOperations.ResolvePath(workspaceDirectory);
        var legacyPath = Path.Combine(directory, LegacyWorkspaceFileName);
        var currentPath = Path.Combine(directory, CurrentWorkspaceFileName);
        var statePath = Path.Combine(directory, AdoptionStateFileName);
        try
        {
            if (!TryReadAdoptionState(fileOperations, statePath, out var state)
                || !MatchesAdoptionState(fileOperations, state!, legacyPath, currentPath)
                || state!.Files.Any(file =>
                    !MatchesHash(fileOperations, currentPath + file.Suffix, file.Sha256)))
            {
                return DefaultWorkspaceResolutionStatus.Conflict;
            }

            if (!DeleteLegacyNames(fileOperations, legacyPath, state))
            {
                return DefaultWorkspaceResolutionStatus.Conflict;
            }
            return MatchesAdoptionState(fileOperations, state, legacyPath, currentPath)
                ? DefaultWorkspaceResolutionStatus.Ready
                : DefaultWorkspaceResolutionStatus.Conflict;
        }
        catch (IOException)
        {
            return DefaultWorkspaceResolutionStatus.Failed;
        }
        catch (UnauthorizedAccessException)
        {
            return DefaultWorkspaceResolutionStatus.Failed;
        }
        catch (InvalidDataException)
        {
            return DefaultWorkspaceResolutionStatus.Failed;
        }
    }

    internal static void PublishAdoptionState(
        IWorkspaceFileOperations fileOperations,
        IIdentifierGenerator identifierGenerator,
        string statePath,
        AdoptionStateDocument state)
    {
        var candidatePath = fileOperations.GetCandidatePath(
            statePath,
            identifierGenerator.NewIdentifier());
        try
        {
            fileOperations.EnsureParentDirectory(statePath);
            fileOperations.WriteAllText(candidatePath, JsonSerializer.Serialize(state));
            fileOperations.Flush(candidatePath);
            fileOperations.Publish(candidatePath, statePath);
        }
        finally
        {
            fileOperations.DeleteCandidate(candidatePath);
        }
    }

    private static AdoptionStateDocument CaptureAdoptionState(
        IWorkspaceFileOperations fileOperations,
        string legacyPath)
    {
        var files = WorkspaceSuffixes
            .Where(suffix => fileOperations.Exists(legacyPath + suffix))
            .Select(suffix => new AdoptionFileDocument(
                suffix,
                fileOperations.ComputeSha256(legacyPath + suffix)))
            .ToArray();
        if (files.Length == 0 || files[0].Suffix.Length != 0)
        {
            throw new InvalidDataException();
        }

        return new(AdoptionStateVersion, files);
    }

    private static bool TryReadAdoptionState(
        IWorkspaceFileOperations fileOperations,
        string statePath,
        out AdoptionStateDocument? state)
    {
        try
        {
            state = JsonSerializer.Deserialize<AdoptionStateDocument>(
                fileOperations.ReadAllText(statePath));
            return IsValidState(state);
        }
        catch (JsonException)
        {
            state = null;
            return false;
        }
    }

    private static bool IsValidState(AdoptionStateDocument? state)
    {
        if (state is not { Version: AdoptionStateVersion, Files: not null }
            || state.Files.Length == 0
            || state.Files.Length > WorkspaceSuffixes.Length)
        {
            return false;
        }

        var suffixes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in state.Files)
        {
            if (file is null
                || file.Suffix is null
                || file.Sha256 is not { Length: 64 }
                || !WorkspaceSuffixes.Contains(file.Suffix, StringComparer.Ordinal)
                || !suffixes.Add(file.Suffix)
                || !file.Sha256.All(Uri.IsHexDigit))
            {
                return false;
            }
        }

        return suffixes.Contains(string.Empty);
    }

    private static bool MatchesAdoptionState(
        IWorkspaceFileOperations fileOperations,
        AdoptionStateDocument state,
        string legacyPath,
        string currentPath)
    {
        var filesBySuffix = state.Files.ToDictionary(file => file.Suffix, StringComparer.Ordinal);
        foreach (var suffix in WorkspaceSuffixes)
        {
            var hasLegacy = fileOperations.Exists(legacyPath + suffix);
            var hasCurrent = fileOperations.Exists(currentPath + suffix);
            if (!filesBySuffix.TryGetValue(suffix, out var expected))
            {
                if (hasLegacy || hasCurrent)
                {
                    return false;
                }

                continue;
            }

            if ((!hasLegacy && !hasCurrent)
                || (hasLegacy
                    && !MatchesHash(fileOperations, legacyPath + suffix, expected.Sha256))
                || (hasCurrent
                    && !MatchesHash(fileOperations, currentPath + suffix, expected.Sha256)))
            {
                return false;
            }
        }

        var preservedRecoveryStatePath = Path.Combine(
            Path.GetDirectoryName(legacyPath)!,
            PreservedRecoveryStateFileName);
        if (!fileOperations.Exists(preservedRecoveryStatePath))
        {
            return true;
        }

        var recoveryState = state.Files.SingleOrDefault(file =>
            string.Equals(file.Suffix, ".recovery-state.json", StringComparison.Ordinal));
        return recoveryState is not null
            && MatchesHash(
                fileOperations,
                preservedRecoveryStatePath,
                recoveryState.Sha256);
    }

    private static bool MatchesHash(
        IWorkspaceFileOperations fileOperations,
        string path,
        string expectedHash) =>
        fileOperations.Exists(path)
        && string.Equals(
            fileOperations.ComputeSha256(path),
            expectedHash,
            StringComparison.Ordinal);

    private static bool DeleteLegacyNames(
        IWorkspaceFileOperations fileOperations,
        string legacyPath,
        AdoptionStateDocument state)
    {
        var main = state.Files.Single(file => file.Suffix.Length == 0);
        if (fileOperations.Exists(legacyPath))
        {
            fileOperations.DeleteFile(legacyPath);
        }

        foreach (var file in state.Files.Where(file =>
                     file.Suffix is "-journal" or "-wal" or "-shm"))
        {
            fileOperations.DeleteFile(legacyPath + file.Suffix);
        }

        var recoveryState = state.Files.SingleOrDefault(file =>
            string.Equals(file.Suffix, ".recovery-state.json", StringComparison.Ordinal));
        var legacyRecoveryStatePath = legacyPath + ".recovery-state.json";
        var preservedRecoveryStatePath = Path.Combine(
            Path.GetDirectoryName(legacyPath)!,
            PreservedRecoveryStateFileName);
        if (fileOperations.Exists(preservedRecoveryStatePath))
        {
            if (recoveryState is null
                || !MatchesHash(
                    fileOperations,
                    preservedRecoveryStatePath,
                    recoveryState.Sha256))
            {
                return false;
            }
        }
        else if (fileOperations.Exists(legacyRecoveryStatePath))
        {
            fileOperations.Publish(legacyRecoveryStatePath, preservedRecoveryStatePath);
            if (recoveryState is null
                || !MatchesHash(
                    fileOperations,
                    preservedRecoveryStatePath,
                    recoveryState.Sha256))
            {
                return false;
            }
        }

        fileOperations.DeleteFile(preservedRecoveryStatePath);
        fileOperations.EnsureDirectory(legacyRecoveryStatePath);
        return true;
    }

    private static CompanionState[] GetCompanionStates(
        IWorkspaceFileOperations fileOperations,
        string legacyPath,
        string currentPath) =>
        CompanionSuffixes
            .Select(suffix => new CompanionState(
                fileOperations.Exists(legacyPath + suffix),
                fileOperations.Exists(currentPath + suffix)))
            .ToArray();

    internal sealed record AdoptionStateDocument(
        int Version,
        AdoptionFileDocument[] Files);

    internal sealed record AdoptionFileDocument(
        string Suffix,
        string Sha256);

    private sealed record CompanionState(
        bool HasLegacy,
        bool HasCurrent);
}
