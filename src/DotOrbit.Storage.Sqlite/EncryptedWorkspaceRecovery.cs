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

    private const string AutomaticRecoveryPrefix = "dot-orbit-auto-";
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
    // Scheduling only: never evidence that a generation is covered or a file is valid.
    private DateTimeOffset? _nextReconciliationUtc;
    private bool _recoveryDeadlineElapsed;
    private long _scheduleGeneration;
    private readonly EncryptedWorkspaceStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly string _workspacePath;
    private readonly string _recoveryStatePath;
    private SqliteConnection? _sessionReadConnection;

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
        SqliteConnection sessionReadConnection,
        Action closeWorkspace)
    {
        _store = store;
        _workspacePath = workspacePath;
        _passphrase = passphrase;
        _fileOperations = fileOperations;
        _timeProvider = timeProvider;
        _gate = gate;
        _sessionReadConnection = sessionReadConnection;
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

                InvalidateSchedule();
                _recoveryDeadlineElapsed = false;
                _automaticRecoveryDirectoryPath = resolvedPath;
                _recoverySetIdentifier = recoverySetIdentifier;
                _generationReconciliationRequired = !TryReconcileChangeGenerationWithRecoveryPoints();
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
                includePointIdentifier: true);
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
                    InvalidateSchedule();
                    _recoveryDeadlineElapsed = false;
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

            return ScheduleOrCreateAutomaticRecovery(useScheduledDeadline: true);
        }
    }

    private RecoveryPointCreationResult CreateRecoveryPointCore(
        string directoryPath,
        string fileNamePrefix,
        bool includePointIdentifier,
        SqliteConnection? existingSource = null)
    {
        var passphrase = GetPassphrase();
        var directory = _fileOperations.ResolvePath(directoryPath);
        var pointIdentifierSuffix = includePointIdentifier
            ? $"-{_store.GetIdentifier()}"
            : string.Empty;
        var recoveryPointPath = Path.Combine(
            directory,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{fileNamePrefix}{_timeProvider.GetUtcNow():yyyyMMdd'T'HHmmssfffffff'Z'}{pointIdentifierSuffix}{RecoveryPointExtension}"));
        var candidatePath = Path.Combine(
            directory,
            $".{Path.GetFileName(recoveryPointPath)}.creating");

        SqliteConnection? ownedSource = null;
        try
        {
            _fileOperations.EnsureDirectory(directory);
            var source = existingSource ?? _sessionReadConnection;
            if (source is null)
            {
                ownedSource = EncryptedWorkspaceStore.OpenConnection(
                    _workspacePath,
                    passphrase,
                    SqliteOpenMode.ReadOnly);
                EncryptedWorkspaceStore.ConfigureConnection(ownedSource);
                source = ownedSource;
            }
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
            ownedSource?.Dispose();
            _fileOperations.DeleteCandidate(candidatePath);
        }
    }

    internal RecoveryPointCreationResult CreatePassphraseRotationRecoveryPoint()
    {
        lock (_gate)
        {
            ThrowIfClosed();
            var directoryPath = _automaticRecoveryDirectoryPath
                ?? Path.GetDirectoryName(_workspacePath);
            return string.IsNullOrEmpty(directoryPath)
                ? RecoveryPointCreationResult.Failed()
                : CreateRecoveryPointCore(
                    directoryPath,
                    "dot-orbit-pre-passphrase-rotation-",
                    includePointIdentifier: true);
        }
    }

    internal RecoveryPointCreationResult CreateEmptyBinRecoveryPoint(SqliteConnection source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            ThrowIfClosed();
            var directoryPath = _automaticRecoveryDirectoryPath
                ?? Path.GetDirectoryName(_workspacePath);
            return string.IsNullOrEmpty(directoryPath)
                ? RecoveryPointCreationResult.Failed()
                : CreateRecoveryPointCore(
                    directoryPath,
                    "dot-orbit-pre-empty-bin-",
                    includePointIdentifier: true,
                    source);
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
                    includePointIdentifier: true);
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

    internal void CloseScheduling()
    {
        lock (_gate)
        {
            _closed = true;
            InvalidateSchedule();
            _sessionReadConnection = null;
        }
    }

    internal void ClearPassphrase()
    {
        lock (_gate)
        {
            _passphrase = null;
        }
    }

    private AutomaticRecoveryAttempt ScheduleOrCreateAutomaticRecovery(
        bool useScheduledDeadline = false,
        bool deadlineElapsed = false)
    {
        if (_automaticRecoveryDirectoryPath is null)
        {
            return AutomaticRecoveryAttempt.NotConfigured;
        }

        var now = _timeProvider.GetUtcNow();
        deadlineElapsed |= _recoveryDeadlineElapsed;
        if (!deadlineElapsed && useScheduledDeadline && _nextReconciliationUtc is { } scheduledDeadline)
        {
            // A rollback can shorten the deadline, but later commits never extend it.
            scheduledDeadline = scheduledDeadline > now + AutomaticRecoveryCadence
                ? now + AutomaticRecoveryCadence
                : scheduledDeadline;
            if (now < scheduledDeadline)
            {
                return ScheduleRecovery(scheduledDeadline, now);
            }

            deadlineElapsed = true;
        }

        _recoveryDeadlineElapsed = deadlineElapsed;
        InvalidateSchedule();
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

            _recoveryDeadlineElapsed = false;
            _nextReconciliationUtc = (newest.CreatedAtUtc > now ? now : newest.CreatedAtUtc)
                + AutomaticRecoveryCadence;
            return AutomaticRecoveryAttempt.Ignored;
        }

        var dueAtUtc = newest.CreatedAtUtc > now
            ? (_pendingChangeUtc ?? now) + AutomaticRecoveryCadence
            : newest.CreatedAtUtc + AutomaticRecoveryCadence;
        if (deadlineElapsed || now >= dueAtUtc)
        {
            return CreateAutomaticRecoveryPoint();
        }

        return ScheduleRecovery(dueAtUtc, now);
    }

    private AutomaticRecoveryAttempt ScheduleRecovery(DateTimeOffset dueAtUtc, DateTimeOffset now)
    {
        _nextReconciliationUtc = dueAtUtc;
        // Keep an already-running relative timer: clock changes and repeated commits
        // must not restart its wait and postpone an outstanding recovery obligation.
        _scheduledRecovery ??= _timeProvider.CreateTimer(
            static state =>
            {
                var scheduled = (ScheduledAutomaticRecovery)state!;
                scheduled.Recovery.ProcessPendingAutomaticRecovery(scheduled.Generation);
            },
            new ScheduledAutomaticRecovery(this, _scheduleGeneration),
            dueAtUtc - now,
            Timeout.InfiniteTimeSpan);
        return AutomaticRecoveryAttempt.Scheduled;
    }

    private void InvalidateSchedule()
    {
        _scheduleGeneration++;
        _nextReconciliationUtc = null;
        _scheduledRecovery?.Dispose();
        _scheduledRecovery = null;
    }

    private AutomaticRecoveryAttempt CreateAutomaticRecoveryPoint()
    {
        // Failed due work is retried on the next commit, even after clock rollback.
        _recoveryDeadlineElapsed = true;
        InvalidateSchedule();
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
            includePointIdentifier: false);
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
        _recoveryDeadlineElapsed = false;
        _nextReconciliationUtc = _timeProvider.GetUtcNow() + AutomaticRecoveryCadence;
        PruneAutomaticRecoveryPoints(directoryPath, GetPassphrase());
        return AutomaticRecoveryAttempt.Created;
    }

    private void ProcessPendingAutomaticRecovery(long? scheduleGeneration = null)
    {
        lock (_gate)
        {
            // Dispose cannot withdraw a callback that is already queued behind the gate.
            if (scheduleGeneration is { } generation && generation != _scheduleGeneration)
            {
                return;
            }

            _scheduledRecovery?.Dispose();
            _scheduledRecovery = null;
            if (_closed || _pendingChangeUtc is null)
            {
                return;
            }

            ScheduleOrCreateAutomaticRecovery(deadlineElapsed: scheduleGeneration is not null);
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
                var now = _timeProvider.GetUtcNow();
                var newest = validatedPoints.Max(point => point.CreatedAtUtc);
                _nextReconciliationUtc = (newest > now ? now : newest) + AutomaticRecoveryCadence;
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
        createdAtUtc = default;
        changeGeneration = default;
        if (fileName[expectedPrefix.Length + ChangeGenerationLength] == '-'
            && long.TryParse(
                generation,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedGeneration)
            && parsedGeneration > 0
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
            return inspection.Status == EncryptedWorkspaceStore.WorkspaceInspectionStatus.UnsupportedSchema
                ? RecoveryValidation.UnsupportedSchema
                : RecoveryValidation.Valid;
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

    private sealed record ScheduledAutomaticRecovery(EncryptedWorkspaceRecovery Recovery, long Generation);

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
