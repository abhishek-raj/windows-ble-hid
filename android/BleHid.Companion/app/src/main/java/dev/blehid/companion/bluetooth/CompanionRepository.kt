package dev.blehid.companion.bluetooth

import android.Manifest
import android.annotation.SuppressLint
import android.app.Application
import android.bluetooth.BluetoothDevice
import android.companion.AssociationInfo
import android.companion.CompanionDeviceManager
import android.content.pm.PackageManager
import android.os.Build
import androidx.annotation.RequiresApi
import androidx.core.content.ContextCompat
import java.util.concurrent.atomic.AtomicLong
import java.util.Locale
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

class CompanionRepository(private val application: Application) {
    private val companionDeviceManager = application.getSystemService(CompanionDeviceManager::class.java)
    private val preferences = CompanionPreferences(application)
    private val nextEventId = AtomicLong()
    private val mutableState = MutableStateFlow(
        CompanionState(monitoringEnabled = preferences.monitoringEnabled),
    )

    val state: StateFlow<CompanionState> = mutableState.asStateFlow()

    fun refresh() {
        val permissionsGranted = hasBluetoothPermissions()
        val association = findAssociation()
        mutableState.update {
            it.copy(
                permissionsGranted = permissionsGranted,
                association = association,
                monitoringEnabled = preferences.monitoringEnabled,
            )
        }

        if (association != null && preferences.selectedAddress != association.address) {
            preferences.selectedAddress = association.address
        }
    }

    fun setMonitoringEnabled(enabled: Boolean) {
        preferences.monitoringEnabled = enabled
        mutableState.update { it.copy(monitoringEnabled = enabled) }
    }

    @RequiresApi(Build.VERSION_CODES.TIRAMISU)
    fun selectAssociation(info: AssociationInfo) {
        val address = info.deviceMacAddress?.toString()?.normalizeBluetoothAddress() ?: return
        preferences.selectedAddress = address
        mutableState.update {
            it.copy(
                association = DeviceAssociation(
                    id = info.id,
                    address = address,
                    displayName = info.displayName?.toString(),
                    bluetoothDevice = info.associatedBluetoothDevice(),
                ),
            )
        }
        record(EventLevel.Info, "Associated with ${info.displayName ?: address}")
    }

    @Suppress("DEPRECATION")
    fun observePresence() {
        val address = state.value.association?.address ?: return
        try {
            companionDeviceManager.startObservingDevicePresence(address)
            record(EventLevel.Info, "Presence monitoring enabled")
        } catch (exception: IllegalStateException) {
            record(EventLevel.Warning, "Presence monitoring is already active")
        } catch (exception: SecurityException) {
            record(EventLevel.Error, "Presence monitoring permission was denied")
        }
    }

    fun onPresenceChanged(presence: DevicePresence) {
        mutableState.update { it.copy(presence = presence) }
        record(
            EventLevel.Info,
            if (presence == DevicePresence.Present) "Associated computer is nearby"
            else "Associated computer is no longer nearby",
        )
    }

    fun onConnecting(attempt: Int) {
        mutableState.update {
            it.copy(
                connection = GattConnection.Connecting,
                attempt = attempt,
                retryInSeconds = null,
                lastError = null,
            )
        }
        record(EventLevel.Info, "Opening GATT connection (attempt $attempt)")
    }

    fun onDiscovering() {
        mutableState.update {
            it.copy(connection = GattConnection.Discovering, retryInSeconds = null)
        }
        record(EventLevel.Info, "Connected; discovering GATT services")
    }

    fun onDatabaseDiscovered(serviceCount: Int, hasHidService: Boolean, hidReportCount: Int) {
        mutableState.update {
            it.copy(
                database = it.database.copy(
                    serviceCount = serviceCount,
                    hasHidService = hasHidService,
                    hidReportCount = hidReportCount,
                ),
            )
        }
        val hidSummary = if (hasHidService) "$hidReportCount HID reports" else "HID service missing"
        record(EventLevel.Info, "$serviceCount services discovered; $hidSummary")
    }

    fun onReady(databaseHash: ByteArray?) {
        val address = state.value.association?.address
        val hash = databaseHash?.toHexString()
        val previousHash = if (address != null) preferences.databaseHash(address) else null
        val assessment = assessDatabaseHash(previousHash, hash)

        if (address != null && hash != null) preferences.setDatabaseHash(address, hash)
        mutableState.update {
            it.copy(
                connection = GattConnection.Ready,
                attempt = 0,
                retryInSeconds = null,
                database = it.database.copy(databaseHash = hash, hashAssessment = assessment),
                lastError = null,
            )
        }
        record(EventLevel.Info, hashMessage(assessment))
    }

    fun onServiceChanged() {
        mutableState.update {
            it.copy(
                database = it.database.copy(
                    serviceChangedCount = it.database.serviceChangedCount + 1,
                ),
            )
        }
        record(EventLevel.Warning, "Service Changed received; rediscovering the database")
    }

