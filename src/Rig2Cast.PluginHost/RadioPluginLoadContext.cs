using System.Reflection;
using System.Runtime.Loader;
using Rig2Cast.Abstractions.Drivers;

namespace Rig2Cast.PluginHost;

internal sealed class RadioPluginLoadContext(string pluginAssemblyPath)
    : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(pluginAssemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        Assembly abstractions = typeof(IRadioDriverFactory).Assembly;
        if (AssemblyName.ReferenceMatchesDefinition(assemblyName, abstractions.GetName()))
            return abstractions;

        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }
}
