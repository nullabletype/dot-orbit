using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DotOrbit.McpIpcPrototype;

public static class McpPrototypeServer
{
    public static async Task<int> RunAsync(string[] args)
    {
        var options = AdapterOptions.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine("mcp-prototype: result=failed reason=invalid-arguments");
            return 2;
        }

        await using var broker = new DesktopBrokerClient(
            options.RuntimeRoot,
            options.ClientName,
            options.ClientVersion);
        var builder = Host.CreateApplicationBuilder([]);
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(broker);
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<SyntheticOrbitTools>();

        using var host = builder.Build();
        Console.Error.WriteLine("mcp-prototype: stdio=ready");
        await host.RunAsync().ConfigureAwait(false);
        Console.Error.WriteLine("mcp-prototype: stdio=stopped");
        return 0;
    }

    private sealed record AdapterOptions(string RuntimeRoot, string ClientName, string ClientVersion)
    {
        public static AdapterOptions? Parse(string[] args)
        {
            string? runtimeRoot = null;
            var clientName = "unverified-mcp-host";
            var clientVersion = "unknown";
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--stdio":
                        break;
                    case "--runtime-root" when index + 1 < args.Length:
                        runtimeRoot = Path.GetFullPath(args[++index]);
                        break;
                    case "--client-name" when index + 1 < args.Length:
                        clientName = args[++index];
                        break;
                    case "--client-version" when index + 1 < args.Length:
                        clientVersion = args[++index];
                        break;
                    default:
                        return null;
                }
            }

            if (runtimeRoot is null
                || string.IsNullOrWhiteSpace(clientName)
                || clientName.Length > PrototypeProtocol.MaximumClientMetadataCharacters
                || string.IsNullOrWhiteSpace(clientVersion)
                || clientVersion.Length > PrototypeProtocol.MaximumClientMetadataCharacters)
            {
                return null;
            }

            return new AdapterOptions(runtimeRoot, clientName, clientVersion);
        }
    }
}

[McpServerToolType]
internal sealed class SyntheticOrbitTools(DesktopBrokerClient broker)
{
    [McpServerTool(Name = "orbit_status")]
    [Description("Returns fixed synthetic availability metadata from a consented local dot-orbit prototype. Reads no workspace content.")]
    public async Task<SyntheticToolResult> GetStatusAsync(
        int delayMilliseconds = 0,
        CancellationToken cancellationToken = default)
    {
        var result = await broker.ReadAsync(delayMilliseconds, cancellationToken).ConfigureAwait(false);
        return SyntheticToolResult.From(result);
    }

    [McpServerTool(Name = "orbit_submit_synthetic_proposal")]
    [Description("Delivers a fixed synthetic proposal-shaped message for local review. It cannot approve or apply changes.")]
    public async Task<SyntheticToolResult> SubmitSyntheticProposalAsync(
        [Description("Caller-generated idempotency key, up to 64 characters.")] string idempotencyKey,
        int delayMilliseconds = 0,
        CancellationToken cancellationToken = default)
    {
        var result = await broker.SubmitAsync(idempotencyKey, delayMilliseconds, cancellationToken)
            .ConfigureAwait(false);
        return SyntheticToolResult.From(result);
    }
}

public sealed record SyntheticToolResult(
    string Kind,
    string Status,
    bool ReviewRequired,
    string? Receipt,
    bool Duplicate)
{
    internal static SyntheticToolResult From(SyntheticResult result) =>
        new(result.Kind, result.Status, result.ReviewRequired, result.Receipt, result.Duplicate);
}
