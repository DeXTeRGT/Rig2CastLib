using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rig2Cast.Abstractions.Radios;

[JsonConverter(typeof(ReceiverIdJsonConverter))]
public readonly record struct ReceiverId
{
    public ReceiverId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 64 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '.' and not '_'))
        {
            throw new ArgumentException(
                "A receiver identifier must contain 1-64 ASCII letters, digits, '.', '_' or '-'.",
                nameof(value));
        }

        Value = value.ToLowerInvariant();
    }

    public string Value { get; }

    public static ReceiverId Main => new("main");

    public static ReceiverId Sub => new("sub");

    public static ReceiverId Indexed(int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        return new($"receiver-{index}");
    }

    public override string ToString() => Value;
}

public sealed class ReceiverIdJsonConverter : JsonConverter<ReceiverId>
{
    public override ReceiverId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("Receiver identifier cannot be null."));

    public override void Write(Utf8JsonWriter writer, ReceiverId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);

    public override ReceiverId ReadAsPropertyName(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString() ?? throw new JsonException("Receiver identifier cannot be null."));

    public override void WriteAsPropertyName(
        Utf8JsonWriter writer, ReceiverId value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.Value);
}

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
