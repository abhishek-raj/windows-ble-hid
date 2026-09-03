package dev.blehid.companion.bluetooth

import android.Manifest
import android.annotation.SuppressLint
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothGatt
import android.bluetooth.BluetoothGattCallback
import android.bluetooth.BluetoothGattCharacteristic
import android.bluetooth.BluetoothProfile
import android.content.Context
import android.content.pm.PackageManager
import android.os.Handler
import android.os.Looper
import androidx.core.content.ContextCompat
import java.util.UUID

internal class GattMonitor(
    private val context: Context,
    private val repository: CompanionRepository,
    private val retryPolicy: RetryPolicy = RetryPolicy(),
) {
    private val handler = Handler(Looper.getMainLooper())
    private var target: BluetoothDevice? = null
    private var currentGatt: BluetoothGatt? = null
    private var attempt = 0
    private var active = false
    private var timeoutRunnable: Runnable? = null

    private val connectRunnable = Runnable { connect() }

    fun start(device: BluetoothDevice) {
        stopConnection()
        target = device
        attempt = 0
        active = true
        connect()
    }

    fun retryNow() {
        if (target == null) return
        stopConnection()
        attempt = 0
        active = true
        connect()
    }

    fun close() {
        active = false
        target = null
        stopConnection()
        repository.onStopped()
    }

    @SuppressLint("MissingPermission")
    private fun connect() {
        if (!active) return
        if (!hasConnectPermission()) {
            repository.onFailed("Nearby devices permission is required")
            return
        }

        val device = target ?: return
        attempt += 1
        repository.onConnecting(attempt)
        try {
            currentGatt = device.connectGatt(
                context,
                false,
                callback,
                BluetoothDevice.TRANSPORT_LE,
            )
            scheduleTimeout("GATT connection timed out", CONNECTION_TIMEOUT_MILLIS)
        } catch (exception: SecurityException) {
            failOrRetry("Bluetooth permission was denied")
        } catch (exception: IllegalArgumentException) {
            repository.onFailed("The associated Bluetooth address is invalid")
        }
    }

    private val callback = object : BluetoothGattCallback() {
        override fun onConnectionStateChange(gatt: BluetoothGatt, status: Int, newState: Int) {
            handler.post {
                if (gatt !== currentGatt) {
                    closeGatt(gatt)
                    return@post
                }
                when {
                    status == BluetoothGatt.GATT_SUCCESS && newState == BluetoothProfile.STATE_CONNECTED -> {
                        cancelTimeout()
                        repository.onDiscovering()
                        discoverServices(gatt)
                    }
                    newState == BluetoothProfile.STATE_DISCONNECTED -> {
                        cancelTimeout()
                        currentGatt = null
                        closeGatt(gatt)
                        failOrRetry("GATT disconnected (status $status)")
                    }
                    status != BluetoothGatt.GATT_SUCCESS -> {
                        cancelTimeout()
                        failOrRetry("GATT connection failed (status $status, state $newState)")
                    }
                }
            }
        }

        override fun onServicesDiscovered(gatt: BluetoothGatt, status: Int) {
            handler.post {
                if (gatt !== currentGatt) return@post
                cancelTimeout()
                if (status != BluetoothGatt.GATT_SUCCESS) {
                    failOrRetry("Service discovery failed (status $status)")
                    return@post
                }
                inspectDatabase(gatt)
            }
        }

        override fun onCharacteristicRead(
            gatt: BluetoothGatt,
            characteristic: BluetoothGattCharacteristic,
            value: ByteArray,
            status: Int,
        ) {
            handler.post { handleCharacteristicRead(gatt, characteristic, value, status) }
        }

        @Deprecated("Required for Android 12")
        override fun onCharacteristicRead(
            gatt: BluetoothGatt,
            characteristic: BluetoothGattCharacteristic,
            status: Int,
        ) {
            @Suppress("DEPRECATION")
            val value = characteristic.value ?: byteArrayOf()
            handler.post { handleCharacteristicRead(gatt, characteristic, value, status) }
        }

        override fun onServiceChanged(gatt: BluetoothGatt) {
            handler.post {
                if (gatt !== currentGatt) return@post
                repository.onServiceChanged()
                handler.postDelayed({ discoverServices(gatt) }, SERVICE_CHANGED_SETTLE_MILLIS)
            }
        }
    }

    @SuppressLint("MissingPermission")
    private fun discoverServices(gatt: BluetoothGatt) {
        if (!hasConnectPermission() || !gatt.discoverServices()) {
            failOrRetry("Could not start GATT service discovery")
            return
        }
        scheduleTimeout("GATT service discovery timed out", DISCOVERY_TIMEOUT_MILLIS)
    }

    @SuppressLint("MissingPermission")
    private fun inspectDatabase(gatt: BluetoothGatt) {
        val services = gatt.services
        val hidService = services.firstOrNull { it.uuid == HID_SERVICE_UUID }
        val reportCount = hidService?.characteristics?.count { it.uuid == HID_REPORT_UUID } ?: 0
        repository.onDatabaseDiscovered(
            serviceCount = services.size,
            hasHidService = hidService != null,
            hidReportCount = reportCount,
        )

        val databaseHash = services
            .firstOrNull { it.uuid == GENERIC_ATTRIBUTE_SERVICE_UUID }
            ?.getCharacteristic(DATABASE_HASH_UUID)
        if (databaseHash == null || !gatt.readCharacteristic(databaseHash)) {
            attempt = 0
            repository.onReady(null)
        } else {
            scheduleTimeout(
                "Database Hash read timed out",
                HASH_READ_TIMEOUT_MILLIS,
                retry = false,
            )
        }
    }

    private fun handleCharacteristicRead(
        gatt: BluetoothGatt,
        characteristic: BluetoothGattCharacteristic,
        value: ByteArray,
        status: Int,
    ) {
        if (gatt !== currentGatt || characteristic.uuid != DATABASE_HASH_UUID) return
        cancelTimeout()
        attempt = 0
        repository.onReady(if (status == BluetoothGatt.GATT_SUCCESS) value else null)
    }

    private fun scheduleTimeout(error: String, delayMillis: Long, retry: Boolean = true) {
        cancelTimeout()
        timeoutRunnable = Runnable {
            timeoutRunnable = null
            if (!active) return@Runnable
            if (retry) {
                failOrRetry(error)
            } else {
                attempt = 0
                repository.onReady(null)
            }
        }.also { handler.postDelayed(it, delayMillis) }
    }

    private fun cancelTimeout() {
        timeoutRunnable?.let(handler::removeCallbacks)
        timeoutRunnable = null
    }

    private fun failOrRetry(error: String) {
        if (!active) return
        stopConnection()
        if (attempt == 0) attempt = 1
        val delay = retryPolicy.delayAfterAttempt(attempt)
        if (delay == null) {
            active = false
            repository.onFailed("$error; automatic retries exhausted")
            return
        }
        repository.onRetryScheduled(attempt, delay, error)
        handler.postDelayed(connectRunnable, delay)
    }

    @SuppressLint("MissingPermission")
    private fun stopConnection() {
        handler.removeCallbacks(connectRunnable)
        cancelTimeout()
        currentGatt?.let { gatt ->
            try {
                gatt.disconnect()
            } catch (_: SecurityException) {
                // Closing still releases this app's GATT client when permission is revoked.
            }
            closeGatt(gatt)
        }
        currentGatt = null
    }

    @SuppressLint("MissingPermission")
    private fun closeGatt(gatt: BluetoothGatt) {
        try {
            gatt.close()
        } catch (_: SecurityException) {
            // The system still releases the client when its process or service exits.
        }
    }

    private fun hasConnectPermission(): Boolean =
        ContextCompat.checkSelfPermission(context, Manifest.permission.BLUETOOTH_CONNECT) ==
            PackageManager.PERMISSION_GRANTED

    private companion object {
        val HID_SERVICE_UUID: UUID = UUID.fromString("00001812-0000-1000-8000-00805f9b34fb")
        val HID_REPORT_UUID: UUID = UUID.fromString("00002a4d-0000-1000-8000-00805f9b34fb")
        val GENERIC_ATTRIBUTE_SERVICE_UUID: UUID = UUID.fromString("00001801-0000-1000-8000-00805f9b34fb")
        val DATABASE_HASH_UUID: UUID = UUID.fromString("00002b2a-0000-1000-8000-00805f9b34fb")
        const val SERVICE_CHANGED_SETTLE_MILLIS = 500L
        const val CONNECTION_TIMEOUT_MILLIS = 15_000L
        const val DISCOVERY_TIMEOUT_MILLIS = 10_000L
        const val HASH_READ_TIMEOUT_MILLIS = 5_000L
    }
}