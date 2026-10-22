using System.Text.Json;

namespace DotOrbit.McpIpcPrototype;

internal sealed class ConsentCoordinator
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _pending;

    public async Task<bool> RequestAsync(BootstrapRequest request, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> pending;
        lock (_gate)
        {
            if (_pending is not null)
            {
                throw new ProtocolException("consent-busy");
            }

            pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = pending;
        }

        Console.Error.WriteLine(
            $"mcp-prototype: consent=pending client={JsonSerializer.Serialize(request.ClientName)} "
            + $"version={JsonSerializer.Serialize(request.ClientVersion)} "
            + $"scopes={string.Join(',', request.Scopes)} identity=self-asserted os-account=verified");
        try
        {
            return await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                }
            }
        }
    }

    public bool Resolve(bool approved)
    {
        lock (_gate)
        {
            return _pending?.TrySetResult(approved) is true;
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _pending?.TrySetCanceled();
        }
    }
}
