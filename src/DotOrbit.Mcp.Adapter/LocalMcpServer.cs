using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DotOrbit.Mcp.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace DotOrbit.Mcp.Adapter;

internal static class LocalMcpServer
{
    internal const int MaximumStdioRequestBytes = 32 * 1024 * 1024;
    internal static TimeSpan CredentialStoreDeadline { get; } = TimeSpan.FromSeconds(10);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args is ["--package-smoke"])
        {
            return RunPackageSmoke();
        }

        var options = AdapterOptions.Parse(args);
        if (options is null)
        {
            Console.Error.WriteLine("dot-orbit-mcp: result=failed reason=invalid-arguments");
            return 2;
        }

        byte[] credential;
        try
        {
            using var credentialDeadline = new CancellationTokenSource(CredentialStoreDeadline);
            credential = await new AdapterProfileCredentialStore()
                .GetOrCreateAsync(options.Profile, credentialDeadline.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or PlatformNotSupportedException
                                          or UnauthorizedAccessException
                                          or OperationCanceledException)
        {
            Console.Error.WriteLine("dot-orbit-mcp: result=failed reason=credential-store-unavailable");
            return 1;
        }

        string verifier;
        try
        {
            verifier = Convert.ToHexStringLower(SHA256.HashData(credential));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }

        await using var broker = new LocalMcpBrokerClient(
            LocalMcpRuntimePath.GetDefault(),
            options.ClientName,
            options.ClientVersion,
            verifier,
            options.Scopes);
        using var input = new BoundedLineInputStream(
            Console.OpenStandardInput(),
            MaximumStdioRequestBytes);
        using var host = BuildHost(broker, input, Console.OpenStandardOutput());
        try
        {
            await host.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is IOException
                                          or InvalidOperationException
                                          or LocalMcpBrokerException)
        {
            Console.Error.WriteLine("dot-orbit-mcp: result=failed reason=transport-unavailable");
            return 1;
        }
    }

    internal static int RunPackageSmoke()
    {
        var expectedVersion = Environment.GetEnvironmentVariable("DOTORBIT_PACKAGE_SMOKE_VERSION");
        var actualVersion = typeof(LocalMcpServer).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrWhiteSpace(expectedVersion)
            || !string.Equals(expectedVersion, actualVersion, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("mcp-package-smoke: result=failed reason=version");
            return 1;
        }

        Console.WriteLine("mcp-package-smoke: transport=stdio tools=3 result=passed");
        return 0;
    }

    internal static IHost BuildHost(
        ILocalMcpBrokerClient broker,
        Stream input,
        Stream output)
    {
        var builder = Host.CreateApplicationBuilder([]);
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(broker);
        var server = builder.Services.AddMcpServer();
        server.WithStreamServerTransport(input, output);
        server.WithTools<OrbitTools>();
        server.WithResources<OrbitResources>();
        return builder.Build();
    }

    private sealed record AdapterOptions(
        string Profile,
        string ClientName,
        string ClientVersion,
        string[] Scopes)
    {
        public static AdapterOptions? Parse(string[] args)
        {
            var profile = "default";
            var clientName = "unverified-mcp-host";
            var clientVersion = "unknown";
            var scopes = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--stdio":
                        break;
                    case "--profile" when index + 1 < args.Length:
                        profile = args[++index];
                        break;
                    case "--client-name" when index + 1 < args.Length:
                        clientName = args[++index];
                        break;
                    case "--client-version" when index + 1 < args.Length:
                        clientVersion = args[++index];
                        break;
                    case "--scope" when index + 1 < args.Length:
                        scopes.Add(args[++index] switch
                        {
                            "read" => LocalMcpProtocol.ReadScope,
                            "submit" => LocalMcpProtocol.SubmitScope,
                            _ => string.Empty,
                        });
                        break;
                    default:
                        return null;
                }
            }

            if (scopes.Contains(string.Empty)
                || scopes.Count == 0
                || !ValidMetadata(clientName)
                || !ValidMetadata(clientVersion)
                || string.IsNullOrWhiteSpace(profile)
                || profile.Length > 64
                || profile.Any(character => !char.IsAsciiLetterOrDigit(character)
                                            && character is not '-' and not '_'))
            {
                return null;
            }

            return new(profile, clientName, clientVersion, [.. scopes]);
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
    }
}

[McpServerToolType]
internal sealed class OrbitTools(ILocalMcpBrokerClient broker)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [McpServerTool(Name = "orbit_get_instructions")]
    [Description("Returns dot-orbit proposal instructions and schemas. Supported schema versions: 1 and 2 (default/latest). Versioned resources retain the v1 contract.")]
    public async Task<OrbitTextResult> GetInstructionsAsync(
        [Description("Interchange schema version: 1 or 2. Defaults to latest supported version 2.")] int schemaVersion = 2,
        CancellationToken cancellationToken = default)
    {
        using var deadline = RequestDeadline(cancellationToken);
        return ToText(await broker.ReadReusableInstructionsAsync(schemaVersion, deadline.Token).ConfigureAwait(false));
    }

    [McpServerTool(Name = "orbit_get_planning_snapshot")]
    [Description("Returns one atomic revision-bearing Planning snapshot from the unlocked local desktop app. It excludes Archive and Bin content.")]
    public async Task<OrbitTextResult> GetPlanningSnapshotAsync(
        [Description("Interchange schema version: 1 or 2. Defaults to latest supported version 2.")] int schemaVersion = 2,
        CancellationToken cancellationToken = default)
    {
        using var deadline = RequestDeadline(cancellationToken);
        return ToText(await broker.ReadPlanningSnapshotAsync(schemaVersion, deadline.Token).ConfigureAwait(false));
    }

    [McpServerTool(Name = "orbit_submit_proposal")]
    [Description("Submits one v1 or v2 dot-orbit proposal for local human review. This cannot approve or apply changes.")]
    public async Task<OrbitSubmissionResult> SubmitProposalAsync(
        [Description("Caller-generated retry key, up to 128 characters.")] string idempotencyKey,
        [Description("Complete strict UTF-8 v1 or v2 New-work or Change proposal JSON, up to 4 MiB.")] string proposal,
        CancellationToken cancellationToken = default)
    {
        using var deadline = RequestDeadline(cancellationToken);
        byte[] payload;
        try
        {
            payload = StrictUtf8.GetBytes(proposal);
        }
        catch (EncoderFallbackException)
        {
            throw new LocalMcpBrokerException("proposal-utf8-invalid");
        }

        if (payload.Length > LocalMcpProtocol.MaximumProposalBytes)
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new LocalMcpBrokerException("proposal-too-large");
        }

        try
        {
            var response = await broker.SubmitProposalAsync(idempotencyKey, payload, deadline.Token)
                .ConfigureAwait(false);
            return new(
                response.Status,
                response.Receipt,
                response.Duplicate,
                ReviewRequired: response.Status == "pending-local-review");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static OrbitTextResult ToText(LocalMcpBrokerResponse response) =>
        new(response.Status, StrictUtf8.GetString(response.Payload.Span));

    private static CancellationTokenSource RequestDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(LocalMcpProtocol.RequestLifetime);
        return deadline;
    }
}

internal sealed record OrbitTextResult(string Status, string Content);

internal sealed record OrbitSubmissionResult(
    string Status,
    string? Receipt,
    bool Duplicate,
    bool ReviewRequired);
