using Rig2Cast.Protocols.Declarative;
using Rig2Cast.Drivers.Elecraft.K3Family;
using Rig2Cast.Drivers.Yaesu.Ftdx10;
using Rig2Cast.Abstractions.Radios;

namespace Rig2Cast.Runtime.Tests;

public sealed class DeclarativeDescriptorTests
{
    [Fact]
    public void ValueMapProvidesImmutableBidirectionalLookup()
    {
        var descriptor = new ValueMapDescriptor<char, string>(
            "test modes",
            new Dictionary<char, string> { ['1'] = "LSB", ['2'] = "USB" });

        Assert.True(descriptor.TryDecode('2', out string? decoded));
        Assert.Equal("USB", decoded);
        Assert.True(descriptor.TryEncode("LSB", out char encoded));
        Assert.Equal('1', encoded);
        Assert.False(descriptor.TryDecode('9', out _));
        Assert.False(descriptor.TryEncode("CW", out _));
    }

    [Fact]
    public void ValueMapRejectsEmptyAndAmbiguousDeclarations()
    {
        Assert.Throws<ArgumentException>(() => new ValueMapDescriptor<char, string>(
            "empty", Array.Empty<KeyValuePair<char, string>>()));
        Assert.Throws<ArgumentException>(() => new ValueMapDescriptor<char, string>(
            "duplicate wire",
            [new('1', "LSB"), new('1', "USB")]));
        Assert.Throws<ArgumentException>(() => new ValueMapDescriptor<char, string>(
            "duplicate value",
            [new('1', "USB"), new('2', "USB")]));
    }

    [Fact]
    public void ExistingModeProfilesAreValidatedBijections()
    {
        Assert.Equal(Ftdx10CatProfile.Modes.Count, Ftdx10CatProfile.ModeMap.ValueToWire.Count);
        Assert.Equal(ElecraftK3Profile.Modes.Count, ElecraftK3Profile.ModeMap.ValueToWire.Count);
    }

