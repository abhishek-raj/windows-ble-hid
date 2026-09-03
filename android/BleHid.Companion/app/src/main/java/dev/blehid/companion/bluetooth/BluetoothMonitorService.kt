package dev.blehid.companion.bluetooth

import android.Manifest
import android.annotation.SuppressLint
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.bluetooth.BluetoothManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.os.IBinder
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import dev.blehid.companion.CompanionApplication
import dev.blehid.companion.MainActivity
import dev.blehid.companion.R

class BluetoothMonitorService : Service() {
    private val repository: CompanionRepository
        get() = (application as CompanionApplication).repository

    private lateinit var monitor: GattMonitor

    override fun onCreate() {
        super.onCreate()
        Log.i(TAG, "created")
        createNotificationChannel()
        monitor = GattMonitor(this, repository)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        Log.i(TAG, "start action=${intent?.action ?: ACTION_START}")
        try {
            startInForeground()
        } catch (exception: RuntimeException) {
            Log.e(TAG, "foreground start rejected", exception)
            repository.onFailed("Android did not allow Bluetooth monitoring: ${exception.message}")
            stopSelfResult(startId)
            return START_NOT_STICKY
        }
        when (intent?.action ?: ACTION_START) {
            ACTION_START, ACTION_DEVICE_PRESENT, ACTION_RETRY -> startMonitoring()
            ACTION_DEVICE_AWAY -> pauseForAbsence()
            ACTION_STOP -> stopMonitoring()
        }
        return START_STICKY
    }

    override fun onDestroy() {
        Log.i(TAG, "destroyed")
        monitor.close()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    @SuppressLint("MissingPermission")
    private fun startMonitoring() {
        repository.refresh()
        val association = repository.state.value.association
        if (association == null) {
            Log.w(TAG, "stopping: no association")
            repository.onFailed("Associate a BleHid computer before starting monitoring")
            stopSelf()
            return
        }
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.BLUETOOTH_CONNECT) !=
            PackageManager.PERMISSION_GRANTED
        ) {
            Log.w(TAG, "stopping: Bluetooth permission missing")
            repository.onFailed("Nearby devices permission is required")
            stopSelf()
            return
        }

        repository.setMonitoringEnabled(true)
        repository.observePresence()
        val adapter = getSystemService(BluetoothManager::class.java).adapter
        val device = association.bluetoothDevice ?: try {
            adapter.getRemoteDevice(association.address)
        } catch (exception: IllegalArgumentException) {
            Log.e(TAG, "stopping: invalid associated address")
            repository.onFailed("The associated Bluetooth address is invalid")
            stopSelf()
            return
        }
        Log.i(TAG, "starting GATT monitor")
        monitor.start(device)
    }

    private fun pauseForAbsence() {
        Log.i(TAG, "pausing: associated device away")
        monitor.close()
        stopSelf()
    }

    private fun stopMonitoring() {
        Log.i(TAG, "stopping: user request")
        repository.setMonitoringEnabled(false)
        monitor.close()
        stopSelf()
    }

    private fun startInForeground() {
        ServiceCompat.startForeground(
            this,
            NOTIFICATION_ID,
            buildNotification(),
            ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE,
        )
    }

    private fun buildNotification(): Notification {
        val contentIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        val stopIntent = PendingIntent.getService(
            this,
            1,
            Intent(this, BluetoothMonitorService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        return NotificationCompat.Builder(this, NOTIFICATION_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_ble_hid)
            .setContentTitle(getString(R.string.monitor_notification_title))
            .setContentText(getString(R.string.monitor_notification_text))
            .setContentIntent(contentIntent)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .addAction(0, getString(R.string.stop_monitoring), stopIntent)
            .build()
    }

    private fun createNotificationChannel() {
        val channel = NotificationChannel(
            NOTIFICATION_CHANNEL_ID,
            getString(R.string.monitor_notification_channel),
            NotificationManager.IMPORTANCE_LOW,
        )
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    companion object {
        private const val ACTION_START = "dev.blehid.companion.action.START"
        private const val ACTION_STOP = "dev.blehid.companion.action.STOP"
        private const val ACTION_RETRY = "dev.blehid.companion.action.RETRY"
        private const val ACTION_DEVICE_PRESENT = "dev.blehid.companion.action.DEVICE_PRESENT"
        private const val ACTION_DEVICE_AWAY = "dev.blehid.companion.action.DEVICE_AWAY"
        private const val NOTIFICATION_CHANNEL_ID = "blehid_connection"
        private const val NOTIFICATION_ID = 1001
        private const val TAG = "BleHidMonitor"

        fun start(context: Context) = sendForegroundCommand(context, ACTION_START)
        fun stop(context: Context) = sendForegroundCommand(context, ACTION_STOP)
        fun retry(context: Context) = sendForegroundCommand(context, ACTION_RETRY)
        fun devicePresent(context: Context) = sendForegroundCommand(context, ACTION_DEVICE_PRESENT)
        fun deviceAway(context: Context) = sendForegroundCommand(context, ACTION_DEVICE_AWAY)

        private fun sendForegroundCommand(context: Context, action: String) {
            val intent = Intent(context, BluetoothMonitorService::class.java).setAction(action)
            try {
                ContextCompat.startForegroundService(context, intent)
            } catch (exception: RuntimeException) {
                val application = context.applicationContext as? CompanionApplication
                application?.repository?.onFailed(
                    "Android did not allow Bluetooth monitoring: ${exception.message}",
                )
            }
        }
    }
}