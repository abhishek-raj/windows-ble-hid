package dev.blehid.companion.bluetooth

import org.junit.Assert.assertEquals
import org.junit.Test

class BluetoothAddressTest {
    @Test
    fun `normalizes companion device addresses for BluetoothAdapter`() {
        assertEquals("01:23:45:67:89:AB", "01:23:45:67:89:ab".normalizeBluetoothAddress())
    }
}