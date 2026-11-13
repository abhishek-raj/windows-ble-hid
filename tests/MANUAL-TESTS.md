# Manual test checklist

Scenarios that need a phone, a second machine, or human judgement. Everything automatable lives
in `BleHid.Core.Tests` (`dotnet test`) and `Invoke-HandoverTests.ps1`.

Run the automated suites first — if those fail, don't bother with these.

```powershell
dotnet test tests\BleHid.Core.Tests\BleHid.Core.Tests.csproj
.\tests\Invoke-HandoverTests.ps1
```

## Known device behaviour

Use this as the baseline. A device behaving *as recorded here* is not a regression.

Bluetooth addresses are persistent hardware identifiers, so they are deliberately not recorded
here. Keep your own mapping from these roles to real devices in `tests/devices.local.md`, which
is gitignored.

| Role | Expected |
|---|---|
| macOS laptop | pairs, auto-reconnects; **lock-key reporting unverified** |
| Android 16 phone (primary test device) | full function; the reference for latency; **reports lock keys** |
| Windows 11 laptop A | pairs; **known reconnect bug**; lock-key reporting unverified |
| Windows 11 laptop B | pairs; **known reconnect bug**; lock-key reporting unverified |
| Android 11 phone | **known failure**, binds BR/EDR instead of LE |
| iPhone | pairs; pointer needs AssistiveTouch enabled; lock-key reporting unverified |

---

## 1. Pairing and advertising

- [ ] `--diagnose` reports `Peripheral role : True` and `Advertisement status: Started`
- [ ] Phone's Bluetooth scan lists the PC while advertising
- [ ] Pairing completes and the host appears in `host` (CLI) / the target list (UI)
- [ ] `Aborted (expected while starting)` appears once before `Started` and is **not** alarming
- [ ] After unpairing on the phone, re-pairing still works without restarting the peripheral

## 2. Input, per paired device

For each device in the table above:

- [ ] `type hello world` produces the exact text
- [ ] Capitals and digits are correct (`type Hello 123`) — catches usage/modifier mapping
- [ ] `key enter`, `key esc`, `key f5` behave as named
- [ ] `move 100 0` / `move 0 100` move the pointer in the expected direction
- [ ] `click l`, `click r`, `click m` register
- [ ] `scroll 3` and `scroll -3` scroll opposite ways
- [ ] Numpad digits type digits with **this PC's** Num Lock on
- [ ] **Numpad digits still type digits with this PC's Num Lock off** — the remote host's own
      Num Lock decides, not ours
- [ ] Numpad `.` `/` `*` `-` `+` all arrive correctly
- [ ] Keypad Enter works where the host binds it separately from the main Enter
- [ ] The dedicated Home/End/arrow keys still send navigation, not numpad digits
- [ ] `status` shows caps and num lock lines, labelled `measured` or `inferred`
- [ ] Record in the table above whether this host reports lock keys

## 2a. Lock key reporting

Needs a host that reports lock keys. Android 16 is the only confirmed one.

- [ ] A fresh connection logs `LED reports available from …`
- [ ] Toggling Caps Lock **on the host itself** updates BleHid without any keystroke from the PC
- [ ] Switching to a host with Caps Lock on logs `- CAPS LOCK ON` on the switch line
- [ ] Switching away from it logs `left <host> with CAPS LOCK ON`
- [ ] The Capture page shows a warning bar while the target has Caps Lock on
- [ ] The tray tooltip shows the target and the lock state; a balloon appears once, not repeatedly
- [ ] **Num Lock off alone raises no warning until a numpad key is used on that host**
- [ ] With two hosts connected, each reports its **own** lock state — not the other's
- [ ] In broadcast mode, Num Lock on at one host does not hide a Num Lock-off warning for another host whose numpad has been used
- [ ] Disconnecting a host clears its remembered lock state rather than carrying it over
- [ ] A host that does **not** report lamps still shows a state, labelled `inferred`
- [ ] `Ctrl+Alt+CapsLock` still does not toggle Caps Lock on the host — now directly observable

## 2b. Physical keyboard layouts

Run live capture in both the desktop app and CLI. Keep a text field focused on the receiving
device before typing; do not test against its Bluetooth settings screen. Scripted `type`
still requires the receiver's US layout and is not a layout-capture test.

