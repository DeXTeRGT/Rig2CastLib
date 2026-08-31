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
    DataFm,
    DataFmNarrow,
    Psk,
    Rtty,
    RttyReverse
}

public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Faulted
}
