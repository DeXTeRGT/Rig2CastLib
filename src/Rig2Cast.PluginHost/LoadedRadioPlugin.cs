using Rig2Cast.Abstractions.Drivers;

namespace Rig2Cast.PluginHost;

public sealed class LoadedRadioPlugin : IDisposable
{
    private readonly RadioPluginLoadContext _loadContext;
    private int _disposed;

    internal LoadedRadioPlugin(
        PluginManifest manifest,
        string manifestPath,
        string assemblyPath,
        string assemblySha256,
        IRadioDriverFactory factory,
        RadioPluginLoadContext loadContext)
    {
        Manifest = manifest;
        ManifestPath = manifestPath;
        AssemblyPath = assemblyPath;
        AssemblySha256 = assemblySha256;
        Factory = factory;
        _loadContext = loadContext;
    }

    public PluginManifest Manifest { get; }

    public string ManifestPath { get; }

    public string AssemblyPath { get; }

    public string AssemblySha256 { get; }

    public IRadioDriverFactory Factory { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _loadContext.Unload();
    }
}
