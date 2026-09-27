namespace DotOrbit.Core.Workspaces;

public interface IWorkspaceStore
{
    bool Exists(string path);

    WorkspaceCreationResult Create(
        string path,
        WorkspacePassphrase passphrase,
        CategoryName firstCategory);

    WorkspaceOpenResult Open(string path, WorkspacePassphrase passphrase);
}

public interface IWorkspaceSession : IDisposable
{
    int SchemaVersion { get; }

    string FirstCategoryName { get; }

    IWorkspaceRecovery Recovery { get; }
}

public interface IWorkspaceRecovery
{
    string? AutomaticRecoveryDirectoryPath { get; }

    RecoveryDirectoryConfigurationResult ConfigureAutomaticRecoveryDirectory(
        string directoryPath);

    RecoveryPointCreationResult CreateRecoveryPoint(string directoryPath);

    WorkspaceRestoreResult Restore(
        string recoveryPointPath,
        string preRestoreRecoveryDirectoryPath);
}

public enum RecoveryDirectoryConfigurationStatus
{
    Configured,
    Failed,
}

public sealed class RecoveryDirectoryConfigurationResult
{
    private RecoveryDirectoryConfigurationResult(
        RecoveryDirectoryConfigurationStatus status,
        string? directoryPath)
    {
        Status = status;
        DirectoryPath = directoryPath;
    }

    public RecoveryDirectoryConfigurationStatus Status { get; }

    public string? DirectoryPath { get; }

    public static RecoveryDirectoryConfigurationResult Configured(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        return new(RecoveryDirectoryConfigurationStatus.Configured, directoryPath);
    }

    public static RecoveryDirectoryConfigurationResult Failed() =>
        new(RecoveryDirectoryConfigurationStatus.Failed, null);
}

public interface IIdentifierGenerator
{
    string NewIdentifier();
}

public sealed class SystemIdentifierGenerator : IIdentifierGenerator
{
    public string NewIdentifier() => Guid.NewGuid().ToString("N");
}

public enum WorkspaceCreationStatus
{
    Created,
    AlreadyExists,
    Failed,
}

public sealed class WorkspaceCreationResult
{
    private WorkspaceCreationResult(
        WorkspaceCreationStatus status,
        IWorkspaceSession? session)
    {
        Status = status;
        Session = session;
    }

    public WorkspaceCreationStatus Status { get; }

    public IWorkspaceSession? Session { get; }

    public static WorkspaceCreationResult Created(IWorkspaceSession session) =>
        new(WorkspaceCreationStatus.Created, session);

    public static WorkspaceCreationResult AlreadyExists() =>
        new(WorkspaceCreationStatus.AlreadyExists, null);

    public static WorkspaceCreationResult Failed() => new(WorkspaceCreationStatus.Failed, null);

}

public enum WorkspaceOpenStatus
{
    Opened,
    InvalidPassphraseOrStore,
    UnsupportedSchema,
    Failed,
}

public sealed class WorkspaceOpenResult
{
    private WorkspaceOpenResult(WorkspaceOpenStatus status, IWorkspaceSession? session)
    {
        Status = status;
        Session = session;
    }

    public WorkspaceOpenStatus Status { get; }

    public IWorkspaceSession? Session { get; }

    public static WorkspaceOpenResult Opened(IWorkspaceSession session) =>
        new(WorkspaceOpenStatus.Opened, session);

    public static WorkspaceOpenResult InvalidPassphraseOrStore() =>
        new(WorkspaceOpenStatus.InvalidPassphraseOrStore, null);

    public static WorkspaceOpenResult UnsupportedSchema() =>
        new(WorkspaceOpenStatus.UnsupportedSchema, null);

    public static WorkspaceOpenResult Failed() => new(WorkspaceOpenStatus.Failed, null);

}

public enum RecoveryPointCreationStatus
{
    Created,
    Failed,
}

public sealed class RecoveryPointCreationResult
{
    private RecoveryPointCreationResult(
        RecoveryPointCreationStatus status,
        string? recoveryPointPath)
    {
        Status = status;
        RecoveryPointPath = recoveryPointPath;
    }

    public RecoveryPointCreationStatus Status { get; }

    public string? RecoveryPointPath { get; }

    public static RecoveryPointCreationResult Created(string recoveryPointPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPointPath);
        return new(RecoveryPointCreationStatus.Created, recoveryPointPath);
    }

    public static RecoveryPointCreationResult Failed() =>
        new(RecoveryPointCreationStatus.Failed, null);
}

public enum WorkspaceRestoreStatus
{
    Restored,
    InvalidRecoveryPoint,
    UnsupportedSchema,
    PreRestoreRecoveryFailed,
    Failed,
}

public sealed class WorkspaceRestoreResult
{
    private WorkspaceRestoreResult(
        WorkspaceRestoreStatus status,
        IWorkspaceSession? session)
    {
        Status = status;
        Session = session;
    }

    public WorkspaceRestoreStatus Status { get; }

    public IWorkspaceSession? Session { get; }

    public static WorkspaceRestoreResult Restored(IWorkspaceSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new(WorkspaceRestoreStatus.Restored, session);
    }

    public static WorkspaceRestoreResult InvalidRecoveryPoint() =>
        new(WorkspaceRestoreStatus.InvalidRecoveryPoint, null);

    public static WorkspaceRestoreResult UnsupportedSchema() =>
        new(WorkspaceRestoreStatus.UnsupportedSchema, null);

    public static WorkspaceRestoreResult PreRestoreRecoveryFailed() =>
        new(WorkspaceRestoreStatus.PreRestoreRecoveryFailed, null);

    public static WorkspaceRestoreResult Failed(IWorkspaceSession? session = null) =>
        new(WorkspaceRestoreStatus.Failed, session);
}
