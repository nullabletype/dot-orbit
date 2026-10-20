using System.Text.Json.Serialization;

namespace DotOrbit.McpIpcPrototype;

internal static class PrototypeProtocol
{
    public const int Version = 1;
    public const int MaximumFrameBytes = 64 * 1024;
    public const int MaximumClientMetadataCharacters = 80;
    public const int MaximumOutstandingRequests = 4;
    public const int MaximumSessions = 4;
    public const int MaximumSubmissionReceipts = 128;
    public static readonly TimeSpan BootstrapHandshakeLifetime = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ConsentLifetime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan GrantLifetime = TimeSpan.FromSeconds(15);
}

internal sealed record EndpointAddress(string Transport, string Address);

internal sealed record EndpointDescriptor(int Version, string Transport, string Address);

internal sealed record BootstrapRequest(
    int Version,
    string Type,
    string ClientName,
    string ClientVersion,
    string[] Scopes);

internal sealed record BootstrapResponse(
    int Version,
    string Type,
    EndpointAddress? Endpoint,
    byte[]? Token,
    string? Error);

internal sealed record SessionMessage(
    int Version,
    string Type,
    string? Id = null,
    string? Operation = null,
    string? IdempotencyKey = null,
    int DelayMilliseconds = 0,
    byte[]? Token = null,
    string? Error = null,
    SyntheticResult? Result = null);

internal sealed record SyntheticResult(
    string Kind,
    string Status,
    bool ReviewRequired,
    string? Receipt = null,
    bool Duplicate = false);

[JsonSerializable(typeof(EndpointDescriptor))]
[JsonSerializable(typeof(BootstrapRequest))]
[JsonSerializable(typeof(BootstrapResponse))]
[JsonSerializable(typeof(SessionMessage))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    MaxDepth = 16,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
internal sealed partial class PrototypeJsonContext : JsonSerializerContext;