    fun onRetryScheduled(attempt: Int, delayMillis: Long, error: String) {
        mutableState.update {
            it.copy(
                connection = GattConnection.WaitingToRetry,
                attempt = attempt,
                retryInSeconds = (delayMillis / 1_000).toInt(),
                lastError = error,
            )
        }
        record(EventLevel.Warning, "$error; retrying in ${delayMillis / 1_000}s")
    }

    fun onFailed(error: String) {
        mutableState.update {
            it.copy(
                connection = GattConnection.Failed,
                retryInSeconds = null,
                lastError = error,
            )
        }
        record(EventLevel.Error, error)
    }

    fun onStopped() {
        mutableState.update {
            it.copy(
                connection = GattConnection.Idle,
                attempt = 0,
                retryInSeconds = null,
            )
        }
    }

    fun diagnosticReport(): String {
        val current = state.value
        return buildString {
            appendLine("BleHid Companion diagnostics")
            appendLine("Device: ${current.association?.displayName ?: "Unknown"}")
            appendLine("Address: ${current.association?.address ?: "Not associated"}")
            appendLine("Presence: ${current.presence}")
            appendLine("GATT: ${current.connection}")
            appendLine("HID service: ${current.database.hasHidService}")
            appendLine("HID reports: ${current.database.hidReportCount}")
            appendLine("Database hash: ${current.database.databaseHash ?: "Unavailable"}")
            appendLine("Hash assessment: ${current.database.hashAssessment}")
            appendLine("Service Changed count: ${current.database.serviceChangedCount}")
            appendLine()
            appendLine("Timeline:")
            current.events.forEach { event ->
                appendLine("${event.timestampMillis}\t${event.level}\t${event.message}")
            }
        }
    }

    fun record(level: EventLevel, message: String) {
        val event = DiagnosticEvent(
            id = nextEventId.incrementAndGet(),
            timestampMillis = System.currentTimeMillis(),
            level = level,
            message = message,
        )
        mutableState.update { it.copy(events = (listOf(event) + it.events).take(MAX_EVENTS)) }
    }

    @SuppressLint("MissingPermission")
    private fun findAssociation(): DeviceAssociation? {
        val selectedAddress = preferences.selectedAddress
        val associations = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            companionDeviceManager.myAssociations.mapNotNull { info ->
                val address = info.deviceMacAddress?.toString()?.normalizeBluetoothAddress()
                    ?: return@mapNotNull null
                DeviceAssociation(
                    id = info.id,
                    address = address,
                    displayName = info.displayName?.toString(),
                    bluetoothDevice = info.associatedBluetoothDevice(),
                )
            }
        } else {
            @Suppress("DEPRECATION")
            companionDeviceManager.associations.map { address ->
                DeviceAssociation(null, address.normalizeBluetoothAddress(), null)
            }
        }
        return associations.firstOrNull {
            it.address.equals(selectedAddress, ignoreCase = true)
        } ?: associations.firstOrNull()
    }

    private fun hasBluetoothPermissions(): Boolean =
        ContextCompat.checkSelfPermission(application, Manifest.permission.BLUETOOTH_CONNECT) ==
            PackageManager.PERMISSION_GRANTED &&
            ContextCompat.checkSelfPermission(application, Manifest.permission.BLUETOOTH_SCAN) ==
            PackageManager.PERMISSION_GRANTED

    private fun hashMessage(assessment: HashAssessment): String = when (assessment) {
        HashAssessment.Unavailable -> "GATT database ready; Database Hash is unavailable"
        HashAssessment.FirstSeen -> "GATT database ready; Database Hash saved"
        HashAssessment.Unchanged -> "GATT database ready; Database Hash is unchanged"
        HashAssessment.Changed -> "GATT database ready; Database Hash changed"
    }

    private fun ByteArray.toHexString(): String = joinToString("") { byte -> "%02x".format(byte) }

    private companion object {
        const val MAX_EVENTS = 100
    }
}

internal fun assessDatabaseHash(previousHash: String?, currentHash: String?): HashAssessment = when {
    currentHash == null -> HashAssessment.Unavailable
    previousHash == null -> HashAssessment.FirstSeen
    previousHash == currentHash -> HashAssessment.Unchanged
    else -> HashAssessment.Changed
}

internal fun String.normalizeBluetoothAddress(): String = uppercase(Locale.ROOT)

private fun AssociationInfo.associatedBluetoothDevice(): BluetoothDevice? =
    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
        associatedBluetoothDeviceApi34()
    } else {
        null
    }

@RequiresApi(Build.VERSION_CODES.UPSIDE_DOWN_CAKE)
private fun AssociationInfo.associatedBluetoothDeviceApi34() =
    associatedDevice?.bleDevice?.device ?: associatedDevice?.bluetoothDevice