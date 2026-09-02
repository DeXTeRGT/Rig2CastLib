using System.Security.Cryptography;
using System.Text.Json;
using Rig2Cast.Drivers.Yaesu.Ftdx10;
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
                    model.DefaultConnectionSettings)).ToArray());
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
