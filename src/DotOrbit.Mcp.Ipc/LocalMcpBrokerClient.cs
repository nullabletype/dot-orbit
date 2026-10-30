using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DotOrbit.Mcp.Ipc;

public sealed class LocalMcpBrokerException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record LocalMcpBrokerResponse(
    string Status,
    ReadOnlyMemory<byte> Payload,
    string? Receipt = null,
    bool Duplicate = false);

public interface ILocalMcpBrokerClient : IAsyncDisposable
{
    Task<LocalMcpBrokerResponse> ReadReusableInstructionsAsync(CancellationToken cancellationToken = default);
    Task<LocalMcpBrokerResponse> ReadPlanningSnapshotAsync(CancellationToken cancellationToken = default);
    Task<LocalMcpBrokerResponse> ReadReusableInstructionsAsync(
        int schemaVersion,
        CancellationToken cancellationToken = default) =>
        schemaVersion == 1
            ? ReadReusableInstructionsAsync(cancellationToken)
            : throw new LocalMcpBrokerException("schema-version-unsupported");

    Task<LocalMcpBrokerResponse> ReadPlanningSnapshotAsync(
        int schemaVersion,
        CancellationToken cancellationToken = default) =>
        schemaVersion == 1
            ? ReadPlanningSnapshotAsync(cancellationToken)
            : throw new LocalMcpBrokerException("schema-version-unsupported");

    Task<LocalMcpBrokerResponse> SubmitProposalAsync(
        string idempotencyKey,
        ReadOnlyMemory<byte> strictUtf8,
        CancellationToken cancellationToken = default);
}

public sealed class LocalMcpBrokerClient : ILocalMcpBrokerClient
{
    private readonly string _runtimeRoot;
    private readonly string _clientName;
    private readonly string _clientVersion;
    private readonly string _credentialVerifier;
    private readonly string[] _scopes;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _admission = new(LocalMcpProtocol.MaximumOutstandingRequests);
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<LocalMcpBrokerResponse>> _pending =
        new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private LocalConnection? _connection;
    private Task? _reader;
    private long _requestSequence;

    public LocalMcpBrokerClient(
        string runtimeRoot,
        string clientName,
        string clientVersion,
        string credentialVerifier,
        IReadOnlyCollection<string> scopes,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialVerifier);
        ArgumentNullException.ThrowIfNull(scopes);
        _runtimeRoot = runtimeRoot;
        _clientName = clientName;
        _clientVersion = clientVersion;
        _credentialVerifier = credentialVerifier;
        _scopes = [.. scopes];
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<LocalMcpBrokerResponse> ReadReusableInstructionsAsync(CancellationToken cancellationToken = default) =>
        SendAsync("read.instructions", null, ReadOnlyMemory<byte>.Empty, cancellationToken);

    public Task<LocalMcpBrokerResponse> ReadPlanningSnapshotAsync(CancellationToken cancellationToken = default) =>
        SendAsync("read.snapshot", null, ReadOnlyMemory<byte>.Empty, cancellationToken);

    public Task<LocalMcpBrokerResponse> ReadReusableInstructionsAsync(
        int schemaVersion,
        CancellationToken cancellationToken = default) =>
        SendAsync(VersionedReadOperation("instructions", schemaVersion), null, ReadOnlyMemory<byte>.Empty, cancellationToken);

    public Task<LocalMcpBrokerResponse> ReadPlanningSnapshotAsync(
        int schemaVersion,
        CancellationToken cancellationToken = default) =>
        SendAsync(VersionedReadOperation("snapshot", schemaVersion), null, ReadOnlyMemory<byte>.Empty, cancellationToken);

    private static string VersionedReadOperation(string resource, int schemaVersion) =>
        schemaVersion switch
        {
            1 => $"read.{resource}.v1",
            2 => $"read.{resource}.v2",
            _ => throw new LocalMcpBrokerException("schema-version-unsupported"),
        };

    public Task<LocalMcpBrokerResponse> SubmitProposalAsync(
        string idempotencyKey,
        ReadOnlyMemory<byte> strictUtf8,
        CancellationToken cancellationToken = default) =>
        SendAsync("submit.proposal", idempotencyKey, strictUtf8, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        if (_reader is not null)
        {
            try
            {
                await _reader.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException)
            {
            }
        }

        FailPending("broker-closed");
        _admission.Dispose();
        _writer.Dispose();
        _connectGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task<LocalMcpBrokerResponse> SendAsync(
        string operation,
        string? idempotencyKey,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (!await _admission.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new LocalMcpBrokerException("busy");
        }

        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var id = Interlocked.Increment(ref _requestSequence)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            var completion = new TaskCompletionSource<LocalMcpBrokerResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, completion))
            {
                throw new LocalMcpBrokerException("request-id-collision");
            }

            try
            {
                await WritePayloadAsync(id, operation, idempotencyKey, payload, cancellationToken)
                    .ConfigureAwait(false);
                using var registration = cancellationToken.Register(
                    static state =>
                    {
                        var (client, requestId) = ((LocalMcpBrokerClient, string))state!;
                        _ = client.CancelAsync(requestId);
                    },
                    (this, id));
                return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }
        finally
        {
            _admission.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return;
        }

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                return;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            deadline.CancelAfter(LocalMcpProtocol.ConsentLifetime + LocalMcpProtocol.BootstrapHandshakeLifetime);
            var response = await RequestCapabilityAsync(deadline.Token).ConfigureAwait(false);
            if (response.Version != LocalMcpProtocol.Version)
            {
                throw new LocalMcpBrokerException("protocol-version-unsupported");
            }

