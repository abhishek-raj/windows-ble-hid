using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace BleHid.Core;

public sealed record PeripheralDiagnostics(string Step, string Detail, bool Ok);

/// <summary>A host that has subscribed to input reports and can be targeted individually.</summary>
public sealed record HostTarget(string DeviceId, string Address, string? Name)
{
    public string Display => Name is { Length: > 0 } ? $"{Name} [{Address}]" : Address;
}

/// <summary>
/// Exposes this PC as a BLE HID keyboard + mouse (HID over GATT) using the in-box Windows stack.
/// </summary>
public sealed class BleHidPeripheral : IAsyncDisposable
{
    private readonly List<PeripheralDiagnostics> _diagnostics = [];
    private readonly GattProtectionLevel _protection;
    private readonly ConcurrentDictionary<string, string> _hostNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BluetoothLEDevice> _hostDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _connectionIntervalsMs = new(StringComparer.OrdinalIgnoreCase);
    // Caps Lock is lock state owned by the host: we send the keycode, the host decides what it
    // means, and HID gives it no way to tell us the result unless it reports its lamps.
    private readonly HostLockState _lockState = new();
    private readonly SemaphoreSlim _connectionParameterGate = new(1, 1);
    private readonly PointerPacingOverrides _pointerPacing =
        PointerPacingOverrides.Load(AppPaths.InRoot("pointer-pacing.json"));
    private int _pacingWarningLogged;
    private string? _selectedHostId;
    private bool _localOnly;
    private bool _warnedMissingHost;
    private bool _everAdvertised;
    private TaskCompletionSource<bool>? _advertisingStarted;
    private GattServiceProvider? _provider;
    private GattServiceProvider? _batteryProvider;
    private GattLocalCharacteristic? _keyboardInput;
    private GattLocalCharacteristic? _mouseInput;
    private byte[] _lastKeyboardReport = new byte[HidReports.KeyboardReportLength];
    private byte[] _lastMouseReport = new byte[HidReports.MouseReportLength];
    private const byte CapsLockUsage = 0x39;
    private const byte NumLockUsage = 0x53;
    // Keypad / through Keypad . - the keys whose meaning Num Lock actually changes.
    private const byte NumpadFirstUsage = 0x54;
    private const byte NumpadLastUsage = 0x63;

    private byte _protocolMode = 0x01; // 0x00 = boot, 0x01 = report

    [SupportedOSPlatformGuard("windows10.0.22000.0")]
    private static bool CanReadConnectionParameters =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public IReadOnlyList<PeripheralDiagnostics> Diagnostics => _diagnostics;
    public GattServiceProviderAdvertisementStatus AdvertisementStatus =>
        _provider?.AdvertisementStatus ?? GattServiceProviderAdvertisementStatus.Created;

    public int SubscribedKeyboardClients => CountLiveClients(_keyboardInput);
    public int SubscribedMouseClients => CountLiveClients(_mouseInput);

    // WinRT keeps a bonded host's CCCD entry in SubscribedClients after the ACL drops, so a
    // subscriber only counts while the device it belongs to has not been seen disconnecting.
    private bool IsLiveHost(string deviceId) =>
        !_hostDevices.TryGetValue(deviceId, out var device) ||
        device.ConnectionStatus == BluetoothConnectionStatus.Connected;

    private int CountLiveClients(GattLocalCharacteristic? characteristic) =>
        characteristic?.SubscribedClients.Count(client => IsLiveHost(client.Session.DeviceId.Id)) ?? 0;

    public int MouseReportIntervalMs(int configuredIntervalMs)
    {
        if (_pointerPacing.Warning is { } warning && Interlocked.Exchange(ref _pacingWarningLogged, 1) == 0)
            Log?.Invoke($"[link] {warning}");

        var clients = _mouseInput?.SubscribedClients ?? [];
        return clients
            .Where(client => _selectedHostId is null || string.Equals(
                client.Session.DeviceId.Id, _selectedHostId, StringComparison.OrdinalIgnoreCase))
            .Select(client => Math.Max(
                _connectionIntervalsMs.GetValueOrDefault(client.Session.DeviceId.Id),
                _pointerPacing.MinimumIntervalMs(
                    _hostNames.GetValueOrDefault(client.Session.DeviceId.Id), configuredIntervalMs)))
            .DefaultIfEmpty(_pointerPacing.MinimumIntervalMs(null, configuredIntervalMs))
            .Max();
    }

