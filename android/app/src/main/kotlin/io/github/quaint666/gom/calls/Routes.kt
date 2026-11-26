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
