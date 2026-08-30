# Development guide

This document records the implementation constraints, measured behavior, debugging methods,
and failed approaches behind BleHid. Read it before changing Bluetooth behavior. The public
[README](README.md) is intentionally limited to installation, usage, compatibility, and
workarounds.

## Repository layout

- `src/BleHid.Core` owns the GATT peripheral, HID descriptors and reports, input capture,
  pacing, and diagnostics.
- `src/BleHid.Cli` is the interactive diagnostic client and resident background process.
- `src/BleHid.App` is the WPF desktop UI. It shares one `PeripheralService` instance for
  the lifetime of the process.
- `tests/BleHid.Core.Tests` contains unit tests for report encoding, paths, pacing
  configuration, key mapping, and other platform-independent behavior.
- `experiments/bumble-hid` is a separate HOGP control peripheral and central probe built
  on Google Bumble. It is not the production implementation.
- `spike` contains early PowerShell capability probes.

There is no solution file. Build projects directly:

```powershell
dotnet build src\BleHid.Cli\BleHid.Cli.csproj --configuration Debug
dotnet build src\BleHid.App\BleHid.App.csproj --configuration Debug
dotnet test tests\BleHid.Core.Tests\BleHid.Core.Tests.csproj --configuration Debug
```

A running app locks `BleHid.Core.dll`. Stop it before rebuilding or MSBuild can retry the
copy and leave an old binary in the output directory.

The projects compile against the Windows 11 build 22000 SDK so they can use connection
parameter APIs. `SupportedOSPlatformVersion` remains build 19041. Windows 11-only calls
must stay behind runtime guards so Windows 10 remains supported.

## Architecture constraints

### Provider lifetime is service lifetime

`GattServiceProvider` registrations are owned by the process. Closing a window is harmless
if the process remains in the notification area, but terminating the owner removes HOGP
service `0x1812`. Starting another process creates a new provider and a new server lifetime.

This is why the app closes to the notification area and why CLI background mode exists.
The provider cannot survive a Windows restart. Autostart recreates it after sign-in; it does
not transfer the previous provider.

### One process owns the peripheral

The UI, interactive CLI, and background CLI cannot publish the same service concurrently.
They coordinate through `SingleInstance.MutexName` and `SingleInstance.StopEventName`.

### Input capture is isolated from the UI thread

`InputCapture` runs an MTA thread with its own message loop. Making that thread STA caused
WinRT completions to contend with the hook message pump and stalled GATT notifications for
seconds.

The process manifest requests per-monitor DPI awareness. The low-level mouse hook reports
physical pixels; virtualized `GetSystemMetrics` or `SetCursorPos` coordinates cause a
constant offset and remote-pointer drift. Injected recentering events are explicitly
ignored and are not a feedback source.

### Motion is coalesced; keys are not

Pointer deltas may be merged before notification because a mouse produces events faster
than BLE can carry them. Every keyboard state transition must be preserved. Target changes
travel through the keyboard queue so a key-release report cannot be delivered to the new
host and leave a modifier stuck on the old one.

## GATT and HOGP layout

The app exposes one composite HID service:

- Keyboard input: Report ID 1
- Mouse input: Report ID 2
- HID Information flags: `RemoteWake | NormallyConnectable`
- Report Map: 113 bytes
- Battery Service: `0x180F`

On the tested Windows build, system services occupy the handles before HOGP. The measured
layout was:

```text
0x1800 Generic Access
0x1801 Generic Attribute
0x180A Device Information
0x184C Generic Telephone Bearer
0x1849 Generic Media Control
0x1855 Telephony and Media Audio
0x1812 HID                         0x005D-0x006D
  keyboard Report value           0x0067
  mouse Report value              0x006B
```

Two discovery-only dumps across provider restarts produced the same handles and Database
Hash. Handle instability is not the reconnect bug.

## Android reconnect failure after provider restart

### Definitive lifecycle control

While the same provider process remains alive, Android reconnects automatically after:

