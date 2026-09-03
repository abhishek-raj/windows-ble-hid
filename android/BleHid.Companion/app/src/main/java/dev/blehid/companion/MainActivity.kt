package dev.blehid.companion

import android.Manifest
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothStatusCodes
import android.companion.AssociationInfo
import android.companion.AssociationRequest
import android.companion.BluetoothLeDeviceFilter
import android.companion.CompanionDeviceManager
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.ParcelUuid
import android.provider.Settings
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.core.content.ContextCompat
import androidx.annotation.RequiresApi
import dev.blehid.companion.bluetooth.BluetoothMonitorService
import dev.blehid.companion.bluetooth.CompanionRepository
import dev.blehid.companion.bluetooth.EventLevel
import dev.blehid.companion.bluetooth.ProfileConnector
import dev.blehid.companion.ui.CompanionApp
import java.util.UUID

class MainActivity : ComponentActivity() {
    private val repository: CompanionRepository
        get() = (application as CompanionApplication).repository

    private val permissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions(),
    ) {
        repository.refresh()
    }

    private val associationLauncher = registerForActivityResult(
        ActivityResultContracts.StartIntentSenderForResult(),
    ) {
        repository.refresh()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            CompanionApp(
                repository = repository,
                onGrantPermissions = ::requestBluetoothPermissions,
                onAssociate = ::associateComputer,
                onMonitoringChange = ::setMonitoring,
                onRetryGatt = { BluetoothMonitorService.retry(this) },
                onConnectProfiles = ::connectProfiles,
                onOpenBluetoothSettings = ::openBluetoothSettings,
                onShareDiagnostics = ::shareDiagnostics,
            )
        }
    }

    override fun onResume() {
        super.onResume()
        repository.refresh()
    }

    private fun requestBluetoothPermissions() {
        val permissions = buildList {
            add(Manifest.permission.BLUETOOTH_SCAN)
            add(Manifest.permission.BLUETOOTH_CONNECT)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                add(Manifest.permission.POST_NOTIFICATIONS)
            }
        }
        permissionLauncher.launch(permissions.toTypedArray())
    }

    private fun associateComputer() {
        if (!hasBluetoothPermissions()) {
            requestBluetoothPermissions()
            return
        }

        val scanFilter = android.bluetooth.le.ScanFilter.Builder()
            .setServiceUuid(ParcelUuid(HID_SERVICE_UUID))
            .build()
        val filter = BluetoothLeDeviceFilter.Builder()
            .setScanFilter(scanFilter)
            .build()
        val request = AssociationRequest.Builder()
            .addDeviceFilter(filter)
            .setSingleDevice(false)
            .build()
        val manager = getSystemService(CompanionDeviceManager::class.java)
        val callback = associationCallback()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            manager.associate(request, mainExecutor, callback)
        } else {
            @Suppress("DEPRECATION")
            manager.associate(request, callback, Handler(mainLooper))
        }
    }

    private fun associationCallback(): CompanionDeviceManager.Callback =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            api33AssociationCallback()
        } else {
            legacyAssociationCallback()
        }

    @RequiresApi(Build.VERSION_CODES.TIRAMISU)
    private fun api33AssociationCallback() = object : CompanionDeviceManager.Callback() {
        override fun onAssociationCreated(associationInfo: AssociationInfo) {
            repository.selectAssociation(associationInfo)
            repository.observePresence()
        }

        override fun onAssociationPending(intentSender: android.content.IntentSender) {
            launchAssociationPicker(intentSender)
        }

        override fun onFailure(error: CharSequence?) {
            recordAssociationFailure(error)
        }
    }

    private fun legacyAssociationCallback() = object : CompanionDeviceManager.Callback() {
        @Deprecated("Android 12 uses this callback")
        override fun onDeviceFound(intentSender: android.content.IntentSender) {
            launchAssociationPicker(intentSender)
        }

        override fun onFailure(error: CharSequence?) {
            recordAssociationFailure(error)
        }
    }

    private fun recordAssociationFailure(error: CharSequence?) {
        repository.record(
            EventLevel.Error,
            error?.toString() ?: "Device association failed",
        )
    }

    private fun launchAssociationPicker(intentSender: android.content.IntentSender) {
        associationLauncher.launch(IntentSenderRequest.Builder(intentSender).build())
    }

    private fun setMonitoring(enabled: Boolean) {
        if (enabled) {
            if (!hasBluetoothPermissions()) {
                requestBluetoothPermissions()
                return
            }
            BluetoothMonitorService.start(this)
        } else {
            BluetoothMonitorService.stop(this)
        }
    }

    private fun connectProfiles() {
        if (!ProfileConnector.isSupported()) {
            openBluetoothSettings()
            return
        }
        val address = repository.state.value.association?.address ?: return
        val adapter = getSystemService(BluetoothManager::class.java).adapter
        val device = runCatching { adapter.getRemoteDevice(address) }.getOrElse {
            repository.record(EventLevel.Error, "The associated Bluetooth address is invalid")
            return
        }
        ProfileConnector.connect(device)
            .onSuccess { status ->
                if (status == BluetoothStatusCodes.SUCCESS) {
                    repository.record(EventLevel.Info, "Bluetooth profile reconnect requested")
                } else {
                    repository.record(EventLevel.Error, "Profile reconnect was rejected (status $status)")
                }
            }
            .onFailure { error ->
                repository.record(EventLevel.Error, error.message ?: "Profile reconnect failed")
            }
    }

    private fun openBluetoothSettings() {
        startActivity(Intent(Settings.ACTION_BLUETOOTH_SETTINGS))
    }

    private fun shareDiagnostics() {
        val sendIntent = Intent(Intent.ACTION_SEND).apply {
            type = "text/plain"
            putExtra(Intent.EXTRA_SUBJECT, "BleHid Companion diagnostics")
            putExtra(Intent.EXTRA_TEXT, repository.diagnosticReport())
        }
        startActivity(Intent.createChooser(sendIntent, getString(R.string.share_diagnostics)))
    }

    private fun hasBluetoothPermissions(): Boolean =
        ContextCompat.checkSelfPermission(this, Manifest.permission.BLUETOOTH_CONNECT) ==
            PackageManager.PERMISSION_GRANTED &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.BLUETOOTH_SCAN) ==
            PackageManager.PERMISSION_GRANTED

    private companion object {
        val HID_SERVICE_UUID: UUID = UUID.fromString("00001812-0000-1000-8000-00805f9b34fb")
    }
}