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

public sealed class WorkspaceCreationResult : IDisposable
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

    public void Dispose() => Session?.Dispose();
}

public enum WorkspaceOpenStatus
{
    Opened,
    InvalidPassphraseOrStore,
    UnsupportedSchema,
    Failed,
}

public sealed class WorkspaceOpenResult : IDisposable
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

    public void Dispose() => Session?.Dispose();
}
