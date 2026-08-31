using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Radios;

namespace Rig2Cast.Runtime.Sessions;

public delegate ValueTask<IRadioDriver> RadioDriverConnector(
    CancellationToken cancellationToken = default);

public sealed record RadioConnectionSupervisorOptions
{
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    public double BackoffMultiplier { get; init; } = 2;

    internal void Validate()
    {
        if (InitialRetryDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(InitialRetryDelay));
        if (MaximumRetryDelay < InitialRetryDelay)
            throw new ArgumentOutOfRangeException(nameof(MaximumRetryDelay));
        if (BackoffMultiplier < 1 || double.IsNaN(BackoffMultiplier) || double.IsInfinity(BackoffMultiplier))
            throw new ArgumentOutOfRangeException(nameof(BackoffMultiplier));
    }
}

public sealed record RadioReconnectAttempt(
    int Attempt,
    TimeSpan NextDelay,
    string Error);

public sealed class RadioConnectionUnavailableException(ConnectionStatus status)
    : IOException($"The radio is not available while its connection state is '{status}'.")
{
    public ConnectionStatus Status { get; } = status;
}
