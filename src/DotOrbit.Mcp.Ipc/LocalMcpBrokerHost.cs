using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;

namespace DotOrbit.Mcp.Ipc;

[Flags]
public enum LocalMcpScopes
{
    None = 0,
    ReadInterchangeContext = 1,
    SubmitProposals = 2,
}

public sealed record LocalMcpClientIdentity(
    string OperatingSystemAccount,
    int? ProcessId,
    string ClientName,
    string ClientVersion,
    string CredentialVerifier,
    LocalMcpScopes RequestedScopes);

public sealed record LocalMcpAuthorizationDecision(
    bool Approved,
    string? AuthorizationId,
    string? Error = null,
    bool RevokeOnAbandon = false);

public interface ILocalMcpAuthorizationHandler
{
    ValueTask<LocalMcpAuthorizationDecision> AuthorizeAsync(
        LocalMcpClientIdentity client,
        CancellationToken cancellationToken);

    bool IsAuthorizationActive(string authorizationId, LocalMcpScopes exactScopes);

    void ReleaseAuthorization(string authorizationId);

    void AbandonAuthorization(string authorizationId);
}

public enum LocalMcpOperation
{
    ReadReusableInstructions,
    ReadPlanningSnapshot,
    SubmitProposal,
}

public sealed record LocalMcpOperationRequest(
    LocalMcpOperation Operation,
    string? IdempotencyKey,
    ReadOnlyMemory<byte> Payload);

public sealed record LocalMcpOperationResult(
    string Status,
    ReadOnlyMemory<byte> Payload,
    string? Receipt = null,
    bool Duplicate = false,
    string? Error = null);

public interface ILocalMcpOperationHandler
{
    ValueTask<LocalMcpOperationResult> HandleAsync(
        string authorizationId,
        LocalMcpOperationRequest request,
        CancellationToken cancellationToken);
}

