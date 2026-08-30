namespace Rig2Cast.Abstractions.Radios;

public enum VfoId
{
    Current,
    A,
    B,
    Main,
    Sub,
    Memory
}

public enum RadioMode
{
    Unknown,
    Lsb,
    Usb,
    Cw,
    CwReverse,
    Am,
    AmNarrow,
    Fm,
    FmNarrow,
    DataLsb,
    DataUsb,
    Rtty,
    RttyReverse
}

public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Faulted
}
