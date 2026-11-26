package io.github.quaint666.gom.calls

import androidx.core.telecom.CallEndpointCompat

enum class RouteKind { EARPIECE, SPEAKER, BLUETOOTH, HEADSET, OTHER }

/** One place the call's sound can go, as Android's call system names it ([id] is its endpoint's). */
data class Route(val id: String, val kind: RouteKind, val name: String)

/** Where the sound can go, and where it goes now. */
data class Routes(val list: List<Route> = emptyList(), val current: Route? = null) {
    /** More than the phone's own two (earpiece, speaker): the left button opens a choice (mockup 26) instead of switching. */
    val choice: Boolean get() = list.any { it.kind == RouteKind.BLUETOOTH || it.kind == RouteKind.HEADSET }
    /** The other of the phone's own two, for the left button when there is no choice; null when there is no other. */
    val toggle: Route? get() = list.firstOrNull {
        it.kind == if (current?.kind == RouteKind.SPEAKER) RouteKind.EARPIECE else RouteKind.SPEAKER
    }
}

fun routeOf(e: CallEndpointCompat): Route = Route(e.identifier.toString(), when (e.type) {
    CallEndpointCompat.TYPE_EARPIECE -> RouteKind.EARPIECE
    CallEndpointCompat.TYPE_SPEAKER -> RouteKind.SPEAKER
    CallEndpointCompat.TYPE_BLUETOOTH -> RouteKind.BLUETOOTH
    CallEndpointCompat.TYPE_WIRED_HEADSET -> RouteKind.HEADSET
    else -> RouteKind.OTHER
}, e.name.toString())

/** Where Android says a call's sound goes ([android.media.AudioDeviceInfo]'s type), as a route; null for a kind a call never uses. */
fun kindOfDevice(type: Int): RouteKind? = when (type) {
    android.media.AudioDeviceInfo.TYPE_BUILTIN_EARPIECE -> RouteKind.EARPIECE
    android.media.AudioDeviceInfo.TYPE_BUILTIN_SPEAKER, android.media.AudioDeviceInfo.TYPE_BUILTIN_SPEAKER_SAFE -> RouteKind.SPEAKER
    android.media.AudioDeviceInfo.TYPE_BLUETOOTH_SCO, android.media.AudioDeviceInfo.TYPE_BLE_HEADSET,
    android.media.AudioDeviceInfo.TYPE_BLE_SPEAKER, android.media.AudioDeviceInfo.TYPE_HEARING_AID -> RouteKind.BLUETOOTH
    android.media.AudioDeviceInfo.TYPE_WIRED_HEADSET, android.media.AudioDeviceInfo.TYPE_WIRED_HEADPHONES,
    android.media.AudioDeviceInfo.TYPE_USB_HEADSET -> RouteKind.HEADSET
    else -> null
}

/** One of Android's own communication devices as a route, when Telecom offers none (its id says so: "audio:" and the device's). */
fun routeOf(d: android.media.AudioDeviceInfo): Route? =
    kindOfDevice(d.type)?.let { Route("audio:${d.id}", it, d.productName?.toString().orEmpty()) }
