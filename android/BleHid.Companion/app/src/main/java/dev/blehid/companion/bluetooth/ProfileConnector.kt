package dev.blehid.companion.bluetooth

import android.annotation.SuppressLint
import android.bluetooth.BluetoothDevice
import android.os.Build

object ProfileConnector {
    const val PROFILE_CONNECT_API_LEVEL = 37

    fun isSupported(): Boolean = Build.VERSION.SDK_INT >= PROFILE_CONNECT_API_LEVEL

    @SuppressLint("MissingPermission")
    fun connect(device: BluetoothDevice): Result<Int> {
        if (!isSupported()) {
            return Result.failure(UnsupportedOperationException("Profile reconnect requires Android 17"))
        }
        return runCatching {
            device.javaClass.getMethod("connect").invoke(device) as Int
        }
    }
}