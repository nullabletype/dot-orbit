using DotOrbit.Core.Workspaces;

namespace DotOrbit.Storage.Sqlite;

public sealed class DefaultWorkspaceStore : IWorkspaceStore
{
    private readonly Action? _afterLegacyValidation;
    private readonly string? _defaultWorkspaceDirectory;
    private readonly IWorkspaceStore _inner;
    private readonly IWorkspaceFileOperations _fileOperations;
    private readonly IIdentifierGenerator _identifierGenerator;

    public DefaultWorkspaceStore()
        : this(
            new EncryptedWorkspaceStore(),
            new WorkspaceFileOperations(),
            new SystemIdentifierGenerator(),
            WorkspacePathDefaults.GetDefaultWorkspaceDirectory())
    {
    }

    internal DefaultWorkspaceStore(
        IWorkspaceStore inner,
        IWorkspaceFileOperations fileOperations,
        IIdentifierGenerator identifierGenerator,
        string? defaultWorkspaceDirectory = null,
        Action? afterLegacyValidation = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(fileOperations);
        ArgumentNullException.ThrowIfNull(identifierGenerator);
        _inner = inner;
        _fileOperations = fileOperations;
        _identifierGenerator = identifierGenerator;
        _afterLegacyValidation = afterLegacyValidation;
        _defaultWorkspaceDirectory = defaultWorkspaceDirectory is null
            ? null
            : _fileOperations.ResolvePath(defaultWorkspaceDirectory);
    }

    public bool Exists(string path)
    {
        var resolution = ResolveManagedPath(path);
        return resolution is null
            ? _inner.Exists(path)
            : resolution.Status != DefaultWorkspaceResolutionStatus.Ready
                || _inner.Exists(resolution.WorkspacePath);
    }

    public WorkspaceCreationResult Create(
        string path,
        WorkspacePassphrase passphrase,
        CategoryName firstCategory)
    {
        var resolution = ResolveManagedPath(path);
        if (resolution is not null
            && (resolution.Status != DefaultWorkspaceResolutionStatus.Ready
                || !string.Equals(
                    resolution.WorkspacePath,
                    _fileOperations.ResolvePath(path),
                    StringComparison.Ordinal)))
        {
            return WorkspaceCreationResult.AlreadyExists();
        }

        var created = _inner.Create(path, passphrase, firstCategory);
        if (resolution is null || created.Status != WorkspaceCreationStatus.Created)
        {
            return created;
        }

        var postCreateResolution = ResolveManagedPath(path);
        if (postCreateResolution is { Status: DefaultWorkspaceResolutionStatus.Ready }
            && string.Equals(
                postCreateResolution.WorkspacePath,
                _fileOperations.ResolvePath(path),
                StringComparison.Ordinal))
        {
            return created;
        }

        created.Session?.Dispose();
        return WorkspaceCreationResult.Failed();
    }

