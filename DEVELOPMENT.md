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
- `android/BleHid.Companion` is the Android 12+ companion. It owns a separate diagnostic
  GATT connection and never receives or subscribes to HID input reports.
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
cd android\BleHid.Companion
.\gradlew.bat :app:testDebugUnitTest :app:assembleDebug
```

A running app locks `BleHid.Core.dll`. Stop it before rebuilding or MSBuild can retry the
copy and leave an old binary in the output directory.

The projects compile against the Windows 11 build 22000 SDK so they can use connection
parameter APIs. `SupportedOSPlatformVersion` remains build 19041. Windows 11-only calls
must stay behind runtime guards so Windows 10 remains supported.

### Android companion boundaries

The companion uses only public Android APIs. Companion Device Manager associates it with
the advertised HOGP service, and a `connectedDevice` foreground service owns an app-level
`BluetoothGatt` client. That client discovers services, counts Report characteristics,
reads Database Hash `0x2B2A`, receives the framework's Service Changed callback, and keeps
a bounded diagnostic event timeline. Failed connections retry after 2, 4, 8, 16, and 30
seconds; retry exhaustion requires an explicit user action or a new presence event. GATT
connect and service discovery have explicit timeouts because Android may leave a direct
connection pending without delivering a callback. Database Hash is optional, so its read
timeout records the connection as ready with the hash unavailable instead of reconnecting.

The manifest must declare `android.software.companion_device_setup`; Samsung Android 16
throws from `CompanionDeviceManager.associate` if it is absent. Association MAC strings
are normalized before `BluetoothAdapter.getRemoteDevice` because `MacAddress.toString()`
is lowercase while that API rejects lowercase addresses on the tested phone. On Android
14 or later, the exact scanned `BluetoothDevice` from `AssociationInfo.associatedDevice`
is preferred so identity, pseudo, and private-address resolution stays with the platform.

The app-level GATT client must not enable either HID report CCCD. Android routes input
through its system HID Host and UHID path, not through an application's GATT callback. An
app subscription would create a Windows subscriber without repairing system input and
would make diagnostics misleading.

Android 17 adds public `BluetoothDevice.connect()` profile reconnection for a Companion
Device-associated app. The project currently compiles against SDK 36.1, so the guarded
compatibility bridge invokes that public API only when runtime API level 37 or later is
present. Android 12-16 provide no non-privileged HID Host connect or cache-repair API; the
companion opens system Bluetooth settings for manual recovery on those versions.

The companion's presence and GATT connection can keep the BLE ACL useful and improve
observability, but neither is evidence that Android's HID Host is open or that Windows has
restored the bonded report CCCDs. The Windows subscriber count and actual keyboard/mouse
input remain the authoritative end-to-end checks.

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

### Host loss fails back to local input

If the selected host disconnects during capture, `BleHidPeripheral` immediately selects
the local target and `InputCapture` stops swallowing keyboard and mouse events. Broadcast
mode does the same after its last connected host disappears. Reconnection does not restore
the previous target automatically, because sending input to a host without an explicit
user switch would be unsafe.

This uses `BluetoothLEDevice.ConnectionStatusChanged`, not only
`GattLocalCharacteristic.SubscribedClientsChanged`. Bonded CCCD entries can remain in
WinRT's subscribed-client collection after the physical ACL has dropped, which is exactly
the state that previously stranded local input. Subscriber removal remains a secondary
signal for hosts that do remove their entries.

In resident mode, `Ctrl+Alt+Q` also enables hook pass-through synchronously before its
`GoLocal` command enters the report queue. The queued command is retained to preserve key
release ordering on a healthy connection, while local recovery no longer depends on a
possibly stalled `NotifyValueAsync` call to a vanished host.

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

## Android provider-restart reconnect root cause

### Executive summary

The Android failure is tied specifically to destruction and recreation of the WinRT
`GattServiceProvider`. It is not a normal BLE reconnect failure.

When the provider process exits, Windows temporarily removes the application-owned HOGP
service. When the process starts again, it recreates the same service with the same
attribute handles and the same Database Hash. Windows nevertheless retains and later sends
a pending Service Changed indication for the HID range. Android reads the unchanged hash,
receives the indication, invalidates its HOGP state, and then fails to rebuild that state
because its generic GATT cache concludes that the identical hash requires no rediscovery.

There is a second, independent part of the same provider-lifetime problem: the recreated
Windows server does not restore the bonded client's HID report CCCD values. Even when a
real database change forces Android to reconnect and run full discovery, Android can open
HOGP from stale local report metadata without writing the report CCCDs again. The phone UI
then says Input is enabled while WinRT has no subscribed clients and cannot deliver input.

In short:

```text
provider removed/recreated
  -> pending Service Changed survives
  -> final GATT definitions and Database Hash are unchanged
  -> Android HID cache is invalidated while generic GATT cache is retained
  -> HOGP is not opened, or opens without restored report subscriptions
