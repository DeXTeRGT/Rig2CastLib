namespace Rig2Cast.WebGui;

public sealed class StationStartupService(
    StationConfiguration station,
    RadioWebHost radios,
    ILogger<StationStartupService> logger) : IHostedService
{
    private static readonly Action<ILogger, string, Exception?> Started =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(2001, "StationStarted"), "Locked station {StationId} radio started.");
    private static readonly Action<ILogger, string, Exception?> StartFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(2002, "StationStartFailed"), "Locked station {StationId} radio failed to start; the service remains available for diagnostics.");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!station.IsLocked || !station.Station.AutoConnect) return;
        try
        {
            await radios.EnsureStationStartedAsync(cancellationToken);
            Started(logger, station.Station.Id, null);
        }
        catch (Exception ex)
        {
            StartFailed(logger, station.Station.Id, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