    /// <summary>Null means reports are broadcast to every subscribed host.</summary>
    public string? SelectedHostId => _selectedHostId;

    /// <summary>True when input should stay on this PC instead of going to any host.</summary>
    public bool IsLocalTarget => _localOnly;

    public string SelectedHostDisplay => _localOnly
        ? LocalDisplay
        : _selectedHostId is null
            ? "all hosts"
            : Hosts().FirstOrDefault(h => h.DeviceId == _selectedHostId)?.Display ?? "(disconnected host)";

    private const string LocalDisplay = "this PC (input stays local)";

    public event Action<string>? Log;

    /// <summary>Raised on every target change so a running capture can re-evaluate pass-through.</summary>
    public event Action? TargetChanged;

    /// <summary>Raised when a host reports new lock-lamp state.</summary>
    public event Action? CapsLockChanged;

    /// <summary>HOGP mandates encryption, but hosts differ in how they bond with a Windows peripheral.</summary>
    public BleHidPeripheral(bool requireEncryption = true) =>
        _protection = requireEncryption
            ? GattProtectionLevel.EncryptionRequired
            : GattProtectionLevel.Plain;

    private void Record(string step, string detail, bool ok)
    {
        _diagnostics.Add(new PeripheralDiagnostics(step, detail, ok));
        Log?.Invoke($"[{(ok ? " ok " : "FAIL")}] {step}: {detail}");
    }

    public async Task StartAsync()
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync()
            ?? throw new InvalidOperationException("No Bluetooth adapter found.");

        Record("Adapter", $"LE={adapter.IsLowEnergySupported}, Peripheral={adapter.IsPeripheralRoleSupported}",
            adapter.IsLowEnergySupported && adapter.IsPeripheralRoleSupported);

        Record("Protection level", _protection.ToString(), true);

        if (!adapter.IsPeripheralRoleSupported)
            throw new NotSupportedException("This Bluetooth radio does not support the LE peripheral role.");

        var serviceResult = await GattServiceProvider.CreateAsync(HidDescriptors.HidService);
        Record("HID service 0x1812", serviceResult.Error.ToString(), serviceResult.Error == BluetoothError.Success);
        if (serviceResult.Error != BluetoothError.Success)
            throw new InvalidOperationException($"Could not create HID service: {serviceResult.Error}");

        _provider = serviceResult.ServiceProvider;
        var service = _provider.Service;

        await CreateReadableCharacteristicAsync("HID Information", HidDescriptors.HidInformation,
            HidDescriptors.HidInformationValue, service);

        await CreateReadableCharacteristicAsync("Report Map", HidDescriptors.ReportMap,
            HidDescriptors.ReportMapValue, service);

        await CreateControlPointAsync(service);
        await CreateProtocolModeAsync(service);

        _keyboardInput = await CreateInputReportAsync("Keyboard input report", service,
            HidDescriptors.KeyboardReportId, () => _lastKeyboardReport);

        // Carries the host's lock lamps back to us, which is the only feedback a standard HID
        // keyboard gets. Hosts bonded before this existed keep a cached GATT database and will
        // never write it, so caps lock stays inferred for them until they re-pair.
        await CreateOutputReportAsync(service, HidDescriptors.KeyboardReportId);

        _mouseInput = await CreateInputReportAsync("Mouse input report", service,
            HidDescriptors.MouseReportId, () => _lastMouseReport);

        await CreateBatteryServiceAsync();

        _advertisingStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _provider.AdvertisementStatusChanged += OnAdvertisementStatusChanged;

