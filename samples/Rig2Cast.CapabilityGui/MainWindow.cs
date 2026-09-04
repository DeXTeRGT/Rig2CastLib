using System.Globalization;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Rig2Cast.Abstractions.Capabilities;
using Rig2Cast.Abstractions.Controls;
using Rig2Cast.Abstractions.Drivers;
using Rig2Cast.Abstractions.Events;
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
using Rig2Cast.Simulator.Civ;
using Rig2Cast.Transports.Serial;
using Rig2Cast.Transports.Tcp;

namespace Rig2Cast.CapabilityGui;

public sealed class MainWindow : Window, IAsyncDisposable
{
    private readonly RadioDriverCatalog _catalog = new();
    private readonly ComboBox _model = new() { MinWidth = 260 };
    private readonly ComboBox _transport = new() { MinWidth = 150 };
    private readonly ComboBox _baud = new() { MinWidth = 120 };
    private readonly ComboBox _ports = new() { MinWidth = 150 };
    private readonly CheckBox _usePortOverride = new() { Content = "Override discovered port" };
    private readonly TextBox _manualPort = new()
    {
        PlaceholderText = "COM16 or /dev/ttyUSB0",
        MinWidth = 210,
        IsEnabled = false
    };
    private readonly TextBox _tcpHost = new() { Text = "127.0.0.1", MinWidth = 180 };
    private readonly NumericUpDown _tcpPort = new() { Value = 5555, Minimum = 1, Maximum = 65535, MinWidth = 110 };
    private readonly CheckBox _allowWrites = new() { Content = "Enable non-PTT writes" };
    private readonly StackPanel _transportFields = new() { Spacing = 8 };
    private readonly StackPanel _protocolFields = new() { Spacing = 8 };
    private readonly StackPanel _radioContent = new() { Spacing = 12 };
    private readonly StackPanel _controlContent = new() { Spacing = 12 };
    private readonly StackPanel _meterContent = new() { Spacing = 12 };
    private readonly ObservableCollection<string> _diagnosticEntries = [];
    private readonly TextBlock _status = new() { Text = "Select a model and transport.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _radioTitle = new() { Text = "No radio connected", FontSize = 24, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _connectionBadge = new() { Text = "OFFLINE", FontSize = 12, FontWeight = FontWeight.Bold };
    private readonly TextBlock _radioSummary = new() { Foreground = Brush.Parse("#94A3B8") };
    private readonly WrapPanel _headerVfos = new() { Orientation = Orientation.Horizontal };
    private readonly Button _connect = new()
    {
        Content = "Connect",
        HorizontalAlignment = HorizontalAlignment.Left,
        Background = Brush.Parse("#0284C7"),
        Foreground = Brushes.White
    };
    private readonly Button _disconnect = new() { Content = "Disconnect", IsEnabled = false };
    private readonly Button _refreshPorts = new() { Content = "Refresh ports" };
    private readonly Button _refreshState = new() { Content = "Refresh all", IsEnabled = false };
    private readonly Dictionary<string, Control> _settingEditors = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Name, Func<Task> Refresh)> _controlRefreshers = [];
    private readonly Dictionary<RadioControlId, NumericUpDown> _numericEditors = [];
    private readonly Dictionary<RadioSwitchId, CheckBox> _switchEditors = [];
    private readonly Dictionary<RadioChoiceId, ComboBox> _choiceEditors = [];
    private readonly Dictionary<RadioMeterId, TextBlock> _meterEditors = [];
    private readonly Dictionary<VfoId, TextBox> _frequencyEditors = [];
    private readonly Dictionary<VfoId, TextBlock> _frequencyDisplays = [];
    private readonly Dictionary<VfoId, TextBlock> _vfoRoleLabels = [];
    private readonly Dictionary<VfoId, Border> _vfoCards = [];
    private readonly Dictionary<string, StackPanel> _controlCategories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Grid> _switchCategoryPanels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _switchCategoryCounts = new(StringComparer.Ordinal);
    private ComboBox? _modeEditor;
    private ComboBox? _activeVfoEditor;
    private CheckBox? _splitEditor;
    private ManagedRadio? _radio;
    private IRadioSession? _session;
    private CivRadioSimulator? _civSimulator;
    private CancellationTokenSource? _watchStopping;
    private Task? _watchTask;
    private long _connectionGeneration;
    private bool _updatingEditors;

    public MainWindow()
    {
        Title = "Rig2Cast capability-driven sample";
        Width = 1280;
        Height = 820;
        MinWidth = 900;
        MinHeight = 620;
        Background = Brush.Parse("#0B1120");

        _catalog.Register(new Ftdx10DriverFactory());
        _catalog.Register(new ElecraftK3DriverFactory());
        _catalog.Register(new Ic7300DriverFactory());
        _catalog.Register(new G90DriverFactory());

        _model.ItemsSource = _catalog.Models;
        _model.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<RadioModelRegistration>(
            (item, _) => new TextBlock { Text = $"{item.Model.Manufacturer} {item.Model.Model}" });
        _model.SelectionChanged += (_, _) => RunUiAction(RenderModelConfiguration);
        _transport.SelectionChanged += (_, _) => RunUiAction(RenderTransportConfiguration);
        _usePortOverride.IsCheckedChanged += (_, _) =>
            RunUiAction(() => _manualPort.IsEnabled = _usePortOverride.IsChecked == true);
        _refreshPorts.Click += (_, _) => RunUiAction(RefreshPorts);
        _connect.Click += async (_, _) => await ConnectAsync();
        _disconnect.Click += async (_, _) =>
        {
            if (await DisconnectAsync()) _status.Text = "Disconnected.";
        };
        _refreshState.Click += async (_, _) => await RefreshAllAsync();
        Closed += async (_, _) => await DisconnectAsync();

        Content = BuildContent();
        RefreshPorts();
        _model.SelectedIndex = 0;
    }

