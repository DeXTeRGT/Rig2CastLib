using Rig2Cast.Rotators;

namespace Rig2Cast.WebGui;

public sealed record RotatorStatus(
    string Id,
    string DisplayName,
    string ModelId,
    bool Connected,
    bool AllowControl,
    RotatorCapabilities? Capabilities,
    RotatorPosition? Position,
    string? MapImage,
    double MapNorthOffsetDegrees,
    double MapOpacity,
    string BezelColor,
    bool ShowBeam,
    double BeamWidthDegrees,
    string BeamColor,
    double BeamOpacity,
    string? Error,
    DateTimeOffset ObservedAt);

public sealed class RotatorWebHost : BackgroundService
{
    private readonly StationConfiguration _station;
    private readonly RotatorDriverCatalog _catalog = new();
    private readonly Dictionary<string, RotatorSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public RotatorWebHost(StationConfiguration station)
    {
        _station = station;
        _catalog.Register(new Gs232RotatorDriverFactory());
        _catalog.Register(new ProsistelRotatorDriverFactory());
        if (!station.Presentation.ShowRotatorGui) return;
        foreach (StationRotator configuration in station.Rotators.Where(item => item.Enabled))
            _sessions.Add(configuration.Id, new RotatorSession(configuration, _catalog.Find(configuration.ModelId)));
    }

    public object GetConfiguration() => new
    {
        showRotatorGui = _station.Presentation.ShowRotatorGui,
        models = _catalog.Models,
        rotators = _sessions.Values.Select(session => EffectiveStatus(session.Status)).ToArray()
    };

    public IReadOnlyList<RotatorStatus> GetRotators() => _sessions.Values.Select(session => EffectiveStatus(session.Status)).ToArray();
    public RotatorStatus GetRotator(string id) => EffectiveStatus(Find(id).Status);

    public async Task MoveToAsync(string id, double azimuth, CancellationToken cancellationToken) =>
        await FindControllable(id).ExecuteAsync(driver => driver.MoveToAsync(new RotatorPosition(azimuth), cancellationToken), cancellationToken);
    public async Task StartAsync(string id, RotatorDirection direction, CancellationToken cancellationToken) =>
        await FindControllable(id).ExecuteAsync(driver => driver.StartAsync(direction, cancellationToken), cancellationToken);
    public async Task StopAsync(string id, CancellationToken cancellationToken) =>
        await FindControllable(id).ExecuteAsync(driver => driver.StopAsync(cancellationToken), cancellationToken);
    public async Task ParkAsync(string id, CancellationToken cancellationToken)
    {
        RotatorSession session = FindControllable(id);
        if (session.Configuration.ParkAzimuth is double park)
            await session.ExecuteAsync(driver => driver.MoveToAsync(new RotatorPosition(park), cancellationToken), cancellationToken);
        else
            await session.ExecuteAsync(driver => driver.ParkAsync(cancellationToken), cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_station.Presentation.ShowRotatorGui) return;
        await Task.WhenAll(_sessions.Values.Where(session => session.Configuration.AutoConnect)
            .Select(session => session.RunAsync(stoppingToken))).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        foreach (RotatorSession session in _sessions.Values) await session.DisposeAsync().ConfigureAwait(false);
    }

    private RotatorSession Find(string id) => _sessions.TryGetValue(id, out RotatorSession? session)
        ? session : throw new KeyNotFoundException($"Rotator '{id}' is not configured.");
    private RotatorSession FindControllable(string id)
    {
        RotatorSession session = Find(id);
        if (!_station.Access.AllowRotatorControl || !session.Configuration.AllowControl)
            throw new UnauthorizedAccessException($"Rotator '{id}' is read-only.");
        return session;
    }
    private RotatorStatus EffectiveStatus(RotatorStatus status) =>
        status with { AllowControl = _station.Access.AllowRotatorControl && status.AllowControl };

