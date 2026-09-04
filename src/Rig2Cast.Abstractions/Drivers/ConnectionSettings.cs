using System.Collections.ObjectModel;
using System.Globalization;

namespace Rig2Cast.Abstractions.Drivers;

public enum ConnectionSettingValueType
{
    Text,
    Boolean,
    WholeNumber,
    Byte
}

public enum ConnectionSettingFormat
{
    Default,
    Base10,
    Hexadecimal
}

public enum ConnectionSettingValueSource
{
    ModelDefault,
    ApplicationDefault,
    Explicit
}

/// <summary>
/// Describes one model-specific connection setting. Values supplied by applications remain text
/// at the boundary and are parsed into the declared type by <see cref="ConnectionSettingsResolver"/>.
/// </summary>
public sealed record ConnectionSettingDefinition(
    string Id,
    ConnectionSettingValueType ValueType,
    string DisplayName,
    string Description,
    bool IsRequired = false,
    string? DefaultValue = null,
    ConnectionSettingFormat Format = ConnectionSettingFormat.Default,
    long? Minimum = null,
    long? Maximum = null,
    IReadOnlyList<string>? Choices = null)
{
    public ConnectionSettingDefinition Validate()
    {
        if (string.IsNullOrWhiteSpace(Id))
            throw new ArgumentException("A connection-setting identifier is required.", nameof(Id));
        if (string.IsNullOrWhiteSpace(DisplayName))
            throw new ArgumentException($"Connection setting '{Id}' requires a display name.", nameof(DisplayName));
        if (Minimum is not null && Maximum is not null && Minimum > Maximum)
            throw new ArgumentException($"Connection setting '{Id}' has an invalid numeric range.");
        if (DefaultValue is not null)
            _ = ConnectionSettingsResolver.Parse(this, DefaultValue);
        return this;
    }
}

public sealed record ResolvedConnectionSetting(
    ConnectionSettingDefinition Definition,
    object Value,
    string Text,
    ConnectionSettingValueSource Source);

public sealed class ResolvedConnectionSettings
{
    private readonly ReadOnlyDictionary<string, ResolvedConnectionSetting> _values;

    internal ResolvedConnectionSettings(
        string modelId,
        IReadOnlyDictionary<string, ResolvedConnectionSetting> values)
    {
        ModelId = modelId;
        _values = new ReadOnlyDictionary<string, ResolvedConnectionSetting>(
            new Dictionary<string, ResolvedConnectionSetting>(values, StringComparer.OrdinalIgnoreCase));
    }

    public string ModelId { get; }
    public IReadOnlyDictionary<string, ResolvedConnectionSetting> Values => _values;

    public T Get<T>(string id)
    {
        if (!_values.TryGetValue(id, out ResolvedConnectionSetting? setting))
            throw new KeyNotFoundException($"Connection setting '{id}' was not resolved for model '{ModelId}'.");
        if (setting.Value is T value)
            return value;
        throw new InvalidOperationException(
            $"Connection setting '{id}' contains {setting.Value.GetType().Name}, not {typeof(T).Name}.");
    }
}