- [ ] German Windows + German ISO keyboard + iPhone German **Hardware Keyboard** setting:
      verify Y/Z, umlauts, sharp S, punctuation, and the extra key beside left Shift
- [ ] Verify Shift, AltGr combinations (`@`, euro, braces, pipe), and dead-key compositions;
      record any host-specific modifier behavior, including Windows' synthetic Ctrl for AltGr
- [ ] US and UK ANSI/ISO keyboards: verify punctuation, Shift combinations, and the ISO extra key
- [ ] French AZERTY: verify A/Q, Z/W, M, digits with Shift, and punctuation
- [ ] Change the Windows input language while capturing: physical usages remain unchanged
- [ ] Change the receiver's hardware layout: output follows that layout, not the Windows layout
- [ ] Switch targets and broadcast to two layouts: each host interprets the same key positions
- [ ] Confirm key releases, Caps Lock, numpad/navigation distinction, and all capture hotkeys
- [ ] Test with an existing bond: no forget/re-pair should be required, because GATT is unchanged

## 3. Capture mode (CLI)

- [ ] `capture` refuses to arm when no host is subscribed
- [ ] Once armed, local keyboard input goes to the host and **not** to Windows
- [ ] Mouse movement is redirected too, not just the keyboard
- [ ] `Ctrl+D+C` switches to the next host; the switch is visible in the log
- [ ] `Ctrl+Alt+CapsLock` cycles the same targets without toggling Caps Lock on either device
- [ ] Holding Caps Lock in the new chord cycles only once; releasing and pressing it cycles again
- [ ] `Ctrl+Alt+Q` ends the session and returns input to the PC
- [ ] After `Ctrl+Alt+Q`, the local keyboard works normally again

## 3a. Shake to switch

Off by default. Turn it on with `gesture on` or the Settings toggle.

- [ ] `gesture` with no argument reports the current state and tuning
- [ ] `gesture on` / `gesture off` persist, and `gesture` reflects the change
- [ ] Toggling preserves hand-edited `MinStrokePx` / `RequiredReversals` / `WindowMs`
- [ ] Shaking the pointer rapidly left-right cycles the target, logged like the hotkey
- [ ] Shaking up-down works too
- [ ] **Ordinary mouse use never triggers it** — spend a few minutes working normally
- [ ] Slamming the pointer into a screen corner does not trigger it
- [ ] One long shake fires once, not repeatedly
- [ ] Shaking while a mouse button is held does not leave a drag stuck on the old host
- [ ] Both cycle hotkeys also release held mouse buttons on the outgoing host before switching
- [ ] Holding a mouse button on **This PC**, shaking to a host, then releasing it leaves no local drag stuck after returning
- [ ] Editing `switch-gesture.json` applies to a **running** session without a restart
- [ ] The same edit reaches a running **background** instance, which is a separate process
- [ ] With Settings open, enabling or disabling the gesture in the config file updates the toggle without clicking it
- [ ] Leaving Settings, editing the config, then returning shows the current value
- [ ] Out-of-range values are clamped and the clamping is logged
- [ ] With the gesture off, shaking does nothing

## 4. Capture mode (UI)

- [ ] Start/Stop capture buttons follow the actual session state
- [ ] `Ctrl+Alt+Q` un-ticks the capture toggle (hotkey and UI stay in sync)
- [ ] Selecting a target in the UI actually changes where input goes
- [ ] `Ctrl+D+C` updates the UI's selected radio button
- [ ] `Ctrl+Alt+CapsLock` updates the selected target, including from **This PC**
- [ ] Switching away from **This PC** with `Ctrl+Alt+CapsLock` and back with `Ctrl+D+C` leaves neither Alt nor Ctrl pressed locally
- [ ] Selecting **This PC** keeps capture armed but returns input locally
- [ ] A host that drops while selected shows "no longer subscribed" rather than silently failing
- [ ] Disconnecting the selected host immediately returns keyboard and mouse input to this PC
- [ ] Reconnecting that host leaves the target on **This PC** until explicitly selected again
- [ ] `Ctrl+Alt+Q` returns input to this PC immediately even while the selected host is unreachable