    private sealed class RotatorSession(StationRotator configuration, IRotatorDriverFactory factory) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _connectionGate = new(1, 1);
        private IRotatorDriver? _driver;
        private int _commandsWaiting;
        private RotatorStatus _status = new(configuration.Id, configuration.DisplayName, configuration.ModelId,
            false, configuration.AllowControl, null, null, configuration.MapImage, configuration.MapNorthOffsetDegrees,
            configuration.MapOpacity, configuration.BezelColor, configuration.ShowBeam, configuration.BeamWidthDegrees,
            configuration.BeamColor, configuration.BeamOpacity, null, DateTimeOffset.MinValue);
        public StationRotator Configuration => configuration;
        public RotatorStatus Status => Volatile.Read(ref _status);

        public async Task RunAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_driver is null) await ConnectAsync(stoppingToken).ConfigureAwait(false);
                    await Task.Delay(configuration.PollIntervalMilliseconds, stoppingToken).ConfigureAwait(false);
                    if (Volatile.Read(ref _commandsWaiting) != 0) continue;
                    await _connectionGate.WaitAsync(stoppingToken).ConfigureAwait(false);
                    try
                    {
                        if (Volatile.Read(ref _commandsWaiting) != 0) continue;
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        timeout.CancelAfter(configuration.ResponseTimeoutMilliseconds);
                        RotatorPosition position = await Driver.ReadPositionAsync(timeout.Token).ConfigureAwait(false);
                        Update(Status with { Connected = true, Position = position, Error = null, ObservedAt = DateTimeOffset.UtcNow });
                    }
                    finally { _connectionGate.Release(); }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    Update(Status with { Connected = false, Error = error.Message, ObservedAt = DateTimeOffset.UtcNow });
                    await DisconnectAsync().ConfigureAwait(false);
                    try { await Task.Delay(2000, stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        public async Task ExecuteAsync(Func<IRotatorDriver, ValueTask> command, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _commandsWaiting);
            try
            {
                await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await command(Driver).ConfigureAwait(false);
                    if (configuration.CommandSettleMilliseconds > 0)
                        await Task.Delay(configuration.CommandSettleMilliseconds, cancellationToken).ConfigureAwait(false);
                }
                finally { _connectionGate.Release(); }
            }
            finally { Interlocked.Decrement(ref _commandsWaiting); }
        }

        private async Task ConnectAsync(CancellationToken cancellationToken)
        {
            IRotatorTransport transport = configuration.Transport.Equals("Tcp", StringComparison.OrdinalIgnoreCase)
                ? new TcpRotatorTransport(configuration.TcpHost ?? throw new InvalidDataException($"Rotator '{configuration.Id}' requires TcpHost."),
                    configuration.TcpPort ?? throw new InvalidDataException($"Rotator '{configuration.Id}' requires TcpPort."))
                : new SerialRotatorTransport(configuration.SerialPort ?? throw new InvalidDataException($"Rotator '{configuration.Id}' requires SerialPort."), configuration.BaudRate);
            _driver = await factory.OpenAsync(transport, configuration.Settings, cancellationToken).ConfigureAwait(false);
            RotatorCapabilities capabilities = configuration.ParkAzimuth is not null
                ? _driver.Capabilities with { CanPark = true }
                : _driver.Capabilities;
            Update(Status with { Connected = true, Capabilities = capabilities, Error = null, ObservedAt = DateTimeOffset.UtcNow });
        }

        private IRotatorDriver Driver => _driver ?? throw new InvalidOperationException($"Rotator '{configuration.Id}' is disconnected.");
        private void Update(RotatorStatus value) => Volatile.Write(ref _status, value);
        private async Task DisconnectAsync()
        {
            IRotatorDriver? driver = Interlocked.Exchange(ref _driver, null);
            if (driver is not null) await driver.DisposeAsync().ConfigureAwait(false);
        }
        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync().ConfigureAwait(false);
            _connectionGate.Dispose();
        }
    }
}
