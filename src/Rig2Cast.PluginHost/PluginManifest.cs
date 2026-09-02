using Rig2Cast.Abstractions.Drivers;

namespace Rig2Cast.PluginHost;

public sealed record PluginModelManifest(
    string Id,
    string Manufacturer,
    string Model,
    IReadOnlyList<RadioTransportKind> SupportedTransports,
    IReadOnlyList<int> SupportedBaudRates,
    int? DefaultBaudRate = null,
    IReadOnlyDictionary<string, string>? DefaultConnectionSettings = null);

public sealed record PluginManifest(
    string Id,
    string Version,
    string ApiVersion,
    string EntryAssembly,
    string FactoryType,
    IReadOnlyList<PluginModelManifest> Models);

public sealed record PluginTrustRecord(
    string PluginId,
    string AssemblySha256);

public enum PluginLoadStatus
{
    Loaded,
    InvalidManifest,
    Untrusted,
    Incompatible,
    Duplicate,
    LoadFailed
}

public sealed record PluginLoadDiagnostic(
    string ManifestPath,
    string? PluginId,
    PluginLoadStatus Status,
    string Message);
