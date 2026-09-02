namespace Rig2Cast.PluginHost;

/// <summary>
/// Defines the compatibility boundary between a plugin driver and its host.
/// API versions are canonical major/minor values and must match exactly.
/// </summary>
public static class RadioDriverApiCompatibility
{
    public static Version CurrentVersion { get; } = new(1, 0);

    public static bool IsCanonical(Version version) =>
        version.Build < 0 && version.Revision < 0;

    public static bool IsCompatible(Version hostVersion, Version pluginVersion) =>
        IsCanonical(hostVersion) &&
        IsCanonical(pluginVersion) &&
        hostVersion == pluginVersion;

    public static string DescribeMismatch(Version hostVersion, Version pluginVersion) =>
        $"Plugin driver API '{pluginVersion}' is incompatible with host API '{hostVersion}'. " +
        "Rig2Cast currently requires an exact canonical major.minor match; " +
        "forward, backward, and build/revision compatibility are not implied.";
}
