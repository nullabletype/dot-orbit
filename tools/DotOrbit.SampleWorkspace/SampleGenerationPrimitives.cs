using DotOrbit.Core.Workspaces;

namespace DotOrbit.SampleWorkspace;

internal sealed class DeterministicIdentifierGenerator : IIdentifierGenerator
{
    private long _next;

    public string NewIdentifier() => (++_next).ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class AdjustableTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = initialUtcNow.ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void SetDate(DateOnly date, int minute)
    {
        _utcNow = new DateTimeOffset(
            date.Year,
            date.Month,
            date.Day,
            9,
            minute,
            0,
            TimeSpan.Zero);
    }
}
