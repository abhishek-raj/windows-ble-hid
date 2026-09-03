package dev.blehid.companion

import android.app.Application
import dev.blehid.companion.bluetooth.CompanionRepository

class CompanionApplication : Application() {
    lateinit var repository: CompanionRepository
        private set

    override fun onCreate() {
        super.onCreate()
        repository = CompanionRepository(this)
        repository.refresh()
    }
}