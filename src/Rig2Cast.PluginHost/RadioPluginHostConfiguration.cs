using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rig2Cast.PluginHost;

public sealed record RadioPluginHostConfiguration(
    IReadOnlyList<string> PluginDirectories,
    IReadOnlyList<PluginTrustRecord> TrustRecords,
    bool DevelopmentMode = false)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async ValueTask<RadioPluginHostConfiguration> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        try
        {
            await using FileStream stream = File.OpenRead(fullPath);
            RadioPluginHostConfiguration configuration =
                await JsonSerializer.DeserializeAsync<RadioPluginHostConfiguration>(
                    stream, JsonOptions, cancellationToken).ConfigureAwait(false) ??
                throw new JsonException("The plugin host configuration is empty.");
            return ValidateAndResolve(configuration, Path.GetDirectoryName(fullPath)!);
        }
        catch (PluginLoadException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            throw new PluginLoadException(
                PluginLoadStatus.InvalidManifest,
                $"Plugin host configuration '{fullPath}' could not be read: {exception.Message}",
                exception);
        }
    }

    public static RadioPluginHostConfiguration Create(
        IEnumerable<string> pluginDirectories,
        IEnumerable<PluginTrustRecord>? trustRecords = null,
        bool developmentMode = false,
        string? baseDirectory = null) =>
        ValidateAndResolve(
            new RadioPluginHostConfiguration(
                pluginDirectories?.ToArray() ?? throw new ArgumentNullException(nameof(pluginDirectories)),
                trustRecords?.ToArray() ?? [],
                developmentMode),
            Path.GetFullPath(baseDirectory ?? Environment.CurrentDirectory));

    private static RadioPluginHostConfiguration ValidateAndResolve(
        RadioPluginHostConfiguration configuration,
        string baseDirectory)
    {
        if (configuration.PluginDirectories is null)
            throw Invalid("PluginDirectories is required.");
        if (configuration.TrustRecords is null)
            throw Invalid("TrustRecords is required.");

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in configuration.PluginDirectories)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw Invalid("Plugin directories cannot be empty.");
            string resolved = Path.GetFullPath(directory, baseDirectory);
            if (!directories.Add(resolved))
                throw Invalid($"Plugin directory '{resolved}' is configured more than once.");
        }

        var trustIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginTrustRecord trust in configuration.TrustRecords)
        {
            if (trust is null || string.IsNullOrWhiteSpace(trust.PluginId))
                throw Invalid("Every trust record requires a plugin ID.");
            if (!trustIds.Add(trust.PluginId))
                throw Invalid($"Plugin trust identity '{trust.PluginId}' is configured more than once.");
            if (trust.AssemblySha256 is null || trust.AssemblySha256.Length != 64 ||
                !trust.AssemblySha256.All(Uri.IsHexDigit))
                throw Invalid($"Plugin trust identity '{trust.PluginId}' requires a 64-character SHA-256 hash.");
        }

        return configuration with { PluginDirectories = directories.ToArray() };
    }

    private static PluginLoadException Invalid(string message) =>
        new(PluginLoadStatus.InvalidManifest, message);
}
