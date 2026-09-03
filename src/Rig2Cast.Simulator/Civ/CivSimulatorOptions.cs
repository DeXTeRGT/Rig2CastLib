namespace Rig2Cast.Simulator.Civ;

public sealed record CivSimulatorOptions
{
    public byte RadioAddress { get; init; } = 0x94;

    public byte ControllerAddress { get; init; } = 0xE0;

    public long InitialFrequencyHz { get; init; } = 14_200_000;

    public byte InitialMode { get; init; } = 0x01;

    public byte InitialFilter { get; init; } = 0x01;

    public byte InitialPassbandCode { get; init; } = 0x28;

    public bool InitialSplit { get; init; }

    public bool InitialTransmitting { get; init; }

    public bool EchoCommands { get; init; }

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
