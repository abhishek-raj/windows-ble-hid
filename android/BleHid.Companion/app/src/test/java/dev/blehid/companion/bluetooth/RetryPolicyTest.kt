package dev.blehid.companion.bluetooth

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class RetryPolicyTest {
    private val policy = RetryPolicy()

    @Test
    fun `uses bounded exponential delays`() {
        assertEquals(2_000L, policy.delayAfterAttempt(1))
        assertEquals(4_000L, policy.delayAfterAttempt(2))
        assertEquals(8_000L, policy.delayAfterAttempt(3))
        assertEquals(16_000L, policy.delayAfterAttempt(4))
        assertEquals(30_000L, policy.delayAfterAttempt(5))
        assertNull(policy.delayAfterAttempt(6))
    }

    @Test
    fun `allows the initial connection plus five retries`() {
        assertEquals(6, policy.maxAttempts)
    }
}