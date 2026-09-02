namespace Rig2Cast.PluginHost;

public sealed record RadioPluginLoaderOptions
{
    public Version DriverApiVersion { get; init; } = RadioDriverApiCompatibility.CurrentVersion;

    public bool DevelopmentMode { get; init; }
}
