using System.Collections.Frozen;
using Rig2Cast.Abstractions.Radios;

namespace Rig2Cast.Protocols.Declarative;

public sealed record ModeValueDescriptor<TValue>(
    TValue Value,
    string DisplayName,
    IReadOnlySet<RadioMode> ApplicableModes)
    where TValue : notnull;

public sealed class ModeApplicabilityDescriptor<TValue> where TValue : notnull
{
    private readonly FrozenDictionary<RadioMode, IReadOnlyList<ModeValueDescriptor<TValue>>> _valuesByMode;

    public ModeApplicabilityDescriptor(
        string name,
        IEnumerable<RadioMode> supportedModes,
        IEnumerable<ModeValueDescriptor<TValue>> values,
        int? requiredValuesPerMode = null,
        IEqualityComparer<TValue>? valueComparer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(supportedModes);
        ArgumentNullException.ThrowIfNull(values);
        if (requiredValuesPerMode is not null)
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requiredValuesPerMode.Value);

        FrozenSet<RadioMode> modes = supportedModes.ToFrozenSet();
        if (modes.Count == 0)
            throw new ArgumentException($"Mode applicability '{name}' must support at least one mode.", nameof(supportedModes));

        var declaredValues = new List<ModeValueDescriptor<TValue>>();
        var uniqueValues = new HashSet<TValue>(valueComparer);
        foreach (ModeValueDescriptor<TValue> value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            ArgumentException.ThrowIfNullOrWhiteSpace(value.DisplayName);
            ArgumentNullException.ThrowIfNull(value.ApplicableModes);
            if (!uniqueValues.Add(value.Value))
                throw new ArgumentException(
                    $"Mode applicability '{name}' declares value '{value.Value}' more than once.", nameof(values));
            FrozenSet<RadioMode> applicableModes = value.ApplicableModes.ToFrozenSet();
            if (applicableModes.Count == 0)
                throw new ArgumentException(
                    $"Value '{value.Value}' in mode applicability '{name}' has no applicable modes.", nameof(values));
            foreach (RadioMode mode in applicableModes)
            {
                if (!modes.Contains(mode))
                    throw new ArgumentException(
                        $"Value '{value.Value}' references unsupported mode '{mode}'.", nameof(values));
            }
            declaredValues.Add(value with { ApplicableModes = applicableModes });
        }
        if (declaredValues.Count == 0)
            throw new ArgumentException($"Mode applicability '{name}' must declare at least one value.", nameof(values));

        var valuesByMode = new Dictionary<RadioMode, IReadOnlyList<ModeValueDescriptor<TValue>>>();
        foreach (RadioMode mode in modes)
        {
            ModeValueDescriptor<TValue>[] applicable = declaredValues
                .Where(value => value.ApplicableModes.Contains(mode))
                .ToArray();
            if (applicable.Length == 0)
                throw new ArgumentException(
                    $"Mode applicability '{name}' does not declare a value for mode '{mode}'.", nameof(values));
            if (requiredValuesPerMode is int required && applicable.Length != required)
                throw new ArgumentException(
                    $"Mode '{mode}' in applicability '{name}' requires exactly {required} values but has {applicable.Length}.",
                    nameof(values));
            valuesByMode.Add(mode, Array.AsReadOnly(applicable));
        }

        Name = name;
        SupportedModes = modes;
        Values = Array.AsReadOnly(declaredValues.ToArray());
        _valuesByMode = valuesByMode.ToFrozenDictionary();
    }

    public string Name { get; }
    public IReadOnlySet<RadioMode> SupportedModes { get; }
    public IReadOnlyList<ModeValueDescriptor<TValue>> Values { get; }

    public bool TryGetValues(RadioMode mode, out IReadOnlyList<ModeValueDescriptor<TValue>> values) =>
        _valuesByMode.TryGetValue(mode, out values!);
}
