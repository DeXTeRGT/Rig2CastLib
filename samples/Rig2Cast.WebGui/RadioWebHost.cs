using System.Net;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Meters;
using Rig2Cast.Abstractions.Radios;
using Rig2Cast.Abstractions.Security;
using Rig2Cast.Abstractions.Sessions;
using Rig2Cast.Abstractions.Transports;
using Rig2Cast.Core.Drivers;
using Rig2Cast.Drivers.Elecraft.K3Family;
using Rig2Cast.Drivers.Icom.Ic7300;
using Rig2Cast.Drivers.Xiegu.G90;
using Rig2Cast.Drivers.Yaesu.Ftdx10;
using Rig2Cast.Runtime.Sessions;
using Rig2Cast.Simulator;
using Rig2Cast.Transports.Serial;
using Rig2Cast.Transports.Tcp;

namespace Rig2Cast.WebGui;

public sealed record ConnectRequest(
    string ClientId,
    string ModelId,
    string Transport,
    string? SerialPort,
    int? BaudRate,
    string? TcpHost,
    int? TcpPort,
    IReadOnlyDictionary<string, string>? Settings,
    bool EnableWrites = false,
    string ModeRestrictions = "Enforce");

public sealed class RadioWebHost : IAsyncDisposable
{
    private static readonly TimeSpan WebTransmitLeaseDuration = TimeSpan.FromSeconds(10);
    private readonly RadioDriverCatalog _catalog = new();
    private readonly SystemSerialPortDiscovery _ports = new();
    private readonly SemaphoreSlim _registryGate = new(1, 1);
    private readonly Dictionary<string, RadioEntry> _radios = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _radioIdsByEndpoint = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _serverAllowsWrites;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public RadioWebHost(IConfiguration configuration)
    {
        _serverAllowsWrites = configuration.GetValue("Rig2Cast:AllowWrites", false);
        _catalog.Register(new Ftdx10DriverFactory());
        _catalog.Register(new ElecraftK3DriverFactory());
        _catalog.Register(new Ic7300DriverFactory());
        _catalog.Register(new G90DriverFactory());
    }

    public object GetStatus()
    {
        _registryGate.Wait();
        try
        {
            return new
            {
                activeRadios = _radios.Count,
                serverAllowsWrites = _serverAllowsWrites,
                pttAvailable = true,
                rawCatAvailable = false
            };
        }
        finally { _registryGate.Release(); }
    }

    public object GetModels() => _catalog.Models.Select(item => new
    {
        id = item.Model.Id,
        item.Model.Manufacturer,
        item.Model.Model,
        transports = item.Model.SupportedTransports,
        baudRates = item.Model.SupportedBaudRates,
        item.Model.DefaultBaudRate,
        connectionSettings = item.Model.ConnectionSettings,
        simulatorAvailable = item.Model.Id.Equals(Ftdx10CatProfile.ModelId, StringComparison.OrdinalIgnoreCase)
    });

    public IReadOnlyList<SerialPortDescriptor> GetSerialPorts() => _ports.GetPorts();

    public object GetRadios()
    {
        _registryGate.Wait();
        try { return _radios.Values.Select(ProjectRadio).ToArray(); }
        finally { _registryGate.Release(); }
    }

