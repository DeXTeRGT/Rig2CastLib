using System.Collections.Frozen;

namespace Rig2Cast.Protocols.Declarative;

public sealed record ConditionalValueDescriptor<TContext, TValue, TWire>(
    TValue Value,
    TWire WireValue,
    string DisplayName,
    Func<TContext, bool> IsAvailable)
    where TValue : notnull
    where TWire : notnull;

public sealed class ConditionalValueSetDescriptor<TContext, TValue, TWire>
    where TValue : notnull
    where TWire : notnull
{
    private readonly FrozenDictionary<TValue, ConditionalValueDescriptor<TContext, TValue, TWire>> _byValue;
    private readonly FrozenDictionary<TWire, ConditionalValueDescriptor<TContext, TValue, TWire>> _byWire;

    public ConditionalValueSetDescriptor(
        string name,
        IEnumerable<ConditionalValueDescriptor<TContext, TValue, TWire>> values,
        IEqualityComparer<TValue>? valueComparer = null,
        IEqualityComparer<TWire>? wireComparer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(values);
        var byValue = new Dictionary<TValue, ConditionalValueDescriptor<TContext, TValue, TWire>>(valueComparer);
        var byWire = new Dictionary<TWire, ConditionalValueDescriptor<TContext, TValue, TWire>>(wireComparer);
        foreach (ConditionalValueDescriptor<TContext, TValue, TWire> value in values)
        {
            ArgumentNullException.ThrowIfNull(value);
            ArgumentException.ThrowIfNullOrWhiteSpace(value.DisplayName);
            ArgumentNullException.ThrowIfNull(value.IsAvailable);
            if (!byValue.TryAdd(value.Value, value))
                throw new ArgumentException(
                    $"Conditional value set '{name}' declares value '{value.Value}' more than once.", nameof(values));
            if (!byWire.TryAdd(value.WireValue, value))
                throw new ArgumentException(
                    $"Conditional value set '{name}' declares wire value '{value.WireValue}' more than once.", nameof(values));
        }
        if (byValue.Count == 0)
            throw new ArgumentException($"Conditional value set '{name}' must contain at least one value.", nameof(values));

        Name = name;
        _byValue = byValue.ToFrozenDictionary(valueComparer);
        _byWire = byWire.ToFrozenDictionary(wireComparer);
        Values = Array.AsReadOnly(byValue.Values.ToArray());
    }

    public string Name { get; }
    public IReadOnlyList<ConditionalValueDescriptor<TContext, TValue, TWire>> Values { get; }

    public IReadOnlyList<ConditionalValueDescriptor<TContext, TValue, TWire>> GetAvailable(TContext context) =>
        Array.AsReadOnly(Values.Where(value => value.IsAvailable(context)).ToArray());

    public bool TryEncode(TContext context, TValue value, out TWire wireValue)
    {
        if (_byValue.TryGetValue(value, out ConditionalValueDescriptor<TContext, TValue, TWire>? descriptor) &&
            descriptor.IsAvailable(context))
        {
            wireValue = descriptor.WireValue;
            return true;
        }
        wireValue = default!;
        return false;
    }

    public bool TryDecode(TContext context, TWire wireValue, out TValue value)
    {
        if (_byWire.TryGetValue(wireValue, out ConditionalValueDescriptor<TContext, TValue, TWire>? descriptor) &&
            descriptor.IsAvailable(context))
        {
            value = descriptor.Value;
            return true;
        }
        value = default!;
        return false;
    }
}
