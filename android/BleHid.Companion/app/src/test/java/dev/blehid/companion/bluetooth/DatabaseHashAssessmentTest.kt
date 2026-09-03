package dev.blehid.companion.bluetooth

import org.junit.Assert.assertEquals
import org.junit.Test

class DatabaseHashAssessmentTest {
    @Test
    fun `reports unavailable when server exposes no hash`() {
        assertEquals(HashAssessment.Unavailable, assessDatabaseHash("old", null))
    }

    @Test
    fun `records the first observed hash`() {
        assertEquals(HashAssessment.FirstSeen, assessDatabaseHash(null, "abc"))
    }

    @Test
    fun `recognizes an unchanged database`() {
        assertEquals(HashAssessment.Unchanged, assessDatabaseHash("abc", "abc"))
    }

    @Test
    fun `recognizes a changed database`() {
        assertEquals(HashAssessment.Changed, assessDatabaseHash("abc", "def"))
    }
}