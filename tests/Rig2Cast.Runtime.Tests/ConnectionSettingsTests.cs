using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Transports;
using Rig2Cast.Drivers.Icom.Ic7300;
using Rig2Cast.Transports.Serial;

namespace Rig2Cast.Runtime.Tests;

public sealed class ConnectionSettingsTests
{
    [Fact]
    public void ModelDefaultIsParsedIntoDeclaredType()
    {
        RadioModelDescriptor model = Assert.Single(new Ic7300DriverFactory().Descriptor.Models);

        ResolvedConnectionSettings settings = ConnectionSettingsResolver.Resolve(model);

        Assert.Equal(0x94, settings.Get<byte>("icom.civAddress"));
        Assert.Equal(ConnectionSettingValueSource.ModelDefault,
            settings.Values["icom.civAddress"].Source);
    }

    [Fact]
    public void ExplicitValueWinsOverApplicationAndModelDefaults()
    {
        RadioModelDescriptor model = Assert.Single(new Ic7300DriverFactory().Descriptor.Models);

        ResolvedConnectionSettings settings = ConnectionSettingsResolver.Resolve(
            model,
            new Dictionary<string, string> { ["icom.civAddress"] = "0x70" },
            new Dictionary<string, string> { ["icom.civAddress"] = "88" });

        Assert.Equal(0x70, settings.Get<byte>("icom.civAddress"));
        Assert.Equal(ConnectionSettingValueSource.Explicit,
            settings.Values["icom.civAddress"].Source);
    }

    [Fact]
    public void ApplicationCanOverrideDefinitionMetadataExplicitly()
    {
        RadioModelDescriptor model = Assert.Single(new Ic7300DriverFactory().Descriptor.Models);
        var replacement = new ConnectionSettingDefinition(
            "icom.civAddress", ConnectionSettingValueType.Byte, "Custom address",
            "Restricted by the host application.", true, "70",
            ConnectionSettingFormat.Hexadecimal, 0x70, 0x70);

        ResolvedConnectionSettings settings = ConnectionSettingsResolver.Resolve(
            model, definitionOverrides: new Dictionary<string, ConnectionSettingDefinition>
            {
                [replacement.Id] = replacement
            });

        Assert.Equal(0x70, settings.Get<byte>(replacement.Id));
        Assert.Equal("Custom address", settings.Values[replacement.Id].Definition.DisplayName);
    }

    [Theory]
    [InlineData("1000")]
    [InlineData("not-hex")]
    public void InvalidTypedValueIsRejectedBeforeDriverOpen(string value)
    {
        RadioModelDescriptor model = Assert.Single(new Ic7300DriverFactory().Descriptor.Models);

        Assert.ThrowsAny<ArgumentException>(() => ConnectionSettingsResolver.Resolve(
            model, new Dictionary<string, string> { ["icom.civAddress"] = value }));
    }

    [Fact]
    public void UnknownSettingIsRejected()
    {
        RadioModelDescriptor model = Assert.Single(new Ic7300DriverFactory().Descriptor.Models);

        Assert.Throws<ArgumentException>(() => ConnectionSettingsResolver.Resolve(
            model, new Dictionary<string, string> { ["icom.civAdress"] = "70" }));
    }

    [Fact]
    public void PortDiscoveryReturnsDistinctNaturallySortedDescriptors()
    {
        var discovery = new SystemSerialPortDiscovery(() => ["COM10", "COM2", "COM2", "/dev/ttyUSB0"]);

        IReadOnlyList<SerialPortDescriptor> ports = discovery.GetPorts();

        Assert.Equal(["/dev/ttyUSB0", "COM2", "COM10"], ports.Select(port => port.PortName));
        Assert.All(ports, port => Assert.Equal(port.PortName, port.DisplayName));
    }
}
