using Rig2Cast.Abstractions.Radios;

namespace Rig2Cast.Abstractions.Capabilities;

public sealed record ModeApplicabilityDescriptor
{
    public IReadOnlySet<RadioMode>? ReadModes { get; init; }

    public IReadOnlySet<RadioMode>? WriteModes { get; init; }

    public IReadOnlySet<RadioMode>? OperationalModes { get; init; }

    public bool CanRead(RadioMode mode) => ReadModes is null || ReadModes.Contains(mode);

    public bool CanWrite(RadioMode mode) => WriteModes is null || WriteModes.Contains(mode);

    public bool IsOperationallyRelevant(RadioMode mode) =>
        OperationalModes is null || OperationalModes.Contains(mode);
}
