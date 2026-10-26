using Microsoft.Extensions.Configuration;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.WebGui;

namespace Rig2Cast.Runtime.Tests;

public sealed class WebGuiPttTests
{
    [Fact]
    public async Task WebPttRequiresOwnerRenewsAndDeKeysWhenOwnerDetaches()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Rig2Cast:AllowWrites"] = "true"
        });
        await using var host = new RadioWebHost(configuration, new StationConfiguration());

        WebConnectionResult owner = await host.ConnectOrAttachAsync(
            Request("owner", enableWrites: true), CancellationToken.None);
        WebConnectionResult observer = await host.ConnectOrAttachAsync(
            Request("observer", enableWrites: true), CancellationToken.None);

        Assert.Equal(ClientRole.Operator, owner.Role);
        Assert.Equal(ClientRole.Observer, observer.Role);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            host.SetPttAsync(owner.RadioId, "observer", true, CancellationToken.None).AsTask());

        var transmitting = await host.SetPttAsync(owner.RadioId, "owner", true, CancellationToken.None);
        Assert.True(transmitting.State.IsTransmitting);
        PttLeaseStatus renewed = await host.RenewPttAsync(owner.RadioId, "owner", CancellationToken.None);
        Assert.True(renewed.ExpiresAt > DateTimeOffset.UtcNow);

        await host.DetachAsync(owner.RadioId, "owner");
        var receiving = await host.GetSnapshotAsync(owner.RadioId, "observer", CancellationToken.None);
        Assert.False(receiving.State.IsTransmitting);
    }

    [Fact]
    public async Task LockedStationRangeRestrictsRemoteFrequencyAndRequiresEveryVfoForPtt()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Rig2Cast:AllowWrites"] = "true"
        });
        var station = new StationConfiguration
        {
            Station = new StationIdentity { Id = "test", Locked = true, AutoConnect = false },
            Radio = new StationRadio { ModelId = "yaesu.ftdx10", Transport = "Simulator" },
            Access = new StationAccess { AllowRadioWrites = true, AllowPtt = true },
            RemoteFrequencyPolicy = new StationRemoteFrequencyPolicy
            {
                AllowedRanges =
                [
                    new StationFrequencyRange
                    {
                        Name = "20 metres", MinimumHz = 14_000_000, MaximumHz = 14_350_000
                    }
                ]
            }
        };
        await using var host = new RadioWebHost(configuration, station);
        WebConnectionResult owner = await host.ConnectOrAttachAsync(
            Request("owner", enableWrites: true), CancellationToken.None);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            host.SetPttAsync(owner.RadioId, "owner", true, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            host.SetFrequencyAsync(owner.RadioId, "owner", Rig2Cast.Abstractions.Radios.VfoId.A,
                7_100_000, CancellationToken.None).AsTask());

        await host.SetFrequencyAsync(owner.RadioId, "owner", Rig2Cast.Abstractions.Radios.VfoId.B,
            14_250_000, CancellationToken.None);
        var transmitting = await host.SetPttAsync(owner.RadioId, "owner", true, CancellationToken.None);
        Assert.True(transmitting.State.IsTransmitting);
        await host.SetPttAsync(owner.RadioId, "owner", false, CancellationToken.None);
    }

    private static ConnectRequest Request(string clientId, bool enableWrites) => new(
        clientId,
        "yaesu.ftdx10",
        "Simulator",
        null,
        null,
        null,
        null,
        null,
        enableWrites);
}
