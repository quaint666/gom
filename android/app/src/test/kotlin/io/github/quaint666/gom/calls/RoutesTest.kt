package io.github.quaint666.gom.calls

import android.media.AudioDeviceInfo
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class RoutesTest {
    private val ear = Route("1", RouteKind.EARPIECE, "Earpiece")
    private val speaker = Route("2", RouteKind.SPEAKER, "Speaker")
    private val buds = Route("3", RouteKind.BLUETOOTH, "Galaxy Buds")

    @Test
    fun the_speaker_button_switches_between_the_phones_own_two() {
        assertEquals(speaker, Routes(listOf(ear, speaker), ear).toggle)
        assertEquals(ear, Routes(listOf(ear, speaker), speaker).toggle)
        assertNull(Routes(listOf(ear), ear).toggle)   // no speaker offered: nothing to switch to
        assertFalse(Routes(listOf(ear, speaker), ear).choice)
        assertTrue(Routes(listOf(ear, speaker, buds), buds).choice)   // a headset too: a choice instead
    }

    @Test
    fun androids_devices_are_read_as_routes() {
        assertEquals(RouteKind.EARPIECE, kindOfDevice(AudioDeviceInfo.TYPE_BUILTIN_EARPIECE))
        assertEquals(RouteKind.SPEAKER, kindOfDevice(AudioDeviceInfo.TYPE_BUILTIN_SPEAKER))
        assertEquals(RouteKind.BLUETOOTH, kindOfDevice(AudioDeviceInfo.TYPE_BLUETOOTH_SCO))
        assertEquals(RouteKind.BLUETOOTH, kindOfDevice(AudioDeviceInfo.TYPE_BLE_HEADSET))
        assertEquals(RouteKind.HEADSET, kindOfDevice(AudioDeviceInfo.TYPE_WIRED_HEADSET))
        assertNull(kindOfDevice(AudioDeviceInfo.TYPE_HDMI))
    }
}
