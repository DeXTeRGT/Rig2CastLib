using System.Globalization;
using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Sessions;

namespace Rig2Cast.Adapters.Rigctld;

public sealed class RigctldSessionHandler(IRadioSession session, bool writesEnabled = false)
{
    private static readonly RadioReadRequest NetworkRead =
        RadioReadRequest.FreshWithin(TimeSpan.FromMilliseconds(250));

    public async ValueTask<RigctldResult> ExecuteAsync(RigctldRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return request.Command switch
            {
                "get_freq" => await GetFrequencyAsync(cancellationToken),
                "set_freq" => await SetFrequencyAsync(request.Arguments, cancellationToken),
                "get_vfo" => await GetVfoAsync(cancellationToken),
                "set_vfo" => await SetVfoAsync(request.Arguments, cancellationToken),
                "get_mode" => await GetModeAsync(cancellationToken),
                "set_mode" => await SetModeAsync(request.Arguments, cancellationToken),
                "get_split_vfo" => await GetSplitAsync(cancellationToken),
                "set_split_vfo" => await SetSplitAsync(request.Arguments, cancellationToken),
                "get_ptt" => await GetPttAsync(cancellationToken),
                "set_ptt" => Error("set_ptt", RigctldError.NotAvailable),
                "quit" => new("quit", [], RigctldError.Ok, true),
                _ => Error(request.Command, RigctldError.NotImplemented)
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Error(request.Command, RigctldError.Timeout);
        }
        catch (UnauthorizedAccessException) { return Error(request.Command, RigctldError.Rejected); }
        catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
        { return Error(request.Command, RigctldError.InvalidParameter); }
        catch (NotSupportedException) { return Error(request.Command, RigctldError.NotAvailable); }
        catch (IOException) { return Error(request.Command, RigctldError.Io); }
    }

    private async ValueTask<RigctldResult> GetFrequencyAsync(CancellationToken token)
    {
        RadioState state = await session.ReadStateAsync(NetworkRead, token);
        VfoId activeVfo = GetRepresentableActiveVfo(state);
        if (!state.FrequenciesHz.TryGetValue(activeVfo, out long frequency))
            throw new NotSupportedException("The active frequency is not representable as a rigctld VFO.");
        return Success("get_freq", new RigctldValue("Frequency", frequency.ToString(CultureInfo.InvariantCulture)));
    }

