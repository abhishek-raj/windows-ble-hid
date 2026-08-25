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
  startup; if it says `Peripheral=False`, nothing else will work. See [Adapter
  compatibility](#adapter-compatibility) for what has been tested
- [.NET SDK 8.0](https://dotnet.microsoft.com/download)

---

## Download

Prebuilt `win-x64` and `win-arm64` executables are attached to each
[release](../../releases). They are self-contained single files — no .NET runtime needed.
Unzip and run `BleHid.Cli.exe`. The arm64 build has been verified on a Snapdragon-based
Surface laptop, including `capture` driving a remote host.

The binaries are **unsigned**, so SmartScreen will warn on first launch
(*More info → Run anyway*). Build from source if you would rather not trust them.

---

## Quick start

1. Run `BleHid.Cli.exe`. It publishes the GATT services and starts advertising
   immediately.
2. On the device you want to control, pair with the PC from its Bluetooth settings — the
   PC appears under its own hostname. A Windows host lists the same machine twice; pick the
   entry shown as a **PC**, not the audio one, or the bond lands on Classic and nothing
   works ([details](#a-windows-host-can-pair-to-the-wrong-entry)).
3. Type **`capture`** and press Enter.

`capture` is the command you actually use. Your real keyboard and mouse now drive the
paired device instead of this PC.

| While capturing | |
| --- | --- |
| `Ctrl` + `Alt` + `Q` | Stop and return control to this PC |
| `Ctrl` + `D` + `C` | Switch which device you are driving, including back to this PC |

Everything else in the app is either scripted input (`type`, `move`) or diagnostics.

---

## Background mode

Two reasons to use it. Convenience: the peripheral is already up and the hotkeys already
live, so switching to another machine costs one keystroke instead of opening a console and
starting a session. And it sidesteps the [Windows reconnect
limitation](#a-windows-host-will-not-reconnect-after-the-app-restarts) — one long-lived
peripheral means no restarts for a bonded host to fail to recover from.

```powershell
BleHid.Cli.exe --background          # run detached, no console window
BleHid.Cli.exe --stop                # shut it down
BleHid.Cli.exe --install-autostart   # run at login (per-user, no admin)
BleHid.Cli.exe --remove-autostart
```

Auto-start is opt-in. Nothing is written to the `Run` key unless you run
`--install-autostart` yourself, and `--remove-autostart` undoes it.

It starts on the **local** target, so your keyboard and mouse behave normally until you
pick a host. From there it is hotkeys only — there is no console to type into:

| Hotkey | Action |
| --- | --- |
| `Ctrl` + `D` + `C` | Switch target: this PC → host 1 → … → host N → this PC |
| `Ctrl` + `Alt` + `Q` | Return input to this PC (does **not** exit — use `--stop`) |

Only one instance runs at a time; a second `--background` is refused. Output goes to
`%LOCALAPPDATA%\BleHid\logs\blehid.log`.

The interactive console cannot run at the same time — both would try to publish the same
GATT service. Run `--stop` first when you need the diagnostics.

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
| macOS | Yes | Yes | **Yes** — automatic |
| Android 16 (Galaxy S24 FE) | Yes | Yes | Yes — **manual Connect** required |
| Android 11 (Galaxy S20 FE) | Yes | **No** — binds BR/EDR instead of LE | — |
| Windows 11 | Yes — **pick the right entry**, see below | Yes | **No** — see below |
| iOS (iPhone) | Yes | Yes — pointer needs **AssistiveTouch**, see below | Untested |
| iPadOS | Untested | | |
| Linux | Untested | | |
| Smart TVs, consoles, BIOS/UEFI | Untested | | |

Older Android builds attach to the PC's Classic radio rather than the LE peripheral and
never bind HOGP. Newer builds handle it correctly. The cutoff between the two has not been
narrowed down.

Only macOS re-establishes the link entirely on its own. Android reconnects reliably across
app restarts, but you have to tap **Connect** in its Bluetooth settings each time — it does
not come back unaided.

iOS accepts the keyboard immediately, but draws no pointer for the mouse until **Settings
→ Accessibility → Touch → AssistiveTouch** is enabled. Confirmed on an iPhone: with
AssistiveTouch on, the pointer appears and tracks. Nothing is wrong with the report
descriptor — iOS simply has no cursor without that setting.

---

## Adapter compatibility

The radio on *this* PC has to support the LE peripheral role. Most do; the cheap ones
often do not. Measured, and again not exhaustive — this is what I happened to have access
to, not a shortlist worth buying from:

| Adapter | Works |
| --- | --- |
| ASUS USB-BT500 (USB dongle) | Yes |
| ThinkPad P14s built-in radio | Yes |
| Surface (ARM) built-in radio | Yes |
| Intel Wireless Bluetooth | Yes |
| Generic no-name dongle, enumerates as a CSR radio | **No** |

The CSR dongle reports Bluetooth Core Specification 4.0, which is high enough on paper —
BLE and HOGP both arrived in 4.0 — but it never worked in practice. Supporting a spec
version is not the same as supporting the peripheral role, and these generic dongles are a
known weak spot.

If you need to buy one, the rule of thumb is to avoid anything that only claims 4.0 or
shows up as a generic CSR radio, and prefer a dongle that names its chipset and advertises
5.0 or later.

Run `BleHid.Cli.exe --diagnose` to check your own: if it reports `Peripheral role : False`,
that radio cannot act as a keyboard and no amount of pairing will fix it.

To find the spec version your radio claims, follow Microsoft's instructions for [what
Bluetooth version is on a Windows
device](https://support.microsoft.com/en-us/windows/hardware/bluetooth/what-bluetooth-version-is-on-a-windows-device)
— Device Manager → the radio's **Advanced** tab → the **LMP** number, which maps to a core
spec version (LMP 6 = 4.0, LMP 9 = 5.0, and so on).

---

## Known issues

The two Windows-host problems below are independent. The first bites when you pair; the
second bites every time the app restarts.

### A Windows host can pair to the wrong entry

This PC is dual-mode: it advertises the LE HID peripheral *and* its ordinary Classic
BR/EDR identity. A Windows host's **Add device** list therefore shows **two entries for the
same machine** — one is the Classic/audio side, the other is the LE peripheral.

Pairing the Classic one binds a transport this app cannot service at all (see [Classic
Bluetooth HID Device role is impossible in user
mode](#classic-bluetooth-hid-device-role-is-impossible-in-user-mode)). The host reports
itself as connected, but `peers` lists it under `connected classic`, no GATT read ever
arrives, the subscriber counts stay flat, and the link drops after roughly 30 s.

**Pair the entry that shows up as a PC, not the audio one.** Measured on a Windows 11
host: after removing the device and re-pairing to the correct entry, GATT discovery ran
within seconds (`k=2 m=2`), and toggling the host's Bluetooth off and back on reconnected
it automatically.

Use `peers` to tell the two apart — a working bond lists the host under `connected LE`.

### A Windows host will not reconnect after the app restarts

The most significant limitation, and it is **not** explained by the pairing problem above.
Measured against a host that was correctly bonded on LE and driving input seconds earlier:

- 180 s hands-off after restarting the peripheral — the subscriber count stayed at 1 (the
  Mac, which had already reconnected by itself).
- A further 120 s after clicking **Connect** on the host — no LE bind, no ATT traffic.

Polling `peers` throughout, rather than checking it once at the end, changed the picture.
The host is not silent: it reappears under `classic:`, holds for roughly 20–45 s, and
drops. It never appears under `connected LE`, and the HID service is never discovered.
That is the same failure mode as Android 11 above — the host attaches to the PC's Classic
radio instead of the LE peripheral and never binds HOGP.

macOS reconnects on its own from the identical peripheral, and Android 16 reconnects
reliably when you tap **Connect**. The Windows failure is stronger than either: even a
manual Connect does nothing. **Removing the device on the host and pairing again is the
only remedy** — verified directly, after which input worked and the host was listed under
`connected LE`.

#### What has been ruled out

The original hypothesis was a stale GATT attribute cache: `GattServiceProvider` rebuilds
its attribute table on every process start, and a bonded client is permitted to skip
service discovery. The conforming remedy is the Service Changed characteristic (`0x2A05`
in `0x1801`), which Windows blocks applications from creating:

```
> probe 1801
  service 0x1801: DisabledByPolicy
```

A GATT client inspecting the PC confirms Windows exposes `0x1801` with `0x2A05` itself, so
the platform reserves the mechanism and does not fire it on behalf of applications. That
is a real platform gap, but it is not what breaks reconnection here.

| Hypothesis | Test | Result |
| --- | --- | --- |
| Stale attribute cache | Does the host connect and then misread the table? | **No** — it never binds LE, so nothing reads the table |
| Handle layout shifting between runs | Publish filler services first to force the HID service onto a different handle range | **No change** — identical classic-only failure with shifted and with original handles |
| Host-side cache expiry on a timer | Wait hours between restarts | **No** — failure is immediate and persistent |
| Lock/unlock or Bluetooth toggle refreshing the cache | Toggle the host radio, lock and unlock the host | **No** — neither restores the LE bind |

The handle test is the decisive one. If the host were reconnecting against a cached table,
moving the HID service would have changed the symptom; keeping it in place would have made
reconnection work. Neither happened, and the failure signature was identical in both
directions. Handle layout has no bearing on it.

Android was used as a control. It re-reads HID Information and the Report Map on every
reconnect, so it performs a full service discovery and could never be caught out by a
rebuilt attribute table:

```
[read] host read HID Information
[read] host read Report Map
[subs] Keyboard input report: 1 subscriber(s)
```

That explains why Android survives restarts, but it also means Android cannot show
whether the handles move.

What remains is transport selection: after the peripheral's process restarts, the Windows
host resolves the bond to the PC's Classic radio rather than to the LE peripheral, and
never retries on LE. Why it does so, and why re-pairing corrects it, is unknown.

The practical mitigation is to avoid restarting: [background mode](#background-mode) keeps
one peripheral alive across sessions.

(Note that the subscriber counts `k=`/`m=` do *not* drop when a bonded host disconnects,
so they are not a liveness signal. Use the peer list.)

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

### Pointer behaviour on scaled and mismatched displays

HID mouse reports are relative and unitless — there is no DPI negotiation in the profile.
Driving a 1280p host from a 4K screen feels roughly three times too fast, and there is no
sensitivity multiplier yet.

Separately, the app requests per-monitor DPI awareness at capture start. Without it
`GetSystemMetrics` and `SetCursorPos` are virtualised while the mouse hook reports physical
pixels, and the mismatch adds a constant offset to every delta that walks the remote
pointer into a corner.

**The DPI fix is untested.** It was written against a 1920×1080 machine, where the bug
cannot reproduce. To verify on a scaled display, run `capture` and read the hook line — the
`screen=` value must match the panel's physical resolution:

```
[hook] keyboard=0x..., mouse=0x..., screen=3840x2160, center=1920,1080
```

If it still reports the scaled size, `SetProcessDpiAwarenessContext` is not taking effect.

### The switch hotkey leaks one keypress

Whichever of `D` or `C` you press first reaches the current target before the combination
completes. Press `D` first — `Ctrl`+`D` is harmless in most applications, whereas
`Ctrl`+`C` would copy.

At the `>` prompt — that is, when *not* capturing — `Ctrl`+`D` is console EOF and exits the
app. The hotkey only means "switch host" while capture is running.

### Apps that grab the keyboard can win the hook chain

Low-level hook chains run **newest-first**, so an app that installs its own
`WH_KEYBOARD_LL` hook after this one — Windows App / `mstsc`, some games and remote-desktop
clients — is called first and can swallow the hotkey before it ever arrives.

The fix is to re-install both hooks on every foreground change (`SetWinEventHook` on
`EVENT_SYSTEM_FOREGROUND`), which puts this app back at the head of the chain whenever you
switch windows. Verified against Windows App: `Ctrl`+`D`+`C` switches correctly with the
remote session focused.

Two cases this does *not* solve:

- **Elevation.** If the grabbing app runs at a higher integrity level than this one, its
  input never reaches this app's hook at all. Run as administrator to match it.
- **A competing re-arm.** If the other app also re-installs on focus, whoever hooks last
  wins and the result is a race.

Many remote-desktop clients also have a setting for whether shortcuts go to the local PC or
the remote session, and typically grab everything only in full screen. Changing that is
cheaper than winning the hook chain.

### Other limitations

- **No control over the LE identity address.** The peripheral shares the radio's public
  address with the Classic side, so hosts see one dual-mode device — and some hosts list it
  twice ([details](#a-windows-host-can-pair-to-the-wrong-entry)).
- **GAP Appearance cannot be set.** The PC advertises as a computer, not a keyboard, so
  some hosts show the wrong icon. `AppearanceAdvertiser` attempts a workaround; it has no
  observable effect.
- **BR/EDR cannot be suppressed.** `BluetoothEnableIncomingConnections` returns
  `E_INVALIDARG` for every variant tried, including a null radio handle.
- **Consumer-control and media keys are not implemented** — the report descriptor covers a
  boot-style keyboard and a 3-button mouse with wheel only.

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
cannot expose Service Changed — a bonded client that skips service discovery cannot be
told the attribute table was rebuilt.

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

- Find why a Windows host never re-establishes the link after the app restarts — it
  reconnects on the Classic radio instead of LE, and the attribute table has been ruled out
- Test iPad, smart TV and Linux hosts
- Consumer-control and media keys
- A UI — the console interface is deliberately the first step

---

## License

[MIT](LICENSE)
