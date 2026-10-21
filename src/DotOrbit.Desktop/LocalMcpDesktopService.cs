using System.Collections.Concurrent;
using System.Security.Cryptography;
using DotOrbit.Core.Interchange;
using DotOrbit.Core.Workspaces;
using DotOrbit.Interchange.Json;
using DotOrbit.Mcp.Ipc;

namespace DotOrbit.Desktop;

internal enum LocalMcpServiceStatus
{
    Stopped,
    Running,
    Error,
}

internal enum LocalMcpApprovalChoice
{
    Deny,
    AllowOnce,
    AlwaysAllow,
}

internal sealed class LocalMcpApprovalRequest
{
    private readonly TaskCompletionSource<LocalMcpApprovalChoice> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LocalMcpApprovalRequest(LocalMcpClientIdentity client)
    {
        Client = client;
    }

    public LocalMcpClientIdentity Client { get; }

    public Task<LocalMcpApprovalChoice> Completion => _completion.Task;

    public bool Resolve(LocalMcpApprovalChoice choice) => _completion.TrySetResult(choice);

    public void Cancel(CancellationToken cancellationToken) =>
        _completion.TrySetCanceled(cancellationToken);
}

internal sealed class LocalMcpDesktopService :
    ILocalMcpAuthorizationHandler,
    ILocalMcpOperationHandler,
    IAsyncDisposable
{
    private sealed class ActiveAuthorization(
        string id,
        string clientName,
        string credentialVerifier,
        string operatingSystemAccount,
        AgentClientScopes scopes,
        bool remembered)
    {
        private int _leases = 1;

        public string Id { get; } = id;
        public string ClientName { get; } = clientName;
        public string CredentialVerifier { get; } = credentialVerifier;
        public string OperatingSystemAccount { get; } = operatingSystemAccount;
        public AgentClientScopes Scopes { get; } = scopes;
        public bool Remembered { get; } = remembered;

        public void AddLease() => Interlocked.Increment(ref _leases);

        public bool ReleaseLease() => Interlocked.Decrement(ref _leases) == 0;
    }

    private readonly IPlanningSnapshotSource _planning;
    private readonly PendingProposalCoordinator _proposals;
    private readonly IAgentIntegrationStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly LocalMcpBrokerHost _host;
    private readonly object _lifecycleGate = new();
    private readonly ConcurrentDictionary<string, ActiveAuthorization> _active = new(StringComparer.Ordinal);
    private LocalMcpApprovalRequest? _pendingApproval;
    private LocalMcpServiceStatus _status;
    private int _admissionOpen;
    private bool _admissionClosed;

    public LocalMcpDesktopService(
        IPlanningSnapshotSource planning,
        IAgentIntegrationStore store,
        PendingProposalCoordinator proposals,
        TimeProvider timeProvider,
        string? runtimeRoot = null)
    {
        ArgumentNullException.ThrowIfNull(planning);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _planning = planning;
        _proposals = proposals;
        _store = store;
        _timeProvider = timeProvider;
        _host = new LocalMcpBrokerHost(
            runtimeRoot ?? LocalMcpRuntimePath.GetDefault(),
            this,
            this,
            timeProvider);
    }

    public event EventHandler? StatusChanged;
    public event EventHandler<LocalMcpApprovalRequest>? ApprovalRequested;
    public event EventHandler? AuthorizationsChanged;

    public LocalMcpServiceStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<AgentClientAuthorization> ReadAuthorizations() => _store.ReadAuthorizations();

    public async Task StartAsync()
    {
        lock (_lifecycleGate)
        {
            if (_admissionClosed)
            {
                return;
            }

            Volatile.Write(ref _admissionOpen, 1);
            try
            {
                _host.Start();
            }
            catch
            {
                Volatile.Write(ref _admissionOpen, 0);
                throw;
            }
        }

        try
        {
            await _host.Ready.ConfigureAwait(false);
            lock (_lifecycleGate)
            {
                if (_admissionClosed)
                {
                    return;
                }

                Status = LocalMcpServiceStatus.Running;
            }
        }
        catch (OperationCanceledException) when (_admissionClosed)
        {
            Status = LocalMcpServiceStatus.Stopped;
        }
        catch (Exception)
        {
            Volatile.Write(ref _admissionOpen, 0);
            Status = LocalMcpServiceStatus.Error;
        }

        _ = ObserveCompletionAsync();
    }

    public void CloseAdmission()
    {
        lock (_lifecycleGate)
        {
            _admissionClosed = true;
            Volatile.Write(ref _admissionOpen, 0);
            var approval = Interlocked.Exchange(ref _pendingApproval, null);
            approval?.Cancel(new CancellationToken(canceled: true));
            _active.Clear();
            _host.StopAdmission();
        }
    }

    public async Task<bool> RevokeAsync(string authorizationId)
    {
        Exception? failure = null;
        var removed = false;
        lock (_lifecycleGate)
        {
            try
            {
                removed = _store.RevokeAuthorization(authorizationId);
            }
            catch (Exception exception) when (IsWorkspaceUnavailable(exception))
            {
                failure = exception;
            }
            finally
            {
                _active.TryRemove(authorizationId, out _);
            }
        }

        await _host.RevokeSessionsAsync().ConfigureAwait(false);
        AuthorizationsChanged?.Invoke(this, EventArgs.Empty);
        if (failure is not null)
        {
            throw failure;
        }

        return removed;
    }

    public async ValueTask DisposeAsync()
    {
        CloseAdmission();
        await _host.DisposeAsync().ConfigureAwait(false);
        Status = LocalMcpServiceStatus.Stopped;
    }

    public async ValueTask<LocalMcpAuthorizationDecision> AuthorizeAsync(
        LocalMcpClientIdentity client,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _admissionOpen) == 0)
        {
            return new(false, null, "mcp-disabled");
        }

        var scopes = ToCoreScopes(client.RequestedScopes);
        AgentClientAuthorization? remembered;
        try
        {
            remembered = _store.FindAuthorization(
                client.CredentialVerifier,
                client.OperatingSystemAccount,
                scopes);
        }
        catch (Exception exception) when (IsWorkspaceUnavailable(exception))
        {
            return new(false, null, "workspace-unavailable");
        }
        if (remembered is not null)
        {
            lock (_lifecycleGate)
            {
                if (_admissionClosed || Volatile.Read(ref _admissionOpen) == 0)
                {
                    return new(false, null, "mcp-disabled");
                }

                AgentClientAuthorization? current;
                try
                {
                    current = _store.FindAuthorization(
                        client.CredentialVerifier,
                        client.OperatingSystemAccount,
                        scopes);
                }
                catch (Exception exception) when (IsWorkspaceUnavailable(exception))
                {
                    return new(false, null, "workspace-unavailable");
                }

                if (current?.Id != remembered.Id)
                {
                    return new(false, null, "authorization-revoked");
                }

                _active.AddOrUpdate(
                    remembered.Id,
                    _ => new(
                        remembered.Id,
                        client.ClientName,
                        client.CredentialVerifier,
                        client.OperatingSystemAccount,
                        scopes,
                        remembered: true),
                    (_, active) =>
                    {
                        active.AddLease();
                        return active;
                    });
                return new(true, remembered.Id);
            }
        }

        var approval = new LocalMcpApprovalRequest(client);
        if (Interlocked.CompareExchange(ref _pendingApproval, approval, null) is not null)
        {
            return new(false, null, "approval-pending");
        }

        try
        {
            ApprovalRequested?.Invoke(this, approval);
            var choice = await approval.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (choice == LocalMcpApprovalChoice.Deny)
            {
                return new(false, null, "consent-denied");
            }

            var id = NewOpaqueIdentifier();
            var active = new ActiveAuthorization(
                id,
                client.ClientName,
                client.CredentialVerifier,
                client.OperatingSystemAccount,
                scopes,
                remembered: choice == LocalMcpApprovalChoice.AlwaysAllow);
            var authorizationsChanged = false;
            lock (_lifecycleGate)
            {
                if (_admissionClosed || Volatile.Read(ref _admissionOpen) == 0)
                {
                    return new(false, null, "mcp-disabled");
                }

                if (choice == LocalMcpApprovalChoice.AlwaysAllow)
                {
                    _store.RememberAuthorization(new(
                        id,
                        client.CredentialVerifier,
                        client.OperatingSystemAccount,
                        client.ClientName,
                        client.ClientVersion,
                        scopes,
                        _timeProvider.GetUtcNow()));
                    authorizationsChanged = true;
                }

                _active[id] = active;
            }

            if (authorizationsChanged)
            {
                AuthorizationsChanged?.Invoke(this, EventArgs.Empty);
            }

            return new(
                true,
                id,
                RevokeOnAbandon: choice == LocalMcpApprovalChoice.AlwaysAllow);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref _admissionOpen) == 0)
        {
            return new(false, null, "mcp-disabled");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            approval.Cancel(cancellationToken);
            return new(false, null, "consent-timeout");
        }
        catch (Exception exception) when (IsWorkspaceUnavailable(exception))
        {
            return new(false, null, "workspace-unavailable");
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _pendingApproval, null, approval);
        }
    }

    public bool IsAuthorizationActive(string authorizationId, LocalMcpScopes exactScopes)
    {
        try
        {
            return Volatile.Read(ref _admissionOpen) == 1
                   && _active.TryGetValue(authorizationId, out var active)
                   && active.Scopes == ToCoreScopes(exactScopes)
                   && (!active.Remembered
                       || _store.FindAuthorization(
                           active.CredentialVerifier,
                           active.OperatingSystemAccount,
                           active.Scopes) is { Id: var rememberedId }
                       && rememberedId == active.Id);
        }
        catch (Exception exception) when (IsWorkspaceUnavailable(exception))
        {
            return false;
        }
    }

    public void ReleaseAuthorization(string authorizationId)
    {
        if (_active.TryGetValue(authorizationId, out var active)
            && active.ReleaseLease())
        {
            _active.TryRemove(
                new KeyValuePair<string, ActiveAuthorization>(authorizationId, active));
        }
    }

    public void AbandonAuthorization(string authorizationId)
    {
        var authorizationsChanged = false;
        lock (_lifecycleGate)
        {
            _active.TryRemove(authorizationId, out _);
            try
            {
                authorizationsChanged = _store.RevokeAuthorization(authorizationId);
            }
            catch (Exception exception) when (IsWorkspaceUnavailable(exception))
            {
                Volatile.Write(ref _admissionOpen, 0);
                _admissionClosed = true;
                _host.StopAdmission();
            }
        }

        if (authorizationsChanged)
        {
            AuthorizationsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask<LocalMcpOperationResult> HandleAsync(
        string authorizationId,
        LocalMcpOperationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _admissionOpen) == 0)
        {
            return new("unavailable", ReadOnlyMemory<byte>.Empty, Error: "mcp-disabled");
        }

        try
        {
            return request.Operation switch
            {
                LocalMcpOperation.ReadReusableInstructions => Instructions(),
                LocalMcpOperation.ReadPlanningSnapshot => Snapshot(),
                LocalMcpOperation.SubmitProposal => await SubmitAsync(
                    authorizationId,
                    request,
                    cancellationToken).ConfigureAwait(false),
                _ => new("rejected", ReadOnlyMemory<byte>.Empty, Error: "operation-unsupported"),
            };
        }
        catch (Exception exception) when (IsWorkspaceUnavailable(exception))
        {
            return new("unavailable", ReadOnlyMemory<byte>.Empty, Error: "workspace-unavailable");
        }
    }

    private static LocalMcpOperationResult Instructions()
    {
        var generated = AgentInterchangeDocumentComposer.ComposeReusableInstructionsAndSchemas();
        return Generated(generated);
    }

    private LocalMcpOperationResult Snapshot()
    {
        var snapshot = _planning.Read();
        var generated = PlanningInterchangeCopyComposer.ComposeSnapshot(
            snapshot,
            includeAgentInstructionsAndSchema: false);
        return Generated(generated);
    }

    private async ValueTask<LocalMcpOperationResult> SubmitAsync(
        string authorizationId,
        LocalMcpOperationRequest request,
        CancellationToken cancellationToken)
    {
        if (!_active.TryGetValue(authorizationId, out var active))
        {
            return new("rejected", ReadOnlyMemory<byte>.Empty, Error: "authorization-revoked");
        }

        var submission = new InboundProposalSubmission(
            NewOpaqueIdentifier(),
            request.IdempotencyKey!,
            authorizationId,
            CreateClientIdentity(active.CredentialVerifier, active.OperatingSystemAccount),
            active.ClientName,
            _timeProvider.GetUtcNow(),
            request.Payload);
        var result = await _proposals.TrySubmitAsync(submission, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            InboundProposalSubmissionStatus.Accepted =>
                new("pending-local-review", ReadOnlyMemory<byte>.Empty, result.Receipt),
            InboundProposalSubmissionStatus.Duplicate =>
                new(
                    result.ReviewRequired ? "pending-local-review" : "already-processed",
                    ReadOnlyMemory<byte>.Empty,
                    result.Receipt,
                    Duplicate: true),
            InboundProposalSubmissionStatus.Occupied =>
                new("rejected", ReadOnlyMemory<byte>.Empty, Error: "proposal-queue-full"),
            InboundProposalSubmissionStatus.Invalid =>
                new("rejected", ReadOnlyMemory<byte>.Empty, Error: result.FailureCode ?? "proposal-invalid"),
            InboundProposalSubmissionStatus.Cancelled =>
                new("cancelled", ReadOnlyMemory<byte>.Empty, Error: "cancelled"),
            InboundProposalSubmissionStatus.IdempotencyConflict =>
                new("rejected", ReadOnlyMemory<byte>.Empty, Error: "idempotency-key-conflict"),
            _ => new("unavailable", ReadOnlyMemory<byte>.Empty, Error: "workspace-unavailable"),
        };
    }

    private static LocalMcpOperationResult Generated(InterchangeGenerationResult generated) =>
        generated.Payload is { } payload
            ? new("ok", payload)
            : new("rejected", ReadOnlyMemory<byte>.Empty, Error: generated.ErrorCode ?? "response-unavailable");

    private async Task ObserveCompletionAsync()
    {
        try
        {
            await _host.Completion.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            Status = LocalMcpServiceStatus.Error;
        }
    }

    private static AgentClientScopes ToCoreScopes(LocalMcpScopes scopes)
    {
        var result = AgentClientScopes.None;
        if (scopes.HasFlag(LocalMcpScopes.ReadInterchangeContext))
        {
            result |= AgentClientScopes.ReadInterchangeContext;
        }

        if (scopes.HasFlag(LocalMcpScopes.SubmitProposals))
        {
            result |= AgentClientScopes.SubmitProposals;
        }

        return result;
    }

    private static bool IsWorkspaceUnavailable(Exception exception) => exception is
        WorkspaceWorkException
        or IOException
        or UnauthorizedAccessException
        or InvalidOperationException
        or ObjectDisposedException;

    private static string NewOpaqueIdentifier()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    private static string CreateClientIdentity(string credentialVerifier, string operatingSystemAccount) =>
        Convert.ToHexStringLower(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{credentialVerifier}\n{operatingSystemAccount}")));
}
