using System.Security.Cryptography;

using DotOrbit.Mcp.Ipc;

namespace DotOrbit.Mcp.Ipc.Tests;

public sealed class CapabilityAuthorityTests
{
    [Fact]
    public void IssuedCapabilityIs256BitScopedAndSingleUse()
    {
        var time = new ManualTimeProvider();
        using var authority = new CapabilityAuthority(time, new SequenceRandomBytes());

        var token = authority.Issue([LocalMcpProtocol.ReadScope]);
        var authorization = authority.TryConsume(token);

        Assert.Equal(32, token.Length);
        Assert.NotNull(authorization);
        Assert.Contains(LocalMcpProtocol.ReadScope, authorization.Scopes);
        Assert.DoesNotContain(LocalMcpProtocol.SubmitScope, authorization.Scopes);
        Assert.Null(authority.TryConsume(token));
    }

    [Fact]
    public void ForgedCapabilityDoesNotConsumeTheRealGrant()
    {
        using var authority = new CapabilityAuthority(TimeProvider.System, new SequenceRandomBytes());
        var token = authority.Issue([LocalMcpProtocol.ReadScope, LocalMcpProtocol.SubmitScope]);
        var forged = token.ToArray();
        forged[0] ^= 0xff;

        Assert.Null(authority.TryConsume(forged));
        Assert.NotNull(authority.TryConsume(token));
    }

    [Fact]
    public void ExpiredCapabilityCannotBeConsumed()
    {
        var time = new ManualTimeProvider();
        using var authority = new CapabilityAuthority(time, new SequenceRandomBytes());
        var token = authority.Issue([LocalMcpProtocol.ReadScope]);

        time.Advance(LocalMcpProtocol.GrantLifetime + TimeSpan.FromTicks(1));

        Assert.Null(authority.TryConsume(token));
    }

    [Fact]
    public void UnsupportedScopeIsRejectedBeforeTokenIssuance()
    {
        using var authority = new CapabilityAuthority(TimeProvider.System, new SequenceRandomBytes());

        var exception = Assert.Throws<ProtocolException>(() => authority.Issue(["workspace.apply"]));

        Assert.Equal("scope-invalid", exception.Code);
    }

    private sealed class SequenceRandomBytes : IRandomBytes
    {
        public void Fill(Span<byte> bytes)
        {
            for (var index = 0; index < bytes.Length; index++)
            {
                bytes[index] = checked((byte)(index + 1));
            }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
