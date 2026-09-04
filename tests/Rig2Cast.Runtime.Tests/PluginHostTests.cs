using System.Security.Cryptography;
using System.Text.Json;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Drivers.Yaesu.Ftdx10;
using Rig2Cast.Core.Drivers;
using Rig2Cast.PluginHost;

namespace Rig2Cast.Runtime.Tests;

public sealed class PluginHostTests
{
    [Fact]
    public async Task TrustedPluginLoadsAndMatchesFactoryDescriptor()
    {
        using var fixture = PluginFixture.Create();
        string hash = fixture.ComputeAssemblyHash();
        var loader = new RadioPluginLoader();

        using LoadedRadioPlugin plugin = await loader.LoadAsync(
            fixture.ManifestPath,
            [new PluginTrustRecord(fixture.Manifest.Id, hash)]);

        Assert.Equal(fixture.Manifest.Id, plugin.Factory.Descriptor.Id);
        Assert.Equal(hash, plugin.AssemblySha256);
        Assert.Equal([Ftdx10CatProfile.ModelId], plugin.Manifest.Models.Select(model => model.Id));
    }

    [Fact]
    public async Task UntrustedHashRejectsPlugin()
    {
        using var fixture = PluginFixture.Create();
        var loader = new RadioPluginLoader();

        PluginLoadException failure = await Assert.ThrowsAsync<PluginLoadException>(() => loader.LoadAsync(
            fixture.ManifestPath,
            [new PluginTrustRecord(fixture.Manifest.Id, new string('0', 64))]).AsTask());

        Assert.Equal(PluginLoadStatus.Untrusted, failure.Status);
    }

    [Fact]
    public async Task DevelopmentModeAllowsExplicitTrustBypass()
    {
        using var fixture = PluginFixture.Create();
        var loader = new RadioPluginLoader(new RadioPluginLoaderOptions { DevelopmentMode = true });

        using LoadedRadioPlugin plugin = await loader.LoadAsync(fixture.ManifestPath);

        Assert.Equal(fixture.Manifest.Id, plugin.Manifest.Id);
    }

    [Fact]
    public async Task ManifestRejectsTraversalAndUnknownFieldsBeforeAssemblyLoad()
    {
        using var fixture = PluginFixture.Create();
        var traversal = fixture.Manifest with { EntryAssembly = "..\\outside.dll" };
        fixture.WriteManifest(traversal, new { unexpected = true });
        var loader = new RadioPluginLoader();

        PluginLoadException failure = await Assert.ThrowsAsync<PluginLoadException>(
            () => loader.ReadManifestAsync(fixture.ManifestPath).AsTask());

        Assert.Equal(PluginLoadStatus.InvalidManifest, failure.Status);
    }