    public WorkspaceOpenResult Open(string path, WorkspacePassphrase passphrase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(passphrase);

        var resolution = ResolveManagedPath(path);
        if (resolution is { Status: DefaultWorkspaceResolutionStatus.Conflict })
        {
            return WorkspaceOpenResult.AdoptionConflict();
        }
        if (resolution is { Status: DefaultWorkspaceResolutionStatus.Failed })
        {
            return WorkspaceOpenResult.AdoptionFailed();
        }

        var resolvedPath = resolution?.WorkspacePath ?? _fileOperations.ResolvePath(path);
        var resolvedDirectory = Path.GetDirectoryName(resolvedPath);
        var adoptionInProgress = resolvedDirectory is not null
            && _fileOperations.Exists(Path.Combine(
                resolvedDirectory,
                WorkspacePathDefaults.AdoptionStateFileName));
        if (!string.Equals(
                Path.GetFileName(resolvedPath),
                WorkspacePathDefaults.LegacyWorkspaceFileName,
                StringComparison.Ordinal)
            && !adoptionInProgress)
        {
            return VerifyManagedOpenResult(
                resolvedPath,
                _inner.Open(resolvedPath, passphrase));
        }

        var validated = _inner.Open(resolvedPath, passphrase);
        if (validated.Status != WorkspaceOpenStatus.Opened || validated.Session is null)
        {
            return validated;
        }

        validated.Session.Dispose();
        _afterLegacyValidation?.Invoke();
        var transitionStatus = _inner is EncryptedWorkspaceStore encryptedStore
            ? encryptedStore.ExecuteValidatedAdoption(
                resolvedPath,
                passphrase,
                _ => WorkspacePathDefaults.LinkValidatedWorkspace(
                    resolvedPath,
                    _fileOperations,
                    _identifierGenerator,
                    removeLegacyNames: false),
                OperatingSystem.IsWindows()
                    ? null
                    : () => WorkspacePathDefaults.RemoveLegacyNames(
                        Path.GetDirectoryName(resolvedPath)!,
                        _fileOperations))
            : WorkspacePathDefaults.LinkValidatedWorkspace(
                resolvedPath,
                _fileOperations,
                _identifierGenerator,
                removeLegacyNames: true);
        if (transitionStatus == DefaultWorkspaceResolutionStatus.Conflict)
        {
            return WorkspaceOpenResult.AdoptionConflict();
        }
        if (transitionStatus == DefaultWorkspaceResolutionStatus.Failed)
        {
            return WorkspaceOpenResult.AdoptionFailed();
        }

        var directory = Path.GetDirectoryName(resolvedPath)!;
        var completionStatus = WorkspacePathDefaults.CompleteAdoption(
            directory,
            _fileOperations);
        if (completionStatus == DefaultWorkspaceResolutionStatus.Conflict)
        {
            return WorkspaceOpenResult.AdoptionConflict();
        }
        if (completionStatus == DefaultWorkspaceResolutionStatus.Failed)
        {
            return WorkspaceOpenResult.AdoptionFailed();
        }

        var currentPath = Path.Combine(directory, WorkspacePathDefaults.CurrentWorkspaceFileName);
        var reopened = _inner.Open(currentPath, passphrase);
        return reopened.Status == WorkspaceOpenStatus.Opened && reopened.Session is not null
            ? VerifyManagedOpenResult(currentPath, reopened)
            : WorkspaceOpenResult.AdoptionFailed();
    }

    public MigrationRecoveryRestoreResult RestoreMigrationRecovery(
        string workspacePath,
        WorkspacePassphrase passphrase,
        string recoveryPointPath)
    {
        var resolution = ResolveManagedPath(workspacePath);
        var restored = resolution is { Status: DefaultWorkspaceResolutionStatus.Ready }
            ? _inner.RestoreMigrationRecovery(
                resolution.WorkspacePath,
                passphrase,
                recoveryPointPath)
            : resolution is null
                ? _inner.RestoreMigrationRecovery(workspacePath, passphrase, recoveryPointPath)
                : MigrationRecoveryRestoreResult.Failed();
        if (resolution is null)
        {
            return restored;
        }

        var postRestoreResolution = ResolveManagedPath(workspacePath);
        return postRestoreResolution is { Status: DefaultWorkspaceResolutionStatus.Ready }
            && string.Equals(
                postRestoreResolution.WorkspacePath,
                resolution.WorkspacePath,
                StringComparison.Ordinal)
            ? restored
            : MigrationRecoveryRestoreResult.Failed();
    }

    private WorkspaceOpenResult VerifyManagedOpenResult(
        string currentPath,
        WorkspaceOpenResult result)
    {
        if (result.Status != WorkspaceOpenStatus.Opened || result.Session is null)
        {
            return result;
        }

        var postOpenResolution = ResolveManagedPath(currentPath);
        if (postOpenResolution is { Status: DefaultWorkspaceResolutionStatus.Ready }
            && string.Equals(
                postOpenResolution.WorkspacePath,
                currentPath,
                StringComparison.Ordinal))
        {
            return result;
        }

        result.Session.Dispose();
        return postOpenResolution is { Status: DefaultWorkspaceResolutionStatus.Conflict }
            ? WorkspaceOpenResult.AdoptionConflict()
            : WorkspaceOpenResult.AdoptionFailed();
    }

    private DefaultWorkspaceResolution? ResolveManagedPath(string path)
    {
        var resolvedPath = _fileOperations.ResolvePath(path);
        var fileName = Path.GetFileName(resolvedPath);
        if (!string.Equals(
                fileName,
                WorkspacePathDefaults.CurrentWorkspaceFileName,
                StringComparison.Ordinal)
            && !string.Equals(
                fileName,
                WorkspacePathDefaults.LegacyWorkspaceFileName,
                StringComparison.Ordinal))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(resolvedPath);
        if (string.IsNullOrEmpty(directory)
            || (_defaultWorkspaceDirectory is not null
                && !string.Equals(
                    directory,
                    _defaultWorkspaceDirectory,
                    StringComparison.Ordinal)))
        {
            return null;
        }

        return WorkspacePathDefaults.ResolveDefaultWorkspace(directory, _fileOperations);
    }
}
