package dev.blehid.companion.bluetooth

import android.bluetooth.BluetoothDevice

data class CompanionState(
    val permissionsGranted: Boolean = false,
    val association: DeviceAssociation? = null,
    val monitoringEnabled: Boolean = false,
    val presence: DevicePresence = DevicePresence.Unknown,
    val connection: GattConnection = GattConnection.Idle,
    val attempt: Int = 0,
    val retryInSeconds: Int? = null,
    val database: GattDatabase = GattDatabase(),
    val lastError: String? = null,
    val events: List<DiagnosticEvent> = emptyList(),
)

data class DeviceAssociation(
    val id: Int?,
    val address: String,
    val displayName: String?,
    val bluetoothDevice: BluetoothDevice? = null,
)

enum class DevicePresence {
    Unknown,
    Present,
    Away,
}

enum class GattConnection {
    Idle,
    Connecting,
    Discovering,
    Ready,
    WaitingToRetry,
    Failed,
}

data class GattDatabase(
    val serviceCount: Int = 0,
    val hasHidService: Boolean = false,
    val hidReportCount: Int = 0,
    val databaseHash: String? = null,
    val hashAssessment: HashAssessment = HashAssessment.Unavailable,
    val serviceChangedCount: Int = 0,
)

enum class HashAssessment {
    Unavailable,
    FirstSeen,
    Unchanged,
    Changed,
}

data class DiagnosticEvent(
    val id: Long,
    val timestampMillis: Long,
    val level: EventLevel,
    val message: String,
)

enum class EventLevel {
    Info,
    Warning,
    Error,
}