public sealed class LocalMcpBrokerHost(
    string runtimeRoot,
    ILocalMcpAuthorizationHandler authorization,
    ILocalMcpOperationHandler operations,
    TimeProvider? timeProvider = null,
    TimeSpan? requestLifetime = null) : IAsyncDisposable
{
    private readonly object _startGate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _requestLifetime = requestLifetime ?? LocalMcpProtocol.RequestLifetime;
    private readonly SessionRegistry _sessions = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _run;

    public Task Completion => _run ?? Task.CompletedTask;
    public Task Ready => _ready.Task;

    public void Start()
    {
        lock (_startGate)
        {
            if (_run is not null)
            {
                throw new InvalidOperationException("The local MCP broker is already running.");
            }

            _run = RunAsync(_stop.Token);
        }
    }

    public Task RevokeSessionsAsync() => _sessions.RevokeAsync();

    public void StopAdmission()
    {
        _stop.Cancel();
        _sessions.BeginRevocation();
    }

    public async ValueTask DisposeAsync()
    {
        StopAdmission();
        await _sessions.RevokeAsync().ConfigureAwait(false);
        if (_run is not null)
        {
            try
            {
                await _run.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or InvalidOperationException
                                              or ProtocolException)
            {
            }
        }

        await _sessions.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await using var listener = await LocalEndpoint.CreateBootstrapAsync(runtimeRoot, cancellationToken)
                    .ConfigureAwait(false);
                _ready.TrySetResult();
                await using var connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                await HandleBootstrapAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
            throw;
        }
        finally
        {
            if (!_ready.Task.IsCompleted)
            {
                _ready.TrySetCanceled(cancellationToken);
            }

            await _sessions.RevokeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleBootstrapAsync(LocalConnection connection, CancellationToken cancellationToken)
    {
        LocalEndpointListener? pendingListener = null;
        CapabilityAuthority? pendingAuthority = null;
        byte[]? pendingIssuedToken = null;
        LocalMcpAuthorizationDecision? pendingDecision = null;
        var pendingScopes = LocalMcpScopes.None;
        var reservationHeld = false;
        try
        {
            BootstrapResponse response;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(LocalMcpProtocol.BootstrapHandshakeLifetime);
                var request = await JsonLineCodec.ReadAsync(
                    connection.Stream,
                    LocalMcpJsonContext.Default.BootstrapRequest,
                    deadline.Token).ConfigureAwait(false)
                    ?? throw new ProtocolException("bootstrap-empty");
                var scopes = ValidateBootstrap(request);
                using var consent = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                consent.CancelAfter(LocalMcpProtocol.ConsentLifetime);
                var client = new LocalMcpClientIdentity(
                    connection.PeerIdentity.OperatingSystemAccount,
                    connection.PeerIdentity.ProcessId,
                    request.ClientName,
                    request.ClientVersion,
                    request.CredentialVerifier,
                    scopes);
                var disconnected = MonitorBootstrapDisconnectAsync(connection.Stream, consent.Token);
                var authorizationTask = authorization.AuthorizeAsync(client, consent.Token).AsTask();
                if (await Task.WhenAny(authorizationTask, disconnected).ConfigureAwait(false) == disconnected
                    && await disconnected.ConfigureAwait(false))
                {
                    consent.Cancel();
                    try
                    {
                        var abandoned = await authorizationTask.ConfigureAwait(false);
                        if (abandoned.Approved && !string.IsNullOrWhiteSpace(abandoned.AuthorizationId))
                        {
                            pendingDecision = abandoned;
                        }
                    }
                    catch (OperationCanceledException) when (consent.IsCancellationRequested)
                    {
                    }

                    return;
                }

                var decision = await authorizationTask.ConfigureAwait(false);
                var disconnectedBeforeReply = disconnected.IsCompleted
                    && await disconnected.ConfigureAwait(false);
                consent.Cancel();
                if (!disconnectedBeforeReply)
                {
                    disconnectedBeforeReply = await disconnected.ConfigureAwait(false);
                }
                if (decision.Approved && !string.IsNullOrWhiteSpace(decision.AuthorizationId))
                {
                    pendingDecision = decision;
                }

                if (disconnectedBeforeReply)
                {
                    return;
                }

                if (pendingDecision is null)
                {
                    response = Error(decision.Error ?? "consent-denied");
                }
                else if (!_sessions.TryReserve())
                {
                    response = Error("session-limit");
                }
                else
                {
                    reservationHeld = true;
                    cancellationToken.ThrowIfCancellationRequested();
                    pendingListener = await LocalEndpoint.CreateSessionAsync(runtimeRoot).ConfigureAwait(false);
                    pendingAuthority = new CapabilityAuthority(_timeProvider, new CryptographicRandomBytes());
                    var token = pendingAuthority.Issue(request.Scopes);
                    pendingIssuedToken = token.ToArray();
                    pendingScopes = scopes;
                    response = new(
                        LocalMcpProtocol.Version,
                        "capability",
                        pendingListener.Address,
                        token,
                        null);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                response = Error("consent-timeout");
            }
            catch (ProtocolException exception)
            {
                response = Error(exception.Code);
            }
            catch (Exception exception) when (exception is IOException
                                              or ObjectDisposedException
                                              or UnauthorizedAccessException)
            {
                return;
            }

            try
            {
                await JsonLineCodec.WriteAsync(
                    connection.Stream,
                    response,
                    LocalMcpJsonContext.Default.BootstrapResponse,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                return;
            }
            finally
            {
                if (response.Token is not null)
                {
                    CryptographicOperations.ZeroMemory(response.Token);
                }
            }

            if (pendingListener is not null
                && pendingAuthority is not null
                && pendingIssuedToken is not null
                && pendingDecision?.AuthorizationId is { } authorizationId)
            {
                _sessions.StartReserved(
                    pendingListener,
                    pendingAuthority,
                    pendingIssuedToken,
                    authorizationId,
                    pendingScopes,
                    authorization,
                    operations,
                    _requestLifetime,
                    cancellationToken);
                pendingListener = null;
                pendingAuthority = null;
                pendingIssuedToken = null;
                pendingDecision = null;
                reservationHeld = false;
            }
        }
        finally
        {
            if (pendingIssuedToken is not null)
            {
                CryptographicOperations.ZeroMemory(pendingIssuedToken);
            }

            pendingAuthority?.Dispose();
            if (pendingListener is not null)
            {
                await pendingListener.DisposeAsync().ConfigureAwait(false);
            }

            if (reservationHeld)
            {
                _sessions.ReleaseReservation();
            }

            if (pendingDecision?.AuthorizationId is { } authorizationId)
            {
                authorization.ReleaseAuthorization(authorizationId);
                if (pendingDecision.RevokeOnAbandon)
                {
                    authorization.AbandonAuthorization(authorizationId);
                }
            }
        }
    }

    private static LocalMcpScopes ValidateBootstrap(BootstrapRequest request)
    {
        if (request.Version != LocalMcpProtocol.Version || request.Type != "request-capability")
        {
            throw new ProtocolException("protocol-version-unsupported");
        }

        if (!ValidMetadata(request.ClientName)
            || !ValidMetadata(request.ClientVersion)
            || request.CredentialVerifier is null
            || request.CredentialVerifier.Length != 64
            || request.CredentialVerifier.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f'))
            || request.Scopes is null
            || request.Scopes.Length is < 1 or > 2
            || request.Scopes.Distinct(StringComparer.Ordinal).Count() != request.Scopes.Length
            || request.Scopes.Any(scope => scope is not (LocalMcpProtocol.ReadScope or LocalMcpProtocol.SubmitScope)))
        {
            throw new ProtocolException("bootstrap-invalid");
        }

        var scopes = LocalMcpScopes.None;
        if (request.Scopes.Contains(LocalMcpProtocol.ReadScope, StringComparer.Ordinal))
        {
            scopes |= LocalMcpScopes.ReadInterchangeContext;
        }

        if (request.Scopes.Contains(LocalMcpProtocol.SubmitScope, StringComparer.Ordinal))
        {
            scopes |= LocalMcpScopes.SubmitProposals;
        }

        return scopes;
    }

    private static async Task<bool> MonitorBootstrapDisconnectAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        try
        {
            _ = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static bool ValidMetadata(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= LocalMcpProtocol.MaximumClientMetadataCharacters
        && !value.Any(character => char.IsControl(character)
                                   || char.GetUnicodeCategory(character) is UnicodeCategory.Format
                                       or UnicodeCategory.Surrogate
                                       or UnicodeCategory.PrivateUse
                                       or UnicodeCategory.OtherNotAssigned
                                       or UnicodeCategory.LineSeparator
                                       or UnicodeCategory.ParagraphSeparator);

    private static BootstrapResponse Error(string code) =>
        new(LocalMcpProtocol.Version, "error", null, null, code);

    private sealed class SessionRegistry : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly HashSet<Task> _tasks = [];
        private CancellationTokenSource _revocation = new();
        private int _reserved;

        public bool TryReserve()
        {
            lock (_gate)
            {
                if (_reserved >= LocalMcpProtocol.MaximumSessions)
                {
                    return false;
                }

                _reserved++;
                return true;
            }
        }

        public void ReleaseReservation()
        {
            lock (_gate)
            {
                if (_reserved <= 0)
                {
                    throw new InvalidOperationException("session-reservation-underflow");
                }

                _reserved--;
            }
        }

        public void StartReserved(
            LocalEndpointListener listener,
            CapabilityAuthority capability,
            byte[] issuedToken,
            string authorizationId,
            LocalMcpScopes scopes,
            ILocalMcpAuthorizationHandler authorization,
            ILocalMcpOperationHandler operations,
            TimeSpan requestLifetime,
            CancellationToken hostCancellation)
        {
            lock (_gate)
            {
                var task = RunSessionAsync(
                    listener,
                    capability,
                    issuedToken,
                    authorizationId,
                    scopes,
                    authorization,
                    operations,
                    requestLifetime,
                    hostCancellation,
                    _revocation.Token);
                _tasks.Add(task);
                _ = task.ContinueWith(
                    completed =>
                    {
                        lock (_gate)
                        {
                            _tasks.Remove(completed);
                            _reserved--;
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        public async Task RevokeAsync()
        {
            Task[] tasks;
            CancellationTokenSource prior;
            lock (_gate)
            {
                prior = _revocation;
                _revocation = new CancellationTokenSource();
                tasks = [.. _tasks];
            }

            prior.Cancel();
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException
                                              or IOException
                                              or ObjectDisposedException
                                              or ProtocolException)
            {
            }
            finally
            {
                prior.Dispose();
            }
        }

        public void BeginRevocation()
        {
            lock (_gate)
            {
                _revocation.Cancel();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await RevokeAsync().ConfigureAwait(false);
            _revocation.Dispose();
        }

        private static async Task RunSessionAsync(
            LocalEndpointListener listener,
            CapabilityAuthority capability,
            byte[] issuedToken,
            string authorizationId,
            LocalMcpScopes scopes,
            ILocalMcpAuthorizationHandler authorization,
            ILocalMcpOperationHandler operations,
            TimeSpan requestLifetime,
            CancellationToken hostCancellation,
            CancellationToken revocation)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(hostCancellation, revocation);
            try
            {
                using var bindDeadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                bindDeadline.CancelAfter(LocalMcpProtocol.GrantLifetime);
                await using var connection = await listener.AcceptAsync(bindDeadline.Token).ConfigureAwait(false);
                var bind = await JsonLineCodec.ReadAsync(
                    connection.Stream,
                    LocalMcpJsonContext.Default.SessionMessage,
                    bindDeadline.Token).ConfigureAwait(false);
                var bindToken = bind?.Token;
                var accepted = false;
                try
                {
                    accepted = bind is not null
                               && SessionMessageValidation.IsBind(bind)
                               && bindToken is not null
                               && capability.TryConsume(bindToken) is not null;
                }
                finally
                {
                    if (bindToken is not null)
                    {
                        CryptographicOperations.ZeroMemory(bindToken);
                    }
                }

                if (!accepted)
                {
                    throw new ProtocolException("capability-invalid");
                }

                await JsonLineCodec.WriteAsync(
                    connection.Stream,
                    new SessionMessage(LocalMcpProtocol.Version, "bound"),
                    LocalMcpJsonContext.Default.SessionMessage,
                    linked.Token).ConfigureAwait(false);
                await RunRequestsAsync(
                    connection.Stream,
                    authorizationId,
                    scopes,
                    authorization,
                    operations,
                    requestLifetime,
                    linked.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException
                                              or IOException
                                              or ObjectDisposedException
                                              or ProtocolException)
            {
            }
            finally
            {
                authorization.ReleaseAuthorization(authorizationId);
                CryptographicOperations.ZeroMemory(issuedToken);
                capability.Dispose();
                await listener.DisposeAsync().ConfigureAwait(false);
            }
        }

        private static async Task RunRequestsAsync(
            Stream stream,
            string authorizationId,
            LocalMcpScopes scopes,
            ILocalMcpAuthorizationHandler authorization,
            ILocalMcpOperationHandler operations,
            TimeSpan requestLifetime,
            CancellationToken cancellationToken)
        {
            var requests = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
            var executions = new HashSet<Task>();
            var executionsGate = new object();
            var writer = new SemaphoreSlim(1, 1);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var message = await JsonLineCodec.ReadAsync(
                        stream,
                        LocalMcpJsonContext.Default.SessionMessage,
                        cancellationToken).ConfigureAwait(false);
                    if (message is null)
                    {
                        break;
                    }

                    if (message.Type == "cancel")
                    {
                        if (!SessionMessageValidation.IsCancel(message))
                        {
                            throw new ProtocolException("session-message-invalid");
                        }

                        var cancelledId = message.Id!;
                        if (requests.TryGetValue(cancelledId, out var cancelled))
                        {
                            cancelled.Cancel();
                        }

                        continue;
                    }

                    if (requests.Count >= LocalMcpProtocol.MaximumOutstandingRequests)
                    {
                        throw new ProtocolException("busy");
                    }

                    using var uploadCancellation =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    uploadCancellation.CancelAfter(requestLifetime);
                    using var request = await ChunkedPayloadProtocol.ReadFollowingAsync(
                            stream,
                            message,
                            "request",
                            LocalMcpProtocol.MaximumProposalBytes,
                            uploadCancellation.Token)
                        .ConfigureAwait(false);
                    var requestId = request.Header.Id!;
                    var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    requestCancellation.CancelAfter(requestLifetime);
                    if (!requests.TryAdd(requestId, requestCancellation))
                    {
                        requestCancellation.Dispose();
                        throw new ProtocolException("request-id-duplicate");
                    }

                    var payload = request.Bytes.ToArray();
                    var execution = ExecuteAsync(
                        stream,
                        writer,
                        authorizationId,
                        scopes,
                        authorization,
                        operations,
                        request.Header,
                        payload,
                        requestCancellation,
                        requests);
                    lock (executionsGate)
                    {
                        executions.Add(execution);
                    }

                    _ = execution.ContinueWith(
                        completed =>
                        {
                            lock (executionsGate)
                            {
                                executions.Remove(completed);
                            }
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
            finally
            {
                foreach (var request in requests.Values)
                {
                    request.Cancel();
                }

                Task[] remaining;
                lock (executionsGate)
                {
                    remaining = [.. executions];
                }

                await Task.WhenAll(remaining).ConfigureAwait(false);
                writer.Dispose();
            }
        }

        private static async Task ExecuteAsync(
            Stream stream,
            SemaphoreSlim writer,
            string authorizationId,
            LocalMcpScopes scopes,
            ILocalMcpAuthorizationHandler authorization,
            ILocalMcpOperationHandler operations,
            SessionMessage header,
            byte[] payload,
            CancellationTokenSource requestCancellation,
            ConcurrentDictionary<string, CancellationTokenSource> requests)
        {
            try
            {
                LocalMcpOperationResult result;
                try
                {
                    if (!authorization.IsAuthorizationActive(authorizationId, scopes))
                    {
                        result = new("revoked", ReadOnlyMemory<byte>.Empty, Error: "authorization-revoked");
                    }
                    else if (!TryCreateRequest(header, payload, scopes, out var request, out var error))
                    {
                        result = new("rejected", ReadOnlyMemory<byte>.Empty, Error: error);
                    }
                    else
                    {
                        result = await operations.HandleAsync(
                            authorizationId,
                            request!,
                            requestCancellation.Token).ConfigureAwait(false);
                    }

                    if (result.Payload.Length > LocalMcpProtocol.MaximumResponseBytes)
                    {
                        result = new("rejected", ReadOnlyMemory<byte>.Empty, Error: "response-too-large");
                    }
                }
                catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
                {
                    result = new("cancelled", ReadOnlyMemory<byte>.Empty, Error: "cancelled");
                }
                catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
                {
                    result = new("unavailable", ReadOnlyMemory<byte>.Empty, Error: "workspace-unavailable");
                }

                try
                {
                    await writer.WaitAsync(requestCancellation.Token).ConfigureAwait(false);
                    try
                    {
                        await ChunkedPayloadProtocol.WriteAsync(
                            stream,
                            "response",
                            header.Id!,
                            null,
                            null,
                            result.Error,
                            result.Status,
                            result.Receipt,
                            result.Duplicate,
                            result.Payload,
                            requestCancellation.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        writer.Release();
                    }
                }
                catch (Exception exception) when (exception is IOException
                                                  or ObjectDisposedException
                                                  or OperationCanceledException)
                {
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
                requests.TryRemove(header.Id!, out _);
                requestCancellation.Dispose();
            }
        }

        private static bool TryCreateRequest(
            SessionMessage header,
            byte[] payload,
            LocalMcpScopes scopes,
            out LocalMcpOperationRequest? request,
            out string? error)
        {
            request = null;
            error = null;
            switch (header.Operation)
            {
                case "read.instructions" or "read.snapshot":
                    if (!scopes.HasFlag(LocalMcpScopes.ReadInterchangeContext) || payload.Length != 0)
                    {
                        error = "scope-denied";
                        return false;
                    }

                    request = new(
                        header.Operation == "read.instructions"
                            ? LocalMcpOperation.ReadReusableInstructions
                            : LocalMcpOperation.ReadPlanningSnapshot,
                        null,
                        ReadOnlyMemory<byte>.Empty);
                    return true;
                case "submit.proposal":
                    if (!scopes.HasFlag(LocalMcpScopes.SubmitProposals))
                    {
                        error = "scope-denied";
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(header.IdempotencyKey)
                        || header.IdempotencyKey.Length > LocalMcpProtocol.MaximumIdempotencyKeyCharacters
                        || header.IdempotencyKey.Any(char.IsControl)
                        || payload.Length is <= 0 or > LocalMcpProtocol.MaximumProposalBytes)
                    {
                        error = "submission-invalid";
                        return false;
                    }

                    request = new(LocalMcpOperation.SubmitProposal, header.IdempotencyKey, payload);
                    return true;
                default:
                    error = "operation-unsupported";
                    return false;
            }
        }
    }
}
