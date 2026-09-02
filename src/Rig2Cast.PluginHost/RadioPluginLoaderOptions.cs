namespace Rig2Cast.PluginHost;

public sealed record RadioPluginLoaderOptions
{
    public Version DriverApiVersion { get; init; } = new(1, 0);

    public bool DevelopmentMode { get; init; }
}