    [Fact]
    public void NumericFieldValidatesWidthRangeAndStep()
    {
        var field = new NumericFieldDescriptor("level", 3, 10, 250, 10);

        Assert.True(field.TryParse("010", out int minimum));
        Assert.Equal(10, minimum);
        Assert.True(field.TryParse("250", out int maximum));
        Assert.Equal(250, maximum);
        Assert.False(field.TryParse("009", out _));
        Assert.False(field.TryParse("011", out _));
        Assert.False(field.TryParse("0250", out _));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NumericFieldDescriptor("bad width", 0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NumericFieldDescriptor("bad range", 2, 50, 40));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NumericFieldDescriptor("does not fit", 2, 0, 100));
        Assert.Throws<ArgumentException>(() =>
            new NumericFieldDescriptor("unaligned", 3, 0, 255, 10));
    }

    [Fact]
    public void AsciiQueryValidatesAndParsesEnvelopeWithoutOwningFraming()
    {
        var query = new AsciiQueryDescriptor(
            "signal", "SM0", "SM0", 7,
            new NumericFieldDescriptor("raw signal", 3, 0, 255));

        Assert.True(query.TryParseValue("SM0123;", out int value));
        Assert.Equal(123, value);
        Assert.False(query.TryParseValue("SM0256;", out _));
        Assert.False(query.TryParseValue("XX0123;", out _));
        Assert.False(query.TryParseValue("SM0123", out _));
        Assert.Throws<ArgumentException>(() => new AsciiQueryDescriptor(
            "framed query", "SM0;", "SM0", 7,
            new NumericFieldDescriptor("value", 3, 0, 255)));
    }

    [Fact]
    public void AsciiQuerySetRejectsDuplicateCommandsAndAmbiguousResponses()
    {
        AsciiQueryDescriptor first = Query("first", "A", "R");
        AsciiQueryDescriptor duplicateCommand = Query("second", "A", "S");
        AsciiQueryDescriptor overlappingResponse = Query("second", "B", "RM");

        Assert.Throws<ArgumentException>(() => new AsciiQuerySet<int>(
            "duplicate command", [new(1, first), new(2, duplicateCommand)]));
        Assert.Throws<ArgumentException>(() => new AsciiQuerySet<int>(
            "ambiguous response", [new(1, first), new(2, overlappingResponse)]));

        static AsciiQueryDescriptor Query(string name, string command, string prefix) =>
            new(name, command, prefix, prefix.Length + 2,
                new NumericFieldDescriptor("value", 1, 0, 9));
    }

    [Fact]
    public void ModeApplicabilityPreservesValueOrderAndBuildsReverseApplicability()
    {
        var descriptor = new ModeApplicabilityDescriptor<string>(
            "steps",
            [RadioMode.Usb, RadioMode.Fm],
            [
                new("10hz", "10 Hz", new HashSet<RadioMode> { RadioMode.Usb }),
                new("100hz", "100 Hz", new HashSet<RadioMode> { RadioMode.Usb, RadioMode.Fm }),
                new("1khz", "1 kHz", new HashSet<RadioMode> { RadioMode.Fm })
            ],
            requiredValuesPerMode: 2,
            valueComparer: StringComparer.OrdinalIgnoreCase);

        Assert.True(descriptor.TryGetValues(RadioMode.Usb, out IReadOnlyList<ModeValueDescriptor<string>>? usb));
        Assert.Equal(["10hz", "100hz"], usb!.Select(value => value.Value));
        Assert.True(descriptor.TryGetValues(RadioMode.Fm, out IReadOnlyList<ModeValueDescriptor<string>>? fm));
        Assert.Equal(["100hz", "1khz"], fm!.Select(value => value.Value));
        Assert.False(descriptor.TryGetValues(RadioMode.Cw, out _));
        Assert.Contains(RadioMode.Fm, descriptor.Values.Single(value => value.Value == "100hz").ApplicableModes);
    }

    [Fact]
    public void ModeApplicabilityRejectsMissingUnsupportedAndDuplicateDeclarations()
    {
        Assert.Throws<ArgumentException>(() => new ModeApplicabilityDescriptor<string>(
            "missing mode",
            [RadioMode.Usb, RadioMode.Fm],
            [new("10hz", "10 Hz", new HashSet<RadioMode> { RadioMode.Usb })]));
        Assert.Throws<ArgumentException>(() => new ModeApplicabilityDescriptor<string>(
            "unsupported mode",
            [RadioMode.Usb],
            [new("1khz", "1 kHz", new HashSet<RadioMode> { RadioMode.Fm })]));
        Assert.Throws<ArgumentException>(() => new ModeApplicabilityDescriptor<string>(
            "duplicate value",
            [RadioMode.Usb],
            [
                new("10hz", "10 Hz", new HashSet<RadioMode> { RadioMode.Usb }),
                new("10HZ", "Duplicate", new HashSet<RadioMode> { RadioMode.Usb })
            ],
            valueComparer: StringComparer.OrdinalIgnoreCase));
        Assert.Throws<ArgumentException>(() => new ModeApplicabilityDescriptor<string>(
            "wrong count",
            [RadioMode.Usb],
            [new("10hz", "10 Hz", new HashSet<RadioMode> { RadioMode.Usb })],
            requiredValuesPerMode: 2));
    }

    [Fact]
    public void ConditionalValuesApplyTypedContextToLookupAndMetadata()
    {
        var descriptor = new ConditionalValueSetDescriptor<bool, string, char>(
            "preamps",
            [
                new("off", '0', "Off", _ => true),
                new("preamp2", '2', "Preamp 2", hasOption => hasOption)
            ],
            valueComparer: StringComparer.OrdinalIgnoreCase);

        Assert.Equal(["off"], descriptor.GetAvailable(false).Select(value => value.Value));
        Assert.Equal(["off", "preamp2"], descriptor.GetAvailable(true).Select(value => value.Value));
        Assert.True(descriptor.TryEncode(true, "PREAMP2", out char code));
        Assert.Equal('2', code);
        Assert.False(descriptor.TryEncode(false, "preamp2", out _));
        Assert.True(descriptor.TryDecode(true, '2', out string? value));
        Assert.Equal("preamp2", value);
        Assert.False(descriptor.TryDecode(false, '2', out _));
    }

    [Fact]
    public void ConditionalValuesRejectEmptyDuplicateValueAndDuplicateWireDeclarations()
    {
        Assert.Throws<ArgumentException>(() =>
            new ConditionalValueSetDescriptor<bool, string, char>("empty", []));
        Assert.Throws<ArgumentException>(() => new ConditionalValueSetDescriptor<bool, string, char>(
            "duplicate value",
            [new("off", '0', "Off", _ => true), new("OFF", '1', "Off alias", _ => true)],
            valueComparer: StringComparer.OrdinalIgnoreCase));
        Assert.Throws<ArgumentException>(() => new ConditionalValueSetDescriptor<bool, string, char>(
            "duplicate wire",
            [new("off", '0', "Off", _ => true), new("on", '0', "On", _ => true)]));
    }
}