```

Terminology matters here: Service Changed is not part of the BLE advertising payload.
Windows sends it as an encrypted ATT Handle Value **indication** on characteristic
`0x2A05` after the LE connection is established.

### Definitive lifecycle control

The strongest control is the provider lifetime itself. With the same process and the same
`GattServiceProvider` instance still alive, Android reconnects automatically after both:

- the phone goes out of range and returns;
- Bluetooth is switched off and back on at the phone.

The phone restores the encrypted LE connection and the HID report subscriptions in those
cases. The failure appears only after the .NET process/provider is stopped and recreated.
This rules out the following as the primary cause:

- the advertisement becoming unreachable after a client disconnects;
- the Android bond or encryption keys being generally invalid;
- the Windows RPA or identity-resolution setup;
- ordinary link supervision loss;
- Android refusing all automatic HOGP reconnects to this peripheral.

Closing the UI while leaving the provider resident is therefore materially different from
terminating the provider process. A Windows restart necessarily destroys the provider too.

### The final GATT database is identical after restart

The first hypothesis was that WinRT assigned new handles every time the application rebuilt
the local service. A discovery-only central probe disproved it.

Two complete GATT discoveries were performed with a .NET provider restart between them:

```text
run 1: HID service 0x1812 = 0x005D-0x006D
       keyboard Report value = 0x0067
       mouse Report value    = 0x006B

run 2: HID service 0x1812 = 0x005D-0x006D
       keyboard Report value = 0x0067
       mouse Report value    = 0x006B
```

The Database Hash read from `0x2B2A` was also unchanged:

```text
77537a94abbc92c684af103158748c74
```

Android's persisted `HidReport` entries pointed to those same service and Report handles.
The failure is therefore not caused by Android using stale numeric handles against a
renumbered HOGP service.

This result needs precise wording. During provider shutdown, the service really is removed
from the live server, so Windows has observed an intermediate database mutation. After the
new process recreates it, however, the database visible to the reconnecting client is
identical to the database it cached before shutdown. Windows does not reconcile or cancel
the pending Service Changed state after reaching that identical final database.

### Stage 1: provider removal destroys Android's working HOGP state

Before the outage test, Android was fully connected and input worked. Its bond record had
a complete HOGP descriptor, both Report entries, and reconnect permission enabled.

When the .NET provider was stopped while that LE relationship still existed, the Android
native log showed this sequence:

```text
04:57:00.922  BTA_GATTC_SRVC_CHG_EVT
04:57:00.925  HID state BTA_HH_CONN_ST receives BTA_HH_GATT_CLOSE_EVT
04:57:00.932  remove cached HID descriptor due to service change
04:57:01.000  BTA_GATTC_SRVC_DISC_DONE_EVT
04:57:01.000  synthetic BTA_HH_GATT_OPEN_EVT
04:57:01.053  HID service not found
04:57:01.054  HOGP open fails
04:57:02.081  Disable rearm concept, don't initiate connection
```

At that moment the failure to find HID is expected: the application process is down, so
`0x1812` is genuinely absent. The harmful part is the resulting persistent state loss.
Afterward, Android's native HID dump reported `num_devices: 0`, and the PC's bond block no
longer contained `HidReport`, `HogpDescriptor`, or `HogpReConnectAllowed`. The generic bond
and `ServiceLe = 0x1812` remained, but the state needed by HID Host to rearm the HOGP
connection had been removed.

This explains why some later experiments saw no automatic attempt at all. Once this stage
has happened, changing the server database cannot help until Android initiates another
connection or the HOGP profile state is repaired.

### Stage 2: recreated identical provider sends a pending Service Changed indication

The provider was restarted and began advertising the same HOGP definition. Android later
made a background LE connection. The raw Android btsnoop trace for that connection is:

```text
04:58:09.351  LE connection complete
04:58:09.435  SMP Security Request
04:58:09.436  Android enables encryption
04:58:09.612  encryption succeeds, key size 16
04:58:09.614  Database Hash response = 77537a94abbc92c684af103158748c74
04:58:09.615  Android reads Server Supported Features
04:58:09.671  Windows sends Service Changed indication
                attribute 0x000C, affected range 0x005D-0x006D