- going out of range and returning;
- switching phone Bluetooth off and on.

After the .NET process recreates the provider, Android does not restore usable input
without manual intervention. This isolates the problem to provider teardown/recreation,
not ordinary link loss, advertising reachability, pairing keys, or privacy addresses.

### Observed packet sequence

On the first background connection after provider recreation:

1. Android establishes LE and encryption succeeds with a 16-byte key.
2. Android reads Database Hash `0x2B2A`.
3. Windows sends Service Changed `0x2A05` for the HID handle range.
4. Android confirms the indication.
5. Android's generic GATT cache can decide that the hash is unchanged and skip discovery.
6. Android HID Host has already reset its report cache and receives a synthetic close while
   in `BTA_HH_IDLE_ST`.
7. No HID client holds the link, so Android disconnects after the idle timeout.

Android native logs identify the failed transition:

```text
BTA_GATTC_SRVC_CHG_EVT
bta_hh_le_co_reset_rpt_cache
BTA_HH_IDLE_ST + BTA_HH_GATT_CLOSE_EVT -> Unexpected event
```

The Android source path is `system/bta/hh/bta_hh_le.cc`, particularly
`bta_hh_le_service_changed`, `bta_hh_le_service_discovery_done`, and
`bta_hh_gattc_callback`.

### Bonded CCCD persistence

Bluetooth Core, Vol 3, Part G, Section 3.3.3.3 requires a Client Characteristic
Configuration Descriptor value to persist across connections for bonded devices.
Provider recreation loses the Windows-side report subscriptions. CCCD values are not part
of the Database Hash, so the client cannot detect that loss by validating the hash.

A forced real database change while Android was offline made Android reconnect and open
HOGP, but it still did not write the keyboard or mouse CCCDs. Android opened HOGP from
cached report metadata before hash validation completed, then performed generic service
discovery without reconciling notification configuration. Its UI showed Input enabled and
UHID opened, while WinRT had zero subscribed clients.

Input-profile toggling and an explicit disconnect/reconnect did not repair that forced
state. Returning to the normal database and manually connecting caused Android to reread
HID Information and Report Map and subscribe to both reports.

The complete platform fixes are therefore:

- Windows persists bonded CCCD values across provider recreation; and/or
- Android rewrites affected report CCCDs after Service Changed and rediscovery.

Keeping one provider resident avoids both conditions until the process or Windows restarts.

## Reconnect hypotheses already tested

Do not repeat these without new evidence or a materially different setup.

| Hypothesis | Result |
| --- | --- |
| Accept-list or directed advertising is required | Falsified. Bumble reconnects with open undirected advertising. |
| Missing PnP ID prevents reconnect | Falsified. Bumble reconnects without Device Information or PnP ID. |
| Resolvable private addresses break reconnect | Falsified. Bumble reconnects using RPA; Windows privacy identity resolution also succeeds. |
| A public identity shared with Classic breaks LE reconnect | Falsified in isolation. |
| GAP Appearance or keyboard icon controls reconnect | Falsified. HOGP reconnects with no appearance; Android changes the icon after discovery. |
| Dual-mode flags or computer Class of Device are sufficient | Falsified. A dual-mode Bumble control with computer CoD reconnected in milliseconds. |
| Missing Battery Service causes the failure | Falsified. Bumble binds and reconnects with `--no-battery`. |
| HID handles move on every provider restart | Falsified. Discovery dumps and Database Hash were stable. |
| Android merely lacked cached HOGP metadata | Incomplete. A working session restored the metadata, but restart still failed. |
| Cache hit alone breaks Android HOGP | Falsified. Bumble reconnects with a valid cache. |
| Changing the database before Android connects fixes everything | Partial only. It restores automatic HOGP open but not report CCCDs. |
| Advertise only a private service, then add HOGP | Falsified. Android will not initiate the staging connection without `0x1812`. |

### Why alternating Database Hash values failed