    [Fact]
    public async Task IncompatibleApiVersionIsRejectedDuringManifestValidation()
    {
        using var fixture = PluginFixture.Create();
        fixture.WriteManifest(fixture.Manifest with { ApiVersion = "2.0" });
        var loader = new RadioPluginLoader();

        PluginLoadException failure = await Assert.ThrowsAsync<PluginLoadException>(
            () => loader.ReadManifestAsync(fixture.ManifestPath).AsTask());

        Assert.Equal(PluginLoadStatus.Incompatible, failure.Status);
        Assert.Contains("exact canonical major.minor match", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Plugin driver API '2.0'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("host API '1.0'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiCompatibilityRequiresExactCanonicalMajorMinorVersion()
    {
        Version host = RadioDriverApiCompatibility.CurrentVersion;

        Assert.True(RadioDriverApiCompatibility.IsCompatible(host, new Version(1, 0)));
        Assert.False(RadioDriverApiCompatibility.IsCompatible(host, new Version(0, 9)));
        Assert.False(RadioDriverApiCompatibility.IsCompatible(host, new Version(1, 1)));
        Assert.False(RadioDriverApiCompatibility.IsCompatible(host, new Version(2, 0)));
        Assert.False(RadioDriverApiCompatibility.IsCompatible(host, new Version(1, 0, 0)));
        Assert.False(RadioDriverApiCompatibility.IsCompatible(host, new Version(1, 0, 0, 0)));
    }

    [Fact]
    public async Task MalformedApiVersionIsAnInvalidManifest()
    {
        using var fixture = PluginFixture.Create();
        fixture.WriteManifest(fixture.Manifest with { ApiVersion = "current" });
        var loader = new RadioPluginLoader();

        PluginLoadException failure = await Assert.ThrowsAsync<PluginLoadException>(
            () => loader.ReadManifestAsync(fixture.ManifestPath).AsTask());

        Assert.Equal(PluginLoadStatus.InvalidManifest, failure.Status);
        Assert.Contains("valid System.Version", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FactoryMetadataMustMatchManifest()
    {
        using var fixture = PluginFixture.Create();
        PluginManifest mismatched = fixture.Manifest with { Id = "rig2cast.tests.wrong" };
        fixture.WriteManifest(mismatched);
        var loader = new RadioPluginLoader();

        PluginLoadException failure = await Assert.ThrowsAsync<PluginLoadException>(() => loader.LoadAsync(
            fixture.ManifestPath,
            [new PluginTrustRecord(mismatched.Id, fixture.ComputeAssemblyHash())]).AsTask());

        Assert.Equal(PluginLoadStatus.Incompatible, failure.Status);
    }

    [Fact]
    public async Task FactoryConnectionMetadataMustMatchManifest()
    {
        using var fixture = PluginFixture.Create();
        PluginModelManifest model = fixture.Manifest.Models[0] with { DefaultBaudRate = 9_600 };
        fixture.WriteManifest(fixture.Manifest with { Models = [model] });
        var loader = new RadioPluginLoader();

        PluginLoadException failure = await Assert.ThrowsAsync<PluginLoadException>(() => loader.LoadAsync(
            fixture.ManifestPath,
            [new PluginTrustRecord(fixture.Manifest.Id, fixture.ComputeAssemblyHash())]).AsTask());

        Assert.Equal(PluginLoadStatus.Incompatible, failure.Status);
    }

    [Fact]
    public async Task MissingFactoryTypeIsReportedAsLoadFailure()
    {
        using var fixture = PluginFixture.Create();
        fixture.WriteManifest(fixture.Manifest with { FactoryType = "Missing.Factory" });
        var loader = new RadioPluginLoader();

        PluginLoadException failure = await Assert.ThrowsAsync<PluginLoadException>(() => loader.LoadAsync(
            fixture.ManifestPath,
            [new PluginTrustRecord(fixture.Manifest.Id, fixture.ComputeAssemblyHash())]).AsTask());

        Assert.Equal(PluginLoadStatus.LoadFailed, failure.Status);
    }

    [Fact]
    public async Task DuplicateTrustRecordsAreRejectedAsAmbiguous()
    {
        using var fixture = PluginFixture.Create();
        string hash = fixture.ComputeAssemblyHash();
        var loader = new RadioPluginLoader();

        PluginLoadException failure = await Assert.ThrowsAsync<PluginLoadException>(() => loader.LoadAsync(
            fixture.ManifestPath,
            [new PluginTrustRecord(fixture.Manifest.Id, hash), new PluginTrustRecord(fixture.Manifest.Id, hash)]).AsTask());

        Assert.Equal(PluginLoadStatus.Untrusted, failure.Status);
    }

    [Fact]
    public async Task DiscoveryIsolatesMalformedManifestAndDetectsDuplicatePlugin()
    {
        using var fixture = PluginFixture.Create();
        string duplicatePath = Path.Combine(fixture.DirectoryPath, "duplicate.rig2cast-plugin.json");
        string malformedPath = Path.Combine(fixture.DirectoryPath, "malformed.rig2cast-plugin.json");
        File.WriteAllText(duplicatePath, JsonSerializer.Serialize(fixture.Manifest));
        File.WriteAllText(malformedPath, "{ not-json }");
        var loader = new RadioPluginLoader();

        (IReadOnlyList<LoadedRadioPlugin> plugins, IReadOnlyList<PluginLoadDiagnostic> diagnostics) =
            await loader.DiscoverAsync(
                fixture.DirectoryPath,
                [new PluginTrustRecord(fixture.Manifest.Id, fixture.ComputeAssemblyHash())]);
        try
        {
            Assert.Single(plugins);
            Assert.Contains(diagnostics, item => item.Status == PluginLoadStatus.Loaded);
            Assert.Contains(diagnostics, item => item.Status == PluginLoadStatus.Duplicate);
            Assert.Contains(diagnostics, item => item.Status == PluginLoadStatus.InvalidManifest);
        }
        finally
        {
            foreach (LoadedRadioPlugin plugin in plugins) plugin.Dispose();
        }
    }

    [Fact]
    public async Task HostConfigurationIsStrictAndResolvesRelativeDirectories()
    {
        using var fixture = PluginFixture.Create();
        string configurationPath = Path.Combine(fixture.DirectoryPath, "plugin-host.json");
        File.WriteAllText(configurationPath, """
            {
              "pluginDirectories": ["plugins"],
              "trustRecords": [],
              "developmentMode": false
            }
            """);

        RadioPluginHostConfiguration configuration =
            await RadioPluginHostConfiguration.ReadAsync(configurationPath);

        Assert.Equal(
            Path.Combine(fixture.DirectoryPath, "plugins"),
            Assert.Single(configuration.PluginDirectories));

        File.WriteAllText(configurationPath, """
            {
              "pluginDirectories": [],
              "trustRecords": [],
              "unknown": true
            }
            """);
        PluginLoadException failure = await Assert.ThrowsAsync<PluginLoadException>(
            () => RadioPluginHostConfiguration.ReadAsync(configurationPath).AsTask());
        Assert.Equal(PluginLoadStatus.InvalidManifest, failure.Status);
    }

    [Fact]
    public void HostConfigurationRejectsDuplicateTrustIdentities()
    {
        const string hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        PluginLoadException failure = Assert.Throws<PluginLoadException>(() =>
            RadioPluginHostConfiguration.Create(
                [],
                [new("plugin.one", hash), new("PLUGIN.ONE", hash)]));

        Assert.Equal(PluginLoadStatus.InvalidManifest, failure.Status);
    }

    [Fact]
    public async Task CatalogCompositionRegistersTrustedPluginAndOwnsItsLifetime()
    {
        using var fixture = PluginFixture.Create();
        var catalog = new RadioDriverCatalog();
        RadioPluginHostConfiguration configuration = RadioPluginHostConfiguration.Create(
            [fixture.DirectoryPath],
            [new(fixture.Manifest.Id, fixture.ComputeAssemblyHash())]);

        using RadioPluginCatalogComposition composition =
            await RadioPluginCatalogComposition.LoadAsync(catalog, configuration);

        Assert.True(catalog.TryFind(Ftdx10CatProfile.ModelId, out RadioModelRegistration? registration));
        Assert.NotNull(registration);
        Assert.Contains(composition.Diagnostics, item => item.Status == PluginLoadStatus.Loaded);

        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        await using IRadioDriver driver = await registration!.Factory.OpenAsync(
            new RadioConnectionOptions(
                "plugin-radio", Ftdx10CatProfile.ModelId,
                new Dictionary<string, string>()),
            transport);
        Assert.Equal("FTDX10", driver.Capabilities.Model);
    }

    [Fact]
    public async Task CatalogCompositionIsolatesBuiltInConflictAndMissingDirectory()
    {
        using var fixture = PluginFixture.Create();
        var catalog = new RadioDriverCatalog();
        catalog.Register(new Ftdx10DriverFactory());
        string missingDirectory = Path.Combine(fixture.DirectoryPath, "missing");
        RadioPluginHostConfiguration configuration = RadioPluginHostConfiguration.Create(
            [fixture.DirectoryPath, missingDirectory],
            [new(fixture.Manifest.Id, fixture.ComputeAssemblyHash())]);

        using RadioPluginCatalogComposition composition =
            await RadioPluginCatalogComposition.LoadAsync(catalog, configuration);

        Assert.Single(catalog.Models);
        Assert.Contains(composition.Diagnostics, item => item.Status == PluginLoadStatus.Duplicate);
        Assert.Contains(composition.Diagnostics, item =>
            item.Status == PluginLoadStatus.InvalidManifest && item.ManifestPath == missingDirectory);
    }

    [Fact]
    public async Task DisposedCompositionRejectsNewDriversWithoutInterruptingActiveDriver()
    {
        using var fixture = PluginFixture.Create();
        var catalog = new RadioDriverCatalog();
        RadioPluginHostConfiguration configuration = RadioPluginHostConfiguration.Create(
            [fixture.DirectoryPath],
            [new(fixture.Manifest.Id, fixture.ComputeAssemblyHash())]);
        RadioPluginCatalogComposition composition =
            await RadioPluginCatalogComposition.LoadAsync(catalog, configuration);
        RadioModelRegistration registration = catalog.Find(Ftdx10CatProfile.ModelId);
        var activeTransport = new ScriptedRadioTransport();
        activeTransport.Add("ID;", "ID0761;");
        IRadioDriver activeDriver = await registration.Factory.OpenAsync(
            new RadioConnectionOptions("active", Ftdx10CatProfile.ModelId, new Dictionary<string, string>()),
            activeTransport);

        composition.Dispose();

        Assert.Equal("FTDX10", activeDriver.Capabilities.Model);
        var rejectedTransport = new ScriptedRadioTransport();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => registration.Factory.OpenAsync(
            new RadioConnectionOptions("rejected", Ftdx10CatProfile.ModelId, new Dictionary<string, string>()),
            rejectedTransport).AsTask());
        Assert.Equal(1, rejectedTransport.DisposeCount);

        await activeDriver.DisposeAsync();
        Assert.Equal(1, activeTransport.DisposeCount);
    }

    [Fact]
    public async Task DuplicatePluginLoadLeavesExistingRegistrationAvailable()
    {
        using var fixture = PluginFixture.Create();
        var catalog = new RadioDriverCatalog();
        RadioPluginHostConfiguration configuration = RadioPluginHostConfiguration.Create(
            [fixture.DirectoryPath],
            [new(fixture.Manifest.Id, fixture.ComputeAssemblyHash())]);
        using RadioPluginCatalogComposition original =
            await RadioPluginCatalogComposition.LoadAsync(catalog, configuration);
        RadioModelRegistration existing = catalog.Find(Ftdx10CatProfile.ModelId);

        using RadioPluginCatalogComposition replacement =
            await RadioPluginCatalogComposition.LoadAsync(catalog, configuration);

        Assert.Same(existing.Factory, catalog.Find(Ftdx10CatProfile.ModelId).Factory);
        Assert.Contains(replacement.Diagnostics, diagnostic =>
            diagnostic.Status == PluginLoadStatus.Duplicate &&
            diagnostic.Message.Contains("existing registration remains active", StringComparison.Ordinal));
        var transport = new ScriptedRadioTransport();
        transport.Add("ID;", "ID0761;");
        await using IRadioDriver driver = await existing.Factory.OpenAsync(
            new RadioConnectionOptions("original", Ftdx10CatProfile.ModelId, new Dictionary<string, string>()),
            transport);
        Assert.Equal("FTDX10", driver.Capabilities.Model);
    }

    private sealed class PluginFixture : IDisposable
    {
        private PluginFixture(string directoryPath, string manifestPath, string assemblyPath, PluginManifest manifest)
        {
            DirectoryPath = directoryPath;
            ManifestPath = manifestPath;
            AssemblyPath = assemblyPath;
            Manifest = manifest;
        }

        public string DirectoryPath { get; }
        public string ManifestPath { get; }
        public string AssemblyPath { get; }
        public PluginManifest Manifest { get; private set; }

        public static PluginFixture Create()
        {
            string directory = Path.Combine(Path.GetTempPath(), $"rig2cast-plugin-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            string sourceAssembly = typeof(Ftdx10DriverFactory).Assembly.Location;
            string assemblyPath = Path.Combine(directory, Path.GetFileName(sourceAssembly));
            File.Copy(sourceAssembly, assemblyPath);
            string manifestPath = Path.Combine(directory, "yaesu.rig2cast-plugin.json");
            var factory = new Ftdx10DriverFactory();
            var manifest = new PluginManifest(
                factory.Descriptor.Id,
                factory.Descriptor.Version.ToString(),
                factory.Descriptor.ApiVersion.ToString(),
                Path.GetFileName(assemblyPath),
                typeof(Ftdx10DriverFactory).FullName!,
                factory.Descriptor.Models.Select(model => new PluginModelManifest(
                    model.Id,
                    model.Manufacturer,
                    model.Model,
                    model.SupportedTransports.ToArray(),
                    model.SupportedBaudRates,
                    model.DefaultBaudRate,
                    model.DefaultConnectionSettings)
                {
                    ConnectionSettings = model.ConnectionSettings
                }).ToArray());
            var fixture = new PluginFixture(directory, manifestPath, assemblyPath, manifest);
            fixture.WriteManifest(manifest);
            return fixture;
        }

        public void WriteManifest(PluginManifest manifest, object? additional = null)
        {
            Manifest = manifest;
            if (additional is null)
            {
                File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest));
                return;
            }
            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(manifest));
            var values = document.RootElement.EnumerateObject()
                .ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
            foreach (System.Reflection.PropertyInfo property in additional.GetType().GetProperties())
                values[property.Name] = property.GetValue(additional);
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(values));
        }

        public string ComputeAssemblyHash()
        {
            using FileStream stream = File.OpenRead(AssemblyPath);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Collectible assembly contexts may release mapped files after a later GC.
            }
        }
    }
}
