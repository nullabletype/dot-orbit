using System.ComponentModel;
using DotOrbit.Mcp.Ipc;
using ModelContextProtocol.Server;

namespace DotOrbit.Mcp.Adapter;

[McpServerResourceType]
internal sealed class OrbitResources(ILocalMcpBrokerClient broker)
{
    [McpServerResource(UriTemplate = "orbit://interchange/v1/instructions", Name = "orbit_instructions_v1", MimeType = "text/plain")]
    [Description("Immutable v1 proposal instructions and schemas. V1 proposals cannot change Today placement.")]
    public Task<string> InstructionsV1Async(CancellationToken cancellationToken = default) =>
        ReadAsync(1, snapshot: false, cancellationToken);

    [McpServerResource(UriTemplate = "orbit://interchange/v2/instructions", Name = "orbit_instructions_v2", MimeType = "text/plain")]
    [Description("Latest supported v2 proposal instructions and schemas, including reviewed Today placement.")]
    public Task<string> InstructionsV2Async(CancellationToken cancellationToken = default) =>
        ReadAsync(2, snapshot: false, cancellationToken);

    [McpServerResource(UriTemplate = "orbit://interchange/v1/planning-snapshot", Name = "orbit_planning_snapshot_v1", MimeType = "application/json")]
    [Description("Atomic v1 Planning snapshot from the unlocked local app. Excludes Archive and Bin content.")]
    public Task<string> SnapshotV1Async(CancellationToken cancellationToken = default) =>
        ReadAsync(1, snapshot: true, cancellationToken);

    [McpServerResource(UriTemplate = "orbit://interchange/v2/planning-snapshot", Name = "orbit_planning_snapshot_v2", MimeType = "application/json")]
    [Description("Latest supported atomic v2 Planning snapshot from the unlocked local app. Excludes Archive and Bin content.")]
    public Task<string> SnapshotV2Async(CancellationToken cancellationToken = default) =>
        ReadAsync(2, snapshot: true, cancellationToken);

    private async Task<string> ReadAsync(int schemaVersion, bool snapshot, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(LocalMcpProtocol.RequestLifetime);
        var response = snapshot
            ? await broker.ReadPlanningSnapshotAsync(schemaVersion, deadline.Token).ConfigureAwait(false)
            : await broker.ReadReusableInstructionsAsync(schemaVersion, deadline.Token).ConfigureAwait(false);
        if (response.Status != "ok")
        {
            throw new LocalMcpBrokerException("resource-unavailable");
        }

        return new System.Text.UTF8Encoding(false, true).GetString(response.Payload.Span);
    }
}