    public void ReportUnhandledUiException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _status.Text = $"Unexpected UI error: {exception.Message}";
        AppendDiagnostic("ERROR", _status.Text);
        _connect.IsEnabled = true;
    }

    private Grid BuildContent()
    {
        var connection = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                Heading("CONNECTION", 13, Brush.Parse("#94A3B8")),
                Row("Radio model", _model),
                Row("Transport", _transport),
                _transportFields,
                Heading("MODEL SETTINGS", 13, Brush.Parse("#94A3B8")),
                _protocolFields,
                _allowWrites,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _connect, _disconnect, _refreshState }
                },
                StatusCard()
            }
        };

        var sidebar = new Border
        {
            Background = Brush.Parse("#111827"),
            BorderBrush = Brush.Parse("#263247"),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Padding = new Thickness(20),
            Child = new ScrollViewer { Content = connection }
        };

        var tabs = new TabControl
        {
            Margin = new Thickness(0, 12, 0, 0),
            ItemsSource = new[]
            {
                WorkspaceTab("Radio", _radioContent),
                WorkspaceTab("Controls", _controlContent),
                WorkspaceTab("Meters", _meterContent),
                DiagnosticsTab()
            }
        };

        var workspace = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        workspace.Children.Add(BuildRadioHeader());
        Grid.SetRow(tabs, 1);
        workspace.Children.Add(tabs);

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("460,*") };
        root.Children.Add(sidebar);
        var workspaceContainer = new Border
        {
            Padding = new Thickness(24, 18),
            Child = workspace
        };
        Grid.SetColumn(workspaceContainer, 1);
        root.Children.Add(workspaceContainer);
        return root;
    }

    private StackPanel BuildRadioHeader()
    {
        var badge = new Border
        {
            Background = Brush.Parse("#334155"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 5),
            Child = _connectionBadge,
            VerticalAlignment = VerticalAlignment.Center
        };
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        title.Children.Add(new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = "RIG2CAST", Foreground = Brush.Parse("#38BDF8"), FontSize = 12, FontWeight = FontWeight.Bold },
                _radioTitle,
                _radioSummary
            }
        });
        Grid.SetColumn(badge, 1);
        title.Children.Add(badge);
        return new StackPanel
        {
            Spacing = 12,
            Children = { title, _headerVfos }
        };
    }

    private Border StatusCard()
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = "SESSION STATUS",
            Foreground = Brush.Parse("#94A3B8"),
            FontSize = 11,
            FontWeight = FontWeight.Bold
        });
        body.Children.Add(_status);
        body.Children.Add(_state);
        return new Border
        {
            Background = Brush.Parse("#0F172A"),
            BorderBrush = Brush.Parse("#263247"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Child = body
        };
    }

    private static TabItem WorkspaceTab(string header, StackPanel content) => new()
    {
        Header = header,
        Content = new ScrollViewer
        {
            Padding = new Thickness(0, 16, 8, 8),
            Content = content
        }
    };

    private TabItem DiagnosticsTab()
    {
        var clear = new Button { Content = "Clear log", HorizontalAlignment = HorizontalAlignment.Left };
        clear.Click += (_, _) => _diagnosticEntries.Clear();
        var entries = new ListBox
        {
            ItemsSource = _diagnosticEntries,
            Margin = new Thickness(0, 10, 0, 0),
            Background = Brush.Parse("#080D17")
        };
        Grid.SetRow(entries, 1);
        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Margin = new Thickness(0, 16, 8, 8)
        };
        content.Children.Add(clear);
        content.Children.Add(entries);
        return new TabItem
        {
            Header = "Diagnostics",
            Content = content
        };
    }

    private void RenderModelConfiguration()
    {
        if (_model.SelectedItem is not RadioModelRegistration registration) return;
        _transport.ItemsSource = registration.Model.SupportedTransports
            .Where(kind => kind is RadioTransportKind.Serial or RadioTransportKind.Tcp or RadioTransportKind.Simulator)
            .OrderBy(kind => kind)
            .ToArray();
        _transport.SelectedItem = registration.Model.SupportedTransports.Contains(RadioTransportKind.Simulator)
            ? RadioTransportKind.Simulator
            : registration.Model.SupportedTransports.First();
        _baud.ItemsSource = registration.Model.SupportedBaudRates;
        _baud.SelectedItem = registration.Model.DefaultBaudRate ??
            (registration.Model.SupportedBaudRates.Count > 0 ? registration.Model.SupportedBaudRates[0] : 0);
        RenderProtocolSettings(registration.Model);
        ClearCapabilityContent();
        _radioTitle.Text = $"{registration.Model.Manufacturer} {registration.Model.Model}";
        _radioContent.Children.Add(EmptyState(
            "Capability-driven controls",
            "Connect to generate operational controls from the driver's runtime capabilities."));
    }

    private void RenderTransportConfiguration()
    {
        _transportFields.Children.Clear();
        if (_transport.SelectedItem is not RadioTransportKind kind) return;
        if (kind == RadioTransportKind.Serial)
        {
            _transportFields.Children.Add(Row("Discovered port", _ports));
            _transportFields.Children.Add(_refreshPorts);
            _transportFields.Children.Add(_usePortOverride);
            _transportFields.Children.Add(Row("Port override", _manualPort));
            _transportFields.Children.Add(Row("Baud", _baud));
        }
        else if (kind == RadioTransportKind.Tcp)
        {
            _transportFields.Children.Add(Row("TCP host", _tcpHost));
            _transportFields.Children.Add(Row("TCP port", _tcpPort));
            _transportFields.Children.Add(new TextBlock
            {
                Text = "Raw serial bytes only; configure serial framing on the remote bridge.",
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap
            });
        }
        else
        {
            _transportFields.Children.Add(new TextBlock { Text = "In-process deterministic simulator" });
        }
    }

    private void RenderProtocolSettings(RadioModelDescriptor model)
    {
        _protocolFields.Children.Clear();
        _settingEditors.Clear();
        if (model.ConnectionSettings.Count == 0)
        {
            _protocolFields.Children.Add(new TextBlock { Text = "No model-specific settings." });
            return;
        }
        foreach (ConnectionSettingDefinition definition in model.ConnectionSettings)
        {
            Control editor;
            if (definition.ValueType == ConnectionSettingValueType.Boolean)
            {
                editor = new CheckBox
                {
                    Content = definition.DisplayName,
                    IsChecked = bool.TryParse(definition.DefaultValue, out bool value) && value
                };
            }
            else if (definition.Choices is { Count: > 0 })
            {
                editor = new ComboBox
                {
                    ItemsSource = definition.Choices,
                    SelectedItem = definition.DefaultValue,
                    MinWidth = 150
                };
            }
            else
            {
                editor = new TextBox { Text = definition.DefaultValue, MinWidth = 150 };
            }
            ToolTip.SetTip(editor, definition.Description);
            _settingEditors.Add(definition.Id, editor);
            _protocolFields.Children.Add(Row(definition.DisplayName, editor));
        }
    }

    private void RefreshPorts()
    {
        string[] names = new SystemSerialPortDiscovery().GetPorts()
            .Select(port => port.PortName).ToArray();
        _ports.ItemsSource = names;
        if (names.Length > 0)
            _ports.SelectedIndex = 0;
    }

    private async Task ConnectAsync()
    {
        if (!await DisconnectAsync()) return;
        if (_model.SelectedItem is not RadioModelRegistration registration ||
            _transport.SelectedItem is not RadioTransportKind transportKind) return;
        SetBusy(true, $"Opening {registration.Model.Manufacturer} {registration.Model.Model}...");
        AppendDiagnostic("INFO", _status.Text ?? "Opening radio.");
        IRadioDriver? openedDriver = null;
        try
        {
            Dictionary<string, string> userValues = ReadConnectionSettings();
            ResolvedConnectionSettings resolved = ConnectionSettingsResolver.Resolve(
                registration.Model, userValues);
            if (transportKind == RadioTransportKind.Simulator)
            {
                openedDriver = await OpenSimulatorDriverAsync(registration, userValues, resolved);
                _radio = await ManagedRadio.CreateAsync("gui-radio", openedDriver);
                openedDriver = null;
            }
            else
            {
                RadioDriverConnector connector = CreatePhysicalConnector(
                    registration, transportKind, userValues, resolved);
                _radio = await ManagedRadio.CreateReconnectableAsync("gui-radio", connector);
            }
            ClientRole role = _allowWrites.IsChecked == true ? ClientRole.Operator : ClientRole.Observer;
            _session = _radio.OpenSession(
                new ClientIdentity("capability-gui", "Rig2Cast capability GUI sample"), role);
            RadioSnapshot snapshot = await _session.GetSnapshotAsync();
            RenderCapabilities(snapshot.Capabilities, snapshot.Authorization.Roles.Contains(ClientRole.Operator));
            RenderState(snapshot.State);
            long generation = ++_connectionGeneration;
            StartEventWatcher(_session, generation);
            await RefreshAllAsync();
            _status.Text = $"Connected to {snapshot.Capabilities.Manufacturer} {snapshot.Capabilities.Model} " +
                $"as {role}. Settings were validated before opening the transport.";
            AppendDiagnostic("INFO", _status.Text);
            _disconnect.IsEnabled = true;
            _refreshState.IsEnabled = true;
        }
        catch (Exception exception)
        {
            string? driverCleanupError = null;
            if (openedDriver is not null)
            {
                try
                {
                    await openedDriver.DisposeAsync();
                }
                catch (Exception cleanupException)
                {
                    driverCleanupError = cleanupException.Message;
                }
            }
            bool cleanupSucceeded = await DisconnectAsync();
            string cleanupDetail = _status.Text ?? "Unknown cleanup error.";
            string cleanupSuffix = cleanupSucceeded ? string.Empty : $" {cleanupDetail}";
            if (driverCleanupError is not null)
                cleanupSuffix += $" Driver cleanup: {driverCleanupError}";
            _status.Text = $"Connection failed: {exception.Message}{cleanupSuffix}";
            AppendDiagnostic("ERROR", _status.Text);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async ValueTask<IRadioDriver> OpenSimulatorDriverAsync(
        RadioModelRegistration registration,
        IReadOnlyDictionary<string, string> userValues,
        ResolvedConnectionSettings resolved)
    {
        if (registration.Model.Id.Equals(Ftdx10CatProfile.ModelId, StringComparison.OrdinalIgnoreCase))
            return new SimulatedFtdx10Driver();

        if (!registration.Model.Id.Equals(Ic7300Profile.ModelId, StringComparison.OrdinalIgnoreCase) &&
            !registration.Model.Id.Equals(G90Profile.ModelId, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("This small sample has no Elecraft simulator peer; use serial or raw TCP.");
        byte radioAddress = resolved.Get<byte>("icom.civAddress");
        byte controllerAddress = resolved.Get<byte>("icom.controllerAddress");
        var transport = new InMemoryRadioTransport($"gui:{registration.Model.Id}");
        await transport.ConnectAsync();
        _civSimulator = new CivRadioSimulator(transport, new CivSimulatorOptions
        {
            RadioAddress = radioAddress,
            ControllerAddress = controllerAddress,
            SupportsXieguIdentity = registration.Model.Id.Equals(G90Profile.ModelId, StringComparison.OrdinalIgnoreCase),
            SupportsXieguExtendedVfo = registration.Model.Id.Equals(G90Profile.ModelId, StringComparison.OrdinalIgnoreCase)
        });

        return await registration.Factory.OpenAsync(
            new RadioConnectionOptions("gui-radio", registration.Model.Id, userValues)
            {
                ResolvedSettings = resolved
            }, transport);
    }

    private RadioDriverConnector CreatePhysicalConnector(
        RadioModelRegistration registration,
        RadioTransportKind transportKind,
        IReadOnlyDictionary<string, string> userValues,
        ResolvedConnectionSettings resolved)
    {
        Func<IRadioTransport> createTransport;
        if (transportKind == RadioTransportKind.Serial)
        {
            string portName = _usePortOverride.IsChecked == true
                ? !string.IsNullOrWhiteSpace(_manualPort.Text)
                    ? _manualPort.Text.Trim()
                    : throw new ArgumentException("Enter a port name when port override is enabled.")
                : _ports.SelectedItem?.ToString() ??
                    throw new ArgumentException("Select a discovered serial port or enable port override.");
            int baudRate = _baud.SelectedItem is int selectedBaud
                ? selectedBaud : registration.Model.DefaultBaudRate ?? throw new ArgumentException("Select a baud rate.");
            SerialConnectionSettings serial = SerialConnectionSettings.FromModel(
                registration.Model, portName, baudRate);
            createTransport = () => SerialRadioTransportFactory.Create(registration.Model, serial);
        }
        else if (transportKind == RadioTransportKind.Tcp)
        {
            var tcp = new TcpRadioTransportOptions
            {
                Host = _tcpHost.Text?.Trim() ?? string.Empty,
                Port = decimal.ToInt32(_tcpPort.Value ?? 0)
            };
            createTransport = () => new TcpRadioTransport(tcp);
        }
        else
        {
            throw new NotSupportedException($"Transport {transportKind} is not supported by this sample.");
        }

        var options = new RadioConnectionOptions("gui-radio", registration.Model.Id,
            new Dictionary<string, string>(userValues, StringComparer.OrdinalIgnoreCase))
        {
            ResolvedSettings = resolved
        };
        return cancellationToken => registration.Factory.OpenAsync(
            options, createTransport(), cancellationToken);
    }

    private Dictionary<string, string> ReadConnectionSettings()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, Control editor) in _settingEditors)
        {
            string? value = editor switch
            {
                CheckBox checkBox => (checkBox.IsChecked == true).ToString(CultureInfo.InvariantCulture),
                ComboBox comboBox => comboBox.SelectedItem?.ToString(),
                TextBox textBox => textBox.Text,
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(value)) values[id] = value.Trim();
        }
        return values;
    }

    private void RenderCapabilities(RadioCapabilities capabilities, bool writesAuthorized)
    {
        ClearCapabilityContent();
        _controlRefreshers.Clear();
        _numericEditors.Clear();
        _switchEditors.Clear();
        _choiceEditors.Clear();
        _meterEditors.Clear();
        _frequencyEditors.Clear();
        _frequencyDisplays.Clear();
        _vfoRoleLabels.Clear();
        _vfoCards.Clear();
        _controlCategories.Clear();
        _switchCategoryPanels.Clear();
        _switchCategoryCounts.Clear();
        _modeEditor = null;
        _activeVfoEditor = null;
        _splitEditor = null;
        _radioTitle.Text = $"{capabilities.Manufacturer} {capabilities.Model}";
        _radioContent.Children.Add(new TextBlock
        {
            Text = $"Driver {capabilities.DriverId} {capabilities.DriverVersion} · capability revision {capabilities.Revision}"
        });
        var core = new StackPanel { Spacing = 8 };
        core.Children.Add(new TextBlock
        {
            Text = $"VFOs: {string.Join(", ", capabilities.Vfos.Available)}\n" +
                   $"Modes: {string.Join(", ", capabilities.Modes.Values)}\n" +
                   $"Frequency: {FormatFeature(capabilities.Frequency.Feature)}\n" +
                   $"Split: {FormatFeature(capabilities.Vfos.Split)}\n" +
                   $"PTT: {FormatFeature(capabilities.Transmit)} (not exposed by this sample)",
            TextWrapping = TextWrapping.Wrap
        });

        AddFrequencyEditors(capabilities, writesAuthorized);
        AddVfoAndSplitEditors(capabilities, writesAuthorized, core);
        AddModeEditor(capabilities, writesAuthorized, core);
        _radioContent.Children.Add(Group("Operating state", core));
        AddNumericControls(capabilities, writesAuthorized);
        AddSwitchControls(capabilities, writesAuthorized);
        AddChoiceControls(capabilities, writesAuthorized);
        RenderControlCategories();
        AddMeters(capabilities);
        if (_controlContent.Children.Count == 0)
            _controlContent.Children.Add(EmptyState(
                "No extended controls",
                "This driver does not advertise numeric, switch, or choice controls."));
        if (_meterContent.Children.Count == 0)
            _meterContent.Children.Add(EmptyState(
                "No meters",
                "This driver does not advertise readable meter values."));
    }

    private void AddFrequencyEditors(RadioCapabilities capabilities, bool writesAuthorized)
    {
        _headerVfos.Children.Clear();
        foreach (VfoId target in capabilities.Frequency.Targets)
        {
            var value = new TextBox { PlaceholderText = "Exact frequency in Hz", MinWidth = 170 };
            var display = new TextBlock
            {
                Text = "--.------ MHz",
                FontSize = 25,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush.Parse("#E2E8F0")
            };
            var role = new TextBlock
            {
                Text = "STANDBY",
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = Brush.Parse("#64748B")
            };
            _frequencyEditors[target] = value;
            _frequencyDisplays[target] = display;
            _vfoRoleLabels[target] = role;
            var apply = new Button
            {
                Content = "Apply",
                IsEnabled = writesAuthorized && CanWrite(capabilities.Frequency.Feature)
            };
            apply.Click += async (_, _) => await RunUiOperationAsync(async () =>
            {
                if (!long.TryParse(value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long frequency))
                    throw new ArgumentException("Frequency must be an integer number of hertz.");
                await _session!.SetFrequencyAsync(target, frequency);
                await RefreshStateAsync();
            });

            long baseStep = capabilities.Frequency.SmallestStepHz is > 0
                ? capabilities.Frequency.SmallestStepHz.Value
                : 10;
            long[] steps = [baseStep, 10, 100, 1_000, 10_000, 100_000];
            var step = new ComboBox
            {
                ItemsSource = steps.Distinct().OrderBy(candidate => candidate).ToArray(),
                SelectedItem = baseStep,
                MinWidth = 100
            };
            var down = new Button
            {
                Content = "−",
                IsEnabled = writesAuthorized && CanWrite(capabilities.Frequency.Feature)
            };
            var up = new Button
            {
                Content = "+",
                IsEnabled = writesAuthorized && CanWrite(capabilities.Frequency.Feature)
            };
            down.Click += async (_, _) => await StepFrequencyAsync(target, value, step, -1);
            up.Click += async (_, _) => await StepFrequencyAsync(target, value, step, 1);

            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            header.Children.Add(Heading($"VFO {target}", 16));
            Grid.SetColumn(role, 1);
            header.Children.Add(role);
            var cardBody = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    header,
                    display,
                    new TextBlock { Text = "Exact value (Hz)", Foreground = Brush.Parse("#94A3B8"), FontSize = 11 },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { value, apply }
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children =
                        {
                            new TextBlock { Text = "Step Hz", VerticalAlignment = VerticalAlignment.Center },
                            step, down, up
                        }
                    }
                }
            };
            var card = new Border
            {
                Width = 340,
                Background = Brush.Parse("#0F172A"),
                BorderBrush = Brush.Parse("#263247"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 12, 12),
                Child = cardBody
            };
            ToolTip.SetTip(card, FeatureTip("Frequency", capabilities.Frequency.Feature,
                $"Smallest advertised step: {capabilities.Frequency.SmallestStepHz?.ToString(CultureInfo.InvariantCulture) ?? "unspecified"} Hz"));
            _vfoCards[target] = card;
            _headerVfos.Children.Add(card);
        }
    }

    private async Task StepFrequencyAsync(VfoId target, TextBox editor, ComboBox stepEditor, int direction)
    {
        await RunUiOperationAsync(async () =>
        {
            if (!long.TryParse(editor.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long frequency) ||
                stepEditor.SelectedItem is not long step)
                throw new ArgumentException("Read the VFO first and select a tuning step.");
            long requested = checked(frequency + direction * step);
            await _session!.SetFrequencyAsync(target, requested);
            await RefreshStateAsync();
        });
    }

    private void AddModeEditor(
        RadioCapabilities capabilities, bool writesAuthorized, StackPanel section)
    {
        var modes = new ComboBox { ItemsSource = capabilities.Modes.Values.ToArray(), MinWidth = 150 };
        _modeEditor = modes;
        modes.SelectedIndex = modes.ItemCount > 0 ? 0 : -1;
        var apply = new Button
        {
            Content = "Apply",
            IsEnabled = writesAuthorized && CanWrite(capabilities.Modes.Feature)
        };
        ToolTip.SetTip(modes, FeatureTip("Mode", capabilities.Modes.Feature,
            $"Available values: {string.Join(", ", capabilities.Modes.Values)}"));
        apply.Click += async (_, _) => await RunUiOperationAsync(async () =>
        {
            if (modes.SelectedItem is not RadioMode mode) throw new ArgumentException("Select a mode.");
            await _session!.SetModeAsync(mode);
            await RefreshStateAsync();
        });
        section.Children.Add(EditorRow("Mode", modes, null, apply, capabilities.Modes.Feature));
    }

    private void AddVfoAndSplitEditors(
        RadioCapabilities capabilities, bool writesAuthorized, StackPanel section)
    {
        var selectedVfo = new ComboBox
        {
            ItemsSource = capabilities.Vfos.Available.ToArray(),
            MinWidth = 120
        };
        _activeVfoEditor = selectedVfo;
        selectedVfo.SelectedIndex = selectedVfo.ItemCount > 0 ? 0 : -1;
        var select = new Button
        {
            Content = "Select VFO",
            IsEnabled = writesAuthorized && CanWrite(capabilities.Vfos.Selection)
        };
        ToolTip.SetTip(selectedVfo, FeatureTip("VFO selection", capabilities.Vfos.Selection));
        select.Click += async (_, _) => await RunUiOperationAsync(async () =>
        {
            if (selectedVfo.SelectedItem is not VfoId vfo) throw new ArgumentException("Select a VFO.");
            await _session!.SetActiveVfoAsync(vfo);
            await RefreshStateAsync();
        });
        section.Children.Add(EditorRow(
            "Active VFO", selectedVfo, null, select, capabilities.Vfos.Selection));

        var split = new CheckBox
        {
            Content = "Enabled",
            IsEnabled = writesAuthorized && CanWrite(capabilities.Vfos.Split)
        };
        ToolTip.SetTip(split, FeatureTip("Split", capabilities.Vfos.Split));
        _splitEditor = split;
        split.IsCheckedChanged += async (_, _) =>
        {
            if (_updatingEditors || _session is null || !CanWrite(capabilities.Vfos.Split)) return;
            await RunUiOperationAsync(async () =>
            {
                await _session.SetSplitAsync(split.IsChecked == true);
                await RefreshStateAsync();
            });
        };
        section.Children.Add(EditorRow("Split", split, null, null, capabilities.Vfos.Split));
    }

    private void AddNumericControls(RadioCapabilities capabilities, bool writesAuthorized)
    {
        if (capabilities.Controls.Count == 0) return;
        foreach (NumericControlDescriptor descriptor in capabilities.Controls.Values)
        {
            var value = new NumericUpDown
            {
                Minimum = descriptor.Minimum,
                Maximum = descriptor.Maximum,
                Increment = descriptor.Step,
                MinWidth = 120
            };
            ToolTip.SetTip(value, FeatureTip(descriptor.DisplayName, descriptor.Feature,
                $"Range: {descriptor.Minimum}..{descriptor.Maximum}; step {descriptor.Step}; unit {descriptor.Unit}"));
            var read = ReadButton(CanRead(descriptor.Feature), async () =>
            {
                RadioControlValue result = await _session!.ReadControlAsync(descriptor.Id);
                value.Value = result.Value;
                return $"{descriptor.DisplayName} = {result.Value} {descriptor.Unit}";
            });
            _numericEditors[descriptor.Id] = value;
            if (CanRead(descriptor.Feature))
                _controlRefreshers.Add((descriptor.DisplayName,
                    async () => _ = await ReadNumericAsync(descriptor, value)));
            var write = new Button
            {
                Content = "Apply",
                IsEnabled = writesAuthorized && CanWrite(descriptor.Feature)
            };
            write.Click += async (_, _) => await RunUiOperationAsync(async () =>
            {
                await _session!.WriteControlAsync(descriptor.Id, decimal.ToInt32(value.Value ?? 0));
            });
            GetControlCategory(ControlCategory(descriptor.Id)).Children.Add(
                EditorRow(descriptor.DisplayName, value, read, write, descriptor.Feature));
        }
    }

    private void AddSwitchControls(RadioCapabilities capabilities, bool writesAuthorized)
    {
        if (capabilities.Switches.Count == 0) return;
        foreach (SwitchControlDescriptor descriptor in capabilities.Switches.Values)
        {
            var value = new CheckBox
            {
                Content = "Enabled",
                IsEnabled = writesAuthorized && CanWrite(descriptor.Feature)
            };
            ToolTip.SetTip(value, FeatureTip(descriptor.DisplayName, descriptor.Feature));
            _switchEditors[descriptor.Id] = value;
            if (CanRead(descriptor.Feature))
                _controlRefreshers.Add((descriptor.DisplayName,
                    async () => _ = await ReadSwitchAsync(descriptor, value)));
            value.IsCheckedChanged += async (_, _) =>
            {
                if (_updatingEditors || _session is null || !CanWrite(descriptor.Feature)) return;
                await RunUiOperationAsync(async () =>
                    await _session.WriteSwitchAsync(descriptor.Id, value.IsChecked == true));
            };

            var tile = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(0, 0, 20, 10),
                MinHeight = 42
            };
            tile.Children.Add(new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    new TextBlock { Text = descriptor.DisplayName, FontWeight = FontWeight.SemiBold },
                    AccessLabel(descriptor.Feature)
                }
            });
            Grid.SetColumn(value, 1);
            tile.Children.Add(value);
            AddSwitchTile(ControlCategory(descriptor.Id), tile);
        }
    }

    private void AddChoiceControls(RadioCapabilities capabilities, bool writesAuthorized)
    {
        if (capabilities.Choices.Count == 0) return;
        foreach (ChoiceControlDescriptor descriptor in capabilities.Choices.Values)
        {
            var value = new ComboBox
            {
                ItemsSource = descriptor.Options.Keys.ToArray(),
                MinWidth = 150
            };
            ToolTip.SetTip(value, FeatureTip(descriptor.DisplayName, descriptor.Feature,
                $"Options: {string.Join(", ", descriptor.Options.Keys)}"));
            var read = ReadButton(CanRead(descriptor.Feature), async () =>
            {
                RadioChoiceValue result = await _session!.ReadChoiceAsync(descriptor.Id);
                value.SelectedItem = result.Value;
                return $"{descriptor.DisplayName} = {result.Value}";
            });
            _choiceEditors[descriptor.Id] = value;
            if (CanRead(descriptor.Feature))
                _controlRefreshers.Add((descriptor.DisplayName,
                    async () => _ = await ReadChoiceAsync(descriptor, value)));
            var write = new Button
            {
                Content = "Apply",
                IsEnabled = writesAuthorized && CanWrite(descriptor.Feature)
            };
            write.Click += async (_, _) => await RunUiOperationAsync(async () =>
            {
                string selected = value.SelectedItem?.ToString() ?? throw new ArgumentException("Select a value.");
                await _session!.WriteChoiceAsync(descriptor.Id, selected);
            });
            GetControlCategory(ControlCategory(descriptor.Id)).Children.Add(
                EditorRow(descriptor.DisplayName, value, read, write, descriptor.Feature));
        }
    }

    private StackPanel GetControlCategory(string category)
    {
        if (_controlCategories.TryGetValue(category, out StackPanel? panel)) return panel;
        panel = new StackPanel { Spacing = 9 };
        _controlCategories.Add(category, panel);
        return panel;
    }

    private Grid GetSwitchCategory(string category)
    {
        if (_switchCategoryPanels.TryGetValue(category, out Grid? panel)) return panel;
        panel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*")
        };
        _switchCategoryPanels.Add(category, panel);
        _switchCategoryCounts.Add(category, 0);
        GetControlCategory(category).Children.Add(Heading("Switches", 12, Brush.Parse("#94A3B8")));
        GetControlCategory(category).Children.Add(panel);
        return panel;
    }

    private void AddSwitchTile(string category, Grid tile)
    {
        Grid panel = GetSwitchCategory(category);
        int index = _switchCategoryCounts[category];
        int row = index / 2;
        if (index % 2 == 0)
            panel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetRow(tile, row);
        Grid.SetColumn(tile, index % 2);
        panel.Children.Add(tile);
        _switchCategoryCounts[category] = index + 1;
    }

    private void RenderControlCategories()
    {
        string[] order = ["Audio", "RF", "Transmit", "DSP", "Filtering", "CW", "Operating", "Other"];
        foreach (string category in order)
        {
            if (_controlCategories.TryGetValue(category, out StackPanel? panel))
                _controlContent.Children.Add(Group(category, panel));
        }
    }

    private static string ControlCategory(RadioControlId id) => id switch
    {
        RadioControlId.AfGain or RadioControlId.Squelch or RadioControlId.MonitorLevel => "Audio",
        RadioControlId.RfGain => "RF",
        RadioControlId.MicrophoneGain or RadioControlId.TransmitPower or
            RadioControlId.SpeechProcessorLevel or RadioControlId.VoxGain or
            RadioControlId.AntiVoxLevel => "Transmit",
        RadioControlId.NoiseReductionLevel or RadioControlId.NoiseBlankerLevel or
            RadioControlId.ManualNotchFrequencyHz or RadioControlId.ContourFrequencyHz or
            RadioControlId.AudioPeakFilterOffsetHz => "DSP",
        RadioControlId.IfShiftHz => "Filtering",
        RadioControlId.CwPitchHz or RadioControlId.KeyerSpeedWpm => "CW",
        RadioControlId.ClarifierOffsetHz => "Operating",
        _ => "Other"
    };

    private static string ControlCategory(RadioSwitchId id) => id switch
    {
        RadioSwitchId.Monitor => "Audio",
        RadioSwitchId.SpeechProcessor or RadioSwitchId.Vox or RadioSwitchId.AntennaTuner => "Transmit",
        RadioSwitchId.NoiseBlanker or RadioSwitchId.NoiseReduction or RadioSwitchId.AutoNotch or
            RadioSwitchId.ManualNotch or RadioSwitchId.Contour or RadioSwitchId.AudioPeakFilter => "DSP",
        RadioSwitchId.NarrowFilter => "Filtering",
        RadioSwitchId.BreakIn => "CW",
        RadioSwitchId.ReceiveClarifier or RadioSwitchId.TransmitClarifier or RadioSwitchId.DialLock => "Operating",
        _ => "Other"
    };

    private static string ControlCategory(RadioChoiceId id) => id switch
    {
        RadioChoiceId.Attenuator or RadioChoiceId.Preamp or RadioChoiceId.Agc => "RF",
        RadioChoiceId.VoxDelay => "Transmit",
        RadioChoiceId.AudioPeakFilterWidth => "DSP",
        RadioChoiceId.RoofingFilter or RadioChoiceId.FilterWidth => "Filtering",
        RadioChoiceId.TuningStep => "Operating",
        _ => "Other"
    };

    private void AddMeters(RadioCapabilities capabilities)
    {
        if (capabilities.Meters.Count == 0) return;
        var section = new StackPanel { Spacing = 8 };
        foreach (RadioMeterDescriptor descriptor in capabilities.Meters.Values)
        {
            var value = new TextBlock { MinWidth = 160, Text = "not read" };
            var read = ReadButton(true, async () =>
            {
                RadioMeterReading result = await _session!.ReadMeterAsync(descriptor.Id);
                value.Text = $"{result.RawValue} {descriptor.RawUnit}";
                return $"{descriptor.DisplayName} raw = {result.RawValue}";
            });
            _meterEditors[descriptor.Id] = value;
            _controlRefreshers.Add((descriptor.DisplayName,
                async () => _ = await ReadMeterAsync(descriptor, value)));
            section.Children.Add(EditorRow(
                descriptor.DisplayName,
                value,
                read,
                null,
                new FeatureDescriptor(CapabilitySupport.Supported, FeatureAccess.Read)));
        }
        _meterContent.Children.Add(Group("Meters (raw values)", section));
    }

    private Button ReadButton(bool enabled, Func<Task<string>> operation)
    {
        var button = new Button { Content = "Read", IsEnabled = enabled };
        button.Click += async (_, _) => await RunUiOperationAsync(async () =>
        {
            _status.Text = await operation();
        });
        return button;
    }

    private async Task<string> ReadNumericAsync(
        NumericControlDescriptor descriptor, NumericUpDown editor)
    {
        RadioControlValue result = await _session!.ReadControlAsync(descriptor.Id);
        editor.Value = result.Value;
        return $"{descriptor.DisplayName} = {result.Value} {descriptor.Unit}";
    }

    private async Task<string> ReadSwitchAsync(
        SwitchControlDescriptor descriptor, CheckBox editor)
    {
        RadioSwitchValue result = await _session!.ReadSwitchAsync(descriptor.Id);
        SetBooleanEditor(editor, result.Enabled);
        return $"{descriptor.DisplayName} = {(result.Enabled ? "on" : "off")}";
    }

    private void SetBooleanEditor(CheckBox editor, bool enabled)
    {
        bool previous = _updatingEditors;
        _updatingEditors = true;
        try
        {
            editor.IsChecked = enabled;
        }
        finally
        {
            _updatingEditors = previous;
        }
    }

    private async Task<string> ReadChoiceAsync(
        ChoiceControlDescriptor descriptor, ComboBox editor)
    {
        RadioChoiceValue result = await _session!.ReadChoiceAsync(descriptor.Id);
        editor.SelectedItem = result.Value;
        return $"{descriptor.DisplayName} = {result.Value}";
    }

    private async Task<string> ReadMeterAsync(
        RadioMeterDescriptor descriptor, TextBlock editor)
    {
        RadioMeterReading result = await _session!.ReadMeterAsync(descriptor.Id);
        editor.Text = $"{result.RawValue} {descriptor.RawUnit}";
        return $"{descriptor.DisplayName} raw = {result.RawValue}";
    }

    private void StartEventWatcher(IRadioSession session, long generation)
    {
        _watchStopping = new CancellationTokenSource();
        CancellationToken token = _watchStopping.Token;
        _watchTask = Task.Run(async () =>
        {
            try
            {
                await foreach (RadioEvent radioEvent in session.WatchEventsAsync(token))
                    Dispatcher.UIThread.Post(() => ApplyRadioEvent(radioEvent, generation));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                Dispatcher.UIThread.Post(() =>
                    _status.Text = $"Event watcher stopped: {exception.Message}");
            }
        }, token);
    }

    private void ApplyRadioEvent(RadioEvent radioEvent, long generation)
    {
        if (generation != _connectionGeneration || _session is null) return;
        if (radioEvent.Kind == RadioEventKind.Diagnostic)
        {
            _status.Text = $"Radio diagnostic: {radioEvent.Payload ?? "(no detail)"}";
            AppendDiagnostic("RADIO", radioEvent.Payload?.ToString() ?? "(no detail)");
        }
        switch (radioEvent.Payload)
        {
            case RadioState state:
                RenderState(state);
                break;
            case RadioControlValue control when _numericEditors.TryGetValue(control.Id, out NumericUpDown? numeric):
                numeric.Value = control.Value;
                break;
            case RadioSwitchValue value when _switchEditors.TryGetValue(value.Id, out CheckBox? toggle):
                SetBooleanEditor(toggle, value.Enabled);
                break;
            case RadioChoiceValue value when _choiceEditors.TryGetValue(value.Id, out ComboBox? choice):
                choice.SelectedItem = value.Value;
                break;
            case RadioMeterReading value when _meterEditors.TryGetValue(value.Id, out TextBlock? meter):
                meter.Text = value.RawValue.ToString(CultureInfo.InvariantCulture);
                break;
        }
    }

    private async Task RefreshAllAsync()
    {
        if (_session is null) return;
        await RunUiOperationAsync(async () =>
        {
            RenderState(await _session.RefreshStateAsync());
            var failures = new List<string>();
            foreach ((string name, Func<Task> refresh) in _controlRefreshers)
            {
                try
                {
                    _status.Text = $"Reading {name}...";
                    await refresh();
                }
                catch (Exception exception)
                {
                    failures.Add($"{name}: {exception.Message}");
                    RadioSnapshot snapshot = await _session.GetSnapshotAsync();
                    if (snapshot.State.Connection != ConnectionStatus.Connected)
                        break;
                }
            }
            _status.Text = failures.Count == 0
                ? "State and readable controls refreshed."
                : $"Refresh stopped after {failures.Count} unavailable read(s): {string.Join("; ", failures)}";
        });
    }

    private async Task RefreshStateAsync()
    {
        if (_session is null) return;
        await RunUiOperationAsync(async () => RenderState(await _session.RefreshStateAsync()));
    }

    private void RenderState(RadioState state)
    {
        foreach ((VfoId vfo, long frequency) in state.FrequenciesHz)
        {
            if (_frequencyEditors.TryGetValue(vfo, out TextBox? editor))
                editor.Text = frequency.ToString(CultureInfo.InvariantCulture);
            if (_frequencyDisplays.TryGetValue(vfo, out TextBlock? display))
                display.Text = $"{frequency / 1_000_000d:0.000000} MHz";
        }
        foreach ((VfoId vfo, Border card) in _vfoCards)
        {
            bool active = vfo == state.ActiveVfo;
            bool transmit = vfo == state.TransmitVfo;
            card.BorderBrush = transmit && state.IsSplit
                ? Brush.Parse("#FB7185")
                : active
                    ? Brush.Parse("#38BDF8")
                    : Brush.Parse("#263247");
            card.BorderThickness = new Thickness(active || transmit && state.IsSplit ? 2 : 1);
            card.Background = active ? Brush.Parse("#122033") : Brush.Parse("#0F172A");
            if (_vfoRoleLabels.TryGetValue(vfo, out TextBlock? role))
            {
                role.Text = active && transmit
                    ? "ACTIVE · RX/TX"
                    : active
                        ? "ACTIVE · RX"
                        : transmit
                            ? "TX"
                            : "STANDBY";
                role.Foreground = transmit && state.IsSplit
                    ? Brush.Parse("#FB7185")
                    : active
                        ? Brush.Parse("#38BDF8")
                        : Brush.Parse("#64748B");
            }
        }
        if (_activeVfoEditor is not null)
            _activeVfoEditor.SelectedItem = state.ActiveVfo;
        if (_modeEditor is not null)
            _modeEditor.SelectedItem = state.Mode;
        if (_splitEditor is not null)
            SetBooleanEditor(_splitEditor, state.IsSplit);
        string frequencies = string.Join(", ", state.FrequenciesHz.Select(item => $"{item.Key}={item.Value:N0} Hz"));
        _state.Text = $"{state.Connection} · {frequencies} · active {state.ActiveVfo} · " +
            $"mode {state.Mode} · split {(state.IsSplit ? "on" : "off")} · " +
            $"PTT {(state.IsTransmitting ? "TX" : "RX")}";
        _connectionBadge.Text = state.IsTransmitting ? "TRANSMITTING" : state.Connection switch
        {
            ConnectionStatus.Connected => "CONNECTED · RX",
            ConnectionStatus.Reconnecting => "RECONNECTING",
            ConnectionStatus.Faulted => "FAULTED",
            _ => state.Connection.ToString().ToUpperInvariant()
        };
        _connectionBadge.Foreground = state.IsTransmitting
            ? Brush.Parse("#FB7185")
            : state.Connection == ConnectionStatus.Connected
                ? Brush.Parse("#4ADE80")
                : state.Connection == ConnectionStatus.Reconnecting
                    ? Brush.Parse("#FBBF24")
                    : Brush.Parse("#CBD5E1");
        _radioSummary.Text = $"VFO {state.ActiveVfo}  ·  {state.Mode}  ·  " +
            $"Split {(state.IsSplit ? "ON" : "OFF")}  ·  {(state.IsTransmitting ? "TX" : "RX")}";
    }

    private async Task RunUiOperationAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (Exception exception)
        {
            _status.Text = $"Operation failed: {exception.Message}";
            AppendDiagnostic("ERROR", _status.Text);
        }
    }

    private void RunUiAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            ReportUnhandledUiException(exception);
        }
    }

    private async Task<bool> DisconnectAsync()
    {
        _connectionGeneration++;
        CancellationTokenSource? watchStopping = _watchStopping;
        Task? watchTask = _watchTask;
        IRadioSession? session = _session;
        ManagedRadio? radio = _radio;
        CivRadioSimulator? simulator = _civSimulator;
        _session = null;
        _radio = null;
        _civSimulator = null;
        _watchStopping = null;
        _watchTask = null;
        _disconnect.IsEnabled = false;
        _refreshState.IsEnabled = false;
        _connectionBadge.Text = "OFFLINE";
        _connectionBadge.Foreground = Brush.Parse("#94A3B8");
        _radioSummary.Text = string.Empty;

        var errors = new List<string>();
        if (watchStopping is not null)
        {
            try
            {
                await watchStopping.CancelAsync();
                if (watchTask is not null) await watchTask;
            }
            catch (OperationCanceledException) when (watchStopping.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                errors.Add($"event watcher: {exception.Message}");
            }
            finally
            {
                watchStopping.Dispose();
            }
        }
        if (session is not null)
        {
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception exception)
            {
                errors.Add($"session: {exception.Message}");
            }
        }
        if (radio is not null)
        {
            try
            {
                await radio.DisposeAsync();
            }
            catch (Exception exception)
            {
                errors.Add($"radio: {exception.Message}");
            }
        }
        if (simulator is not null)
        {
            try
            {
                await simulator.DisposeAsync();
            }
            catch (Exception exception)
            {
                errors.Add($"simulator: {exception.Message}");
            }
        }
        if (errors.Count == 0) return true;
        _status.Text = $"Disconnect completed with cleanup errors: {string.Join("; ", errors)}";
        AppendDiagnostic("WARNING", _status.Text);
        return false;
    }

    public async ValueTask DisposeAsync() => _ = await DisconnectAsync();

    private void SetBusy(bool busy, string? status = null)
    {
        _connect.IsEnabled = !busy;
        if (status is not null) _status.Text = status;
    }

    private void ClearCapabilityContent()
    {
        _radioContent.Children.Clear();
        _controlContent.Children.Clear();
        _meterContent.Children.Clear();
        _headerVfos.Children.Clear();
    }

    private void AppendDiagnostic(string level, string message)
    {
        _diagnosticEntries.Add($"{DateTimeOffset.Now:HH:mm:ss}  {level,-7}  {message}");
        while (_diagnosticEntries.Count > 500)
            _diagnosticEntries.RemoveAt(0);
    }

    private static Border EmptyState(string title, string message) => new()
    {
        Background = Brush.Parse("#111827"),
        BorderBrush = Brush.Parse("#263247"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(24),
        Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Heading(title, 20),
                new TextBlock
                {
                    Text = message,
                    Foreground = Brush.Parse("#94A3B8"),
                    TextWrapping = TextWrapping.Wrap
                }
            }
        }
    };

    private static bool CanRead(FeatureDescriptor descriptor) =>
        descriptor.Support == CapabilitySupport.Supported && descriptor.Access.HasFlag(FeatureAccess.Read);

    private static bool CanWrite(FeatureDescriptor descriptor) =>
        descriptor.Support == CapabilitySupport.Supported && descriptor.Access.HasFlag(FeatureAccess.Write);

    private static string FormatFeature(FeatureDescriptor descriptor) =>
        $"{descriptor.Support}, {descriptor.Access}";

    private static string FeatureTip(
        string name,
        FeatureDescriptor descriptor,
        string? detail = null) =>
        $"{name}\nSupport: {descriptor.Support}\nAccess: {descriptor.Access}" +
        (string.IsNullOrWhiteSpace(detail) ? string.Empty : $"\n{detail}");

    private static TextBlock AccessLabel(FeatureDescriptor descriptor) => new()
    {
        Text = descriptor.Access switch
        {
            FeatureAccess.Read => "READ ONLY",
            FeatureAccess.Write => "WRITE ONLY",
            _ when descriptor.Access.HasFlag(FeatureAccess.Read) && descriptor.Access.HasFlag(FeatureAccess.Write) =>
                "READ / WRITE",
            _ => descriptor.Access.ToString().ToUpperInvariant()
        },
        Foreground = descriptor.Access == FeatureAccess.Write
            ? Brush.Parse("#FBBF24")
            : Brush.Parse("#64748B"),
        FontSize = 10,
        FontWeight = FontWeight.Bold,
        VerticalAlignment = VerticalAlignment.Center
    };

    private static TextBlock Heading(string text, double size, IBrush? foreground = null) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = FontWeight.SemiBold,
        Foreground = foreground,
        Margin = new Thickness(0, 8, 0, 2)
    };

    private static Border Group(string title, StackPanel content)
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Heading(title, 16));
        body.Children.Add(content);
        return new Border
        {
            Background = Brush.Parse("#111827"),
            BorderBrush = Brush.Parse("#263247"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 2, 0, 8),
            Child = body
        };
    }

    private static Grid EditorRow(
        string label,
        Control editor,
        Button? read,
        Button? apply,
        FeatureDescriptor feature)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("200,180,96,96,*"),
            ColumnSpacing = 8,
            MinHeight = 36
        };
        grid.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        if (read is not null)
        {
            Grid.SetColumn(read, 2);
            grid.Children.Add(read);
        }
        if (apply is not null)
        {
            Grid.SetColumn(apply, 3);
            grid.Children.Add(apply);
        }
        TextBlock access = AccessLabel(feature);
        Grid.SetColumn(access, 4);
        grid.Children.Add(access);
        return grid;
    }

    private static Grid Row(string label, Control value)
    {
        // A dynamically rebuilt row may reuse an editor so its value survives the refresh.
        // Avalonia requires detaching it from the previous direct visual parent first.
        if (value.Parent is Panel previousParent)
            previousParent.Children.Remove(value);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("140,*"),
            ColumnSpacing = 8
        };
        grid.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return grid;
    }
}
