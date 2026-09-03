package dev.blehid.companion.bluetooth

internal class RetryPolicy(
    private val delaysMillis: List<Long> = listOf(2_000, 4_000, 8_000, 16_000, 30_000),
) {
    val maxAttempts: Int = delaysMillis.size + 1

    fun delayAfterAttempt(attempt: Int): Long? = delaysMillis.getOrNull(attempt - 1)
}