public static class ConnectionSettingsResolver
{
    public static ResolvedConnectionSettings Resolve(
        RadioModelDescriptor model,
        IReadOnlyDictionary<string, string>? explicitValues = null,
        IReadOnlyDictionary<string, string>? applicationDefaults = null,
        IReadOnlyDictionary<string, ConnectionSettingDefinition>? definitionOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        explicitValues ??= EmptyValues;
        applicationDefaults ??= EmptyValues;
        definitionOverrides ??= EmptyDefinitions;

        var definitions = new Dictionary<string, ConnectionSettingDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (ConnectionSettingDefinition definition in model.ConnectionSettings)
            definitions.Add(definition.Id, definition.Validate());
        foreach ((string id, ConnectionSettingDefinition definition) in definitionOverrides)
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(id, definition.Id))
                throw new ArgumentException($"Definition override key '{id}' does not match definition ID '{definition.Id}'.");
            if (definitions.TryGetValue(id, out ConnectionSettingDefinition? original) &&
                original.ValueType != definition.ValueType)
                throw new ArgumentException(
                    $"Definition override '{id}' cannot change value type from {original.ValueType} to {definition.ValueType}.");
            definitions[id] = definition.Validate();
        }

        ValidateKnownIds(explicitValues, definitions);
        ValidateKnownIds(applicationDefaults, definitions);

        var result = new Dictionary<string, ResolvedConnectionSetting>(StringComparer.OrdinalIgnoreCase);
        foreach (ConnectionSettingDefinition definition in definitions.Values)
        {
            string? text;
            ConnectionSettingValueSource source;
            if (explicitValues.TryGetValue(definition.Id, out text))
                source = ConnectionSettingValueSource.Explicit;
            else if (applicationDefaults.TryGetValue(definition.Id, out text))
                source = ConnectionSettingValueSource.ApplicationDefault;
            else
            {
                text = definition.DefaultValue;
                source = ConnectionSettingValueSource.ModelDefault;
            }

            if (text is null)
            {
                if (definition.IsRequired)
                    throw new ArgumentException($"Required connection setting '{definition.Id}' has no value.");
                continue;
            }

            object value = Parse(definition, text);
            result.Add(definition.Id, new(definition, value, text, source));
        }

        return new(model.Id, result);
    }

    public static ResolvedConnectionSettings ResolveForFactory(
        RadioConnectionOptions options,
        RadioModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(model);
        if (options.ResolvedSettings is { } resolved)
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(resolved.ModelId, model.Id))
                throw new ArgumentException(
                    $"Resolved settings for model '{resolved.ModelId}' cannot be used with model '{model.Id}'.",
                    nameof(options));
            return resolved;
        }
        return Resolve(model, options.Settings);
    }

    internal static object Parse(ConnectionSettingDefinition definition, string text)
    {
        object value = definition.ValueType switch
        {
            ConnectionSettingValueType.Text => text,
            ConnectionSettingValueType.Boolean when bool.TryParse(text, out bool parsed) => parsed,
            ConnectionSettingValueType.WholeNumber => ParseInt32(definition, text),
            ConnectionSettingValueType.Byte => ParseByte(definition, text),
            _ => throw InvalidValue(definition, text)
        };

        if (definition.Choices is { Count: > 0 } choices &&
            !choices.Contains(text, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Setting '{definition.Id}' must be one of: {string.Join(", ", choices)}.", definition.Id);
        if (value is IConvertible convertible && definition.ValueType is not ConnectionSettingValueType.Text)
        {
            long numeric = convertible.ToInt64(CultureInfo.InvariantCulture);
            if (definition.Minimum is long minimum && numeric < minimum ||
                definition.Maximum is long maximum && numeric > maximum)
                throw new ArgumentOutOfRangeException(
                    definition.Id, text,
                    $"Setting '{definition.Id}' must be between {definition.Minimum} and {definition.Maximum}.");
        }
        return value;
    }

    private static int ParseInt32(ConnectionSettingDefinition definition, string text)
    {
        NumberStyles style = definition.Format == ConnectionSettingFormat.Hexadecimal
            ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer;
        ReadOnlySpan<char> value = StripHexPrefix(text, definition.Format);
        if (int.TryParse(value, style, CultureInfo.InvariantCulture, out int parsed))
            return parsed;
        throw InvalidValue(definition, text);
    }

    private static byte ParseByte(ConnectionSettingDefinition definition, string text)
    {
        NumberStyles style = definition.Format == ConnectionSettingFormat.Hexadecimal
            ? NumberStyles.AllowHexSpecifier : NumberStyles.Integer;
        ReadOnlySpan<char> value = StripHexPrefix(text, definition.Format);
        if (byte.TryParse(value, style, CultureInfo.InvariantCulture, out byte parsed))
            return parsed;
        throw InvalidValue(definition, text);
    }

    private static ReadOnlySpan<char> StripHexPrefix(string text, ConnectionSettingFormat format) =>
        format == ConnectionSettingFormat.Hexadecimal && text.AsSpan().StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? text.AsSpan()[2..] : text.AsSpan();

    private static ArgumentException InvalidValue(ConnectionSettingDefinition definition, string text) =>
        new($"Setting '{definition.Id}' value '{text}' is not a valid {definition.ValueType} " +
            $"in {definition.Format} format.", definition.Id);

    private static void ValidateKnownIds(
        IReadOnlyDictionary<string, string> values,
        Dictionary<string, ConnectionSettingDefinition> definitions)
    {
        foreach (string id in values.Keys)
        {
            if (!definitions.ContainsKey(id))
                throw new ArgumentException($"Connection setting '{id}' is not advertised by this model.", id);
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyValues =
        new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<string, ConnectionSettingDefinition> EmptyDefinitions =
        new Dictionary<string, ConnectionSettingDefinition>();
}
