# BleHid

Turn a Windows PC into a Bluetooth Low Energy keyboard and mouse, so it can drive other
devices over the air. No extra hardware, no software installed on the receiving device —
it pairs like any ordinary Bluetooth keyboard.

Built entirely on the in-box Windows BLE stack (WinRT `GattServiceProvider`), implementing
the standard HID over GATT Profile (HOGP).

> **Status: working spike.** The core functionality is verified against real devices, but
> there are real limitations — see [Known issues](#known-issues) before relying on it.
> Everything below was measured on actual hardware; unverified claims are marked as such.

---

## What it does

- **Redirects your real keyboard and mouse** to the paired device via low-level Win32
  hooks — the `capture` command, and the main way you use the app. Input is swallowed
  locally while redirecting.
- Exposes the PC as a **composite BLE HID device** — keyboard (Report ID 1) and mouse
  (Report ID 2) in a single HOGP service.
- **Multiple hosts at once.** Several devices can be paired and connected simultaneously.
- **Per-host switching** with a hotkey, including a "this PC" position so you can hop
  between your own machine and the remotes without stopping capture.
- **Scriptable CLI** for sending individual keystrokes, text, pointer moves, clicks and
  scrolling.
- **Diagnostics** for connection state, subscriber counts, link timing and platform
  capability probes.

---

## Requirements

- Windows 10 2004 (build 19041) or later
- A Bluetooth radio that supports the **LE peripheral role** — the app reports this at
  startup; if it says `Peripheral=False`, nothing else will work
- [.NET SDK 8.0](https://dotnet.microsoft.com/download)

---

## Download

Prebuilt `win-x64` and `win-arm64` executables are attached to each
[release](../../releases). They are self-contained single files — no .NET runtime needed.
Unzip and run `BleHid.Cli.exe`.

The binaries are **unsigned**, so SmartScreen will warn on first launch
(*More info → Run anyway*). Build from source if you would rather not trust them.

---

## Quick start

1. Run `BleHid.Cli.exe`. It publishes the GATT services and starts advertising
   immediately.
2. On the device you want to control, pair with the PC from its Bluetooth settings — the
   PC appears under its own hostname.
3. Type **`capture`** and press Enter.

`capture` is the command you actually use. Your real keyboard and mouse now drive the
paired device instead of this PC.

| While capturing | |
| --- | --- |
| `Ctrl` + `Alt` + `Q` | Stop and return control to this PC |
| `Ctrl` + `D` + `C` | Switch which device you are driving, including back to this PC |

Everything else in the app is either scripted input (`type`, `move`) or diagnostics.

---

## Build and run

```powershell
dotnet build src\BleHid.Cli\BleHid.Cli.csproj
dotnet run   --project src\BleHid.Cli\BleHid.Cli.csproj
```

Then follow the Quick start above.

Run with `--plain` to drop the encryption requirement on the HID characteristics. HOGP
mandates encryption, so this is a diagnostic aid rather than a normal mode.

**Note:** the app holds a lock on its own assemblies while running. You must `quit` before
rebuilding, or the build fails with `MSB3026`.

---

## Commands

### Main

| Command | Description |
| --- | --- |
| **`capture`** | **Redirect the local keyboard and mouse — the primary command** |
| `capture verbose` | Same, with per-report timing diagnostics |
| `capture <ms>` | Same, with a custom pointer report interval |
| `host` | List subscribed hosts and the current target |
| `host <n\|next\|local\|all>` | Choose the target |
| `status` | Advertisement state, subscriber counts, current target |
| `quit` | Exit |

### Hotkeys while capturing

| Hotkey | Action |
| --- | --- |
| `Ctrl` + `D` + `C` | Switch target: this PC → host 1 → … → host N → this PC |
| `Ctrl` + `Alt` + `Q` | Stop capturing |

When the target is **this PC**, input passes through untouched so you can use your own
machine normally. The hotkeys stay live in that mode.

### Scripted input

Useful for automation or for testing the link without handing over your keyboard.

| Command | Description |
| --- | --- |
| `type <text>` | Send a string as keystrokes |
| `key <name>` | Press a named key (`enter`, `esc`, `up`, `f5`, …) |
| `move <dx> <dy>` | Move the pointer |
| `click <l\|r\|m>` | Click a mouse button |
| `scroll <n>` | Scroll the wheel |

### Diagnostics

| Command | Description |
| --- | --- |
| `peers` | List connected Bluetooth peers (LE and Classic) |
| `burst <n>` | Time *n* raw notifications — link diagnostic |
| `watch <secs>` | Log connection and subscription changes as they happen |
| `probe <uuid16>` | Try to create a GATT service, e.g. `probe 1801` |
| `l2cap` | Test whether user mode can open Classic HID L2CAP PSMs |
| `appearance` | Attempt to advertise GAP appearance = keyboard |
| `classic <on\|off>` | Attempt to toggle BR/EDR connectable |

---

## Host compatibility

Measured on real devices. Absence from this table means untested, not unsupported.

| Host | Pairs | Input works | Reconnects after app restart |
| --- | --- | --- | --- |
| macOS | Yes | Yes | **Yes** |
| Android 16 (Galaxy S24 FE) | Yes | Yes | Yes |
| Android 11 (Galaxy S20 FE) | Yes | **No** — binds BR/EDR instead of LE | — |
| Windows 11 | Yes | Yes | **No** — see below |
| iOS / iPadOS | Untested | | |
| Linux | Untested | | |
| Smart TVs, consoles, BIOS/UEFI | Untested | | |

Older Android builds attach to the PC's Classic radio rather than the LE peripheral and
never bind HOGP. Newer builds handle it correctly. The cutoff between the two has not been
narrowed down.

---

## Known issues

### A Windows host will not reconnect after the app restarts

The most significant limitation. After the peripheral process exits and restarts, a paired
**Windows** host never re-establishes the LE connection — measured over 240 s with
per-second polling: zero connection attempts, zero subscriptions. macOS and Android
reconnect automatically from the identical peripheral.

Neither toggling Bluetooth on the host, clicking Connect, nor disabling the peer's Classic
device nodes recovers it. **Only removing the device on the host and pairing again works.**

The likely mechanism is a stale GATT attribute cache: `GattServiceProvider` rebuilds its
attribute table on every process start, and a bonded client is permitted to skip service
discovery. The conforming remedy is the Service Changed characteristic (`0x2A05` in
`0x1801`), which Windows blocks applications from creating:

```
> probe 1801
  service 0x1801: DisabledByPolicy
```

A GATT client inspecting the PC confirms Windows exposes `0x1801` with `0x2A05` itself, so
the platform reserves the mechanism and does not fire it on behalf of applications.

**This mechanism is not proven.** We never verified that attribute handles actually change
across restarts, and the observed symptom — no connection attempt at all — does not
require it. Treat the cache explanation as the leading hypothesis, not a conclusion.

**Practical impact:** every rebuild during development drops all paired hosts. The intended
mitigation is to keep the peripheral alive in a background process so the attribute table
never moves.

### Input is broadcast in `all hosts` mode

With `host all`, every report goes to every subscribed host at once — keystrokes land on
all of them simultaneously. Per-host targeting is the normal mode; broadcast is a fallback.

### Broadcast pointer motion is coarser

Radio capacity does not split proportionally between links. Measured: one host at a 10 ms
report interval is smooth; two hosts at 20 ms drift after you stop moving; two hosts at
40 ms are clean. The pump therefore paces broadcast at `interval × 2 × hostCount`.

`NotifyValueAsync` returns when a report is *queued*, not delivered, so overload is
invisible to the sender and only shows up as pointer lag on the host.

The `× 2` factor is fitted to a **single measurement on one pair of hosts**. It may not
hold for three or more.

### The switch hotkey leaks one keypress

Whichever of `D` or `C` you press first reaches the current target before the combination
completes. Press `D` first — `Ctrl`+`D` is harmless in most applications, whereas
`Ctrl`+`C` would copy.

### Other limitations

- **No control over the LE identity address.** The peripheral shares the radio's public
  address with the Classic side, so hosts see one dual-mode device.
- **GAP Appearance cannot be set.** The PC advertises as a computer, not a keyboard, so
  some hosts show the wrong icon. `AppearanceAdvertiser` attempts a workaround; it has no
  observable effect.
- **BR/EDR cannot be suppressed.** `BluetoothEnableIncomingConnections` returns
  `E_INVALIDARG` for every variant tried, including a null radio handle.
- **Consumer-control and media keys are not implemented** — the report descriptor covers a
  boot-style keyboard and a 3-button mouse with wheel only.
- Reboots and OS updates destroy the attribute table just as a restart does.

---

## Platform findings

Two questions came up repeatedly and are now settled empirically. Both probes ship as
commands so the results can be reproduced.

### Classic Bluetooth HID Device role is impossible in user mode

Classic HID needs L2CAP PSMs `0x11` (control) and `0x13` (interrupt). Windows exposes only
RFCOMM to user mode. The `l2cap` command tests this, with RFCOMM as a control case:

```
> l2cap
  RFCOMM     (control): SUCCESS - listening
  L2CAP stream    0x11: socket() -> WSAEPROTOTYPE (wrong protocol for this socket type)
  L2CAP seqpacket 0x11: socket() -> WSAESOCKTNOSUPPORT (socket type not supported)
  L2CAP seqpacket 0x13: socket() -> WSAESOCKTNOSUPPORT (socket type not supported)
```

The failure is at `socket()` creation, not `bind()` — Windows will not create the socket at
all. A kernel-mode Bluetooth profile driver (`bthddi.h`) could open L2CAP channels, but
that requires driver signing and an installer.

### Generic Attribute (`0x1801`) is reserved

`GattServiceProvider.CreateAsync(0x1801)` returns `DisabledByPolicy`, so applications
cannot expose Service Changed. See the reconnect issue above.

---

## How it works

```
src/
  BleHid.Core/
    HidDescriptors.cs      HID report map + GATT UUIDs
    HidReports.cs          Report encoding, character and key name maps
    BleHidPeripheral.cs    GATT server, advertising, host targeting, notifications
    InputCapture.cs        WH_KEYBOARD_LL / WH_MOUSE_LL hooks, hotkeys, pass-through
    VirtualKeyMap.cs       Windows virtual keys to HID usages
    BluetoothDiagnostics.cs  Connected peer enumeration
    AppearanceAdvertiser.cs  GAP appearance experiment (no effect)
    ClassicRadio.cs        BR/EDR suppression experiment (no effect)
    ServiceProbe.cs        GATT service creation probe
    L2capProbe.cs          Classic HID L2CAP capability probe
  BleHid.Cli/
    Program.cs             Console interface and the report send pump
spike/                     Early PowerShell capability probes
```

A few decisions worth knowing if you read the code:

- **The input hook thread is MTA.** An STA hook thread dispatches WinRT completions through
  the same message pump the input callbacks saturate, which stalls notifications for
  seconds.
- **Pointer motion is coalesced, keystrokes are not.** The hook produces far more motion
  events than a BLE link can carry, so pending deltas are merged; every keystroke must be
  delivered.
- **The send pump is signal-driven, not polled.** `Task.Delay` has ~15 ms granularity on
  Windows, which alone made the pointer feel sluggish.
- **Host switching is queued through the report channel**, not applied directly from the
  hook. Applying it inline races the send pump and can deliver a key-release report to the
  *new* host, leaving a stuck modifier on the old one.

---

## Roadmap

- Keep the peripheral alive in a background process so hosts survive an app restart
- Test iPhone / iPad, smart TV and Linux hosts
- Consumer-control and media keys
- A UI — the console interface is deliberately the first step

---

## License

[MIT](LICENSE)
