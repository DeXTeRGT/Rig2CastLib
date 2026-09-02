using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rig2Cast.Abstractions.Drivers;

namespace Rig2Cast.PluginHost;

public sealed class RadioPluginLoader(RadioPluginLoaderOptions? options = null)
{
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly RadioPluginLoaderOptions _options = options ?? new RadioPluginLoaderOptions();

    public async ValueTask<PluginManifest> ReadManifestAsync(
        string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string fullManifestPath = Path.GetFullPath(manifestPath);
        try
        {
            await using FileStream stream = File.OpenRead(fullManifestPath);
            PluginManifest manifest = await JsonSerializer.DeserializeAsync<PluginManifest>(
                stream, ManifestJson, cancellationToken).ConfigureAwait(false) ??
                throw new JsonException("The plugin manifest is empty.");
            ValidateManifest(manifest);
            return manifest;
        }
        catch (PluginLoadException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            throw new PluginLoadException(
                PluginLoadStatus.InvalidManifest,
                $"Plugin manifest '{fullManifestPath}' could not be read: {exception.Message}",
                exception);
        }
    }

    public async ValueTask<LoadedRadioPlugin> LoadAsync(
        string manifestPath,
        IEnumerable<PluginTrustRecord>? trustRecords = null,
        CancellationToken cancellationToken = default)
    {
        PluginManifest manifest = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        string fullManifestPath = Path.GetFullPath(manifestPath);
        string directory = Path.GetDirectoryName(fullManifestPath)!;
        string assemblyPath = ResolveEntryAssembly(directory, manifest.EntryAssembly);
        string hash = await ComputeSha256Async(assemblyPath, cancellationToken).ConfigureAwait(false);
        ValidateTrust(manifest, hash, trustRecords ?? []);

        var loadContext = new RadioPluginLoadContext(assemblyPath);
        try
        {
            Assembly assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
            Type factoryType = assembly.GetType(manifest.FactoryType, throwOnError: true, ignoreCase: false)!;
            if (!typeof(IRadioDriverFactory).IsAssignableFrom(factoryType) || factoryType.IsAbstract)
                throw new PluginLoadException(
                    PluginLoadStatus.LoadFailed,
                    $"Factory type '{manifest.FactoryType}' does not implement {nameof(IRadioDriverFactory)}.");
            if (Activator.CreateInstance(factoryType) is not IRadioDriverFactory factory)
                throw new PluginLoadException(
                    PluginLoadStatus.LoadFailed,
                    $"Factory type '{manifest.FactoryType}' could not be constructed.");
            ValidateFactory(manifest, factory);
            return new LoadedRadioPlugin(
                manifest, fullManifestPath, assemblyPath, hash, factory, loadContext);
        }
        catch (PluginLoadException)
        {
            loadContext.Unload();
            throw;
        }
        catch (Exception exception)
        {
            loadContext.Unload();
            throw new PluginLoadException(
                PluginLoadStatus.LoadFailed,
                $"Plugin '{manifest.Id}' could not be loaded: {exception.Message}",
                exception);
        }
    }

    public async ValueTask<(IReadOnlyList<LoadedRadioPlugin> Plugins, IReadOnlyList<PluginLoadDiagnostic> Diagnostics)>
        DiscoverAsync(
            string directory,
            IEnumerable<PluginTrustRecord>? trustRecords = null,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string fullDirectory = Path.GetFullPath(directory);
        var plugins = new List<LoadedRadioPlugin>();
        var diagnostics = new List<PluginLoadDiagnostic>();
        var pluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string manifestPath in Directory.EnumerateFiles(
                         fullDirectory, "*.rig2cast-plugin.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase))
            {
                LoadedRadioPlugin? plugin = null;
                try
                {
                    plugin = await LoadAsync(manifestPath, trustRecords, cancellationToken).ConfigureAwait(false);
                    string[] candidateModelIds = plugin.Factory.Descriptor.Models
                        .Select(model => model.Id)
                        .ToArray();
                    bool duplicate = pluginIds.Contains(plugin.Manifest.Id) ||
                        candidateModelIds.Any(modelIds.Contains);
                    if (duplicate)
                    {
                        plugin.Dispose();
                        diagnostics.Add(new(
                            manifestPath, plugin.Manifest.Id, PluginLoadStatus.Duplicate,
                            "The plugin ID or one of its model IDs is already loaded."));
                        continue;
                    }
                    pluginIds.Add(plugin.Manifest.Id);
                    foreach (string modelId in candidateModelIds) modelIds.Add(modelId);
                    plugins.Add(plugin);
                    diagnostics.Add(new(manifestPath, plugin.Manifest.Id, PluginLoadStatus.Loaded, "Plugin loaded."));
                }
                catch (PluginLoadException exception)
                {
                    plugin?.Dispose();
                    diagnostics.Add(new(manifestPath, null, exception.Status, exception.Message));
                }
            }
        }
        catch
        {
            foreach (LoadedRadioPlugin plugin in plugins) plugin.Dispose();
            throw;
        }
        return (plugins, diagnostics);
    }