        _provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
        {
            IsConnectable = true,
            IsDiscoverable = true
        });

        // The provider reports Aborted until the radio actually begins advertising,
        // so wait for the Started transition rather than reading the status here.
        var started = await Task.WhenAny(_advertisingStarted.Task, Task.Delay(TimeSpan.FromSeconds(10)))
            == _advertisingStarted.Task;

        Record("StartAdvertising", _provider.AdvertisementStatus.ToString(), started);
    }

    private void OnAdvertisementStatusChanged(GattServiceProvider sender,
        GattServiceProviderAdvertisementStatusChangedEventArgs args)
    {
        // Aborted is reported once on the way up on a healthy radio, so it only means a failure
        // after advertising has actually started.
        var note = args.Status == GattServiceProviderAdvertisementStatus.Aborted
            ? _everAdvertised ? " -- advertising stopped; hosts can no longer see this PC"
                              : " (expected while starting)"
            : "";

        Log?.Invoke($"[adv ] status -> {args.Status} (error: {args.Error}){note}");

        if (args.Status == GattServiceProviderAdvertisementStatus.Started)
        {
            _everAdvertised = true;
            _advertisingStarted?.TrySetResult(true);
        }
    }

    /// <summary>Serves the value from a handler rather than StaticValue so host reads are observable.</summary>
    private async Task CreateReadableCharacteristicAsync(string name, Guid uuid,
        byte[] value, GattLocalService service)
    {
        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read,
            ReadProtectionLevel = _protection
        };

        var result = await service.CreateCharacteristicAsync(uuid, parameters);
        Record(name, $"{result.Error} ({value.Length} bytes)", result.Error == BluetoothError.Success);
        if (result.Error != BluetoothError.Success) return;

        result.Characteristic.ReadRequested += async (_, args) =>
        {
            using var deferral = args.GetDeferral();
            var request = await args.GetRequestAsync();
            Log?.Invoke($"[read] host read {name}");
            request.RespondWithValue(CryptographicBuffer.CreateFromByteArray(value));
        };
    }

    private async Task CreateBatteryServiceAsync()
    {
        var result = await GattServiceProvider.CreateAsync(HidDescriptors.BatteryService);
        Record("Battery service 0x180F", result.Error.ToString(), result.Error == BluetoothError.Success);
        if (result.Error != BluetoothError.Success) return;

        _batteryProvider = result.ServiceProvider;

        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            ReadProtectionLevel = GattProtectionLevel.Plain
        };

        var levelResult = await _batteryProvider.Service.CreateCharacteristicAsync(
            HidDescriptors.BatteryLevel, parameters);

        Record("Battery Level 0x2A19", levelResult.Error.ToString(),
            levelResult.Error == BluetoothError.Success);
        if (levelResult.Error != BluetoothError.Success) return;

        levelResult.Characteristic.ReadRequested += async (_, args) =>
        {
            using var deferral = args.GetDeferral();
            var request = await args.GetRequestAsync();
            Log?.Invoke("[read] host read Battery Level");
            request.RespondWithValue(CryptographicBuffer.CreateFromByteArray([100]));
        };
    }

    private async Task CreateControlPointAsync(GattLocalService service)
    {
        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.WriteWithoutResponse,
            WriteProtectionLevel = _protection
        };

        var result = await service.CreateCharacteristicAsync(HidDescriptors.HidControlPoint, parameters);
        Record("HID Control Point", result.Error.ToString(), result.Error == BluetoothError.Success);
        if (result.Error != BluetoothError.Success) return;

        result.Characteristic.WriteRequested += async (_, args) =>
        {
            using var deferral = args.GetDeferral();
            var request = await args.GetRequestAsync();
            var command = ReadBytes(request.Value);
            Log?.Invoke($"[hid ] control point <- 0x{(command.Length > 0 ? command[0] : 0):X2}");
        };
    }

    private async Task CreateProtocolModeAsync(GattLocalService service)
    {
        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read |
                                       GattCharacteristicProperties.WriteWithoutResponse,
            ReadProtectionLevel = _protection,
            WriteProtectionLevel = _protection
        };

        var result = await service.CreateCharacteristicAsync(HidDescriptors.ProtocolMode, parameters);
        Record("Protocol Mode", result.Error.ToString(), result.Error == BluetoothError.Success);
        if (result.Error != BluetoothError.Success) return;

        result.Characteristic.ReadRequested += async (_, args) =>
        {
            using var deferral = args.GetDeferral();
            var request = await args.GetRequestAsync();
            request.RespondWithValue(CryptographicBuffer.CreateFromByteArray([_protocolMode]));
        };

        result.Characteristic.WriteRequested += async (_, args) =>
        {
            using var deferral = args.GetDeferral();
            var request = await args.GetRequestAsync();
            var value = ReadBytes(request.Value);
            if (value.Length > 0) _protocolMode = value[0];
            Log?.Invoke($"[hid ] protocol mode -> {(_protocolMode == 0 ? "boot" : "report")}");
        };
    }

    private async Task CreateOutputReportAsync(GattLocalService service, byte reportId)
    {
        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read |
                                       GattCharacteristicProperties.Write |
                                       GattCharacteristicProperties.WriteWithoutResponse,
            ReadProtectionLevel = _protection,
            WriteProtectionLevel = _protection
        };

        var result = await service.CreateCharacteristicAsync(HidDescriptors.Report, parameters);
        Record("Keyboard LED output report", result.Error.ToString(), result.Error == BluetoothError.Success);
        if (result.Error != BluetoothError.Success) return;

        var characteristic = result.Characteristic;

        characteristic.ReadRequested += async (_, args) =>
        {
            using var deferral = args.GetDeferral();
            var request = await args.GetRequestAsync();
            // Answer per session. Lock state belongs to one host, so serving a single shared
            // byte would hand whichever host asked the lamp state of whichever wrote last.
            var leds = (byte)LockKeysFor(args.Session.DeviceId.Id);
            request.RespondWithValue(CryptographicBuffer.CreateFromByteArray([leds]));
        };

        characteristic.WriteRequested += async (_, args) =>
        {
            using var deferral = args.GetDeferral();
            var request = await args.GetRequestAsync();
            var value = ReadBytes(request.Value);
            if (value.Length > 0) NoteLedReport(args.Session.DeviceId.Id, value[0]);
            if (request.Option == GattWriteOption.WriteWithResponse) request.Respond();
        };

        var descriptorParameters = new GattLocalDescriptorParameters
        {
            ReadProtectionLevel = _protection,
            StaticValue = CryptographicBuffer.CreateFromByteArray([reportId, HidDescriptors.ReportTypeOutput])
        };

        var descriptorResult = await characteristic.CreateDescriptorAsync(
            HidDescriptors.ReportReference, descriptorParameters);

        Record("Keyboard LED output report / Report Reference 0x2908",
            $"{descriptorResult.Error} (id={reportId}, type=Output)",
            descriptorResult.Error == BluetoothError.Success);
    }

    /// <summary>
    /// The host told us what its lock lamps are doing. This is measurement rather than inference,
    /// so it overrides whatever the forwarded-keystroke count believed.
    /// </summary>
    private void NoteLedReport(string deviceId, byte leds)
    {
        var (firstReport, changed, state) = _lockState.NoteLedReport(deviceId, leds);

        if (firstReport)
            Log?.Invoke($"[hid ] LED reports available from {ShortAddress(deviceId)} - lock keys are now measured, not inferred");
        if (!changed) return;

        Log?.Invoke($"[hid ] {ShortAddress(deviceId)} locks -> caps {(state.HasFlag(LockKeys.CapsLock) ? "on" : "off")}, num {(state.HasFlag(LockKeys.NumLock) ? "on" : "off")}");
        // Hosts re-assert their lamps periodically, so only a real change is worth waking the UI.
        CapsLockChanged?.Invoke();
    }

    private async Task<GattLocalCharacteristic?> CreateInputReportAsync(string name,
        GattLocalService service, byte reportId, Func<byte[]> currentValue)
    {
        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read |
                                       GattCharacteristicProperties.Notify,
            ReadProtectionLevel = _protection
        };

        var result = await service.CreateCharacteristicAsync(HidDescriptors.Report, parameters);
        Record(name, result.Error.ToString(), result.Error == BluetoothError.Success);
        if (result.Error != BluetoothError.Success) return null;

        var characteristic = result.Characteristic;

        characteristic.ReadRequested += async (_, args) =>
        {
            using var deferral = args.GetDeferral();
            var request = await args.GetRequestAsync();
            Log?.Invoke($"[read] host read {name}");
            request.RespondWithValue(CryptographicBuffer.CreateFromByteArray(currentValue()));
        };

        characteristic.SubscribedClientsChanged += (sender, _) =>
        {
            Log?.Invoke($"[subs] {name}: {sender.SubscribedClients.Count} subscriber(s)");
            ReturnLocalIfTargetUnavailable();
            _ = RefreshConnectionParametersAsync();
        };

        var descriptorParameters = new GattLocalDescriptorParameters
        {
            ReadProtectionLevel = _protection,
            StaticValue = CryptographicBuffer.CreateFromByteArray([reportId, HidDescriptors.ReportTypeInput])
        };

        var descriptorResult = await characteristic.CreateDescriptorAsync(
            HidDescriptors.ReportReference, descriptorParameters);

        Record($"{name} / Report Reference 0x2908",
            $"{descriptorResult.Error} (id={reportId}, type=Input)",
            descriptorResult.Error == BluetoothError.Success);

        return characteristic;
    }

    private async Task RefreshConnectionParametersAsync()
    {
        await _connectionParameterGate.WaitAsync();
        try
        {
            foreach (var host in Hosts())
            {
                if (!_hostDevices.TryGetValue(host.DeviceId, out var device))
                {
                    device = await BluetoothLEDevice.FromIdAsync(host.DeviceId);
                    if (device is null) continue;
                    if (!string.IsNullOrWhiteSpace(device.Name))
                        _hostNames[host.DeviceId] = device.Name;
                    device.ConnectionStatusChanged += OnConnectionStatusChanged;
                    if (CanReadConnectionParameters)
                        device.ConnectionParametersChanged += OnConnectionParametersChanged;
                    _hostDevices[host.DeviceId] = device;
                }

                if (CanReadConnectionParameters)
                    UpdateConnectionInterval(device);
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"[link] could not read connection interval: {ex.Message}");
        }
        finally
        {
            _connectionParameterGate.Release();
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        Log?.Invoke($"[link] connection status -> {sender.ConnectionStatus}");
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;

        // The host owns its lock state and can change it while we are not attached, so a
        // reconnect starts from the default rather than carrying a stale belief forward.
        _lockState.Forget(sender.DeviceId);

        ReturnLocalIfTargetUnavailable(
            _hostDevices
                .Where(pair => pair.Value.ConnectionStatus == BluetoothConnectionStatus.Connected)
                .Select(pair => pair.Key),
            _selectedHostId is null ? "all hosts disconnected" : "selected host disconnected");
    }

    [SupportedOSPlatform("windows10.0.22000.0")]
    private void OnConnectionParametersChanged(BluetoothLEDevice sender, object args) =>
        UpdateConnectionInterval(sender);

    [SupportedOSPlatform("windows10.0.22000.0")]
    private void UpdateConnectionInterval(BluetoothLEDevice device)
    {
        var parameters = device.GetConnectionParameters();
        if (parameters.ConnectionInterval == 0) return;

        var intervalMs = (int)Math.Ceiling(parameters.ConnectionInterval * 1.25);
        if (_connectionIntervalsMs.TryGetValue(device.DeviceId, out var previous) && previous == intervalMs)
            return;

        _connectionIntervalsMs[device.DeviceId] = intervalMs;
        Log?.Invoke($"[link] connection interval -> {intervalMs} ms");
    }

    public Task SendKeyboardAsync(KeyModifiers modifiers, params byte[] usages)
    {
        var report = HidReports.Keyboard(modifiers, usages);
        // Only the rising edge counts. Auto-repeat keeps the usage present across many reports,
        // but the host toggles the lock once, when the key first goes down.
        var pressed = LockKeysIn(report) & ~LockKeysIn(_lastKeyboardReport);
        if (pressed != LockKeys.None) NoteLockKeysForwarded(pressed);
        if (ReportHasNumpadKey(report)) NoteNumpadUsed();
        _lastKeyboardReport = report;
        return NotifyAsync(_keyboardInput, report);
    }

    /// <summary>
    /// Lock-key state of the given host. Measured when the host writes LED reports, otherwise
    /// inferred from the presses we forwarded - see <see cref="LockKeysMeasured"/>.
    /// </summary>
    public LockKeys LockKeysFor(string deviceId) => _lockState.For(deviceId);

    /// <summary>
    /// True when the host has written an LED report, so its lock state is measured rather than
    /// inferred. A host bonded before the LED report existed may never write one.
    /// </summary>
    public bool LockKeysMeasured(string deviceId) => _lockState.Measured(deviceId);

    /// <summary>Lock-key state of the host currently receiving input.</summary>
    public LockKeys TargetLockKeys => _localOnly
        ? LockKeys.None
        : TargetedHosts().Aggregate(LockKeys.None, (all, host) => all | LockKeysFor(host.DeviceId));

    /// <summary>True when every host currently targeted reports its lock lamps to us.</summary>
    public bool TargetLockKeysMeasured
    {
        get
        {
            var hosts = TargetedHosts();
            return hosts.Count > 0 && hosts.All(host => LockKeysMeasured(host.DeviceId));
        }
    }

    /// <summary>True when the host currently receiving input has Caps Lock on.</summary>
    public bool TargetCapsLockOn => TargetLockKeys.HasFlag(LockKeys.CapsLock);

    /// <summary>
    /// Describes only the states that silently corrupt input: Caps Lock on changes case, and
    /// Num Lock off turns numpad digits into cursor keys. Ordinary states say nothing.
    /// </summary>
    /// <param name="numpadUsed">
    /// Whether the numpad has been used on this host. Num Lock off is reported only when it can
    /// actually bite - many hosts report it permanently off (Apple keyboards have no Num Lock
    /// key at all), and warning about that unprompted would be constant noise.
    /// </param>
    public static string? DescribeLockKeys(LockKeys keys, bool numpadUsed) =>
        DescribeLockAlert(LockAlertFor(keys, numpadUsed));

    /// <summary>The lock states worth warning about, without wording, so a UI can phrase them.</summary>
    public static LockAlert LockAlertFor(LockKeys keys, bool numpadUsed)
    {
        var alert = LockAlert.None;
        if (keys.HasFlag(LockKeys.CapsLock)) alert |= LockAlert.CapsLockOn;
        if (numpadUsed && !keys.HasFlag(LockKeys.NumLock)) alert |= LockAlert.NumLockOff;
        return alert;
    }

    /// <summary>English wording for logs and the CLI; null when nothing is worth saying.</summary>
    public static string? DescribeLockAlert(LockAlert alert) => alert switch
    {
        LockAlert.CapsLockOn | LockAlert.NumLockOff => "CAPS LOCK ON, NUM LOCK OFF",
        LockAlert.CapsLockOn => "CAPS LOCK ON",
        LockAlert.NumLockOff => "NUM LOCK OFF",
        _ => null
    };

    internal static string? DescribeTargetLockKeys(IEnumerable<(LockKeys Keys, bool NumpadUsed)> hosts) =>
        DescribeLockAlert(TargetLockAlertFor(hosts));

    // Per host first: one host with Num Lock on must not hide another whose numpad turned into
    // cursor keys.
    internal static LockAlert TargetLockAlertFor(IEnumerable<(LockKeys Keys, bool NumpadUsed)> hosts) =>
        hosts.Aggregate(LockAlert.None, (all, host) => all | LockAlertFor(host.Keys, host.NumpadUsed));

    /// <summary>True when the numpad has been used against the given host.</summary>
    public bool NumpadUsed(string deviceId) => _lockState.NumpadUsed(deviceId);

    /// <summary>
    /// What is worth saying about the current target's lock keys, or null when nothing is.
    /// </summary>
    public string? TargetLockWarning => DescribeLockAlert(TargetLockAlert);

    /// <summary>The current target's lock states worth warning about.</summary>
    public LockAlert TargetLockAlert => _localOnly
        ? LockAlert.None
        : TargetLockAlertFor(TargetedHosts().Select(host =>
            (LockKeysFor(host.DeviceId), NumpadUsed(host.DeviceId))));

    /// <summary>The hosts a report would currently reach: the selected one, or all when broadcasting.</summary>
    private List<HostTarget> TargetedHosts()
    {
        var target = _selectedHostId;
        return Hosts()
            .Where(host => target is null ||
                           string.Equals(host.DeviceId, target, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void NoteLockKeysForwarded(LockKeys pressed)
    {
        if (_localOnly) return;
        foreach (var host in TargetedHosts()) _lockState.NoteForwarded(host.DeviceId, pressed);
    }

    private void NoteNumpadUsed()
    {
        if (_localOnly) return;
        foreach (var host in TargetedHosts()) _lockState.NoteNumpadUsed(host.DeviceId);
    }

    private static bool ReportHasNumpadKey(byte[] report)
    {
        if (report.Length < HidReports.KeyboardReportLength) return false;
        foreach (var usage in report.AsSpan(2))
            if (usage is >= NumpadFirstUsage and <= NumpadLastUsage) return true;
        return false;
    }

    // Report layout is [modifiers, reserved, up to six usages].
    internal static LockKeys LockKeysIn(byte[] report)
    {
        if (report.Length < HidReports.KeyboardReportLength) return LockKeys.None;
        var keys = report.AsSpan(2);
        var found = LockKeys.None;
        if (keys.Contains(CapsLockUsage)) found |= LockKeys.CapsLock;
        if (keys.Contains(NumLockUsage)) found |= LockKeys.NumLock;
        return found;
    }

    public Task ReleaseKeysAsync()
    {
        _lastKeyboardReport = HidReports.KeyboardRelease();
        return NotifyAsync(_keyboardInput, _lastKeyboardReport);
    }

    public Task SendMouseAsync(MouseButtons buttons, int dx, int dy, int wheel)
    {
        _lastMouseReport = HidReports.Mouse(buttons, dx, dy, wheel);
        return NotifyAsync(_mouseInput, _lastMouseReport);
    }

    /// <summary>Subscribed hosts, de-duplicated across the keyboard and mouse reports.</summary>
    public IReadOnlyList<HostTarget> Hosts()
    {
        var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var client in _keyboardInput?.SubscribedClients ?? []) ids.Add(client.Session.DeviceId.Id);
        foreach (var client in _mouseInput?.SubscribedClients ?? []) ids.Add(client.Session.DeviceId.Id);
        ids.RemoveWhere(id => !IsLiveHost(id));

        return ids.Select(id => new HostTarget(id, ShortAddress(id), _hostNames.GetValueOrDefault(id))).ToList();
    }

    private void ReturnLocalIfTargetUnavailable()
    {
        var message = _selectedHostId is null
            ? "all hosts disconnected"
            : "selected host disconnected";
        ReturnLocalIfTargetUnavailable(Hosts().Select(host => host.DeviceId), message);
    }

    private void ReturnLocalIfTargetUnavailable(IEnumerable<string> availableHostIds, string message)
    {
        if (!TargetAvailability.IsUnavailable(_localOnly, _selectedHostId, availableHostIds)) return;

        SelectLocal();
        Log?.Invoke($"[host] {message} - returning input to this PC");
    }

    /// <summary>Cycles this PC -> host1 -> ... -> hostN -> this PC. Safe to call from any thread.</summary>
    public string SelectNextHost()
    {
        var hosts = Hosts();
        _warnedMissingHost = false;

        if (hosts.Count == 0) return SelectLocal();

        // Broadcast is not in the rotation; it is a fallback reached through SelectAllHosts.
        if (_localOnly)
        {
            _localOnly = false;
            _selectedHostId = hosts[0].DeviceId;
            TargetChanged?.Invoke();
            return hosts[0].Display;
        }

        if (_selectedHostId is null) return SelectLocal();

        var index = -1;
        for (var i = 0; i < hosts.Count; i++)
            if (string.Equals(hosts[i].DeviceId, _selectedHostId, StringComparison.OrdinalIgnoreCase)) { index = i; break; }

        var next = index + 1;
        if (next >= hosts.Count) return SelectLocal();

        _selectedHostId = hosts[next].DeviceId;
        TargetChanged?.Invoke();
        return hosts[next].Display;
    }

    public string SelectLocal()
    {
        _localOnly = true;
        _selectedHostId = null;
        _warnedMissingHost = false;
        TargetChanged?.Invoke();
        return LocalDisplay;
    }

    public void SelectAllHosts()
    {
        _localOnly = false;
        _selectedHostId = null;
        _warnedMissingHost = false;
        TargetChanged?.Invoke();
    }

    public bool SelectHost(int index)
    {
        var hosts = Hosts();
        if (index < 0 || index >= hosts.Count) return false;
        _localOnly = false;
        _selectedHostId = hosts[index].DeviceId;
        _warnedMissingHost = false;
        TargetChanged?.Invoke();
        return true;
    }

    /// <summary>Resolves friendly names for subscribed hosts so they can be shown while switching.</summary>
    public async Task RefreshHostNamesAsync()
    {
        foreach (var host in Hosts())
        {
            if (_hostNames.ContainsKey(host.DeviceId)) continue;
            try
            {
                using var device = await BluetoothLEDevice.FromIdAsync(host.DeviceId);
                if (!string.IsNullOrWhiteSpace(device?.Name)) _hostNames[host.DeviceId] = device.Name;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[host] could not resolve name for {host.Address}: {ex.Message}");
            }
        }
    }

    private static string ShortAddress(string deviceId)
    {
        var separator = deviceId.LastIndexOf('-');
        return separator >= 0 && separator < deviceId.Length - 1 ? deviceId[(separator + 1)..] : deviceId;
    }

    private async Task NotifyAsync(GattLocalCharacteristic? characteristic, byte[] payload)
    {
        if (_localOnly) return;
        if (characteristic is null || characteristic.SubscribedClients.Count == 0) return;

        var buffer = CryptographicBuffer.CreateFromByteArray(payload);
        var target = _selectedHostId;
        if (target is null)
        {
            await characteristic.NotifyValueAsync(buffer);
            return;
        }

        foreach (var client in characteristic.SubscribedClients)
        {
            if (!string.Equals(client.Session.DeviceId.Id, target, StringComparison.OrdinalIgnoreCase)) continue;
            await characteristic.NotifyValueAsync(buffer, client);
            return;
        }

        // Dropping is safer than falling back to broadcast: the input was meant for one host.
        if (!_warnedMissingHost)
        {
            _warnedMissingHost = true;
            Log?.Invoke("[host] selected host is no longer subscribed - reports are being dropped");
        }
    }

    private static byte[] ReadBytes(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    public ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            _provider.AdvertisementStatusChanged -= OnAdvertisementStatusChanged;
            if (_provider.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Started)
                _provider.StopAdvertising();
            _provider = null;
        }
        _batteryProvider = null;
        foreach (var device in _hostDevices.Values)
        {
            device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            if (CanReadConnectionParameters)
                device.ConnectionParametersChanged -= OnConnectionParametersChanged;
            device.Dispose();
        }
        _hostDevices.Clear();
        _connectionIntervalsMs.Clear();
        return ValueTask.CompletedTask;
    }
}
