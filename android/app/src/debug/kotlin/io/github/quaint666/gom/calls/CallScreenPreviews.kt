package io.github.quaint666.gom.calls

import android.content.res.Configuration
import androidx.compose.runtime.Composable
import androidx.compose.ui.tooling.preview.Preview

/** The call screens as the mockups draw them (21 to 28), to compare by eye: in Android Studio, or full size in [CallPreviewActivity]. */
private val none = CallActions({}, {}, {}, {}, {}, {}, {}, {})
private val phone = Route("1", RouteKind.EARPIECE, "Earpiece")
private val speaker = Route("2", RouteKind.SPEAKER, "Speaker")
private val buds = Route("3", RouteKind.BLUETOOTH, "Galaxy Buds")
private val mom = Peer("Mẹ")
private fun call(phase: Phase, mine: Boolean = true) = CallView("k", "c", mom, mine, phase)

@Composable private fun Show(v: CallView?, routes: Routes = Routes(listOf(phone, speaker), phone), locked: Boolean = false,
    hidden: String? = null, sheet: Boolean = false) = CallScreen(v, routes, locked, hidden, none, sheet) {}

@Preview(name = "21 calling", widthDp = 390, heightDp = 780, locale = "vi") @Composable fun Calling() = Show(call(Phase.OUTGOING).copy(ringing = true))
@Preview(name = "22 incoming, locked, no name", widthDp = 390, heightDp = 780, locale = "vi") @Composable fun IncomingHidden() = Show(call(Phase.INCOMING, mine = false), locked = true, hidden = "Gom")
@Preview(name = "23 incoming, locked", widthDp = 390, heightDp = 780, locale = "vi") @Composable fun IncomingLocked() = Show(call(Phase.INCOMING, mine = false), locked = true)
@Preview(name = "25 in call, speaker, muted", widthDp = 390, heightDp = 780, locale = "vi") @Composable fun InCall() = Show(call(Phase.ACTIVE).copy(connectedAt = System.currentTimeMillis() - 185_000, muted = true), Routes(listOf(phone, speaker), speaker))
@Preview(name = "26 sound route", widthDp = 390, heightDp = 780, locale = "vi") @Composable fun SoundRoute() = Show(call(Phase.ACTIVE).copy(connectedAt = System.currentTimeMillis() - 185_000), Routes(listOf(phone, speaker, buds), buds), sheet = true)
@Preview(name = "27 reconnecting", widthDp = 390, heightDp = 780, locale = "vi") @Composable fun Reconnecting() = Show(call(Phase.ACTIVE).copy(connectedAt = 0, downSince = 1))
@Preview(name = "28 ended", widthDp = 390, heightDp = 780, locale = "vi") @Composable fun Ended() = Show(call(Phase.ENDED).copy(ending = Ending.Talked(185)))
@Preview(name = "28 ended, dark", widthDp = 390, heightDp = 780, locale = "vi", uiMode = Configuration.UI_MODE_NIGHT_YES) @Composable fun EndedDark() = Show(call(Phase.ENDED).copy(ending = Ending.Talked(185)))
@Preview(name = "no microphone", widthDp = 390, heightDp = 780, locale = "vi") @Composable fun NoMic() = Show(call(Phase.ENDED).copy(ending = Ending.NeedMic))
@Preview(name = "25 in call, English, dark", widthDp = 390, heightDp = 780, locale = "en", uiMode = Configuration.UI_MODE_NIGHT_YES) @Composable fun InCallEnglish() = Show(call(Phase.ACTIVE).copy(connectedAt = System.currentTimeMillis() - 65_000), Routes(listOf(phone, speaker), phone))

/** By mockup number, for [CallPreviewActivity]. */
val previews: Map<String, @Composable () -> Unit> = mapOf(
    "21" to { Calling() }, "22" to { IncomingHidden() }, "23" to { IncomingLocked() }, "25" to { InCall() }, "26" to { SoundRoute() },
    "27" to { Reconnecting() }, "28" to { Ended() }, "28d" to { EndedDark() }, "mic" to { NoMic() }, "25en" to { InCallEnglish() },
)