    private void ValidateFactory(PluginManifest manifest, IRadioDriverFactory factory)
    {
        RadioDriverDescriptor descriptor = factory.Descriptor;
        if (!StringComparer.OrdinalIgnoreCase.Equals(descriptor.Id, manifest.Id))
            throw new PluginLoadException(
                PluginLoadStatus.Incompatible,
                $"Plugin manifest ID '{manifest.Id}' does not match factory driver ID '{descriptor.Id}'.");
        Version manifestVersion = Version.Parse(manifest.Version);
        if (descriptor.Version != manifestVersion)
            throw new PluginLoadException(
                PluginLoadStatus.Incompatible,
                $"Plugin '{manifest.Id}' manifest version '{manifestVersion}' does not match " +
                $"factory version '{descriptor.Version}'.");
        Version manifestApiVersion = Version.Parse(manifest.ApiVersion);
        if (descriptor.ApiVersion != manifestApiVersion)
            throw new PluginLoadException(
                PluginLoadStatus.Incompatible,
                $"Plugin '{manifest.Id}' manifest API version '{manifestApiVersion}' does not match " +
                $"factory API version '{descriptor.ApiVersion}'.");
        if (!RadioDriverApiCompatibility.IsCompatible(_options.DriverApiVersion, descriptor.ApiVersion))
            throw new PluginLoadException(
                PluginLoadStatus.Incompatible,
                RadioDriverApiCompatibility.DescribeMismatch(_options.DriverApiVersion, descriptor.ApiVersion));
        if (manifest.Models.Count != descriptor.Models.Count)
            throw new PluginLoadException(
                PluginLoadStatus.Incompatible,
                $"Plugin '{manifest.Id}' model metadata does not match its factory descriptor.");
        foreach (PluginModelManifest declared in manifest.Models)
        {
            RadioModelDescriptor? actual = descriptor.Models.FirstOrDefault(model =>
                StringComparer.OrdinalIgnoreCase.Equals(model.Id, declared.Id));
            if (actual is null || !ModelMetadataMatches(declared, actual))
                throw new PluginLoadException(
                    PluginLoadStatus.Incompatible,
                    $"Plugin '{manifest.Id}' model '{declared.Id}' metadata does not match its factory descriptor.");
        }
    }

