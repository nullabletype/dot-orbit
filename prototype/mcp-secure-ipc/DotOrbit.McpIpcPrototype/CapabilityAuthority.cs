using System.Security.Cryptography;

namespace DotOrbit.McpIpcPrototype;

internal interface IRandomBytes
{
    void Fill(Span<byte> bytes);
}

internal sealed class CryptographicRandomBytes : IRandomBytes
{
    public void Fill(Span<byte> bytes) => RandomNumberGenerator.Fill(bytes);
}

internal sealed record SessionAuthorization(IReadOnlySet<string> Scopes);

internal sealed class CapabilityAuthority(TimeProvider timeProvider, IRandomBytes randomBytes) : IDisposable
{
    private readonly object _gate = new();
    private byte[]? _token;
    private long _issuedTimestamp;
    private HashSet<string>? _scopes;

    public byte[] Issue(IEnumerable<string> scopes)
    {
        var grantedScopes = scopes.ToHashSet(StringComparer.Ordinal);
        if (grantedScopes.Count == 0
            || grantedScopes.Any(scope => scope is not ("synthetic.read" or "synthetic.submit")))
        {
            throw new ProtocolException("scope-invalid");
        }

        lock (_gate)
        {
            ClearToken();
            _token = new byte[32];
            randomBytes.Fill(_token);
            _issuedTimestamp = timeProvider.GetTimestamp();
            _scopes = grantedScopes;
            return _token.ToArray();
        }
    }

    public SessionAuthorization? TryConsume(ReadOnlySpan<byte> presentedToken)
    {
        lock (_gate)
        {
            if (_token is null || _scopes is null)
            {
                return null;
            }

            var expired = timeProvider.GetElapsedTime(_issuedTimestamp) > PrototypeProtocol.GrantLifetime;
            var matches = presentedToken.Length == _token.Length
                && CryptographicOperations.FixedTimeEquals(presentedToken, _token);
            if (expired || !matches)
            {
                return null;
            }

            var authorization = new SessionAuthorization(_scopes);
            ClearToken();
            return authorization;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            ClearToken();
        }
    }

    private void ClearToken()
    {
        if (_token is not null)
        {
            CryptographicOperations.ZeroMemory(_token);
        }

        _token = null;
        _scopes = null;
    }
}
