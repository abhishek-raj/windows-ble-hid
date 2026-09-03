package dev.blehid.companion.bluetooth

import android.content.Context
import androidx.core.content.edit

internal class CompanionPreferences(context: Context) {
    private val preferences = context.getSharedPreferences("companion", Context.MODE_PRIVATE)

    var monitoringEnabled: Boolean
        get() = preferences.getBoolean(KEY_MONITORING_ENABLED, false)
        set(value) = preferences.edit { putBoolean(KEY_MONITORING_ENABLED, value) }

    var selectedAddress: String?
        get() = preferences.getString(KEY_SELECTED_ADDRESS, null)
        set(value) = preferences.edit { putString(KEY_SELECTED_ADDRESS, value) }

    fun databaseHash(address: String): String? =
        preferences.getString("$KEY_DATABASE_HASH_PREFIX$address", null)

    fun setDatabaseHash(address: String, hash: String) {
        preferences.edit { putString("$KEY_DATABASE_HASH_PREFIX$address", hash) }
    }

    private companion object {
        const val KEY_MONITORING_ENABLED = "monitoring_enabled"
        const val KEY_SELECTED_ADDRESS = "selected_address"
        const val KEY_DATABASE_HASH_PREFIX = "database_hash_"
    }
}