            if (!SessionMessageValidation.IsCapability(response))
            {
                throw new LocalMcpBrokerException(
                    SessionMessageValidation.IsError(response)
                        ? response.Error!
                        : "bootstrap-invalid");
            }

            var session = await ConnectSessionAsync(response.Endpoint!, response.Token!, deadline.Token)
                .ConfigureAwait(false);
            _connection = session;
            _reader = ReadResponsesAsync(session, _lifetime.Token);
        }
        catch (LocalMcpBrokerException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                          or SocketException
                                          or TimeoutException
                                          or ProtocolException)
        {
            throw new LocalMcpBrokerException("broker-unavailable");
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task<BootstrapResponse> RequestCapabilityAsync(CancellationToken cancellationToken)
    {
        const int maximumAttempts = 2;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            await using var connection = await ConnectBootstrapAsync(cancellationToken).ConfigureAwait(false);
            await JsonLineCodec.WriteAsync(
                connection.Stream,
                new BootstrapRequest(
                    LocalMcpProtocol.Version,
                    "request-capability",
                    _clientName,
                    _clientVersion,
                    _credentialVerifier,
                    _scopes),
                LocalMcpJsonContext.Default.BootstrapRequest,
                cancellationToken).ConfigureAwait(false);
            var response = await JsonLineCodec.ReadAsync(
                connection.Stream,
                LocalMcpJsonContext.Default.BootstrapResponse,
                cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                return response;
            }

            if (attempt < maximumAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new LocalMcpBrokerException("bootstrap-empty");
    }

    private async Task<LocalConnection> ConnectBootstrapAsync(CancellationToken cancellationToken)
    {
        const int maximumAttempts = 50;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var bootstrap = await LocalEndpoint.DiscoverBootstrapAsync(_runtimeRoot, cancellationToken)
                    .ConfigureAwait(false);
                return await LocalEndpoint.ConnectAsync(bootstrap, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < maximumAttempts - 1
                                              && exception is FileNotFoundException
                                                  or DirectoryNotFoundException
                                                  or IOException
                                                  or SocketException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task<LocalConnection> ConnectSessionAsync(
        EndpointAddress endpoint,
        byte[] capability,
        CancellationToken cancellationToken)
    {
        LocalConnection? session = null;
        var connected = false;
        try
        {
            session = await LocalEndpoint.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
            await JsonLineCodec.WriteAsync(
                session.Stream,
                new SessionMessage(LocalMcpProtocol.Version, "bind", Token: capability),
                LocalMcpJsonContext.Default.SessionMessage,
                cancellationToken).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(capability);
            var bound = await JsonLineCodec.ReadAsync(
                session.Stream,
                LocalMcpJsonContext.Default.SessionMessage,
                cancellationToken).ConfigureAwait(false);
            if (bound is null)
            {
                throw new LocalMcpBrokerException("bind-invalid");
            }

            if (bound.Version != LocalMcpProtocol.Version)
            {
                throw new LocalMcpBrokerException("protocol-version-unsupported");
            }

            if (!SessionMessageValidation.IsBound(bound))
            {
                throw new LocalMcpBrokerException(bound.Error ?? "bind-invalid");
            }

            connected = true;
            return session;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(capability);
            if (!connected && session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ReadResponsesAsync(LocalConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var begin = await JsonLineCodec.ReadAsync(
                    connection.Stream,
                    LocalMcpJsonContext.Default.SessionMessage,
                    cancellationToken).ConfigureAwait(false);
                if (begin is null)
                {
                    break;
                }

                using var response = await ChunkedPayloadProtocol.ReadFollowingAsync(
                    connection.Stream,
                    begin,
                    "response",
                    LocalMcpProtocol.MaximumResponseBytes,
                    cancellationToken).ConfigureAwait(false);
                if (!_pending.TryGetValue(response.Header.Id!, out var completion))
                {
                    continue;
                }

                if (response.Header.Error is not null)
                {
                    completion.TrySetException(new LocalMcpBrokerException(response.Header.Error));
                    continue;
                }

                completion.TrySetResult(new LocalMcpBrokerResponse(
                    response.Header.Status ?? "ok",
                    response.Bytes.ToArray(),
                    response.Header.Receipt,
                    response.Header.Duplicate));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or ProtocolException)
        {
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _connection, null, connection);
            FailPending("broker-disconnected");
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task WritePayloadAsync(
        string id,
        string operation,
        string? idempotencyKey,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var stream = _connection?.Stream ?? throw new LocalMcpBrokerException("broker-unavailable");
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ChunkedPayloadProtocol.WriteAsync(
                stream,
                "request",
                id,
                operation,
                idempotencyKey,
                null,
                null,
                null,
                false,
                payload,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            throw new LocalMcpBrokerException("broker-disconnected");
        }
        finally
        {
            _writer.Release();
        }
    }

    private async Task CancelAsync(string requestId)
    {
        try
        {
            var stream = _connection?.Stream;
            if (stream is null)
            {
                return;
            }

            await _writer.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                await JsonLineCodec.WriteAsync(
                    stream,
                    new SessionMessage(LocalMcpProtocol.Version, "cancel", requestId),
                    LocalMcpJsonContext.Default.SessionMessage,
                    _lifetime.Token).ConfigureAwait(false);
            }
            finally
            {
                _writer.Release();
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or ObjectDisposedException
                                          or OperationCanceledException)
        {
        }
    }

    private void FailPending(string code)
    {
        foreach (var completion in _pending.Values)
        {
            completion.TrySetException(new LocalMcpBrokerException(code));
        }
    }
}
