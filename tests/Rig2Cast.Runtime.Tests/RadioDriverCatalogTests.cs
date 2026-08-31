using Rig2Cast.Core.Drivers;
using Rig2Cast.Drivers.Yaesu.Ftdx10;

namespace Rig2Cast.Runtime.Tests;

public sealed class RadioDriverCatalogTests
{
    [Fact]
    public void DiscoversFtdx10WithoutOpeningATransport()
    {
        var catalog = new RadioDriverCatalog();
        catalog.Register(new Ftdx10DriverFactory());

        RadioModelRegistration registration = catalog.Find("YAESU.FTDX10");

        Assert.Equal("Yaesu", registration.Model.Manufacturer);
        Assert.Equal("FTDX10", registration.Model.Model);
        Assert.Equal(38_400, registration.Model.DefaultBaudRate);
        Assert.Equal([4_800, 9_600, 19_200, 38_400], registration.Model.SupportedBaudRates);
        Assert.Equal("rig2cast.drivers.yaesu.ftdx10", registration.Driver.Id);
    }

    [Fact]
    public void RejectsDuplicateModelRegistrations()
    {
        var catalog = new RadioDriverCatalog();
        catalog.Register(new Ftdx10DriverFactory());

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => catalog.Register(new Ftdx10DriverFactory()));

        Assert.Contains(Ftdx10CatProfile.ModelId, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownModelIncludesDiscoveryGuidance()
    {
        var catalog = new RadioDriverCatalog();
        catalog.Register(new Ftdx10DriverFactory());

        KeyNotFoundException exception = Assert.Throws<KeyNotFoundException>(() => catalog.Find("elecraft.k3"));

        Assert.Contains("--list-models", exception.Message, StringComparison.Ordinal);
    }
}
