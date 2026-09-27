using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotOrbit.Core.Workspaces;
using Microsoft.Data.Sqlite;

namespace DotOrbit.Storage.Sqlite;

internal sealed class EncryptedWorkspaceRecovery : IWorkspaceRecovery
{
    internal const string RecoveryPointExtension = ".dotorbit-recovery";

    private const string AutomaticRecoveryPrefix = "dot-orbit-auto-recovery-";
    private const string ManualRecoveryPrefix = "dot-orbit-recovery-";
    private const string RecoveryTimestampFormat = "yyyyMMdd'T'HHmmssfffffff'Z'";
    private const int RecoveryTimestampLength = 23;
    private const int ChangeGenerationLength = 20;
    private const int HashedIdentifierLength = 32;
    private const int RecoveryStateVersion = 1;
    private static readonly TimeSpan AutomaticRecoveryCadence = TimeSpan.FromHours(1);
    private static readonly SearchValues<char> HexCharacters =
        SearchValues.Create("0123456789ABCDEF");

    private readonly Action _closeWorkspace;
    private readonly IWorkspaceFileOperations _fileOperations;
    private readonly object _gate;
    private WorkspacePassphrase? _passphrase;
    private DateTimeOffset? _pendingChangeUtc;
    private long? _pendingChangeGeneration;
    private ITimer? _scheduledRecovery;
    private readonly EncryptedWorkspaceStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly string _workspacePath;
    private readonly string _recoveryStatePath;

    private string? _automaticRecoveryDirectoryPath;
    private bool _closed;
    private long _changeGeneration;
    private bool _generationReconciliationRequired;
    private string? _recoverySetIdentifier;

    public EncryptedWorkspaceRecovery(
        EncryptedWorkspaceStore store,
        string workspacePath,
        WorkspacePassphrase passphrase,
        IWorkspaceFileOperations fileOperations,
        TimeProvider timeProvider,
        object gate,
        Action closeWorkspace)
    {
        _store = store;
        _workspacePath = workspacePath;
        _passphrase = passphrase;
        _fileOperations = fileOperations;
        _timeProvider = timeProvider;
        _gate = gate;
        _closeWorkspace = closeWorkspace;
        _recoveryStatePath = workspacePath + ".recovery-state.json";
        LoadRecoveryState();
        _generationReconciliationRequired = !TryReconcileChangeGenerationWithRecoveryPoints();
        if (_pendingChangeUtc is not null && _automaticRecoveryDirectoryPath is not null)
        {
            ProcessPendingAutomaticRecovery();
        }
    }

    public string? AutomaticRecoveryDirectoryPath
    {
        get
        {
            lock (_gate)
            {
                return _automaticRecoveryDirectoryPath;
            }
        }
    }

    public RecoveryDirectoryConfigurationResult ConfigureAutomaticRecoveryDirectory(
        string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        lock (_gate)
        {
            ThrowIfClosed();
            var resolvedPath = _fileOperations.ResolvePath(directoryPath);
            try
            {
                _fileOperations.EnsureDirectory(resolvedPath);
                var recoverySetIdentifier =
                    _recoverySetIdentifier ?? HashIdentifier(_store.GetIdentifier());
                if (!PersistRecoveryState(
                        resolvedPath,
                        _pendingChangeUtc,
                        _changeGeneration,
                        _pendingChangeGeneration,
                        recoverySetIdentifier))
                {
                    return RecoveryDirectoryConfigurationResult.Failed();
                }

                _automaticRecoveryDirectoryPath = resolvedPath;
                _recoverySetIdentifier = recoverySetIdentifier;
                if (_pendingChangeUtc is not null)
                {
                    ScheduleOrCreateAutomaticRecovery();
                }

                return RecoveryDirectoryConfigurationResult.Configured(resolvedPath);
            }
            catch (IOException)
            {
                return RecoveryDirectoryConfigurationResult.Failed();
            }
            catch (UnauthorizedAccessException)
            {
                return RecoveryDirectoryConfigurationResult.Failed();
            }
            catch (InvalidDataException)
            {
                return RecoveryDirectoryConfigurationResult.Failed();
            }
        }
    }