    public async Task<WebConnectionResult> ConnectOrAttachAsync(ConnectRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ClientId);
        await _registryGate.WaitAsync(cancellationToken);
        try
        {
            RadioModelRegistration registration = _catalog.Find(request.ModelId);
            RadioTransportKind transportKind = Enum.Parse<RadioTransportKind>(request.Transport, true);
            string endpointKey = GetEndpointKey(registration.Model, transportKind, request);
            if (_radioIdsByEndpoint.TryGetValue(endpointKey, out string? existingRadioId))
            {
                RadioEntry existing = _radios[existingRadioId];
                ClientAttachment attachment = Attach(existing, request.ClientId);
                return await ConnectionResultAsync(existing, attachment, true, cancellationToken);
            }

            RadioEntry? created = null;
            try
            {
                ManagedRadio radio = await OpenRadioAsync(registration, transportKind, request, cancellationToken);
                string radioId = $"radio-{Guid.NewGuid():N}";
                bool ownerCanWrite = _serverAllowsWrites && request.EnableWrites;
                created = new RadioEntry(
                    radioId, endpointKey, EndpointDisplay(transportKind, request), registration.Model.Id,
                    registration.Model.Manufacturer, registration.Model.Model, transportKind, radio,
                    request.ClientId, ownerCanWrite);
                ClientAttachment attachment = Attach(created, request.ClientId);
                _radios.Add(radioId, created);
                _radioIdsByEndpoint.Add(endpointKey, radioId);
                return await ConnectionResultAsync(created, attachment, false, cancellationToken);
            }
            catch
            {
                if (created is not null) await created.DisposeAsync();
                throw;
            }
        }
        finally { _registryGate.Release(); }
    }

    private static ClientAttachment Attach(RadioEntry entry, string clientId)
    {
        if (entry.Attachments.TryGetValue(clientId, out ClientAttachment? current)) return current;
        bool owner = StringComparer.Ordinal.Equals(clientId, entry.OwnerClientId);
        ClientRole role = owner && entry.OwnerCanWrite ? ClientRole.Operator : ClientRole.Observer;
        IRadioSession session = entry.Radio.OpenSession(
            new ClientIdentity(clientId, owner ? "Web radio owner" : "Web observer"), role);
        var attachment = new ClientAttachment(clientId, session, owner, role);
        entry.Attachments.Add(clientId, attachment);
        return attachment;
    }

    private static async Task<WebConnectionResult> ConnectionResultAsync(
        RadioEntry entry, ClientAttachment attachment, bool attachedExisting, CancellationToken cancellationToken) => new(
        entry.RadioId,
        attachedExisting ? "attached" : "opened",
        attachment.Role,
        attachment.IsOwner,
        attachment.Role == ClientRole.Observer,
        attachedExisting
            ? "This physical radio is already open. This page was attached to the existing connection read-only."
            : attachment.Role == ClientRole.Operator
                ? "The radio was opened and this page has operator access."
                : "The radio was opened read-only by server or connection policy.",
        await attachment.Session.GetSnapshotAsync(cancellationToken));

    public async Task DetachAsync(string radioId, string clientId)
    {
        ClientAttachment? attachment;
        await _registryGate.WaitAsync();
        try
        {
            RadioEntry entry = FindRadio(radioId);
            if (!entry.Attachments.Remove(clientId, out attachment))
                throw new KeyNotFoundException("This browser is not attached to that radio.");
        }
        finally { _registryGate.Release(); }
        await attachment.DisposeAsync();
    }

    public async Task CloseRadioAsync(string radioId, string clientId)
    {
        RadioEntry entry;
        await _registryGate.WaitAsync();
        try
        {
            entry = FindRadio(radioId);
            if (!StringComparer.Ordinal.Equals(entry.OwnerClientId, clientId))
                throw new UnauthorizedAccessException("Only the page that opened this physical radio may close it.");
            _radios.Remove(entry.RadioId);
            _radioIdsByEndpoint.Remove(entry.EndpointKey);
        }
        finally { _registryGate.Release(); }
        await entry.DisposeAsync();
    }

    private static async Task<ManagedRadio> OpenRadioAsync(
        RadioModelRegistration registration, RadioTransportKind transportKind, ConnectRequest request,
        CancellationToken cancellationToken)
    {
        if (transportKind != RadioTransportKind.Simulator && !registration.Model.SupportedTransports.Contains(transportKind))
            throw new ArgumentException($"{registration.Model.Model} does not advertise {transportKind} transport support.");
        Dictionary<string, string> settings = request.Settings is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(request.Settings, StringComparer.OrdinalIgnoreCase);
        ResolvedConnectionSettings resolved = ConnectionSettingsResolver.Resolve(registration.Model, settings);
        var managedOptions = new ManagedRadioOptions
        {
            ModeApplicabilityPolicy = Enum.Parse<ModeApplicabilityPolicy>(request.ModeRestrictions, true)
        };
        if (transportKind == RadioTransportKind.Simulator)
        {
            if (!registration.Model.Id.Equals(Ftdx10CatProfile.ModelId, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("This POC currently provides a simulator only for the FTDX10 model.");
            return await ManagedRadio.CreateAsync("web-radio", new SimulatedFtdx10Driver(), managedOptions,
                cancellationToken: cancellationToken);
        }

        RadioConnectionOptions options = new("web-radio", registration.Model.Id, settings) { ResolvedSettings = resolved };
        Func<IRadioTransport> transportFactory = transportKind switch
        {
            RadioTransportKind.Serial => CreateSerialTransport(registration.Model, request),
            RadioTransportKind.Tcp => CreateTcpTransport(request),
            _ => throw new NotSupportedException($"Transport {transportKind} is not supported by this POC.")
        };
        RadioDriverConnector connector = ct => registration.Factory.OpenAsync(options, transportFactory(), ct);
        return await ManagedRadio.CreateReconnectableAsync("web-radio", connector, managedOptions,
            cancellationToken: cancellationToken);
    }

    private static string GetEndpointKey(RadioModelDescriptor model, RadioTransportKind kind, ConnectRequest request) => kind switch
    {
        RadioTransportKind.Serial when !string.IsNullOrWhiteSpace(request.SerialPort) =>
            $"serial:{request.SerialPort.Trim().ToUpperInvariant()}",
        RadioTransportKind.Tcp when !string.IsNullOrWhiteSpace(request.TcpHost) && request.TcpPort is > 0 and <= 65535 =>
            $"tcp:{NormalizeHost(request.TcpHost)}:{request.TcpPort}",
        RadioTransportKind.Simulator => $"simulator:{model.Id.ToLowerInvariant()}",
        RadioTransportKind.Serial => throw new ArgumentException("Select or enter a serial port."),
        RadioTransportKind.Tcp => throw new ArgumentException("Enter a valid raw TCP host and port."),
        _ => throw new NotSupportedException($"Transport {kind} is not supported by this POC.")
    };

    private static string NormalizeHost(string host)
    {
        string value = host.Trim().ToLowerInvariant();
        if (value is "localhost") return IPAddress.Loopback.ToString();
        return IPAddress.TryParse(value, out IPAddress? address) ? address.ToString() : value;
    }

    private static string EndpointDisplay(RadioTransportKind kind, ConnectRequest request) => kind switch
    {
        RadioTransportKind.Serial => request.SerialPort!.Trim(),
        RadioTransportKind.Tcp => $"{request.TcpHost!.Trim()}:{request.TcpPort}",
        RadioTransportKind.Simulator => "In-process simulator",
        _ => kind.ToString()
    };

    private static Func<IRadioTransport> CreateSerialTransport(RadioModelDescriptor model, ConnectRequest request)
    {
        int baud = request.BaudRate ?? model.DefaultBaudRate ?? throw new ArgumentException("Select a baud rate.");
        SerialConnectionSettings serial = SerialConnectionSettings.FromModel(model, request.SerialPort!.Trim(), baud);
        return () => SerialRadioTransportFactory.Create(model, serial);
    }

    private static Func<IRadioTransport> CreateTcpTransport(ConnectRequest request)
    {
        var options = new TcpRadioTransportOptions { Host = request.TcpHost!.Trim(), Port = request.TcpPort!.Value };
        return () => new TcpRadioTransport(options);
    }

    private RadioEntry FindRadio(string radioId) => _radios.TryGetValue(radioId, out RadioEntry? entry)
        ? entry : throw new KeyNotFoundException($"Radio '{radioId}' is not active.");

    private ClientAttachment GetAttachment(string radioId, string clientId)
    {
        _registryGate.Wait();
        try
        {
            RadioEntry entry = FindRadio(radioId);
            return entry.Attachments.TryGetValue(clientId, out ClientAttachment? attachment)
                ? attachment
                : throw new UnauthorizedAccessException("This browser is not attached to that radio.");
        }
        finally { _registryGate.Release(); }
    }

    private static object ProjectRadio(RadioEntry entry) => new
    {
        entry.RadioId, entry.ModelId, entry.Manufacturer, entry.Model, transport = entry.TransportKind,
        endpoint = entry.EndpointDisplay, attachmentCount = entry.Attachments.Count
    };

    private IRadioSession Session(string radioId, string clientId) => GetAttachment(radioId, clientId).Session;
    public ValueTask<RadioSnapshot> GetSnapshotAsync(string radioId, string clientId, CancellationToken ct) => Session(radioId, clientId).GetSnapshotAsync(ct);
    public async Task<RadioSnapshot> RefreshAsync(string radioId, string clientId, CancellationToken ct) { IRadioSession session = Session(radioId, clientId); await session.RefreshStateAsync(ct); return await session.GetSnapshotAsync(ct); }
    public ValueTask SetFrequencyAsync(string radioId, string clientId, VfoId vfo, long value, CancellationToken ct) => Session(radioId, clientId).SetFrequencyAsync(vfo, value, ct);
    public ValueTask SetActiveVfoAsync(string radioId, string clientId, VfoId vfo, CancellationToken ct) => Session(radioId, clientId).SetActiveVfoAsync(vfo, ct);
    public ValueTask SetModeAsync(string radioId, string clientId, RadioMode mode, CancellationToken ct) => Session(radioId, clientId).SetModeAsync(mode, ct);
    public ValueTask SetSplitAsync(string radioId, string clientId, bool value, CancellationToken ct) => Session(radioId, clientId).SetSplitAsync(value, ct);
    public ValueTask<RadioControlValue> ReadControlAsync(string radioId, string clientId, RadioControlId id, CancellationToken ct) => Session(radioId, clientId).ReadControlAsync(id, ct);
    public ValueTask WriteControlAsync(string radioId, string clientId, RadioControlId id, int value, CancellationToken ct) => Session(radioId, clientId).WriteControlAsync(id, value, ct);
    public ValueTask<RadioSwitchValue> ReadSwitchAsync(string radioId, string clientId, RadioSwitchId id, CancellationToken ct) => Session(radioId, clientId).ReadSwitchAsync(id, ct);
    public ValueTask WriteSwitchAsync(string radioId, string clientId, RadioSwitchId id, bool value, CancellationToken ct) => Session(radioId, clientId).WriteSwitchAsync(id, value, ct);
    public ValueTask<RadioChoiceValue> ReadChoiceAsync(string radioId, string clientId, RadioChoiceId id, CancellationToken ct) => Session(radioId, clientId).ReadChoiceAsync(id, ct);
    public ValueTask WriteChoiceAsync(string radioId, string clientId, RadioChoiceId id, string value, CancellationToken ct) => Session(radioId, clientId).WriteChoiceAsync(id, value, ct);
    public ValueTask<RadioMeterReading> ReadMeterAsync(string radioId, string clientId, RadioMeterId id, CancellationToken ct) => Session(radioId, clientId).ReadMeterAsync(id, ct);
    public ValueTask<RadioPassbandValue> ReadPassbandAsync(string radioId, string clientId, CancellationToken ct) => Session(radioId, clientId).ReadPassbandAsync(ct);
    public ValueTask WritePassbandAsync(string radioId, string clientId, int value, CancellationToken ct) => Session(radioId, clientId).SetPassbandAsync(value, ct);

    public async ValueTask<RadioSnapshot> SetPttAsync(
        string radioId, string clientId, bool enabled, CancellationToken cancellationToken)
    {
        ClientAttachment attachment = GetAttachment(radioId, clientId);
        EnsurePttAuthorized(attachment);
        await attachment.PttGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (enabled)
            {
                LeaseToken lease = attachment.TransmitLease is { } current && current.ExpiresAt > DateTimeOffset.UtcNow
                    ? await attachment.Session.RenewLeaseAsync(current, WebTransmitLeaseDuration, cancellationToken).ConfigureAwait(false)
                    : await attachment.Session.AcquireLeaseAsync(LeaseKinds.Transmit, WebTransmitLeaseDuration, cancellationToken).ConfigureAwait(false);
                try
                {
                    await attachment.Session.SetPttAsync(true, lease, cancellationToken).ConfigureAwait(false);
                    attachment.TransmitLease = lease;
                }
                catch
                {
                    try { await attachment.Session.ReleaseLeaseAsync(lease, CancellationToken.None).ConfigureAwait(false); }
                    catch (InvalidLeaseException) { }
                    attachment.TransmitLease = null;
                    throw;
                }
            }
            else
            {
                await StopPttCoreAsync(attachment, cancellationToken).ConfigureAwait(false);
            }

            return await attachment.Session.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            attachment.PttGate.Release();
        }
    }

    public async ValueTask<PttLeaseStatus> RenewPttAsync(
        string radioId, string clientId, CancellationToken cancellationToken)
    {
        ClientAttachment attachment = GetAttachment(radioId, clientId);
        EnsurePttAuthorized(attachment);
        await attachment.PttGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LeaseToken current = attachment.TransmitLease
                ?? throw new InvalidOperationException("PTT is not active for this browser.");
            LeaseToken renewed = await attachment.Session.RenewLeaseAsync(
                current, WebTransmitLeaseDuration, cancellationToken).ConfigureAwait(false);
            attachment.TransmitLease = renewed;
            return new PttLeaseStatus(renewed.ExpiresAt);
        }
        catch (InvalidLeaseException)
        {
            attachment.TransmitLease = null;
            throw;
        }
        finally
        {
            attachment.PttGate.Release();
        }
    }

    private static void EnsurePttAuthorized(ClientAttachment attachment)
    {
        if (!attachment.IsOwner || attachment.Role != ClientRole.Operator)
            throw new UnauthorizedAccessException("Only the owning Operator page may control PTT.");
    }

    private static async ValueTask StopPttCoreAsync(
        ClientAttachment attachment, CancellationToken cancellationToken)
    {
        LeaseToken lease = attachment.TransmitLease is { } current && current.ExpiresAt > DateTimeOffset.UtcNow
            ? current
            : await attachment.Session.AcquireLeaseAsync(
                LeaseKinds.Transmit, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        try
        {
            await attachment.Session.SetPttAsync(false, lease, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { await attachment.Session.ReleaseLeaseAsync(lease, CancellationToken.None).ConfigureAwait(false); }
            catch (InvalidLeaseException) { }
            attachment.TransmitLease = null;
        }
    }

    public async Task StreamSnapshotsAsync(string radioId, string clientId, WebSocket socket, CancellationToken cancellationToken)
    {
        IRadioSession session = Session(radioId, clientId);
        await SendAsync(socket, new { type = "snapshot", snapshot = await session.GetSnapshotAsync(cancellationToken) }, cancellationToken);
        await foreach (var radioEvent in session.WatchEventsAsync(cancellationToken))
        {
            if (socket.State != WebSocketState.Open) break;
            RadioSnapshot snapshot = await session.GetSnapshotAsync(cancellationToken);
            await SendAsync(socket, new { type = "radioEvent", radioEvent.Sequence, radioEvent.Kind, radioEvent.OccurredAt, snapshot }, cancellationToken);
        }
    }

    private Task SendAsync(WebSocket socket, object value, CancellationToken ct) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, _json), WebSocketMessageType.Text, true, ct);

    public async ValueTask DisposeAsync()
    {
        RadioEntry[] entries;
        await _registryGate.WaitAsync();
        try { entries = _radios.Values.ToArray(); _radios.Clear(); _radioIdsByEndpoint.Clear(); }
        finally { _registryGate.Release(); }
        foreach (RadioEntry entry in entries) await entry.DisposeAsync();
        _registryGate.Dispose();
    }

    private sealed class ClientAttachment(
        string clientId, IRadioSession session, bool isOwner, ClientRole role) : IAsyncDisposable
    {
        public string ClientId { get; } = clientId;
        public IRadioSession Session { get; } = session;
        public bool IsOwner { get; } = isOwner;
        public ClientRole Role { get; } = role;
        public SemaphoreSlim PttGate { get; } = new(1, 1);
        public LeaseToken? TransmitLease { get; set; }

        public async ValueTask DisposeAsync()
        {
            var failures = new List<Exception>();
            await PttGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (TransmitLease is { } lease && lease.ExpiresAt > DateTimeOffset.UtcNow)
                {
                    try { await Session.SetPttAsync(false, lease, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is InvalidLeaseException or ObjectDisposedException) { }
                    catch (Exception exception) { failures.Add(exception); }
                    try { await Session.ReleaseLeaseAsync(lease, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is InvalidLeaseException or ObjectDisposedException) { }
                    catch (Exception exception) { failures.Add(exception); }
                }
                TransmitLease = null;
                try { await Session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { failures.Add(exception); }
            }
            finally
            {
                PttGate.Release();
                PttGate.Dispose();
            }

            if (failures.Count == 1)
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count > 1)
                throw new AggregateException("Web attachment cleanup encountered multiple failures.", failures);
        }
    }

    private sealed class RadioEntry(
        string radioId, string endpointKey, string endpointDisplay, string modelId, string manufacturer,
        string model, RadioTransportKind transportKind, ManagedRadio radio, string ownerClientId, bool ownerCanWrite)
        : IAsyncDisposable
    {
        public string RadioId { get; } = radioId;
        public string EndpointKey { get; } = endpointKey;
        public string EndpointDisplay { get; } = endpointDisplay;
        public string ModelId { get; } = modelId;
        public string Manufacturer { get; } = manufacturer;
        public string Model { get; } = model;
        public RadioTransportKind TransportKind { get; } = transportKind;
        public ManagedRadio Radio { get; } = radio;
        public string OwnerClientId { get; } = ownerClientId;
        public bool OwnerCanWrite { get; } = ownerCanWrite;
        public Dictionary<string, ClientAttachment> Attachments { get; } = new(StringComparer.Ordinal);

        public async ValueTask DisposeAsync()
        {
            foreach (ClientAttachment attachment in Attachments.Values) await attachment.DisposeAsync();
            Attachments.Clear();
            await Radio.DisposeAsync();
        }
    }
}
