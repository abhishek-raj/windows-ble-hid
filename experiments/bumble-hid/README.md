# Bumble HID experiment

An experiment: reimplement the BLE HID peripheral on top of
[Bumble](https://github.com/google/bumble), a full Python Bluetooth stack that drives a USB
controller directly over HCI.

**This is best-effort and not the supported path.** The Windows BLE stack
(`src/BleHid.App`) remains the primary implementation. Everything here requires replacing a
Bluetooth adapter's driver, which is a trade-off most people should not make.

## Why

Windows exposes BLE peripheral mode through `GattServiceProvider`, whose advertising
parameters are only `IsConnectable` and `IsDiscoverable`. There is no way to advertise
restricted to already-bonded centrals, which is the mechanism real HID peripherals use to
get picked up silently after a restart. That is the leading explanation for why a phone
never reconnects on its own to this app — see the "Known issues" section in the root
[README.md](../../README.md).

Bumble bypasses the Windows stack entirely, so the advertising filter policy, the filter
accept list, and directed advertising are all reachable. This experiment exists to test
whether using them actually restores automatic reconnection. If it does, that confirms the
diagnosis is an API limitation rather than a host-side bug.

## Hardware and driver setup

You need a **spare USB Bluetooth dongle** — not the adapter you use day to day.

Assigning the WinUSB driver removes the dongle from Windows' Bluetooth stack. Windows will
no longer see it as a Bluetooth adapter, and anything relying on it (Phone Link, Bluetooth
audio, existing paired devices) stops working through that adapter until you restore the
original driver. Do not do this to your only adapter.

1. Plug in the spare dongle.
2. Install [Zadig](https://zadig.akeo.ie/).
3. In Zadig, choose **Options → List All Devices**, select the dongle, pick **WinUSB** as
   the replacement driver, and click **Replace Driver**.
4. Confirm in Device Manager that the dongle now appears under **Universal Serial Bus
   devices** rather than **Bluetooth**, with `winusb.sys` among its drivers.

To undo this later, right-click the device in Device Manager, uninstall it with "delete the
driver software" ticked, then unplug and replug the dongle.

## Software setup

Requires **Python 3.9+**.

```powershell
cd experiments\bumble-hid
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
```

Create your local device identity. The address and IRK must stay stable across runs, or
bonds will not survive a restart and the reconnect test is meaningless. `device.json` and
`keys.json` hold local identity and bond keys, so both are gitignored.

```powershell
Copy-Item device.json.example device.json
# Generate an IRK and paste it into device.json
-join ((1..16) | ForEach-Object { '{0:X2}' -f (Get-Random -Maximum 256) })
```

Find your dongle's transport moniker:

```powershell
python -m bumble.apps.usb_probe
```

That prints the USB devices Bumble can open. Use the index or the vendor:product pair, e.g.
`usb:0` or `usb:0a12:0001`. Append `!` to force claiming the interface.

## Running

Three advertising modes, selected with `--advertising`:

| Mode | Behaviour |
| --- | --- |
| `open` | Undirected connectable advertising, any device may connect. Needed for the first pairing. |
| `bonded` | Advertising with filter policy 3 and the filter accept list loaded from bonded peers. Only bonded devices may scan or connect. |
| `directed` | Low duty cycle directed advertising aimed at the first bonded peer's identity address. |

First pairing, then type a test string once the host subscribes:

```powershell
python ble_hid_keyboard.py --transport usb:0 --advertising open --text "hello from bumble"
```

Pair from the phone as you would any Bluetooth keyboard. Then stop the script, restart it
in a restricted mode, and leave the phone alone:

```powershell
python ble_hid_keyboard.py --transport usb:0 --advertising bonded
```

```powershell
python ble_hid_keyboard.py --transport usb:0 --advertising directed
```

## What to look for

The question this answers: **does the phone reconnect on its own, with no tap in Bluetooth
settings?**

- If `bonded` or `directed` produces an unprompted reconnection, the accept-list theory
  holds and the Windows implementation is limited by its API, not broken.
- If neither does, the theory is wrong and the cause is somewhere else — most likely
  host-side policy about what it considers worth reconnecting to.

Watch the log for a `connected:` line that you did not trigger. Bond state lives in
`keys.json`; delete it to start over from an unpaired state.

There is a second thing worth checking while this is running. Bumble builds the advertising
payload itself, so this peripheral advertises a GAP Appearance of keyboard — which a Windows
desktop app [cannot set](../../README.md#known-issues). If the Samsung TV lists the PC under
this implementation but not the Windows one, that confirms appearance filtering is why it
never showed up.

## Known gaps

- Milestone 1 is the peripheral only. Real keyboard and mouse capture is not wired up —
  `--text` sends canned keystrokes to prove the HID path works end to end.
- Bumble is alpha software and documents that its API may change between releases.
- Populating the filter accept list has no high-level helper in Bumble, so
  `load_filter_accept_list` issues the HCI commands directly.
- Untested against a real dongle so far.