    public RecoveryPointCreationResult CreateRecoveryPoint(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        lock (_gate)
        {
            ThrowIfClosed();
            return CreateRecoveryPointCore(
                directoryPath,
                ManualRecoveryPrefix,
                useHashedPointIdentifier: false);
        }
    }

    internal AutomaticRecoveryAttempt StoredDataChangeCompleted(
        StoredDataChangeOutcome outcome)
    {
        lock (_gate)
        {
            ThrowIfClosed();
            if (outcome != StoredDataChangeOutcome.Committed)
            {
                return AutomaticRecoveryAttempt.Ignored;
            }

            if (_generationReconciliationRequired)
            {
                if (!TryReconcileChangeGenerationWithRecoveryPoints())
                {
                    _recoverySetIdentifier = HashIdentifier(_store.GetIdentifier());
                    _changeGeneration = 0;
                }

                _generationReconciliationRequired = false;
            }

            var pendingChangeUtc = _timeProvider.GetUtcNow();
            var pendingChangeGeneration = checked(_changeGeneration + 1);
            _changeGeneration = pendingChangeGeneration;
            _pendingChangeUtc = pendingChangeUtc;
            _pendingChangeGeneration = pendingChangeGeneration;
            if (!PersistRecoveryState(
                    _automaticRecoveryDirectoryPath,
                    pendingChangeUtc,
                    _changeGeneration,
                    pendingChangeGeneration,
                    _recoverySetIdentifier))
            {
                return _automaticRecoveryDirectoryPath is null
                    ? AutomaticRecoveryAttempt.NotConfigured
                    : CreateAutomaticRecoveryPoint();
            }

            return ScheduleOrCreateAutomaticRecovery();
        }
    }