## 5. Latency

Measured on Android, historically the worst case.

- [ ] Typing feels immediate, with no perceptible lag per keystroke
- [ ] Pointer movement is smooth, not stepped
- [ ] Sustained typing does not degrade over ~30 seconds
- [ ] `capture verbose` shows no growing per-report send time

## 6. Tray and residency

- [ ] `--tray` starts hidden, with no window
- [ ] Tray icon menu opens the window and exits the app
- [ ] **Resident capture works with no window ever shown** — keyboard reaches the host
- [ ] **Mouse is redirected in tray mode**, not just the keyboard
- [ ] Closing the window with close-to-tray on hides rather than exits
- [ ] Autostart entry launches the app hidden after a reboot

## 7. Handover (the parts needing a click)

`Invoke-HandoverTests.ps1` covers the rest.

- [ ] With background mode running, launching the app shows the takeover prompt
- [ ] Choosing **Yes** stops background mode and the app takes the radio
- [ ] Choosing **No** leaves background mode running and the app does not start
- [ ] The prompt wording names both the tray app and the CLI service

## 8. Reconnect

- [ ] Locking and unlocking the PC leaves the host connected
- [ ] Sleep/resume: host reconnects, or the failure is logged clearly
- [ ] Toggling Bluetooth off/on on the phone reconnects
- [ ] An `Aborted` **after** `Started` is logged as advertising having stopped

## 9. Plain mode — NEVER TESTED

`--plain` / the UI's "Require encryption" toggle have never been exercised against a real host.
Treat every line here as unproven.

- [ ] `--plain` starts and reports `Protection level: Plain`
- [ ] A phone can pair without bonding
- [ ] Input actually reaches the host unencrypted
- [ ] The UI toggle is disabled while running and takes effect after a stop/start
- [ ] Known limitation: the toggle is not persisted, so a tray/autostart launch is always
      encrypted. Confirm whether that blocks any real use before shipping plain mode.

## 10. Logs

- [ ] All three files land in `%LOCALAPPDATA%\BleHid\logs\`
- [ ] A UI crash is written to `blehid-app.log`
- [ ] `--diagnose` output is complete enough to triage a stranger's bug report
- [ ] Background log rotates rather than growing past 5 MB

## 10a. Languages

Run with the Windows display language set to English, then switch languages from the app.

- [ ] With no language chosen, the app follows the Windows display language
- [ ] **Settings → Language** lists Windows display language, Deutsch, English and
      中文（简体）, each in its own script, and a screen reader announces those names
- [ ] Choosing Deutsch, then restarting, shows every page, the tray menu, the tray balloon
      and the already-running prompt in German, with no clipped or overlapping text
- [ ] The same for 中文（简体）, including the Caps Lock warning bar and tray tooltip
- [ ] Changing the language does **not** restart the peripheral or disconnect hosts
- [ ] The Status page log, `--diagnose`, and the background log stay in English
- [ ] `BleHid.Cli.exe --lang zh-Hans` shows readable Chinese help in Windows Terminal and
      conhost, with aligned `status` columns, and the console code page is restored on exit
- [ ] `BLEHID_LANG=de` gives German CLI messages; commands are still typed in English
- [ ] The Android companion follows the phone's language, including its notification
- [ ] A test issue from the **Translation** form gets a bot reply; adding
      `translation-approved` opens a pull request crediting the issue author

## 11. Android companion

- [ ] Fresh install opens without a crash and shows **No computer associated**
- [ ] **Allow nearby devices** grants scan, connect, and notification permissions
- [ ] **Find computer** opens Android's Companion Device picker without an exception
- [ ] Selecting the advertising PC creates an association and shows the selected computer
- [ ] Enabling **Connection monitor** starts an ongoing low-importance notification
- [ ] GATT reaches **Ready** and reports HID service, Report count, and Database Hash state
- [ ] Windows observes no additional keyboard or mouse report subscriber from the companion
- [ ] Force-stopping and reopening the app restores the association and monitoring setting
- [ ] An unreachable computer leaves **Connecting** through timeout and bounded retry
- [ ] **Stop** in the notification disables monitoring and removes the foreground service
- [ ] **Share diagnostics** includes the visible timeline and no account or unrelated device data
