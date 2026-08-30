namespace Rig2Cast.PluginHost;

public sealed record PluginManifest(
    string Id,
    string Version,
    string ApiVersion,
    string EntryAssembly,
    string FactoryType,
    IReadOnlyList<string> Models);

public sealed record PluginTrustRecord(
    string PluginId,
    string AssemblySha256);