    private RecoveryPointCreationResult CreateRecoveryPointCore(
        string directoryPath,
        string fileNamePrefix,
        bool useHashedPointIdentifier)
    {
        var passphrase = GetPassphrase();
        var directory = _fileOperations.ResolvePath(directoryPath);
        var pointIdentifier = useHashedPointIdentifier
            ? HashIdentifier(_store.GetIdentifier())
            : _store.GetIdentifier();
        var recoveryPointPath = Path.Combine(
            directory,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{fileNamePrefix}{_timeProvider.GetUtcNow():yyyyMMdd'T'HHmmssfffffff'Z'}-{pointIdentifier}{RecoveryPointExtension}"));
        var candidatePath = Path.Combine(
            directory,
            $".{Path.GetFileName(recoveryPointPath)}.creating");

        try
        {
            _fileOperations.EnsureDirectory(directory);
            using var source = EncryptedWorkspaceStore.OpenConnection(
                _workspacePath,
                passphrase,
                SqliteOpenMode.ReadOnly);
            EncryptedWorkspaceStore.ConfigureConnection(source);
            var sourceInspection = EncryptedWorkspaceStore.InspectWorkspace(source);
            if (sourceInspection.Status != EncryptedWorkspaceStore.WorkspaceInspectionStatus.Valid)
            {
                return RecoveryPointCreationResult.Failed();
            }

            using (var candidate = EncryptedWorkspaceStore.OpenConnection(
                       candidatePath,
                       passphrase,
                       SqliteOpenMode.ReadWriteCreate))
            {
                EncryptedWorkspaceStore.ConfigureConnection(candidate);
                EncryptedWorkspaceStore.AssertEncryptionProfile(candidate);
                source.BackupDatabase(candidate);
                EncryptedWorkspaceStore.ValidateIntegrity(candidate);
                EncryptedWorkspaceStore.ValidateWorkspaceShape(candidate);
            }

            _fileOperations.Flush(candidatePath);
            if (Validate(candidatePath, passphrase) != RecoveryValidation.Valid)
            {
                return RecoveryPointCreationResult.Failed();
            }

            _fileOperations.Publish(candidatePath, recoveryPointPath);
            return RecoveryPointCreationResult.Created(recoveryPointPath);
        }
        catch (SqliteException)
        {
            return RecoveryPointCreationResult.Failed();
        }
        catch (IOException)
        {
            return RecoveryPointCreationResult.Failed();
        }
        catch (UnauthorizedAccessException)
        {
            return RecoveryPointCreationResult.Failed();
        }
        catch (InvalidDataException)
        {
            return RecoveryPointCreationResult.Failed();
        }
        finally
        {
            _fileOperations.DeleteCandidate(candidatePath);
        }
    }

    public WorkspaceRestoreResult Restore(
        string recoveryPointPath,
        string preRestoreRecoveryDirectoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryPointPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(preRestoreRecoveryDirectoryPath);

        lock (_gate)
        {
            ThrowIfClosed();
            var sourcePath = _fileOperations.ResolvePath(recoveryPointPath);
            var passphrase = GetPassphrase();
            var identifier = _store.GetIdentifier();
            var restoreCandidatePath = _fileOperations.GetCandidatePath(
                _workspacePath,
                $"restore-{identifier}");
            try
            {
                _fileOperations.Copy(sourcePath, restoreCandidatePath);
                _fileOperations.Flush(restoreCandidatePath);

                var validation = Validate(restoreCandidatePath, passphrase);
                if (validation == RecoveryValidation.UnsupportedSchema)
                {
                    return WorkspaceRestoreResult.UnsupportedSchema();
                }

                if (validation != RecoveryValidation.Valid)
                {
                    return WorkspaceRestoreResult.InvalidRecoveryPoint();
                }

                var preRestoreRecovery = CreateRecoveryPointCore(
                    preRestoreRecoveryDirectoryPath,
                    ManualRecoveryPrefix,
                    useHashedPointIdentifier: false);
                if (preRestoreRecovery.Status != RecoveryPointCreationStatus.Created)
                {
                    return WorkspaceRestoreResult.PreRestoreRecoveryFailed();
                }

                _closeWorkspace();
                try
                {
                    _fileOperations.Replace(restoreCandidatePath, _workspacePath);
                }
                catch (IOException)
                {
                    return ResolveReplacementInterruption(
                        restoreCandidatePath,
                        passphrase);
                }
                catch (UnauthorizedAccessException)
                {
                    return ResolveReplacementInterruption(
                        restoreCandidatePath,
                        passphrase);
                }

                var restored = _store.OpenExistingWorkspace(_workspacePath, passphrase);
                if (restored.Status == WorkspaceOpenStatus.Opened && restored.Session is not null)
                {
                    return WorkspaceRestoreResult.Restored(restored.Session);
                }

                return WorkspaceRestoreResult.Failed();
            }
            catch (SqliteException)
            {
                return WorkspaceRestoreResult.InvalidRecoveryPoint();
            }
            catch (InvalidDataException)
            {
                return WorkspaceRestoreResult.InvalidRecoveryPoint();
            }
            catch (IOException)
            {
                return WorkspaceRestoreResult.Failed();
            }
            catch (UnauthorizedAccessException)
            {
                return WorkspaceRestoreResult.Failed();
            }
            finally
            {
                _fileOperations.DeleteCandidate(restoreCandidatePath);
            }
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _scheduledRecovery?.Dispose();
            _scheduledRecovery = null;
            _passphrase = null;
        }
    }

    private AutomaticRecoveryAttempt ScheduleOrCreateAutomaticRecovery()
    {
        if (_automaticRecoveryDirectoryPath is null)
        {
            return AutomaticRecoveryAttempt.NotConfigured;
        }

        var now = _timeProvider.GetUtcNow();
        List<AutomaticRecoveryPoint> validatedPoints;
        try
        {
            validatedPoints = GetValidatedAutomaticRecoveryPoints(
                _automaticRecoveryDirectoryPath,
                GetPassphrase());
        }
        catch (IOException)
        {
            return AutomaticRecoveryAttempt.Failed;
        }
        catch (UnauthorizedAccessException)
        {
            return AutomaticRecoveryAttempt.Failed;
        }
        if (validatedPoints.Count == 0)
        {
            return CreateAutomaticRecoveryPoint();
        }

        var newest = validatedPoints.MaxBy(point => point.CreatedAtUtc);
        if (_pendingChangeGeneration is { } pendingChangeGeneration
            && validatedPoints.Any(
                point => point.ChangeGeneration >= pendingChangeGeneration))
        {
            if (PersistRecoveryState(
                    _automaticRecoveryDirectoryPath,
                    pendingChangeUtc: null,
                    _changeGeneration,
                    pendingChangeGeneration: null,
                    _recoverySetIdentifier))
            {
                _pendingChangeUtc = null;
                _pendingChangeGeneration = null;
            }

            return AutomaticRecoveryAttempt.Ignored;
        }

        var dueAtUtc = newest.CreatedAtUtc > now
            ? (_pendingChangeUtc ?? now) + AutomaticRecoveryCadence
            : newest.CreatedAtUtc + AutomaticRecoveryCadence;
        if (now >= dueAtUtc)
        {
            return CreateAutomaticRecoveryPoint();
        }

        var dueIn = dueAtUtc - now;
        _scheduledRecovery?.Dispose();
        _scheduledRecovery = _timeProvider.CreateTimer(
            static state => ((EncryptedWorkspaceRecovery)state!).ProcessPendingAutomaticRecovery(),
            this,
            dueIn,
            Timeout.InfiniteTimeSpan);
        return AutomaticRecoveryAttempt.Scheduled;
    }

    private AutomaticRecoveryAttempt CreateAutomaticRecoveryPoint()
    {
        var directoryPath = _automaticRecoveryDirectoryPath;
        if (directoryPath is null)
        {
            return AutomaticRecoveryAttempt.NotConfigured;
        }

        var recoverySetIdentifier = _recoverySetIdentifier;
        var pendingChangeGeneration = _pendingChangeGeneration;
        if (recoverySetIdentifier is null || pendingChangeGeneration is null)
        {
            return AutomaticRecoveryAttempt.Failed;
        }

        var creation = CreateRecoveryPointCore(
            directoryPath,
            $"{AutomaticRecoveryPrefix}{recoverySetIdentifier}-{pendingChangeGeneration.Value:D20}-",
            useHashedPointIdentifier: true);
        if (creation.Status != RecoveryPointCreationStatus.Created)
        {
            return AutomaticRecoveryAttempt.Failed;
        }

        if (PersistRecoveryState(
                directoryPath,
                pendingChangeUtc: null,
                _changeGeneration,
                pendingChangeGeneration: null,
                recoverySetIdentifier))
        {
            _pendingChangeUtc = null;
            _pendingChangeGeneration = null;
        }
        PruneAutomaticRecoveryPoints(directoryPath, GetPassphrase());
        return AutomaticRecoveryAttempt.Created;
    }

    private void ProcessPendingAutomaticRecovery()
    {
        lock (_gate)
        {
            _scheduledRecovery?.Dispose();
            _scheduledRecovery = null;
            if (_closed || _pendingChangeUtc is null)
            {
                return;
            }

            ScheduleOrCreateAutomaticRecovery();
        }
    }

    private List<AutomaticRecoveryPoint> GetValidatedAutomaticRecoveryPoints(
        string directoryPath,
        WorkspacePassphrase passphrase)
    {
        var points = new List<AutomaticRecoveryPoint>();
        var recoverySetIdentifier = _recoverySetIdentifier;
        if (recoverySetIdentifier is null)
        {
            return points;
        }

        foreach (var path in _fileOperations.EnumerateFiles(
                     directoryPath,
                     $"{AutomaticRecoveryPrefix}{recoverySetIdentifier}-*{RecoveryPointExtension}"))
        {
            if (TryParseAutomaticRecoveryTimestamp(
                    path,
                    recoverySetIdentifier,
                    out var createdAtUtc,
                    out var changeGeneration)
                && Validate(path, passphrase) == RecoveryValidation.Valid)
            {
                points.Add(new AutomaticRecoveryPoint(
                    path,
                    createdAtUtc,
                    changeGeneration));
            }
        }

        return points;
    }

    private void PruneAutomaticRecoveryPoints(
        string directoryPath,
        WorkspacePassphrase passphrase)
    {
        IReadOnlyList<AutomaticRecoveryPoint> recoveryPoints;
        try
        {
            recoveryPoints = GetValidatedAutomaticRecoveryPoints(directoryPath, passphrase);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var point in AutomaticRecoveryRetentionPolicy.SelectForDeletion(recoveryPoints))
        {
            try
            {
                _fileOperations.DeleteFile(point.Path);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private void LoadRecoveryState()
    {
        try
        {
            if (!_fileOperations.Exists(_recoveryStatePath))
            {
                return;
            }

            var state = JsonSerializer.Deserialize<RecoveryStateDocument>(
                _fileOperations.ReadAllText(_recoveryStatePath));
            if (state is null || state.Version != RecoveryStateVersion)
            {
                return;
            }

            _automaticRecoveryDirectoryPath = string.IsNullOrWhiteSpace(state.DirectoryPath)
                ? null
                : _fileOperations.ResolvePath(state.DirectoryPath);
            var pendingChangeUtc = state.PendingChangeUtc?.ToUniversalTime();
            var now = _timeProvider.GetUtcNow();
            _changeGeneration = Math.Max(0, state.ChangeGeneration);
            if (pendingChangeUtc is not null
                && state.PendingChangeGeneration is > 0
                && state.PendingChangeGeneration <= _changeGeneration)
            {
                _pendingChangeUtc = pendingChangeUtc > now ? now : pendingChangeUtc;
                _pendingChangeGeneration = state.PendingChangeGeneration;
            }
            _recoverySetIdentifier = IsHashedIdentifier(state.RecoverySetIdentifier)
                ? state.RecoverySetIdentifier
                : null;
        }
        catch (JsonException)
        {
            // Invalid machine-local recovery state must not prevent workspace unlock.
        }
        catch (IOException)
        {
            // Unavailable machine-local recovery state must not prevent workspace unlock.
        }
        catch (UnauthorizedAccessException)
        {
            // Unavailable machine-local recovery state must not prevent workspace unlock.
        }
        catch (InvalidDataException)
        {
            // Invalid machine-local recovery state must not prevent workspace unlock.
        }
        catch (ArgumentException)
        {
            // Invalid machine-local recovery state must not prevent workspace unlock.
        }
    }

    private bool TryReconcileChangeGenerationWithRecoveryPoints()
    {
        if (_automaticRecoveryDirectoryPath is null || _recoverySetIdentifier is null)
        {
            return true;
        }

        try
        {
            var validatedPoints = GetValidatedAutomaticRecoveryPoints(
                _automaticRecoveryDirectoryPath,
                GetPassphrase());
            if (validatedPoints.Count > 0)
            {
                _changeGeneration = Math.Max(
                    _changeGeneration,
                    validatedPoints.Max(point => point.ChangeGeneration));
            }

            return true;
        }
        catch (IOException)
        {
            // Recovery-directory availability must not prevent workspace unlock.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // Recovery-directory availability must not prevent workspace unlock.
            return false;
        }
    }

    private bool PersistRecoveryState(
        string? directoryPath,
        DateTimeOffset? pendingChangeUtc,
        long changeGeneration,
        long? pendingChangeGeneration,
        string? recoverySetIdentifier)
    {
        var candidatePath = string.Empty;
        try
        {
            candidatePath = _fileOperations.GetCandidatePath(
                _recoveryStatePath,
                _store.GetIdentifier());
            _fileOperations.EnsureParentDirectory(_recoveryStatePath);
            var contents = JsonSerializer.Serialize(
                new RecoveryStateDocument(
                    RecoveryStateVersion,
                    directoryPath,
                    pendingChangeUtc?.ToUniversalTime(),
                    changeGeneration,
                    pendingChangeGeneration,
                    recoverySetIdentifier));
            _fileOperations.WriteAllText(candidatePath, contents);
            _fileOperations.Flush(candidatePath);
            _fileOperations.PublishOrReplace(candidatePath, _recoveryStatePath);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        finally
        {
            if (!string.IsNullOrEmpty(candidatePath))
            {
                _fileOperations.DeleteCandidate(candidatePath);
            }
        }
    }

    private static bool TryParseAutomaticRecoveryTimestamp(
        string path,
        string recoverySetIdentifier,
        out DateTimeOffset createdAtUtc,
        out long changeGeneration)
    {
        var fileName = Path.GetFileName(path);
        var expectedPrefix = $"{AutomaticRecoveryPrefix}{recoverySetIdentifier}-";
        var expectedLength = expectedPrefix.Length
            + ChangeGenerationLength
            + 1
            + RecoveryTimestampLength
            + 1
            + HashedIdentifierLength
            + RecoveryPointExtension.Length;
        if (fileName.Length != expectedLength
            || !fileName.StartsWith(expectedPrefix, StringComparison.Ordinal)
            || !fileName.EndsWith(RecoveryPointExtension, StringComparison.Ordinal))
        {
            createdAtUtc = default;
            changeGeneration = default;
            return false;
        }

        var generation = fileName.AsSpan(
            expectedPrefix.Length,
            ChangeGenerationLength);
        var timestampStart = expectedPrefix.Length + ChangeGenerationLength + 1;
        var timestamp = fileName.AsSpan(
            timestampStart,
            RecoveryTimestampLength);
        var pointIdentifier = fileName.AsSpan(
            timestampStart + RecoveryTimestampLength + 1,
            HashedIdentifierLength);
        createdAtUtc = default;
        changeGeneration = default;
        if (fileName[expectedPrefix.Length + ChangeGenerationLength] == '-'
            && fileName[timestampStart + RecoveryTimestampLength] == '-'
            && long.TryParse(
                generation,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedGeneration)
            && parsedGeneration > 0
            && IsHashedIdentifier(pointIdentifier)
            && DateTimeOffset.TryParseExact(
                timestamp,
                RecoveryTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            createdAtUtc = parsed;
            changeGeneration = parsedGeneration;
            return true;
        }

        return false;
    }

    private static string HashIdentifier(string identifier)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identifier));
        return Convert.ToHexString(hash.AsSpan(0, HashedIdentifierLength / 2));
    }

    private static bool IsHashedIdentifier(string? identifier) =>
        identifier is not null && IsHashedIdentifier(identifier.AsSpan());

    private static bool IsHashedIdentifier(ReadOnlySpan<char> identifier) =>
        identifier.Length == HashedIdentifierLength
        && identifier.IndexOfAnyExcept(HexCharacters) < 0;

    private void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(_closed, typeof(IWorkspaceSession));
    }

    private WorkspacePassphrase GetPassphrase() =>
        _passphrase ?? throw new ObjectDisposedException(nameof(IWorkspaceSession));

    private IWorkspaceSession? ReopenWorkspace(
        string path,
        WorkspacePassphrase passphrase)
    {
        var opened = _store.Open(path, passphrase);
        return opened.Status == WorkspaceOpenStatus.Opened ? opened.Session : null;
    }

    private WorkspaceRestoreResult ResolveReplacementInterruption(
        string restoreCandidatePath,
        WorkspacePassphrase passphrase)
    {
        var reopened = ReopenWorkspace(_workspacePath, passphrase);
        if (!_fileOperations.Exists(restoreCandidatePath) && reopened is not null)
        {
            return WorkspaceRestoreResult.Restored(reopened);
        }

        return WorkspaceRestoreResult.Failed(reopened);
    }

    private static RecoveryValidation Validate(
        string path,
        WorkspacePassphrase passphrase)
    {
        try
        {
            using var connection = EncryptedWorkspaceStore.OpenConnection(
                path,
                passphrase,
                SqliteOpenMode.ReadOnly);
            EncryptedWorkspaceStore.ConfigureConnection(connection);
            var inspection = EncryptedWorkspaceStore.InspectWorkspace(connection);
            return inspection.Status == EncryptedWorkspaceStore.WorkspaceInspectionStatus.Valid
                ? RecoveryValidation.Valid
                : RecoveryValidation.UnsupportedSchema;
        }
        catch (SqliteException)
        {
            return RecoveryValidation.Invalid;
        }
        catch (InvalidDataException)
        {
            return RecoveryValidation.Invalid;
        }
        catch (IOException)
        {
            return RecoveryValidation.Invalid;
        }
    }

    private enum RecoveryValidation
    {
        Valid,
        Invalid,
        UnsupportedSchema,
    }

    private sealed record RecoveryStateDocument(
        int Version,
        string? DirectoryPath,
        DateTimeOffset? PendingChangeUtc,
        long ChangeGeneration,
        long? PendingChangeGeneration,
        string? RecoverySetIdentifier);
}

internal enum StoredDataChangeOutcome
{
    Committed,
    Failed,
}

internal enum AutomaticRecoveryAttempt
{
    Ignored,
    NotConfigured,
    Scheduled,
    Created,
    Failed,
}