04:58:09.672  Android confirms the indication
04:58:09.673  Android reads preferred connection parameters
04:58:09.732  Android reads Database Hash again
04:58:09.791  Database Hash response is still 77537a94abbc92c684af103158748c74
04:58:10.052  Android requests discovery, but the stack reloads its retained cache
04:58:11.053  no client app holds the link; one-second idle timer starts
04:58:12.055  Android requests disconnect
```

There is no primary-service discovery, HID characteristic discovery, Report Map read, or
report CCCD write in this failed visit.

The Android native stack logs expose the mismatch between its two internal consumers of
the same event:

```text
BTA_GATTC_SRVC_CHG_EVT
bta_hh_le_co_reset_rpt_cache
State BTA_HH_IDLE_ST, Event BTA_HH_GATT_CLOSE_EVT
Unexpected event BTA_HH_GATT_CLOSE_EVT in BTA_HH_IDLE_ST
```

Generic GATT has just validated and retained the unchanged database. HID Host receives the
Service Changed callback, deletes its cached report metadata, marks the HID service as
changed, and injects a synthetic close so that it can reopen after rediscovery. Because
HID Host is still `IDLE` on this background connection, that synthetic close is rejected.
Generic GATT then sees the matching hash and does not produce the rediscovery sequence HID
Host expects. Nothing claims the link, so the generic connection-idle timeout closes it.

The relevant Android source is in `system/bta/hh/bta_hh_le.cc`:

- `bta_hh_gattc_callback` forwards `BTA_GATTC_SRVC_CHG_EVT`;
- `bta_hh_le_service_changed` clears report state and injects the synthetic close;
- `bta_hh_le_service_discovery_done` injects a synthetic open after rediscovery;
- `bta_hh_security_cmpl` either loads cached reports or starts HOGP discovery.

This is why the exact ordering matters. Android reads the final hash before Windows sends
the queued indication. Reading that same hash again after the indication cannot reveal the
intermediate remove/re-add cycle, because the current definitions are identical.

### Successful manual connection control

A successful manual connection to the same provider and same database produced a different
profile-level path:

```text
04:44:35  generic GATT connection and encryption
04:44:35  Database Hash = 77537a94abbc92c684af103158748c74
04:44:35  Connect HOGP(LE) Profile
04:44:36  HOGP Open status = BTA_HH_OK
04:44:36  descriptor length = 113 bytes
```

The .NET server then observed HID Information and Report Map reads followed by keyboard and
mouse report subscriptions. The identical hash is therefore not itself a problem. The
failure requires the provider-restart Service Changed state and the Android HID state/order
described above.

### Why alternating the Database Hash before connection did not solve it

An experimental provider alternated between two private service UUIDs on every process
start. It changed the Database Hash successfully while leaving the HOGP definition stable.
That did not provide a dependable recovery.

The ordering makes this approach insufficient:

1. Android connects and reads the already changed hash.
2. Android stores that value as the current database.
3. Windows sends the older pending Service Changed indication.
4. Android reads the hash again.
5. The second value matches the one stored milliseconds earlier.

Changing the database before the client connects therefore does not guarantee that the
pending indication is consumed before the new hash becomes the client's baseline. The
experiment was reverted.

### Genuine offline database-change control

A second experiment separated an actually changed final database from the identical-final-
database case.

While Android Bluetooth was off:

1. The normal HOGP provider was started.
2. An empty private service was added in the same process.
3. HOGP remained at `0x005D-0x006D`.
4. The private service occupied `0x006E`.
5. Database Hash changed to `7288815729c595c5295c7021f9d1cd7d`.
6. The final advertisement contained both HOGP and the private service.

Only after that final state was verified was Android Bluetooth turned on. Android connected
automatically. The trace showed:

```text
14:44:56.660  LE connection complete
14:44:56.893  HOGP opens from cached Report metadata; Report IDs 1 and 2 registered locally
14:44:56.923  new Database Hash response arrives
14:44:56.924  hash mismatch starts full GATT service discovery
14:44:56.955  Windows sends Service Changed for 0x005D-0x006E
14:44:56.956  Android confirms the indication
14:44:58.669  full generic GATT discovery completes
```

This is an important positive control:

- Android can reconnect automatically after the server changes while Android is offline.
- Windows can produce a new Database Hash for a genuine final database change.
- Android recognizes the mismatch and performs full generic GATT discovery.
- The Service Changed indication covers the affected final range and is confirmed.

It also exposed the second failure. Android had already opened HOGP from its cached Report
entries before the new hash response arrived. It registered Report IDs 1 and 2 in its local
HID state, but sent no ATT Write Request to the Windows keyboard or mouse report CCCDs.
Android UI showed Input enabled and UHID was open, while WinRT reported zero subscribed
clients. No input reports could be delivered.

The trace is bidirectional because both systems expose GATT services. Earlier analysis
incorrectly treated reads of coincidentally numbered phone attributes as Android reading
the Windows HID CCCDs. That conclusion was retracted. The valid evidence is:

- no Android-to-Windows ATT Write Request for either HID report CCCD;
- no WinRT `SubscribedClientsChanged` callback;
- keyboard and mouse subscriber counts remained zero;
- target switching could not select the phone.

Disabling and re-enabling Android's Input device profile did not cause CCCD writes. A
visible manual disconnect/reconnect in that forced state also did not restore subscriptions.
After the experiment was removed and the normal provider returned, a manual connection
caused Android to reread HID Information and Report Map and subscribe to both reports.

### Battery-only staging control

A follow-up replaced the private staging service with the standard Battery Service
`0x180F`. With Android Bluetooth off, the normal provider was stopped and an active,
connectable Battery Service was started with a readable and notifiable Battery Level
`0x2A19`. HOGP did not exist during this stage.

When Android Bluetooth was turned on, Android did not establish an LE connection, read
Battery Level, or subscribe to it. Adding a complete HOGP provider while Battery Service
remained active also did not make Android reconnect automatically, and a manual connection
attempt failed. Android therefore never reached the point where it could rediscover HOGP
or rewrite the input report CCCDs.

An already-connected non-Android bonded host provided a useful positive control. It read
and subscribed to Battery Level during the Battery-only stage. When HOGP was added, it
immediately read HID Information and Report Map and subscribed to both keyboard and mouse
reports. This proves that WinRT successfully added HOGP to the live local database and that
the report CCCDs were writable. It does not prove anything about Android CCCD recovery,
because Android never accepted the prerequisite Battery-only connection.

Battery Service is therefore no better than a private service for staging an Android
connection. Android's background connection is triggered by the HOGP profile/advertised
`0x1812`, not by the presence of an arbitrary standard GATT service.

After the experiment was removed and the normal provider was restored, Android still did
not reconnect automatically and a manual connection attempt failed without reaching the
Windows GATT server. Forgetting and re-pairing the PC caused fresh HID Information and
Report Map reads followed by both keyboard and mouse report subscriptions. This confirms
that the failed service-removal sequence had again damaged Android's persisted HOGP state,
and that a fresh bond repaired the CCCDs.

### HOGP-first, Battery-added control

A corrected control kept HOGP present throughout the final database and used Battery
Service only as the database delta. With Android Bluetooth off, HOGP was created and
started first, then an active Battery Service was appended in the same process. An
independent discovery verified:

```text
Database Hash                         a6cb993dee0d15f8cdefcc9d0e03531e
HOGP 0x1812                           0x005D-0x006D
  keyboard Report value               0x0067
  mouse Report value                  0x006B
