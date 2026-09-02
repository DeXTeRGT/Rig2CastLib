using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Transports;

namespace Rig2Cast.PluginHost;

internal sealed class PluginRegistrationLifetime : IDisposable
{
    private readonly object _gate = new();
    private LoadedRadioPlugin? _plugin;
    private int _activeDrivers;
    private bool _disposed;

    public PluginRegistrationLifetime(LoadedRadioPlugin plugin)
    {
        _plugin = plugin;
        Factory = new LifetimeAwareFactory(this, plugin.Factory.Descriptor);
    }

    public IRadioDriverFactory Factory { get; }

    public void Dispose()
    {
        LoadedRadioPlugin? unload = null;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_activeDrivers == 0)
            {
                unload = _plugin;
                _plugin = null;
            }
        }
        unload?.Dispose();
    }

    private async ValueTask<IRadioDriver> OpenAsync(
        RadioConnectionOptions options,
        IRadioTransport transport,
        CancellationToken cancellationToken)
    {
        IRadioDriverFactory? factory;
        lock (_gate)
        {
            factory = _disposed ? null : _plugin!.Factory;
            if (factory is not null) _activeDrivers++;
        }
        if (factory is null)
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(
                nameof(RadioPluginCatalogComposition),
                "The plugin composition has been disposed and cannot open new drivers.");
        }

        var trackedTransport = new LifetimeTrackingTransport(transport, ReleaseDriver);
        try
        {
            return await factory.OpenAsync(options, trackedTransport, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await trackedTransport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void ReleaseDriver()
    {
        LoadedRadioPlugin? unload = null;
        lock (_gate)
        {
            _activeDrivers--;
            if (_disposed && _activeDrivers == 0)
            {
                unload = _plugin;
                _plugin = null;
            }
        }
        unload?.Dispose();
    }

    private sealed class LifetimeAwareFactory(
        PluginRegistrationLifetime owner,
        RadioDriverDescriptor descriptor) : IRadioDriverFactory
    {
        public RadioDriverDescriptor Descriptor { get; } = descriptor;

        public ValueTask<IRadioDriver> OpenAsync(
            RadioConnectionOptions options,
            IRadioTransport transport,
            CancellationToken cancellationToken = default) =>
            owner.OpenAsync(options, transport, cancellationToken);
    }

    private sealed class LifetimeTrackingTransport(
        IRadioTransport inner,
        Action release) : IRadioTransport
    {
        private int _disposed;

        public string Id => inner.Id;
        public bool IsConnected => inner.IsConnected;

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default) =>
            inner.ConnectAsync(cancellationToken);

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) =>
            inner.DisconnectAsync(cancellationToken);

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(data, cancellationToken);

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                release();
            }
        }
    }
}
