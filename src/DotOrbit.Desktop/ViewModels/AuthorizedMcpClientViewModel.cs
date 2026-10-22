using DotOrbit.Core.Interchange;

namespace DotOrbit.Desktop.ViewModels;

public sealed class AuthorizedMcpClientViewModel
{
    internal AuthorizedMcpClientViewModel(
        AgentClientAuthorization authorization,
        Action<string> revoke)
    {
        Id = authorization.Id;
        ClientLabel = $"Unverified client: {authorization.ClientName} {authorization.ClientVersion}";
        VerifiedAccount = $"Verified OS account: {authorization.OperatingSystemAccount}";
        Scopes = ScopeText(authorization.Scopes);
        RevokeCommand = new RelayCommand(() => revoke(Id));
    }

    public string Id { get; }
    public string ClientLabel { get; }
    public string VerifiedAccount { get; }
    public string Scopes { get; }
    public string RevokeAutomationId => $"settings-local-mcp-revoke-{Id}";
    public string RevokeAccessibleName => $"Revoke remembered local MCP access for {ClientLabel}";
    public RelayCommand RevokeCommand { get; }

    private static string ScopeText(AgentClientScopes scopes)
    {
        var values = new List<string>();
        if (scopes.HasFlag(AgentClientScopes.ReadInterchangeContext))
        {
            values.Add("read context");
        }

        if (scopes.HasFlag(AgentClientScopes.SubmitProposals))
        {
            values.Add("submit proposals");
        }

        return $"Scopes: {string.Join(", ", values)}";
    }
}
