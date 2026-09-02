using System.Collections.Frozen;

namespace Rig2Cast.Protocols.Declarative;

public sealed record AsciiQueryDescriptor
{
    public AsciiQueryDescriptor(
        string displayName,
        string query,
        string responsePrefix,
        int responseLength,
        NumericFieldDescriptor valueField,
        StringComparison responseComparison = StringComparison.Ordinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(responsePrefix);
        ArgumentNullException.ThrowIfNull(valueField);
        if (responseComparison is not StringComparison.Ordinal and not StringComparison.OrdinalIgnoreCase)
            throw new ArgumentOutOfRangeException(nameof(responseComparison));
        if (query.EndsWith(';') || responsePrefix.EndsWith(';'))
            throw new ArgumentException("Query and response prefix declarations omit the frame terminator.");
        int minimumLength = responsePrefix.Length + valueField.Width + 1;
        if (responseLength < minimumLength)
            throw new ArgumentOutOfRangeException(
                nameof(responseLength), $"Response length must be at least {minimumLength} characters.");

        DisplayName = displayName;
        Query = query;
        ResponsePrefix = responsePrefix;
        ResponseLength = responseLength;
        ValueField = valueField;
        ResponseComparison = responseComparison;
    }

    public string DisplayName { get; }
    public string Query { get; }
    public string ResponsePrefix { get; }
    public int ResponseLength { get; }
    public NumericFieldDescriptor ValueField { get; }
    public StringComparison ResponseComparison { get; }

    public bool HasValidEnvelope(string response) =>
        response.Length == ResponseLength &&
        response[^1] == ';' &&
        response.StartsWith(ResponsePrefix, ResponseComparison);

    public bool TryParseValue(string response, out int value)
    {
        value = default;
        return HasValidEnvelope(response) &&
            ValueField.TryParse(response.AsSpan(ResponsePrefix.Length, ValueField.Width), out value);
    }
}

public sealed class AsciiQuerySet<TKey> where TKey : notnull
{
    public AsciiQuerySet(string name, IEnumerable<KeyValuePair<TKey, AsciiQueryDescriptor>> queries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(queries);
        Name = name;

        var entries = new Dictionary<TKey, AsciiQueryDescriptor>();
        var commands = new HashSet<string>(StringComparer.Ordinal);
        var responses = new List<AsciiQueryDescriptor>();
        foreach ((TKey key, AsciiQueryDescriptor descriptor) in queries)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            if (!entries.TryAdd(key, descriptor))
                throw new ArgumentException($"Query set '{name}' declares key '{key}' more than once.", nameof(queries));
            if (!commands.Add(descriptor.Query))
                throw new ArgumentException(
                    $"Query set '{name}' declares command '{descriptor.Query}' more than once.", nameof(queries));
            if (responses.Any(existing => ResponsesOverlap(existing, descriptor)))
                throw new ArgumentException(
                    $"Query set '{name}' has ambiguous response prefix '{descriptor.ResponsePrefix}'.", nameof(queries));
            responses.Add(descriptor);
        }
        if (entries.Count == 0)
            throw new ArgumentException($"Query set '{name}' must contain at least one query.", nameof(queries));
        Entries = entries.ToFrozenDictionary();
    }

    public string Name { get; }
    public IReadOnlyDictionary<TKey, AsciiQueryDescriptor> Entries { get; }

    private static bool ResponsesOverlap(AsciiQueryDescriptor left, AsciiQueryDescriptor right)
    {
        StringComparison comparison =
            left.ResponseComparison == StringComparison.OrdinalIgnoreCase ||
            right.ResponseComparison == StringComparison.OrdinalIgnoreCase
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        return left.ResponsePrefix.StartsWith(right.ResponsePrefix, comparison) ||
            right.ResponsePrefix.StartsWith(left.ResponsePrefix, comparison);
    }
}
