using Rig2Cast.Core.Drivers;

namespace Rig2Cast.PluginHost;

public sealed class RadioPluginCatalogComposition : IDisposable
{
    private readonly IReadOnlyList<PluginRegistrationLifetime> _registrations;
    private int _disposed;

    private RadioPluginCatalogComposition(
        IReadOnlyList<PluginRegistrationLifetime> registrations,
        IReadOnlyList<PluginLoadDiagnostic> diagnostics)
    {
        _registrations = registrations;
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<PluginLoadDiagnostic> Diagnostics { get; }

    public static async ValueTask<RadioPluginCatalogComposition> LoadAsync(
        RadioDriverCatalog catalog,
        RadioPluginHostConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(configuration);

        var discovered = new List<LoadedRadioPlugin>();
        var loaded = new List<PluginRegistrationLifetime>();
        var adopted = new HashSet<LoadedRadioPlugin>();
        var diagnostics = new List<PluginLoadDiagnostic>();
        var driverIds = catalog.Models.Select(item => item.Driver.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var modelIds = catalog.Models.Select(item => item.Model.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loader = new RadioPluginLoader(new RadioPluginLoaderOptions
        {
            DevelopmentMode = configuration.DevelopmentMode
        });

        try
        {
            foreach (string directory in configuration.PluginDirectories.Order(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(directory))
                {
                    diagnostics.Add(new(
                        directory, null, PluginLoadStatus.InvalidManifest,
                        $"Plugin directory '{directory}' does not exist."));
                    continue;
                }

                IReadOnlyList<LoadedRadioPlugin> candidates;
                IReadOnlyList<PluginLoadDiagnostic> discoveryDiagnostics;
                try
                {
                    (candidates, discoveryDiagnostics) = await loader.DiscoverAsync(
                        directory, configuration.TrustRecords, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(new(
                        directory, null, PluginLoadStatus.LoadFailed,
                        $"Plugin directory '{directory}' could not be searched: {exception.Message}"));
                    continue;
                }

                diagnostics.AddRange(discoveryDiagnostics.Where(item => item.Status != PluginLoadStatus.Loaded));
                discovered.AddRange(candidates);
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (LoadedRadioPlugin plugin in discovered)
            {
                string[] candidateModels = plugin.Factory.Descriptor.Models
                    .Select(model => model.Id)
                    .ToArray();
                if (driverIds.Contains(plugin.Factory.Descriptor.Id) || candidateModels.Any(modelIds.Contains))
                {
                    plugin.Dispose();
                    diagnostics.Add(new(
                        plugin.ManifestPath, plugin.Manifest.Id, PluginLoadStatus.Duplicate,
                        $"Plugin version '{plugin.Manifest.Version}' conflicts with an existing catalog registration " +
                        "for its driver ID or one of its model IDs. Hot replacement and side-by-side versions are " +
                        "not supported; the existing registration remains active."));
                    continue;
                }

                var registration = new PluginRegistrationLifetime(plugin);
                try
                {
                    catalog.Register(registration.Factory);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    registration.Dispose();
                    diagnostics.Add(new(
                        plugin.ManifestPath, plugin.Manifest.Id, PluginLoadStatus.Incompatible,
                        $"The plugin could not be registered in the driver catalog: {exception.Message}"));
                    continue;
                }
                driverIds.Add(plugin.Factory.Descriptor.Id);
                foreach (string modelId in candidateModels) modelIds.Add(modelId);
                loaded.Add(registration);
                adopted.Add(plugin);
                diagnostics.Add(new(
                    plugin.ManifestPath, plugin.Manifest.Id, PluginLoadStatus.Loaded,
                    "Plugin loaded and registered."));
            }

            return new RadioPluginCatalogComposition(loaded, diagnostics);
        }
        catch
        {
            for (int index = loaded.Count - 1; index >= 0; index--) loaded[index].Dispose();
            foreach (LoadedRadioPlugin plugin in discovered.Where(plugin => !adopted.Contains(plugin)))
                plugin.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        for (int index = _registrations.Count - 1; index >= 0; index--)
            _registrations[index].Dispose();
    }
}
