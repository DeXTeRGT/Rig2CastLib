namespace Rig2Cast.Simulator.Civ;

public sealed record CivSimulatorOptions
{
    public byte RadioAddress { get; init; } = 0x94;

    public byte ControllerAddress { get; init; } = 0xE0;

    public long InitialFrequencyHz { get; init; } = 14_200_000;

    public long InitialBackgroundFrequencyHz { get; init; } = 7_100_000;

    public byte InitialMode { get; init; } = 0x01;

    public byte InitialBackgroundMode { get; init; }

    public byte InitialFilter { get; init; } = 0x01;

    public byte InitialBackgroundFilter { get; init; } = 0x01;

    public bool InitialDataMode { get; init; }

    public bool InitialBackgroundDataMode { get; init; }

    public byte InitialActiveVfo { get; init; }

    public byte InitialPassbandCode { get; init; } = 0x28;

    public bool InitialSplit { get; init; }

    public bool InitialTransmitting { get; init; }

    public bool EchoCommands { get; init; }

    public bool SupportsXieguIdentity { get; init; }

    public bool SupportsXieguExtendedVfo { get; init; }

    public bool SupportsStandardIdentity { get; init; } = true;

    public int ResponseFragmentLength { get; init; } = int.MaxValue;

    public TimeSpan ResponseDelay { get; init; } = TimeSpan.Zero;
}

public enum CivSimulatorNextResponse
{
    Normal,
    Drop,
    Reject,
    Close
}
