package io.github.quaint666.gom

import org.junit.Assert.assertEquals
import org.junit.Test

class VersionTest {
    /** Google Play's versionCode is the release number as one number (1.2.3 is 10203), so each release is higher than the last. */
    @Test
    fun versionCodeIsTheReleaseNumber() {
        val (major, minor, patch) = BuildConfig.VERSION_NAME.split('.').map(String::toInt)
        assertEquals(major * 10_000 + minor * 100 + patch, BuildConfig.VERSION_CODE)
    }
}
