using System.Collections.Frozen;

namespace Rig2Cast.Protocols.Declarative;

/// <summary>
/// Immutable, validated declaration of a bijection between wire values and domain values.
/// Framing, correlation, timing, and protocol error handling deliberately remain outside it.
/// </summary>
public sealed class ValueMapDescriptor<TWire, TValue>
    where TWire : notnull
    where TValue : notnull
{
    public ValueMapDescriptor(
        string name,
        IEnumerable<KeyValuePair<TWire, TValue>> mappings,
        IEqualityComparer<TWire>? wireComparer = null,
        IEqualityComparer<TValue>? valueComparer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(mappings);
        Name = name;

        var wireToValue = new Dictionary<TWire, TValue>(wireComparer);
        var valueToWire = new Dictionary<TValue, TWire>(valueComparer);
        foreach ((TWire wire, TValue value) in mappings)
        {
            if (!wireToValue.TryAdd(wire, value))
                throw new ArgumentException(
                    $"Value map '{name}' declares wire value '{wire}' more than once.",
                    nameof(mappings));
            if (!valueToWire.TryAdd(value, wire))
                throw new ArgumentException(
                    $"Value map '{name}' maps domain value '{value}' from more than one wire value.",
                    nameof(mappings));
        }
        if (wireToValue.Count == 0)
            throw new ArgumentException($"Value map '{name}' must contain at least one mapping.", nameof(mappings));

        WireToValue = wireToValue.ToFrozenDictionary(wireComparer);
        ValueToWire = valueToWire.ToFrozenDictionary(valueComparer);
    }

    public string Name { get; }

    public IReadOnlyDictionary<TWire, TValue> WireToValue { get; }

    public IReadOnlyDictionary<TValue, TWire> ValueToWire { get; }

    public bool TryDecode(TWire wireValue, out TValue value) =>
        WireToValue.TryGetValue(wireValue, out value!);

    public bool TryEncode(TValue value, out TWire wireValue) =>
        ValueToWire.TryGetValue(value, out wireValue!);
}