An experiment alternated two private service UUIDs on each process start. It successfully
changed the Database Hash while leaving the HID schema stable. It did not fix reconnect:
Android read and stored the new hash before Windows delivered the queued Service Changed
indication. The second hash read therefore matched the value Android had stored moments
earlier. The experiment was removed.

## Separate Windows-host reconnect bug

A Windows host can expose a different failure: after provider restart it resolves the bond
to the PC's Classic radio, appears under the Classic peer list for roughly 20-45 seconds,
and never binds LE/HOGP. Moving GATT handles, waiting for cache expiry, toggling Bluetooth,
and locking/unlocking did not change it. Removing and re-pairing the correct LE entry is the
known recovery.

This is distinct from the Android Service Changed/CCCD failure. Do not combine their
symptoms or conclusions.

## Advertising facts

- A healthy `GattServiceProvider` reports `Aborted` once while starting, immediately before
  `Started`. Treat it as failure only if `Started` never arrives or if `Aborted` occurs after
  advertising was established.
- `StartAdvertising` reliably reaches `Started` only with both `IsConnectable` and
  `IsDiscoverable` true on the tested systems.
- Desktop `BluetoothLEAdvertisementPublisher` cannot publish arbitrary GAP Appearance or
  service-list data sections. Those attempts fail as unauthorized; manufacturer data is
  allowed.
- Windows advertises HOGP from an RPA and distributes the radio's public identity plus IRK.
  That is a correct privacy configuration.
- The measured advertisement contains flags `0x1A`, `0x180A`, `0x1812`, and the PC name.
- A machine policy can disable advertising entirely through
  `HKLM\SOFTWARE\Policies\Microsoft\Bluetooth\AllowAdvertising`. Diagnostics must report
  policy before blaming the radio or driver.

## Pointer pacing

`NotifyValueAsync` returns when a report is queued, not when it is transmitted. Sending
faster than the link can drain creates delayed relative-motion reports and pointer trail.

On Windows 11, `BleHidPeripheral` retains one `BluetoothLEDevice` per subscribed host and
uses `GetConnectionParameters()` plus `ConnectionParametersChanged`. The selected host's
effective report interval is:

```text
max(negotiated interval, app/CLI minimum, file default, host-name override)
```

Idle subscribed hosts do not multiply a selected host's interval; Windows' negotiated
value already reflects radio scheduling. Broadcast mode remains conservatively scaled
because every report is queued once per host.

Windows 10 does not expose the negotiated interval. It uses the configured minimum.
Optional overrides are loaded from `%LOCALAPPDATA%\BleHid\pointer-pacing.json`; the file is
never created automatically. Friendly-name keys are case-insensitive and survive re-pair,
but devices with duplicate names share an override.

Measured in a two-host session, one host negotiated 30 ms and another 15 ms. Per-target
pacing at those exact intervals was smooth and avoided the backlog produced by a 10 ms
global rate.

## Control peripheral and probe tooling

### Bumble environment

The control uses a spare USB Bluetooth adapter detached from the Windows Bluetooth stack
with WinUSB. The tested Realtek adapter requires vendor firmware before it transmits; a ROM
that answers HCI commands is not proof that radio traffic works.

Use the repository virtual environment and select the adapter by VID/PID. Do not use a
generic `usb:0` selector because it may open the machine's primary radio.

The local `device.json`, `keys.json`, firmware, virtual environment, captures, and Android
bug reports are ignored. They contain identities, keys, or unrelated private device data.

### Bumble bugs found during control work

- `PairingConfig.identity_address_type` must match the advertised identity. Advertising a
  static random identity while distributing a public identity makes Android reconnect to an
  address nothing advertises.
- Android rejected pairing when the responder did not distribute its IRK.
- Bumble stores CCCD subscriptions in memory by ATT bearer. A process restart loses them.
  The control restores bonded report subscriptions after encryption for reconnect tests.
