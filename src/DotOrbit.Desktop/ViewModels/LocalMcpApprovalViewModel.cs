using DotOrbit.Mcp.Ipc;

namespace DotOrbit.Desktop.ViewModels;

internal sealed class LocalMcpApprovalViewModel
{
    public LocalMcpApprovalViewModel(LocalMcpApprovalRequest request)
    {
        Request = request;
        VerifiedAccount = $"Verified same OS account: {request.Client.OperatingSystemAccount}";
        VerifiedProcess = request.Client.ProcessId is { } processId
            ? $"Verified local process ID: {processId}"
            : "The OS verified the same account; a process ID is not available on this platform.";
        UnverifiedClient =
            $"Self-asserted client: {request.Client.ClientName} {request.Client.ClientVersion}";
        RequestedScopes = ScopeText(request.Client.RequestedScopes);
    }

    public LocalMcpApprovalRequest Request { get; }
    public string VerifiedAccount { get; }
    public string VerifiedProcess { get; }
    public string UnverifiedClient { get; }
    public string RequestedScopes { get; }
    public string Powers { get; } =
        "Allowed reads expose sensitive plaintext current-work context without another prompt. Proposal submission can only place untrusted JSON into the existing review queue; it cannot approve or apply changes.";

    private static string ScopeText(LocalMcpScopes scopes)
    {
        var requested = new List<string>();
        if (scopes.HasFlag(LocalMcpScopes.ReadInterchangeContext))
        {
            requested.Add("Read interchange instructions and current Planning context");
        }

        if (scopes.HasFlag(LocalMcpScopes.SubmitProposals))
        {
            requested.Add("Submit proposals for local human review");
        }

        return string.Join("\n", requested.Select(scope => $"• {scope}"));
    }
}
