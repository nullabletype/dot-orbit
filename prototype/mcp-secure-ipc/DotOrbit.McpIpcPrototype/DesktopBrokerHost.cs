using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DotOrbit.McpIpcPrototype;

internal static class DesktopBrokerHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = HostOptions.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine("mcp-prototype: result=failed reason=invalid-arguments");
            return 2;
        }

        using var stop = new CancellationTokenSource();
        var consent = new ConsentCoordinator();
        await using var sessions = new SessionRegistry();
        var control = Task.Run(
            () => RunControlLoopAsync(consent, sessions, stop),
            CancellationToken.None);

        if (!options.AiEnabled || !options.McpEnabled)
        {
            Console.Error.WriteLine("mcp-prototype: broker=unavailable reason=gate-off");
            await control.ConfigureAwait(false);
            return 0;
        }

        Console.Error.WriteLine("mcp-prototype: broker=starting");
        try
        {
            await RunBootstrapLoopAsync(options.RuntimeRoot, consent, sessions, stop.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            consent.Cancel();
            await sessions.RevokeAsync().ConfigureAwait(false);
            stop.Cancel();
        }

        await control.ConfigureAwait(false);
        Console.Error.WriteLine("mcp-prototype: broker=stopped");
        return 0;
    }

    private static async Task RunBootstrapLoopAsync(
        string runtimeRoot,
        ConsentCoordinator consent,
        SessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var listener = await LocalEndpoint.CreateBootstrapAsync(runtimeRoot, cancellationToken)
                .ConfigureAwait(false);
            Console.Error.WriteLine("mcp-prototype: broker=ready");
            try
            {
                await using var connection = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                await HandleBootstrapAsync(connection.Stream, runtimeRoot, consent, sessions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                Console.Error.WriteLine("mcp-prototype: bootstrap=closed reason=transport");
            }
        }
    }

    private static async Task HandleBootstrapAsync(
        Stream stream,
        string runtimeRoot,
        ConsentCoordinator consent,
        SessionRegistry sessions,
        CancellationToken cancellationToken)
    {
        BootstrapResponse response;
        var awaitingConsent = false;
        try
        {
            var consentGeneration = sessions.Generation;
            using var phaseDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            phaseDeadline.CancelAfter(PrototypeProtocol.BootstrapHandshakeLifetime);
            var request = await JsonLineCodec.ReadAsync(
                stream,
                PrototypeJsonContext.Default.BootstrapRequest,
                phaseDeadline.Token).ConfigureAwait(false)
                ?? throw new ProtocolException("bootstrap-empty");
            ValidateBootstrap(request);

            awaitingConsent = true;
            phaseDeadline.CancelAfter(PrototypeProtocol.ConsentLifetime);
            if (!await consent.RequestAsync(request, phaseDeadline.Token).ConfigureAwait(false))
            {
                response = new BootstrapResponse(
                    PrototypeProtocol.Version,
                    "error",
                    null,
                    null,
                    "consent-denied");
            }
            else
            {
                var listener = await LocalEndpoint.CreateSessionAsync(runtimeRoot).ConfigureAwait(false);
                var authority = new CapabilityAuthority(TimeProvider.System, new CryptographicRandomBytes());
                var token = authority.Issue(request.Scopes);
                var start = sessions.TryStart(
                    listener,
                    authority,
                    token.ToArray(),
                    consentGeneration);
                if (start == SessionStartResult.Started)
                {
                    response = new BootstrapResponse(
                        PrototypeProtocol.Version,
                        "capability",
                        listener.Address,
                        token,
                        null);
                }
                else
                {
                    CryptographicOperations.ZeroMemory(token);
                    authority.Dispose();
                    await listener.DisposeAsync().ConfigureAwait(false);
                    response = new BootstrapResponse(
                        PrototypeProtocol.Version,
                        "error",
                        null,
                        null,
                        start == SessionStartResult.Revoked ? "consent-revoked" : "session-limit");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            response = new BootstrapResponse(
                PrototypeProtocol.Version,
                "error",
                null,
                null,
                awaitingConsent ? "consent-timeout" : "bootstrap-timeout");
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Console.Error.WriteLine("mcp-prototype: bootstrap=closed reason=transport");
            return;
        }
        catch (ProtocolException exception)
        {
            response = new BootstrapResponse(
                PrototypeProtocol.Version,
                "error",
                null,
                null,
                exception.Code);
        }

        try
        {
            await JsonLineCodec.WriteAsync(
                stream,
                response,
                PrototypeJsonContext.Default.BootstrapResponse,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Console.Error.WriteLine("mcp-prototype: bootstrap=closed reason=transport");
        }
        finally
        {
            if (response.Token is not null)
            {
                CryptographicOperations.ZeroMemory(response.Token);
            }
        }
    }

    private static void ValidateBootstrap(BootstrapRequest request)
    {
        if (request.Version != PrototypeProtocol.Version || request.Type != "request-capability")
        {
            throw new ProtocolException("protocol-version-unsupported");
        }

        if (string.IsNullOrWhiteSpace(request.ClientName)
            || request.ClientName.Length > PrototypeProtocol.MaximumClientMetadataCharacters
            || request.ClientName.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(request.ClientVersion)
            || request.ClientVersion.Length > PrototypeProtocol.MaximumClientMetadataCharacters
            || request.ClientVersion.Any(char.IsControl)
            || request.Scopes is null
            || request.Scopes.Length is < 1 or > 2
            || request.Scopes.Distinct(StringComparer.Ordinal).Count() != request.Scopes.Length
            || request.Scopes.Any(scope => scope is not ("synthetic.read" or "synthetic.submit")))
        {
            throw new ProtocolException("bootstrap-invalid");
        }
    }

    private static async Task RunControlLoopAsync(
        ConsentCoordinator consent,
        SessionRegistry sessions,
        CancellationTokenSource stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var line = await Console.In.ReadLineAsync(stop.Token).ConfigureAwait(false);
                if (line is null)
                {
                    stop.Cancel();
                    return;
                }

                switch (line)
                {
                    case "approve":
                        _ = consent.Resolve(approved: true);
                        break;
                    case "deny":
                        _ = consent.Resolve(approved: false);
                        break;
                    case "revoke":
                        await sessions.RevokeAsync().ConfigureAwait(false);
                        Console.Error.WriteLine("mcp-prototype: sessions=revoked");
                        break;
                    case "disable":
                    case "lock":
                    case "exit":
                        stop.Cancel();
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private sealed record HostOptions(string RuntimeRoot, bool AiEnabled, bool McpEnabled)
    {
        public static HostOptions? Parse(string[] args)
        {
            string? runtimeRoot = null;
            var aiEnabled = false;
            var mcpEnabled = false;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--runtime-root" when index + 1 < args.Length:
                        runtimeRoot = Path.GetFullPath(args[++index]);
                        break;
                    case "--ai-on":
                        aiEnabled = true;
                        break;
                    case "--mcp-on":
                        mcpEnabled = true;
                        break;
                    default:
                        return null;
                }
            }

            return runtimeRoot is null ? null : new HostOptions(runtimeRoot, aiEnabled, mcpEnabled);
        }
    }

    private sealed class SessionRegistry : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly HashSet<Task> _tasks = [];
        private CancellationTokenSource _revocation = new();
        private int _reserved;
        private long _generation;

        public long Generation => Interlocked.Read(ref _generation);

        public SessionStartResult TryStart(
            LocalEndpointListener listener,
            CapabilityAuthority authority,
            byte[] issuedToken,
            long expectedGeneration)
        {
            lock (_gate)
            {
                if (_generation != expectedGeneration)
                {
                    return SessionStartResult.Revoked;
                }

                if (_reserved >= PrototypeProtocol.MaximumSessions)
                {
                    return SessionStartResult.LimitReached;
                }

                _reserved++;
                var task = RunSessionAsync(listener, authority, issuedToken, _revocation.Token);
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

            return SessionStartResult.Started;
        }

        public async Task RevokeAsync()
        {
            Task[] tasks;
            CancellationTokenSource prior;
            lock (_gate)
            {
                _generation++;
                prior = _revocation;
                _revocation = new CancellationTokenSource();
                tasks = [.. _tasks];
            }

            prior.Cancel();
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
            }
            finally
            {
                prior.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await RevokeAsync().ConfigureAwait(false);
            _revocation.Cancel();
            _revocation.Dispose();
        }

    }

    private enum SessionStartResult
    {
        Started,
        Revoked,
        LimitReached,
    }

    private static async Task RunSessionAsync(
        LocalEndpointListener listener,
        CapabilityAuthority authority,
        byte[] issuedToken,
        CancellationToken cancellationToken)
    {
        await using (listener.ConfigureAwait(false))
        using (authority)
        {
            using var grantDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            grantDeadline.CancelAfter(PrototypeProtocol.GrantLifetime);
            try
            {
                await using var connection = await listener.AcceptAsync(grantDeadline.Token).ConfigureAwait(false);
                var bind = await JsonLineCodec.ReadAsync(
                    connection.Stream,
                    PrototypeJsonContext.Default.SessionMessage,
                    grantDeadline.Token).ConfigureAwait(false);
                if (bind is null
                    || bind.Version != PrototypeProtocol.Version
                    || bind.Type != "bind"
                    || bind.Token is null)
                {
                    throw new ProtocolException("bind-invalid");
                }

                var authorization = authority.TryConsume(bind.Token)
                    ?? throw new ProtocolException("capability-invalid");
                CryptographicOperations.ZeroMemory(bind.Token);
                CryptographicOperations.ZeroMemory(issuedToken);
                await JsonLineCodec.WriteAsync(
                    connection.Stream,
                    new SessionMessage(PrototypeProtocol.Version, "bound"),
                    PrototypeJsonContext.Default.SessionMessage,
                    grantDeadline.Token).ConfigureAwait(false);
                await ProcessSessionAsync(connection.Stream, authorization, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested || grantDeadline.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is IOException or ProtocolException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("mcp-prototype: session=closed reason=protocol-or-transport");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(issuedToken);
            }
        }
    }

    private static async Task ProcessSessionAsync(
        Stream stream,
        SessionAuthorization authorization,
        CancellationToken cancellationToken)
    {
        using var outstanding = new SemaphoreSlim(PrototypeProtocol.MaximumOutstandingRequests);
        using var writer = new SemaphoreSlim(1, 1);
        var requests = new ConcurrentDictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        var tasks = new ConcurrentDictionary<string, Task>(StringComparer.Ordinal);
        var receipts = new SubmissionReceiptCache();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await JsonLineCodec.ReadAsync(
                    stream,
                    PrototypeJsonContext.Default.SessionMessage,
                    cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                if (message.Version != PrototypeProtocol.Version)
                {
                    throw new ProtocolException("protocol-version-unsupported");
                }

                if (message.Type == "cancel" && message.Id is not null)
                {
                    if (requests.TryGetValue(message.Id, out var pending))
                    {
                        pending.Cancel();
                    }

                    continue;
                }

                if (message.Type != "request" || string.IsNullOrWhiteSpace(message.Id))
                {
                    throw new ProtocolException("request-invalid");
                }

                if (!outstanding.Wait(0, CancellationToken.None))
                {
                    await WriteSessionAsync(
                        stream,
                        writer,
                        new SessionMessage(
                            PrototypeProtocol.Version,
                            "response",
                            message.Id,
                            Error: "busy"),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (!requests.TryAdd(message.Id, requestCancellation))
                {
                    requestCancellation.Dispose();
                    outstanding.Release();
                    throw new ProtocolException("request-id-duplicate");
                }

                var task = HandleRequestAsync(
                    stream,
                    writer,
                    message,
                    authorization,
                    receipts,
                    requestCancellation.Token);
                tasks[message.Id] = task;
                _ = task.ContinueWith(
                    completed =>
                    {
                        _ = completed.Exception;
                        tasks.TryRemove(message.Id, out _);
                        if (requests.TryRemove(message.Id, out var source))
                        {
                            source.Dispose();
                        }

                        outstanding.Release();
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

            await Task.WhenAll(tasks.Values).ConfigureAwait(false);
        }
    }

    private static async Task HandleRequestAsync(
        Stream stream,
        SemaphoreSlim writer,
        SessionMessage message,
        SessionAuthorization authorization,
        SubmissionReceiptCache receipts,
        CancellationToken cancellationToken)
    {
        try
        {
            if (message.DelayMilliseconds is < 0 or > 5000)
            {
                throw new ProtocolException("delay-invalid");
            }

            if (message.DelayMilliseconds > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(message.DelayMilliseconds), cancellationToken)
                    .ConfigureAwait(false);
            }

            SyntheticResult result;
            switch (message.Operation)
            {
                case "read" when authorization.Scopes.Contains("synthetic.read"):
                    result = new SyntheticResult("status", "available", ReviewRequired: false);
                    break;
                case "submit" when authorization.Scopes.Contains("synthetic.submit"):
                    if (string.IsNullOrWhiteSpace(message.IdempotencyKey)
                        || message.IdempotencyKey.Length > 64)
                    {
                        throw new ProtocolException("idempotency-key-invalid");
                    }

                    var (receipt, duplicate) = receipts.Add(message.IdempotencyKey);
                    result = new SyntheticResult(
                        "proposal",
                        "pending-local-review",
                        ReviewRequired: true,
                        receipt,
                        duplicate);
                    break;
                default:
                    throw new ProtocolException("operation-denied");
            }

            await WriteSessionAsync(
                stream,
                writer,
                new SessionMessage(
                    PrototypeProtocol.Version,
                    "response",
                    message.Id,
                    Result: result),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ProtocolException exception)
        {
            await WriteSessionAsync(
                stream,
                writer,
                new SessionMessage(
                    PrototypeProtocol.Version,
                    "response",
                    message.Id,
                    Error: exception.Code),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteSessionAsync(
        Stream stream,
        SemaphoreSlim writer,
        SessionMessage message,
        CancellationToken cancellationToken)
    {
        await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await JsonLineCodec.WriteAsync(
                stream,
                message,
                PrototypeJsonContext.Default.SessionMessage,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writer.Release();
        }
    }

}