- Android's BR/EDR pairing requested MITM. The dual-mode control required display/yes-no
  I/O capability and numeric comparison; Just Works failed authentication.

### `central_probe.py`

Useful modes:

```powershell
python -u experiments\bumble-hid\central_probe.py --transport usb:VID:PID --dump NAME
python -u experiments\bumble-hid\central_probe.py --transport usb:VID:PID --gatt --name NAME
```

`--dump` prints every advertising structure. `--gatt` performs discovery without pairing
and prints service/characteristic/descriptor handles plus Database Hash.

Only one process can own the USB adapter. Stop the Bumble peripheral before running the
central probe or libusb returns access denied.

### `analyze_btsnoop.py`

Samsung bug reports stored the Bluetooth snoop files under:

```text
FS/data/log/bt/btsnoop_hci.log
FS/data/log/bt/btsnoop_hci.log.last
```

The parser decodes relevant HCI, SMP, ACL, ATT, connection, encryption, Service Changed,
Database Hash, and read-response traffic. Important rules:

- `--since` and `--until` take `HH:MM:SS`, not a full date.
- Use `--verbose` before concluding an event is absent.
- Filter by connection handle after identifying the target link.
- Check `.last` when the active file starts after the event of interest.
- Android btsnoop timestamps are wall-clock values; do not apply a second timezone shift.
- Encryption Change v2 is HCI event `0x59`, not only the legacy `0x08` event.

## Experimental method

### Hardware steps

- Ask the tester before every radio, pairing, process, or Bluetooth-setting change.
- Wait for tester confirmation that the phone visibly connected or disconnected. Peripheral
  application logs do not report every link-layer transition.
- Never send HID text until the tester confirms a text field is focused. Unfocused key
  reports act as system shortcuts and have switched phone Bluetooth off during testing.
- Never kill a process while pairing or numeric confirmation may be in progress.
- Make one change per bond. Changing identity type or GATT layout requires forgetting and
  re-pairing unless the experiment explicitly tests Service Changed behavior.

### Outage tests

For the Bumble control, killing the host process does not necessarily disconnect the link;
the USB controller can retain it. Reset the controller, hold it silent for a measured
interval, then resume advertising. Time reconnect from advertising start, not process exit.

For the WinRT peripheral, distinguish normal link loss from provider recreation:

- range loss or phone Bluetooth toggle with the same provider tests reconnect;
- process stop/start tests GATT server lifetime and Service Changed behavior.

### Logging pitfalls

- `Tee-Object` can lag a live Python pipeline by minutes. A silent output file is not proof
  of no traffic. Read the live terminal and use `python -u`.
- Reused log files mix experiments. Use unique files and verify timestamps.
- Absence of a decoded packet is evidence only after confirming the parser supports it or
  using verbose output.
- Android accept-list add/remove operations are routine churn. Do not infer profile policy
  from one removal.
- Android's PC/keyboard icon can change after HOGP discovery. It is not reliable evidence of
  bond transport or reconnect policy.
- A transient "app required" message can precede a valid HOGP bind by minutes.

## UI and packaging gotchas

- WPF was chosen over WinUI 3 to keep self-contained single-file publishing for x64 and
  Arm64.
- WPF-UI symbol names are resolved when BAML loads, not at compile time. An invalid symbol
  can build successfully and crash only when navigating to the page.
- Observable collections owned by the UI must be cleared on the dispatcher during shutdown.
- The app and CLI share `AppPaths.Root`; logs belong in `%LOCALAPPDATA%\BleHid\logs` and
  user configuration belongs directly in `%LOCALAPPDATA%\BleHid`.

## Before committing Bluetooth changes

1. Stop the running provider.
2. Build CLI and WPF projects.
3. Run all Core tests.
4. Run `git diff --check`.
5. For GATT changes, validate at least one fresh bond and one bonded reconnect.
6. For pacing changes, test one host alone and two hosts connected while switching targets.
7. Keep Bluetooth addresses, bond keys, hostnames, captures, and bug reports out of commits.