    private void ValidateManifest(PluginManifest manifest)
    {
        ValidateId(manifest.Id, "plugin");
        if (!Version.TryParse(manifest.Version, out _))
            throw Invalid("Plugin version must be a valid System.Version value.");
        if (!Version.TryParse(manifest.ApiVersion, out Version? apiVersion))
            throw Invalid("Plugin API version must be a valid System.Version value.");
        if (!RadioDriverApiCompatibility.IsCompatible(_options.DriverApiVersion, apiVersion))
            throw new PluginLoadException(
                PluginLoadStatus.Incompatible,
                RadioDriverApiCompatibility.DescribeMismatch(_options.DriverApiVersion, apiVersion));
        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly) || Path.IsPathRooted(manifest.EntryAssembly) ||
            manifest.EntryAssembly != Path.GetFileName(manifest.EntryAssembly) ||
            !StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(manifest.EntryAssembly), ".dll"))
            throw Invalid("EntryAssembly must be a DLL file name in the manifest directory.");
        if (string.IsNullOrWhiteSpace(manifest.FactoryType))
            throw Invalid("FactoryType is required.");
        if (manifest.Models is null || manifest.Models.Count == 0)
            throw Invalid("At least one model ID is required.");
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginModelManifest model in manifest.Models)
        {
            if (model is null) throw Invalid("A model declaration cannot be null.");
            ValidateId(model.Id, "model");
            if (!models.Add(model.Id)) throw Invalid($"Model ID '{model.Id}' is declared more than once.");
            if (string.IsNullOrWhiteSpace(model.Manufacturer) || string.IsNullOrWhiteSpace(model.Model))
                throw Invalid($"Model '{model.Id}' requires manufacturer and model names.");
            if (model.SupportedTransports is null || model.SupportedTransports.Count == 0 ||
                model.SupportedTransports.Any(value => !Enum.IsDefined(value)))
                throw Invalid($"Model '{model.Id}' must declare valid supported transports.");
            if (model.SupportedBaudRates is null || model.SupportedBaudRates.Any(baud => baud <= 0) ||
                model.SupportedBaudRates.Count != model.SupportedBaudRates.Distinct().Count())
                throw Invalid($"Model '{model.Id}' declares invalid or duplicate baud rates.");
            if (model.DefaultBaudRate is int defaultBaud && !model.SupportedBaudRates.Contains(defaultBaud))
                throw Invalid($"Model '{model.Id}' default baud rate is not supported.");
        }
    }

    private static bool ModelMetadataMatches(PluginModelManifest declared, RadioModelDescriptor actual) =>
        StringComparer.OrdinalIgnoreCase.Equals(declared.Manufacturer, actual.Manufacturer) &&
        StringComparer.OrdinalIgnoreCase.Equals(declared.Model, actual.Model) &&
        declared.SupportedTransports.ToHashSet().SetEquals(actual.SupportedTransports) &&
        declared.SupportedBaudRates.SequenceEqual(actual.SupportedBaudRates) &&
        declared.DefaultBaudRate == actual.DefaultBaudRate &&
        DictionaryEquals(declared.DefaultConnectionSettings, actual.DefaultConnectionSettings);

    private static bool DictionaryEquals(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (left is null || left.Count == 0) return right is null || right.Count == 0;
        if (right is null || left.Count != right.Count) return false;
        return left.All(item => right.TryGetValue(item.Key, out string? value) &&
            StringComparer.Ordinal.Equals(item.Value, value));
    }

    private void ValidateTrust(
        PluginManifest manifest,
        string actualHash,
        IEnumerable<PluginTrustRecord> trustRecords)
    {
        if (_options.DevelopmentMode) return;
        PluginTrustRecord[] matches = trustRecords.Where(record =>
            StringComparer.OrdinalIgnoreCase.Equals(record.PluginId, manifest.Id)).ToArray();
        PluginTrustRecord? trust = matches.Length == 1 ? matches[0] : null;
        if (trust is null || trust.AssemblySha256.Length != 64 ||
            !trust.AssemblySha256.All(Uri.IsHexDigit) ||
            !StringComparer.OrdinalIgnoreCase.Equals(trust.AssemblySha256, actualHash))
        {
            throw new PluginLoadException(
                PluginLoadStatus.Untrusted,
                $"Plugin '{manifest.Id}' is not trusted with SHA-256 '{actualHash}'.");
        }
    }

    private static string ResolveEntryAssembly(string directory, string entryAssembly)
    {
        string path = Path.GetFullPath(Path.Combine(directory, entryAssembly));
        string relative = Path.GetRelativePath(directory, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) || !File.Exists(path))
            throw new PluginLoadException(
                PluginLoadStatus.InvalidManifest,
                $"Entry assembly '{entryAssembly}' does not exist inside the plugin directory.");
        return path;
    }

    private static async ValueTask<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void ValidateId(string value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw Invalid($"The {kind} ID '{value}' is invalid.");
    }

    private static PluginLoadException Invalid(string message) =>
        new(PluginLoadStatus.InvalidManifest, message);
}