    private async ValueTask<RigctldResult> SetFrequencyAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        EnsureWrite(args, 1);
        long frequency = long.Parse(args[0], CultureInfo.InvariantCulture);
        RadioState state = await session.ReadStateAsync(NetworkRead, token);
        await session.SetFrequencyAsync(GetRepresentableActiveVfo(state), frequency, token);
        return Success("set_freq");
    }

    private async ValueTask<RigctldResult> GetVfoAsync(CancellationToken token)
    {
        RadioState state = await session.ReadStateAsync(NetworkRead, token);
        return Success("get_vfo", new RigctldValue("VFO", FormatVfo(GetRepresentableActiveVfo(state))));
    }

    private async ValueTask<RigctldResult> SetVfoAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        EnsureWrite(args, 1);
        await session.SetActiveVfoAsync(ParseVfo(args[0]), token);
        return Success("set_vfo");
    }

    private async ValueTask<RigctldResult> GetModeAsync(CancellationToken token)
    {
        RadioState state = await session.ReadStateAsync(NetworkRead, token);
        int passband = 0;
        try
        {
            passband = (await session.ReadPassbandAsync(token)).WidthHz;
        }
        catch (NotSupportedException)
        {
            try
            {
                RadioChoiceValue width = await session.ReadChoiceAsync(RadioChoiceId.FilterWidth, token);
                passband = ParseWidth(width.Value);
            }
            catch (NotSupportedException)
            {
                // Hamlib uses zero when the current mode/driver has no adjustable passband.
            }
        }
        return new("get_mode", [new("Mode", FormatMode(state.Mode)), new("Passband", passband.ToString(CultureInfo.InvariantCulture))]);
    }

    private async ValueTask<RigctldResult> SetModeAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        EnsureWrite(args, 2);
        RadioMode mode = ParseMode(args[0]);
        int requestedPassband = int.Parse(args[1], CultureInfo.InvariantCulture);
        if (requestedPassband < 0) throw new ArgumentOutOfRangeException(nameof(args), "Passband cannot be negative.");

        RadioCapabilities capabilities = (await session.GetSnapshotAsync(token)).Capabilities;
        int? selectedPassband = requestedPassband > 0
            ? SelectPassband(capabilities.Passband, mode, requestedPassband)
            : null;
        RadioChoiceId filterWidth = RadioChoiceId.FilterWidth;
        string? selectedLegacyWidth = requestedPassband == 0
            ? SelectPassband(capabilities.Choices.GetValueOrDefault(filterWidth), mode, requestedPassband)
            : null;

        await session.ExecuteExclusiveAsync(async (scope, operationToken) =>
        {
            await scope.SetModeAsync(mode, operationToken);
            if (selectedPassband is int widthHz)
                await scope.SetPassbandAsync(widthHz, operationToken);
            else if (selectedLegacyWidth is not null)
                await scope.WriteChoiceAsync(filterWidth, selectedLegacyWidth, operationToken);
        }, token);
        return Success("set_mode");
    }

    private static int SelectPassband(PassbandCapability capability, RadioMode mode, int requestedHz)
    {
        if (capability.Feature.Support != CapabilitySupport.Supported ||
            !capability.ByMode.TryGetValue(mode, out PassbandConstraint? constraint))
            throw new NotSupportedException($"Passband control is not supported in {mode} mode.");
        if (requestedHz < constraint.MinimumHz || requestedHz > constraint.MaximumHz)
            throw new ArgumentOutOfRangeException(nameof(requestedHz));
        if (constraint.DiscreteValuesHz is { Count: > 0 } values)
            return values.OrderBy(value => Math.Abs((long)value - requestedHz)).ThenBy(value => value).First();
        int steps = (int)Math.Round(
            (requestedHz - constraint.MinimumHz) / (double)constraint.StepHz,
            MidpointRounding.AwayFromZero);
        return constraint.MinimumHz + steps * constraint.StepHz;
    }

    private static string? SelectPassband(ChoiceControlDescriptor? descriptor, RadioMode mode, int requestedHz)
    {
        if (descriptor is null) return null;
        if (requestedHz == 0)
        {
            return descriptor.Options.TryGetValue("default", out RadioChoiceOption? defaultOption) &&
                   IsApplicable(defaultOption, mode)
                ? defaultOption.Value
                : null;
        }

        return descriptor.Options.Values
            .Where(option => option.Writable && IsApplicable(option, mode))
            .Select(option => (Option: option, Width: ParseWidth(option.Value)))
            .Where(item => item.Width > 0)
            .OrderBy(item => Math.Abs((long)item.Width - requestedHz))
            .ThenBy(item => item.Width)
            .Select(item => item.Option.Value)
            .FirstOrDefault();
    }

    private static bool IsApplicable(RadioChoiceOption option, RadioMode mode) =>
        option.ApplicableModes is null || option.ApplicableModes.Contains(mode);

    private static int ParseWidth(string value) =>
        value.EndsWith("hz", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(value.AsSpan(0, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            ? width
            : 0;

    private async ValueTask<RigctldResult> GetSplitAsync(CancellationToken token)
    {
        RadioState state = await session.ReadStateAsync(NetworkRead, token);
        _ = GetRepresentableActiveVfo(state);
        return new(
            "get_split_vfo",
            [new("Split", state.IsSplit ? "1" : "0"), new("TX VFO", FormatVfo(state.TransmitVfo))]);
    }

    private async ValueTask<RigctldResult> SetSplitAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        EnsureWrite(args, 2);
        bool enabled = args[0] switch { "0" => false, "1" => true, _ => throw new FormatException("Split must be 0 or 1.") };
        VfoId transmitVfo = ParseVfo(args[1]);
        await session.SetSplitAsync(enabled, transmitVfo, token);
        return Success("set_split_vfo");
    }

    private async ValueTask<RigctldResult> GetPttAsync(CancellationToken token)
    {
        RadioState state = await session.ReadStateAsync(NetworkRead, token);
        return Success("get_ptt", new RigctldValue("PTT", state.IsTransmitting ? "1" : "0"));
    }

    private void EnsureWrite(IReadOnlyList<string> args, int count)
    {
        if (!writesEnabled) throw new UnauthorizedAccessException("Writes are disabled for this rigctld server.");
        if (args.Count != count) throw new ArgumentException($"Expected {count} argument(s).", nameof(args));
    }

    private static VfoId ParseVfo(string value) => value.ToUpperInvariant() switch
    {
        "VFOA" or "A" or "MAIN" => VfoId.A,
        "VFOB" or "B" or "SUB" => VfoId.B,
        _ => throw new ArgumentException($"Unknown VFO '{value}'.", nameof(value))
    };

    private static string FormatVfo(VfoId value) => value switch
    {
        VfoId.A or VfoId.Main => "VFOA",
        VfoId.B or VfoId.Sub => "VFOB",
        _ => throw new NotSupportedException($"VFO '{value}' cannot be represented by rigctld.")
    };

    private static VfoId GetRepresentableActiveVfo(RadioState state)
    {
        if (state.ActiveVfo is not VfoId.A and not VfoId.B)
            throw new NotSupportedException("The active radio path has no representable A/B VFO.");
        if (state.ReceivePaths.Count != 1 || state.ReceivePaths[0].Receiver != state.SelectedReceiver ||
            state.ReceivePaths[0].Vfo != state.ActiveVfo)
        {
            throw new NotSupportedException("The active receive topology is ambiguous in rigctld terminology.");
        }
        return state.ActiveVfo;
    }

    private static RadioMode ParseMode(string value) => value.ToUpperInvariant() switch
    {
        "LSB" => RadioMode.Lsb, "USB" => RadioMode.Usb, "CW" => RadioMode.Cw,
        "CWR" => RadioMode.CwReverse, "AM" => RadioMode.Am, "FM" => RadioMode.Fm,
        "PKTLSB" => RadioMode.DataLsb, "PKTUSB" => RadioMode.DataUsb,
        "PKTFM" => RadioMode.DataFm, "RTTY" => RadioMode.Rtty,
        "RTTYR" => RadioMode.RttyReverse, _ => throw new ArgumentException($"Unknown mode '{value}'.", nameof(value))
    };

    private static string FormatMode(RadioMode value) => value switch
    {
        RadioMode.Lsb => "LSB", RadioMode.Usb => "USB", RadioMode.Cw => "CW",
        RadioMode.CwReverse => "CWR", RadioMode.Am or RadioMode.AmNarrow => "AM",
        RadioMode.Fm or RadioMode.FmNarrow => "FM", RadioMode.DataLsb => "PKTLSB",
        RadioMode.DataUsb or RadioMode.Psk => "PKTUSB", RadioMode.DataFm or RadioMode.DataFmNarrow => "PKTFM",
        RadioMode.Rtty => "RTTY", RadioMode.RttyReverse => "RTTYR", _ => "USB"
    };

    private static RigctldResult Success(string command, params RigctldValue[] values) => new(command, values);
    private static RigctldResult Error(string command, int code) => new(command, [], code);
}
