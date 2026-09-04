using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.Drivers.Elecraft.K3Family;

public sealed class ElecraftK3DriverFactory : IRadioDriverFactory
{
    private readonly TimeProvider _timeProvider;

    public ElecraftK3DriverFactory()
        : this(TimeProvider.System)
    {
    }

    public ElecraftK3DriverFactory(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    public RadioDriverDescriptor Descriptor { get; } = new(
        "rig2cast.drivers.elecraft.k3family",
        new Version(0, 1, 0),
        new Version(1, 0),
        ElecraftK3Profile.Models.Values.Select(CreateDescriptor).ToArray());

    public async ValueTask<IRadioDriver> OpenAsync(
        RadioConnectionOptions options,
        IRadioTransport transport,
        CancellationToken cancellationToken = default)
    {
        if (!ElecraftK3Profile.Models.TryGetValue(options.ModelId, out ElecraftK3Profile? profile))
            throw new NotSupportedException($"Model '{options.ModelId}' is not supported by the Elecraft K3-family driver.");
        RadioModelDescriptor model = Descriptor.Models.Single(item =>
            StringComparer.OrdinalIgnoreCase.Equals(item.Id, options.ModelId));
        ResolvedConnectionSettings settings = ConnectionSettingsResolver.ResolveForFactory(options, model);
        bool autoInformation = settings.Get<bool>("elecraft.autoInformation");
        int autoInformationMode = settings.Get<int>("elecraft.autoInformationMode");
        return await ElecraftK3Driver.OpenAsync(
            transport, profile, autoInformation, autoInformationMode,
            timeProvider: _timeProvider,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static RadioModelDescriptor CreateDescriptor(ElecraftK3Profile profile) => new(
        profile.ModelId,
        "Elecraft",
        profile.Model,
        new HashSet<RadioTransportKind> { RadioTransportKind.Serial, RadioTransportKind.Tcp, RadioTransportKind.Simulator },
        ElecraftK3Profile.SupportedBaudRates,
        38_400,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["serial.dataBits"] = "8",
            ["serial.stopBits"] = "1",
            ["serial.parity"] = "None",
            ["serial.handshake"] = "None",
            ["serial.dtrEnable"] = "false",
            ["serial.rtsEnable"] = "false",
            ["elecraft.autoInformation"] = "false",
            ["elecraft.autoInformationMode"] = "1"
        })
    {
        SerialProfile = SerialConnectionProfile.Create(),
        ConnectionSettings =
        [
            new("elecraft.autoInformation", ConnectionSettingValueType.Boolean, "Automatic information",
                "Enables Elecraft AI unsolicited status messages.", DefaultValue: "false"),
            new("elecraft.autoInformationMode", ConnectionSettingValueType.WholeNumber, "Automatic-information mode",
                "Elecraft AI mode selected when automatic information is enabled.", DefaultValue: "1",
                Format: ConnectionSettingFormat.Base10, Minimum: 0, Maximum: 3)
        ]
    };
}
