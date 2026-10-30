namespace Rig2Cast.WebGui.Security;

public sealed record PermissionDefinition(string Key, string Area, string Description, bool SupportsResource = false);

public static class PermissionCatalog
{
    public static class Keys
    {
        public const string RadioView = "radio.view";
        public const string RadioControl = "radio.control";
        public const string RadioConnect = "radio.connect";
        public const string RadioPreempt = "radio.preempt";
        public const string RadioPowerOn = "radio.power.on";
        public const string RadioPowerOff = "radio.power.off";
        public const string SpectrumView = "spectrum.view";
        public const string SpectrumControl = "spectrum.control";
        public const string SpectrumSettings = "spectrum.settings";
        public const string TuningView = "tuning.view";
        public const string TuningControl = "tuning.control";
        public const string BandView = "band.view";
        public const string BandControl = "band.control";
        public const string ModeView = "mode.view";
        public const string ModeControl = "mode.control";
        public const string DspView = "dsp.view";
        public const string DspControl = "dsp.control";
        public const string ReceiverView = "receiver.view";
        public const string ReceiverControl = "receiver.control";
        public const string TransmitView = "transmit.view";
        public const string TransmitControl = "transmit.control";
        public const string TransmitPower = "transmit.power";
        public const string TransmitPtt = "transmit.ptt";
        public const string AudioView = "audio.view";
        public const string AudioControl = "audio.control";
        public const string AudioTransmit = "audio.transmit";
        public const string RotatorView = "rotator.view";
        public const string RotatorControl = "rotator.control";
        public const string RotatorMove = "rotator.move";
        public const string RotatorStop = "rotator.stop";
        public const string RotatorPark = "rotator.park";
        public const string UsersManage = "users.manage";
        public const string AuditView = "audit.view";
    }

    public static IReadOnlyList<PermissionDefinition> All { get; } =
    [
        new(Keys.RadioView, "Radio", "View radio state and the main radio card."),
        new(Keys.RadioControl, "Radio", "Change ordinary radio controls."),
        new(Keys.RadioConnect, "Radio", "Connect or disconnect the configured radio."),
        new(Keys.RadioPreempt, "Radio", "Take control of a station session from another user."),
        new(Keys.RadioPowerOn, "Physical power", "Power on the physical radio."),
        new(Keys.RadioPowerOff, "Physical power", "Power off the physical radio."),
        new(Keys.SpectrumView, "Spectrum", "View spectrum and waterfall data."),
        new(Keys.SpectrumControl, "Spectrum", "Tune from the spectrum display."),
        new(Keys.SpectrumSettings, "Spectrum", "Change spectrum span and display levels."),
        new(Keys.TuningView, "Tuning", "View VFO tuning controls."),
        new(Keys.TuningControl, "Tuning", "Change VFO frequencies and tuning step."),
        new(Keys.BandView, "Band", "View band controls."),
        new(Keys.BandControl, "Band", "Change the active band."),
        new(Keys.ModeView, "Mode", "View mode controls."),
        new(Keys.ModeControl, "Mode", "Change operating mode."),
        new(Keys.DspView, "DSP", "View DSP controls."),
        new(Keys.DspControl, "DSP", "Change DSP controls."),
        new(Keys.ReceiverView, "Receiver", "View receiver controls."),
        new(Keys.ReceiverControl, "Receiver", "Change receiver controls."),
        new(Keys.TransmitView, "Transmit", "View transmit state and controls."),
        new(Keys.TransmitControl, "Transmit", "Change ordinary transmit controls."),
        new(Keys.TransmitPower, "Transmit", "Change RF transmit power."),
        new(Keys.TransmitPtt, "Transmit", "Key and unkey the transmitter."),
        new(Keys.AudioView, "Audio", "Receive station audio."),
        new(Keys.AudioControl, "Audio", "Change audio controls."),
        new(Keys.AudioTransmit, "Audio", "Send microphone audio to the station."),
        new(Keys.RotatorView, "Rotators", "View a rotator.", true),
        new(Keys.RotatorControl, "Rotators", "Use ordinary rotator controls.", true),
        new(Keys.RotatorMove, "Rotators", "Move a rotator.", true),
        new(Keys.RotatorStop, "Rotators", "Stop a rotator.", true),
        new(Keys.RotatorPark, "Rotators", "Park a rotator.", true),
        new(Keys.UsersManage, "Administration", "Create users and manage direct grants."),
        new(Keys.AuditView, "Administration", "View security audit history.")
    ];

    private static readonly Dictionary<string, PermissionDefinition> ByKey =
        All.ToDictionary(permission => permission.Key, StringComparer.Ordinal);

    public static bool IsKnown(string key) => ByKey.ContainsKey(key);

    public static PermissionDefinition Require(string key) =>
        ByKey.TryGetValue(key, out PermissionDefinition? permission)
            ? permission
            : throw new ArgumentException($"Unknown permission '{key}'.", nameof(key));
}
