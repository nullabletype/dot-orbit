using System.Text.Json.Serialization;

namespace DotOrbit.Mcp.Ipc;

public static class LocalMcpProtocol
{
    public const int Version = 1;
    public const int MaximumFrameBytes = 64 * 1024;
    public const int ChunkBytes = 32 * 1024;
    public const int MaximumProposalBytes = 4 * 1024 * 1024;
    public const int MaximumResponseBytes = 16 * 1024 * 1024;
    public const int MaximumClientMetadataCharacters = 80;
    public const int MaximumIdempotencyKeyCharacters = 128;
    public const int MaximumOutstandingRequests = 4;
    public const int MaximumSessions = 4;
    public const int MaximumSubmissionReceipts = 128;
    public const string ReadScope = "interchange.read";
    public const string SubmitScope = "proposals.submit";
    public static readonly TimeSpan BootstrapHandshakeLifetime = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ConsentLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan GrantLifetime = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromSeconds(30);
}

internal sealed record EndpointAddress(string Transport, string Address);

internal sealed record EndpointDescriptor(int Version, string Transport, string Address);

internal sealed record BootstrapRequest(
    int Version,
    string Type,
    string ClientName,
    string ClientVersion,
    string CredentialVerifier,
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
    int TotalLength = 0,
    int ChunkIndex = 0,
    int ChunkCount = 0,
    byte[]? Payload = null,
    byte[]? Token = null,
    string? Error = null,
    string? Status = null,
    string? Receipt = null,
    bool Duplicate = false);

internal static class SessionMessageValidation
{
    public static bool IsCapability(BootstrapResponse response) =>
        response.Version == LocalMcpProtocol.Version
        && response.Type == "capability"
        && response.Endpoint is not null
        && !string.IsNullOrWhiteSpace(response.Endpoint.Transport)
        && !string.IsNullOrWhiteSpace(response.Endpoint.Address)
        && response.Token is { Length: 32 }
        && response.Error is null;

    public static bool IsError(BootstrapResponse response) =>
        response.Version == LocalMcpProtocol.Version
        && response.Type == "error"
        && response.Endpoint is null
        && response.Token is null
        && !string.IsNullOrWhiteSpace(response.Error);

    public static bool IsBind(SessionMessage message) =>
        HasVersionAndType(message, "bind")
        && message.Id is null
        && HasNoPayloadFields(message)
        && message.Token is { Length: 32 };

    public static bool IsBound(SessionMessage message) =>
        HasVersionAndType(message, "bound")
        && message.Id is null
        && HasNoPayloadFields(message)
        && message.Token is null;

    public static bool IsCancel(SessionMessage message) =>
        HasVersionAndType(message, "cancel")
        && !string.IsNullOrWhiteSpace(message.Id)
        && HasNoPayloadFields(message)
        && message.Token is null;

    public static bool IsPayloadBegin(SessionMessage message, string kind)
    {
        if (!HasVersionAndType(message, $"{kind}-begin")
            || string.IsNullOrWhiteSpace(message.Id)
            || message.ChunkIndex != 0
            || message.Payload is not null
            || message.Token is not null)
        {
            return false;
        }

        return kind switch
        {
            "request" => message.Error is null
                         && message.Status is null
                         && message.Receipt is null
                         && !message.Duplicate,
            "response" => message.Operation is null
                          && message.IdempotencyKey is null,
            _ => false,
        };
    }

    public static bool IsPayloadChunk(SessionMessage message, string kind) =>
        HasVersionAndType(message, $"{kind}-chunk")
        && !string.IsNullOrWhiteSpace(message.Id)
        && message.Operation is null
        && message.IdempotencyKey is null
        && message.TotalLength == 0
        && message.Payload is not null
        && message.Token is null
        && message.Error is null
        && message.Status is null
        && message.Receipt is null
        && !message.Duplicate;

    public static bool IsPayloadEnd(SessionMessage message, string kind) =>
        HasVersionAndType(message, $"{kind}-end")
        && !string.IsNullOrWhiteSpace(message.Id)
        && message.Operation is null
        && message.IdempotencyKey is null
        && message.TotalLength == 0
        && message.ChunkIndex == 0
        && message.ChunkCount == 0
        && message.Payload is null
        && message.Token is null
        && message.Error is null
        && message.Status is null
        && message.Receipt is null
        && !message.Duplicate;

    private static bool HasVersionAndType(SessionMessage message, string type) =>
        message.Version == LocalMcpProtocol.Version
        && message.Type == type;

    private static bool HasNoPayloadFields(SessionMessage message) =>
        message.Operation is null
        && message.IdempotencyKey is null
        && message.TotalLength == 0
        && message.ChunkIndex == 0
        && message.ChunkCount == 0
        && message.Payload is null
        && message.Error is null
        && message.Status is null
        && message.Receipt is null
        && !message.Duplicate;
}

[JsonSerializable(typeof(EndpointDescriptor))]
[JsonSerializable(typeof(BootstrapRequest))]
[JsonSerializable(typeof(BootstrapResponse))]
[JsonSerializable(typeof(SessionMessage))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    MaxDepth = 16,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
internal sealed partial class LocalMcpJsonContext : JsonSerializerContext;
