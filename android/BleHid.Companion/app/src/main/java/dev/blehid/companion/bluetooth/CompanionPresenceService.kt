package dev.blehid.companion.bluetooth

import android.companion.AssociationInfo
import android.companion.CompanionDeviceService
import android.companion.DevicePresenceEvent
import android.os.Build
import androidx.annotation.RequiresApi
import dev.blehid.companion.CompanionApplication

class CompanionPresenceService : CompanionDeviceService() {
    private val repository: CompanionRepository
        get() = (application as CompanionApplication).repository

    @Deprecated("Required for Android 13-15 companion presence delivery")
    override fun onDeviceAppeared(associationInfo: AssociationInfo) {
        handleAppeared()
    }

    @Deprecated("Android 13 dispatches the association-aware callback through this compatibility method")
    override fun onDeviceAppeared(address: String) {
        handleAppeared()
    }

    @Deprecated("Required for Android 13-15 companion presence delivery")
    override fun onDeviceDisappeared(associationInfo: AssociationInfo) {
        handleDisappeared()
    }

    @Deprecated("Android 13 dispatches the association-aware callback through this compatibility method")
    override fun onDeviceDisappeared(address: String) {
        handleDisappeared()
    }

    @RequiresApi(Build.VERSION_CODES.BAKLAVA)
    override fun onDevicePresenceEvent(event: DevicePresenceEvent) {
        when (event.event) {
            DevicePresenceEvent.EVENT_BLE_APPEARED,
            DevicePresenceEvent.EVENT_BT_CONNECTED,
            DevicePresenceEvent.EVENT_SELF_MANAGED_APPEARED -> handleAppeared()

            DevicePresenceEvent.EVENT_BLE_DISAPPEARED,
            DevicePresenceEvent.EVENT_BT_DISCONNECTED,
            DevicePresenceEvent.EVENT_SELF_MANAGED_DISAPPEARED -> handleDisappeared()

            DevicePresenceEvent.EVENT_ASSOCIATION_REMOVED -> repository.refresh()
        }
    }

    private fun handleAppeared() {
        repository.refresh()
        if (repository.state.value.presence == DevicePresence.Present) return
        repository.onPresenceChanged(DevicePresence.Present)
        if (repository.state.value.monitoringEnabled) {
            BluetoothMonitorService.devicePresent(this)
        }
    }

    private fun handleDisappeared() {
        if (repository.state.value.presence == DevicePresence.Away) return
        repository.onPresenceChanged(DevicePresence.Away)
        if (repository.state.value.monitoringEnabled) {
            BluetoothMonitorService.deviceAway(this)
        }
    }
}