namespace DotOrbit.McpIpcPrototype;

internal static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        if (args.FirstOrDefault() == "--mcp-stdio")
        {
            return await McpPrototypeServer.RunAsync(args[1..]).ConfigureAwait(false);
        }

        if (args.FirstOrDefault() == "host")
        {
            return await DesktopBrokerHost.RunAsync(args[1..]).ConfigureAwait(false);
        }

        if (args.FirstOrDefault() == "probe")
        {
            return await RunEndpointProbeAsync(args[1..]).ConfigureAwait(false);
        }

        Console.Error.WriteLine("mcp-prototype: result=failed reason=invalid-mode");
        return 2;
    }

    private static async Task<int> RunEndpointProbeAsync(string[] args)
    {
        if (args.Length != 4 || args[0] != "--transport" || args[2] != "--address")
        {
            return 2;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await using var connection = await LocalEndpoint.ConnectAsync(
                new EndpointAddress(args[1], args[3]),
                timeout.Token).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Net.Sockets.SocketException
            or ProtocolException)
        {
            return 1;
        }
    }
}
