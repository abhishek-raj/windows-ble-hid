# BleHid Companion for Android

An experimental Android 12+ companion for the BleHid Windows peripheral. It monitors a
separate app-owned BLE GATT connection, records HOGP/database state, and exposes the
recovery actions Android allows to a normal application.

## Build

Requirements:

- Android Studio with JDK 17 or later
- Android SDK 36.1 and Build Tools 36.0.0

From this directory:

```powershell
.\gradlew.bat :app:testDebugUnitTest :app:lintDebug :app:assembleDebug
.\gradlew.bat :app:installDebug
```

The APK is written to `app/build/outputs/apk/debug/app-debug.apk`.

## Use

1. Start BleHid on the Windows computer so it advertises the HID service.
2. Pair the computer as an input device in Android's Bluetooth settings if it is not
   already paired.
3. Open BleHid Companion and grant Nearby devices permission.
4. Choose **Find computer** and select the advertising BleHid computer.
5. Enable **Connection monitor**.

The foreground service keeps an application GATT client active, watches presence, and
retries transient failures. Connection and service-discovery operations time out instead
of waiting indefinitely for a missing Android callback. **Retry GATT** restarts only that
diagnostic connection.
**Connect input** requests reconnection of enabled Bluetooth profiles on Android 17 or
later. Android 12-16 instead open Bluetooth settings for manual connection.

## Measured reconnect behavior

On the Android 16 reference phone, two consecutive Windows provider restarts recovered
automatically while monitoring was enabled. Companion Device Manager detected the returning
advertisement and started the app-owned GATT connection; Android HID Host then opened HOGP,
reread HID Information and Report Map, and restored both keyboard and mouse report CCCDs.
Windows showed one subscriber on each report without a manual Connect tap.

This is an experimental ordering workaround, not direct control over HID Host. Samsung
closes the companion's own GATT client after presence settles, while system HOGP remains
connected. Results may differ across Android versions and vendors, so manual Connect and
re-pairing remain fallback recovery steps.

## What it observes

- GATT connection and service-discovery state
- HID service `0x1812` presence and Report `0x2A4D` characteristic count
- Database Hash `0x2B2A`, compared with the last value for the associated device
- Service Changed callbacks received by the app-owned client
- companion-device presence transitions and bounded retry attempts

Diagnostics remain on the phone in application preferences and process memory. They leave
the app only when **Share diagnostics** is used. The exported report includes the associated
Bluetooth address, so review it before posting publicly.

## Deliberate limitations

This app cannot repair Android 16's system HID Host cache or force its HID profile to
reconnect. Those APIs require privileged platform permission. It also does not subscribe
to keyboard or mouse report characteristics: doing so would attach reports to this app,
not to Android's system input path, and would create a misleading Windows subscriber.

An app-owned GATT connection being ready proves only that generic BLE communication works.
It does not prove that Android opened HOGP, created its UHID device, or restored the bonded
HID report CCCDs on Windows.

Clipboard transport is intentionally out of scope for this version.

## Project structure

- `MainActivity` owns permission, association, recovery, and diagnostic-share launchers.
- `CompanionRepository` is the process state and persisted-setting boundary.
- `CompanionPresenceService` receives Companion Device presence callbacks.
- `BluetoothMonitorService` owns the foreground-service lifecycle.
- `GattMonitor` owns one serialized GATT client and its retry policy.
- `CompanionScreen` renders state and emits user intents without Bluetooth dependencies.