Battery Service 0x180F                0x006E-0x0071
```

The HOGP and Report handles were identical to the normal database; Battery was the only
appended service. Android connected automatically when Bluetooth was turned on, reread HID
Information and Report Map, and subscribed to Battery, keyboard, and mouse. Raw HCI proved
that Android explicitly restored both HID report CCCDs:

```text
16:54:50.553  LE connection complete
16:54:50.920  encryption succeeds; cached HOGP opens
16:54:51.106  Service Changed indication for 0x005D-0x0071
16:54:51.109  Android confirms the indication
16:54:51.159  new Database Hash response
16:54:51.160  hash mismatch starts full service discovery
16:54:53.327  Android reads HID Information 0x005F
16:54:53.354  Android reads Report Map 0x0061
16:54:53.459  Android writes 0x0100 to keyboard CCCD 0x0068
16:54:53.476  Android writes 0x0100 to mouse CCCD 0x006C
```

The native state ordering explains why this run repaired the CCCDs. Cached HOGP reached
`BTA_HH_CONN_ST` before Service Changed arrived, so HID Host accepted its synthetic close.
The new hash then forced full generic discovery. HID Host reopened after discovery, found
HOGP, and ran `bta_hh_le_write_ccc` for both reports.

This differs from the earlier private-service run, where the new hash response arrived and
started discovery before the Service Changed indication. HOGP opened from cached metadata,
but the later generic discovery did not make it rewrite the server CCCDs. The successful
Battery run therefore proves that Android can repair the report subscriptions when packet
and HID-state ordering align; it does not prove that Battery UUID `0x180F` has special
recovery semantics.

#### Repeatability matrix

A temporary A/B harness exposed two verified final databases while keeping HOGP handles
fixed:

```text
base     hash 77537a94abbc92c684af103158748c74  HOGP 0x005D-0x006D
battery  hash a6cb993dee0d15f8cdefcc9d0e03531e  HOGP 0x005D-0x006D, Battery 0x006E-0x0071
```

It timestamped provider events and subscriber callbacks. Later cycles used Wireless ADB
to disable Bluetooth, verify `bluetooth_on=0`, start the final database, independently
verify handles and hash, and only then enable Bluetooth. Results were:

| Transition | Result |
| --- | --- |
| Base to Battery, immediate append | No automatic HOGP; manual Connect restored both CCCDs. |
| Battery to Base | Android sometimes showed connected without HOGP or CCCDs; one repetition fully recovered automatically. |
| Base to Battery, 1500 ms append delay | Full automatic HOGP and both CCCDs in a valid synchronized run. |
| Battery to identical Battery, same 1500 ms delay | No automatic HOGP in repeated trials; manual Connect restored subscriptions. |

One apparent failed delayed trial was excluded. Its ADB bug report proved Android connected
during the 1500 ms HOGP-only interval, before Battery existed. It read the base hash, later
received Service Changed for the base range, reread the same hash, and disconnected. Phone
UI timing alone was therefore not sufficient to classify a cycle.

The ADB-synchronized successful delayed trial adds an important nuance. Android was enabled
only after the final Battery database was verified. Its packet sequence was:

```text
18:11:09.503  LE connection complete
18:11:09.579  Android reads Database Hash
18:11:09.761  hash response = a6cb993dee0d15f8cdefcc9d0e03531e
18:11:09.817  Service Changed indication for 0x005D-0x0071
18:11:09.823  Android confirms and rereads Database Hash
18:11:09.878  same hash response; generic GATT skips discovery
18:11:09.940  Android reads HID Information 0x005F
18:11:09.998  Android reads Report Map 0x0061
18:11:10.163  Android writes 0x0100 to keyboard CCCD 0x0068
18:11:10.252  Android writes 0x0100 to mouse CCCD 0x006C
```

This successful run did **not** require a hash mismatch or full generic rediscovery. HID
Host had entered a usable open path and rebuilt HOGP from the retained generic cache, then
rewrote both CCCDs. Conversely, a synchronized restart to the identical Battery database
made controller connection attempts but established no ATT link and no HOGP subscribers.

The complete matrix confirms that neither Battery UUID `0x180F`, a real hash transition,
nor a delay guarantees recovery. They perturb a race between connection initiation,
Service Changed delivery, generic cache validation, and HID Host state. An identical final
database still fails, so alternating Battery presence would manipulate the race rather
than fix it. The harness was removed; Battery remains part of the normal provider but is
not actively advertised as a database-revision mechanism.

### Bluetooth specification requirements

Two GATT requirements interact here.

Bluetooth Core, Vol 3, Part G, Section 2.5.2 says that after receiving Service Changed, a
client must consider the affected handle range invalid and perform discovery before using a
service in that range, unless it obtains the changed database definitions through an
out-of-band mechanism. Android's generic GATT layer does perform full discovery when the
final Database Hash genuinely differs. In the identical-final-database case, however, its
robust-caching path retains the matching cache while HID Host has already deleted its own
report state.

Bluetooth Core, Vol 3, Part G, Section 3.3.3.3 states that the Client Characteristic
Configuration Descriptor value shall persist across connections for bonded devices. The
WinRT provider recreation does not preserve the application report subscriptions. This is
observable because the new `GattLocalCharacteristic` objects have no subscribed clients
until Android explicitly writes their CCCDs again.

CCCD **values** are not inputs to the Database Hash. For a CCCD, the hash includes its
attribute handle and type, not its per-client value. Therefore an identical Database Hash
cannot tell Android that Windows lost the bonded notification configuration.

The specification does not generally require a client to rewrite every bonded CCCD after
every reconnect: the server is required to preserve those values. Android could be more
robust by reconciling CCCDs after Service Changed and HOGP rediscovery, but Windows cannot
depend on that behavior to compensate for lost bonded server state.

### Companion-assisted restart control

The Android companion produced full automatic recovery in two consecutive provider
restart cycles on the Android 16 test phone. No Connect tap, Bluetooth toggle, unpairing,
or phone unlock was used.

Before the first cycle, both Android clients shared the healthy LE channel: system HID Host
and `dev.blehid.companion`. Stopping the WPF provider removed HOGP. The companion briefly
kept the channel alive after HID Host released it, observed the intermediate database, and
then stopped when Companion Device Manager reported the advertisement away. Relaunching
the provider produced this ordering:

```text
15:04:26  Companion Device Manager reports the associated advertisement present
15:04:27  companion GATT becomes the first ACL holder
15:04:27  Android HID Host attaches to the same LE channel
15:04:28  Windows receives HID Information and Report Map reads
15:04:28  Windows receives keyboard and mouse subscriptions; counters = 1 / 1
```

That cycle changed the companion's saved Database Hash from the intermediate no-HOGP value
back to the normal value, so a second cycle tested whether that transition was essential.
Before the second shutdown Samsung had already stopped the companion client after presence
settled; only system HID remained, and the saved hash stayed at the normal value throughout
the outage. The second relaunch still recovered:

```text
15:07:00  companion GATT becomes the first ACL holder
15:07:00  Android HID Host attaches immediately afterward
15:07:01  Windows receives HID Information and Report Map reads
15:07:01  Windows receives keyboard and mouse subscriptions; counters = 1 / 1
```

The second cycle shows that a Database Hash mismatch in the companion is not required. The
useful action is the presence-triggered direct GATT connection when advertising returns. It
changes connection and profile ordering enough for Android HID Host to reopen and explicitly
restore both report CCCDs. Samsung reports the associated device away about 12-16 seconds
after the link settles, because the connected peripheral is no longer observed advertising;
the companion then closes its own GATT client while system HOGP remains connected.

This is a measured workaround, not a privileged HID cache repair or a platform guarantee.
The two cycles were consecutive positive trials rather than a randomized A/B matrix, and no
keystrokes were sent during automation. HOGP state, fresh HID metadata reads, and both WinRT
subscriber counters prove that the input transport was configured, but a human input check
remains part of final release validation.

### Precise platform responsibility

The evidence supports several separate statements:

1. **The Service Changed indication is real, not inferred.** It appears in raw HCI as an
   ATT Handle Value Indication on `0x2A05`, and Android confirms it.
2. **The final database after an ordinary provider restart is identical.** HOGP handles,
   Report handles, Report Map, and Database Hash are stable.
3. **A transient change did occur.** Provider teardown removed HOGP before provider
   recreation restored it. Windows therefore had a reason to mark a service change at the
   time of removal.
4. **Windows does not reconcile the pending indication with the identical final state.** It
   sends the queued HID-range indication after first serving the unchanged final hash.
5. **Android has a state/order bug.** Generic GATT and HID Host leave their caches in
   contradictory states, and HID Host's synthetic close/reopen sequence fails from `IDLE`.
6. **Windows loses bonded CCCD state across provider recreation.** That violates the GATT
   persistence expectation and independently prevents input even when HOGP opens.

Calling the Service Changed indication simply "invalid" is too strong because the service
was transiently removed. Calling the behavior correct is also too strong because the
reconnecting client sees an identical final database/hash and receives the pending
indication in an order that strands a standards-based client. The narrow Windows-side
questions are:

- Can a pending Service Changed record be cancelled or coalesced when the identical service
  definition is restored before the bonded client reconnects?
- Can Windows deliver the pending indication before answering the reconnecting client's
  first Database Hash read?
- Can local-service ownership or bonded CCCD state survive a provider process handoff?
- If the same service UUID/handles return, can Windows restore each bonded client's CCCD
  values on the recreated characteristics?

### User-visible result and workaround

The practical behavior is:

- Same provider, temporary link loss: automatic reconnect and input work.
- Provider stopped: HOGP is absent and cannot accept input.
- Provider recreated: Android may require a manual Connect; after cache damage, forgetting
  and re-pairing may be required to restore report subscriptions.
- Provider recreated with the experimental Android companion monitoring enabled: two
  consecutive Android 16 trials reconnected automatically and restored both report CCCDs.
- Windows restart: always destroys the provider, so autostart reduces downtime but does not
  preserve the old GATT server lifetime.

The current mitigation is to keep one provider process resident for as long as possible.
That avoids the trigger. The companion is a promising measured workaround for Android
recovery after Windows reboot or application-update restarts, but it is not yet proven
across Android vendors or enough cycles to replace the manual fallback.

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
| Advertise only Battery Service, then add HOGP | Falsified. Android did not create the Battery-only link and did not reconnect when `0x1812` was added. |
| Alternate Battery after stable HOGP on every restart | Partial and nondeterministic. Real transitions sometimes restored both CCCDs, but other cycles produced only a link or no HOGP; identical HOGP-plus-Battery restarts failed. |

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
