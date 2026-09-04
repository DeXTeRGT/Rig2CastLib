using System.IO.Ports;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Drivers.Elecraft.K3Family;
using Rig2Cast.Drivers.Icom.Ic7300;
using Rig2Cast.Drivers.Xiegu.G90;
using Rig2Cast.Drivers.Yaesu.Ftdx10;
using Rig2Cast.Transports.Serial;

namespace Rig2Cast.Runtime.Tests;

public sealed class SerialRadioTransportFactoryTests
{
    [Fact]
    public void BuiltInModelsPublishTypedSerialProfiles()
    {
        RadioModelDescriptor[] models =
        [
            .. new Ftdx10DriverFactory().Descriptor.Models,
            .. new ElecraftK3DriverFactory().Descriptor.Models,
            .. new Ic7300DriverFactory().Descriptor.Models,
            .. new G90DriverFactory().Descriptor.Models
        ];

        Assert.All(models, model => Assert.NotNull(model.SerialProfile));
        RadioModelDescriptor ftdx10 = Assert.Single(
            models.Where(model => model.Id == Ftdx10CatProfile.ModelId));
        Assert.Equal(RadioSerialStopBits.Two, ftdx10.SerialProfile!.StopBits.DefaultValue);
        Assert.Equal(RadioSerialHandshake.RequestToSend,
            ftdx10.SerialProfile.Handshake.DefaultValue);
    }

    [Fact]
    public void ModelDefaultsProduceTypedTransportOptions()
    {
        RadioModelDescriptor model = Model();
        SerialConnectionSettings settings = SerialConnectionSettings.FromModel(model, "COM16");

        SerialRadioTransportOptions options = SerialRadioTransportFactory.CreateOptions(model, settings);

        Assert.Equal("COM16", options.PortName);
        Assert.Equal(19_200, options.BaudRate);
        Assert.Equal(8, options.DataBits);
        Assert.Equal(Parity.None, options.Parity);
        Assert.Equal(StopBits.One, options.StopBits);
        Assert.Equal(Handshake.None, options.Handshake);
    }

    [Fact]
    public void ConfigurableSettingsAcceptConstrainedOverride()
    {
        RadioModelDescriptor model = Model() with
        {
            SerialProfile = SerialConnectionProfile.Create() with
            {
                ReadTimeout = new SerialSetting<TimeSpan>(
                    TimeSpan.FromSeconds(2), true,
                    new HashSet<TimeSpan> { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) })
            }
        };
        SerialConnectionSettings settings = SerialConnectionSettings.FromModel(model, "COM16") with
        {
            ReadTimeout = TimeSpan.FromSeconds(1)
        };

        Assert.Equal(TimeSpan.FromSeconds(1),
            SerialRadioTransportFactory.CreateOptions(model, settings).ReadTimeout);
    }

    [Fact]
    public void FixedSettingRejectsOrdinaryOverrideButAllowsExplicitUnsafeOverride()
    {
        RadioModelDescriptor model = Model();
        SerialConnectionSettings settings = SerialConnectionSettings.FromModel(model, "COM16") with
        {
            StopBits = RadioSerialStopBits.Two
        };

        Assert.Throws<ArgumentException>(() =>
            SerialRadioTransportFactory.CreateOptions(model, settings));
        Assert.Equal(StopBits.Two,
            SerialRadioTransportFactory.CreateOptions(model, settings, allowUnsafeOverride: true).StopBits);
    }

    [Fact]
    public void UnsupportedBaudRequiresExplicitUnsafeOverride()
    {
        RadioModelDescriptor model = Model();
        SerialConnectionSettings settings = SerialConnectionSettings.FromModel(model, "COM16") with
        {
            BaudRate = 9_600
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SerialRadioTransportFactory.CreateOptions(model, settings));
        Assert.Equal(9_600,
            SerialRadioTransportFactory.CreateOptions(model, settings, allowUnsafeOverride: true).BaudRate);
    }

    [Fact]
    public void LegacyPluginDescriptorResolvesStringSerialDefaultsCentrally()
    {
        RadioModelDescriptor model = new(
            "plugin.radio", "Plugin", "Radio",
            new HashSet<RadioTransportKind> { RadioTransportKind.Serial },
            [38_400], 38_400,
            new Dictionary<string, string>
            {
                ["serial.dataBits"] = "7",
                ["serial.stopBits"] = "Two",
                ["serial.parity"] = "Even",
                ["serial.handshake"] = "XOnXOff"
            });

        SerialRadioTransportOptions options = SerialRadioTransportFactory.CreateOptions(
            model, SerialConnectionSettings.FromModel(model, "COM9"));

        Assert.Equal(7, options.DataBits);
        Assert.Equal(StopBits.Two, options.StopBits);
        Assert.Equal(Parity.Even, options.Parity);
        Assert.Equal(Handshake.XOnXOff, options.Handshake);
    }

    private static RadioModelDescriptor Model() => new(
        "test.radio", "Test", "Radio",
        new HashSet<RadioTransportKind> { RadioTransportKind.Serial },
        [19_200], 19_200)
    {
        SerialProfile = SerialConnectionProfile.Create()
    };
}
