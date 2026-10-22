using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace DotOrbit.McpIpcPrototype;

internal sealed class BrokerUnavailableException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal sealed class DesktopBrokerClient(
    string runtimeRoot,
    string clientName,
    string clientVersion) : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _admission = new(PrototypeProtocol.MaximumOutstandingRequests);
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<SessionMessage>> _pending =
        new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private LocalConnection? _connection;
    private Task? _reader;
    private long _requestSequence;

    public Task<SyntheticResult> ReadAsync(int delayMilliseconds, CancellationToken cancellationToken) =>
        SendAsync("read", null, delayMilliseconds, cancellationToken);

    public Task<SyntheticResult> SubmitAsync(
        string idempotencyKey,
        int delayMilliseconds,
        CancellationToken cancellationToken) =>
        SendAsync("submit", idempotencyKey, delayMilliseconds, cancellationToken);

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

    private async Task<SyntheticResult> SendAsync(
        string operation,
        string? idempotencyKey,
        int delayMilliseconds,
        CancellationToken cancellationToken)
    {
        if (!await _admission.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new BrokerUnavailableException("busy");
        }

        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var id = Interlocked.Increment(ref _requestSequence)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            var completion = new TaskCompletionSource<SessionMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, completion))
            {
                throw new BrokerUnavailableException("request-id-collision");
            }

            try
            {
                await WriteAsync(
                    new SessionMessage(
                        PrototypeProtocol.Version,
                        "request",
                        id,
                        operation,
                        idempotencyKey,
                        delayMilliseconds),
                    cancellationToken).ConfigureAwait(false);

                using var registration = cancellationToken.Register(
                    static state =>
                    {
                        var (client, requestId) = ((DesktopBrokerClient, string))state!;
                        _ = client.CancelAsync(requestId);
                    },
                    (this, id));
                var response = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (response.Error is not null)
                {
                    throw new BrokerUnavailableException(response.Error);
                }

                return response.Result ?? throw new BrokerUnavailableException("response-invalid");
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
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var bootstrap = await LocalEndpoint.DiscoverBootstrapAsync(runtimeRoot, deadline.Token).ConfigureAwait(false);
            await using var bootstrapConnection = await LocalEndpoint.ConnectAsync(bootstrap, deadline.Token)
                .ConfigureAwait(false);
            await JsonLineCodec.WriteAsync(
                bootstrapConnection.Stream,
                new BootstrapRequest(
                    PrototypeProtocol.Version,
                    "request-capability",
                    clientName,
                    clientVersion,
                    ["synthetic.read", "synthetic.submit"]),
                PrototypeJsonContext.Default.BootstrapRequest,
                deadline.Token).ConfigureAwait(false);
            var response = await JsonLineCodec.ReadAsync(
                bootstrapConnection.Stream,
                PrototypeJsonContext.Default.BootstrapResponse,
                deadline.Token).ConfigureAwait(false)
                ?? throw new BrokerUnavailableException("bootstrap-empty");
            if (response.Version != PrototypeProtocol.Version)
            {
                throw new BrokerUnavailableException("protocol-version-unsupported");
            }

            if (response.Type != "capability"
                || response.Endpoint is null
                || string.IsNullOrWhiteSpace(response.Endpoint.Transport)
                || string.IsNullOrWhiteSpace(response.Endpoint.Address)
                || response.Token is null
                || response.Token.Length != 32)
            {
                throw new BrokerUnavailableException(response.Error ?? "bootstrap-invalid");
            }

            var session = await ConnectSessionAsync(response.Endpoint, response.Token, deadline.Token)
                .ConfigureAwait(false);
            _connection = session;
            _reader = ReadResponsesAsync(session, _lifetime.Token);
        }
        catch (Exception exception) when (exception is IOException
                                          or SocketException
                                          or TimeoutException
                                          or ProtocolException)
        {
            throw new BrokerUnavailableException("broker-unavailable");
        }
        finally
        {
            _connectGate.Release();
        }
    }

    internal static async Task<LocalConnection> ConnectSessionAsync(
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
                new SessionMessage(
                    PrototypeProtocol.Version,
                    "bind",
                    Token: capability),
                PrototypeJsonContext.Default.SessionMessage,
                cancellationToken).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(capability);
            var bound = await JsonLineCodec.ReadAsync(
                session.Stream,
                PrototypeJsonContext.Default.SessionMessage,
                cancellationToken).ConfigureAwait(false);
            if (bound?.Version != PrototypeProtocol.Version)
            {
                throw new BrokerUnavailableException("protocol-version-unsupported");
            }

            if (bound.Type != "bound")
            {
                throw new BrokerUnavailableException(bound?.Error ?? "bind-invalid");
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
                var response = await JsonLineCodec.ReadAsync(
                    connection.Stream,
                    PrototypeJsonContext.Default.SessionMessage,
                    cancellationToken).ConfigureAwait(false);
                if (response is null)
                {
                    break;
                }

                if (response.Version != PrototypeProtocol.Version)
                {
                    throw new ProtocolException("protocol-version-unsupported");
                }

                if (response.Type != "response" || response.Id is null)
                {
                    throw new ProtocolException("response-invalid");
                }

                if (_pending.TryGetValue(response.Id, out var completion))
                {
                    completion.TrySetResult(response);
                }
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

    private async Task CancelAsync(string requestId)
    {
        try
        {
            await WriteAsync(
                new SessionMessage(PrototypeProtocol.Version, "cancel", requestId),
                _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or BrokerUnavailableException)
        {
        }
    }

    private async Task WriteAsync(SessionMessage message, CancellationToken cancellationToken)
    {
        var stream = _connection?.Stream ?? throw new BrokerUnavailableException("broker-unavailable");
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await JsonLineCodec.WriteAsync(
                stream,
                message,
                PrototypeJsonContext.Default.SessionMessage,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            throw new BrokerUnavailableException("broker-disconnected");
        }
        finally
        {
            _writer.Release();
        }
    }

    private void FailPending(string code)
    {
        foreach (var completion in _pending.Values)
        {
            completion.TrySetException(new BrokerUnavailableException(code));
        }
    }
}
