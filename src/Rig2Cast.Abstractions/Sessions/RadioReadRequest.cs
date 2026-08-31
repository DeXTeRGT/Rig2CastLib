namespace Rig2Cast.Abstractions.Sessions;

public enum RadioReadConsistency
{
    Cached,
    Fresh,
    ForceRefresh
}

public sealed record RadioReadRequest
{
    private RadioReadRequest(RadioReadConsistency consistency, TimeSpan maximumAge)
    {
        Consistency = consistency;
        MaximumAge = maximumAge;
    }

    public RadioReadConsistency Consistency { get; }

    public TimeSpan MaximumAge { get; }

    public static RadioReadRequest Cached { get; } = new(RadioReadConsistency.Cached, Timeout.InfiniteTimeSpan);

    public static RadioReadRequest ForceRefresh { get; } = new(RadioReadConsistency.ForceRefresh, TimeSpan.Zero);

    public static RadioReadRequest FreshWithin(TimeSpan maximumAge)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAge, TimeSpan.Zero);
        return new RadioReadRequest(RadioReadConsistency.Fresh, maximumAge);
    }
}
