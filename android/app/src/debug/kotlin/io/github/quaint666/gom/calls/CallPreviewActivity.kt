package io.github.quaint666.gom.calls

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * Debug builds only: shows one of [CallScreenPreviews]' screens full size on a phone or emulator, with no call and no server
 * (`adb shell am start -n <package>/io.github.quaint666.gom.calls.CallPreviewActivity --es screen 25`). `--es screen loopback`
 * instead connects two of the app's WebRTC connections to each other, with no server, and logs how it went (tag GomLoopback);
 * `--es screen dial` starts a call as the page's button does, to a chat that does not exist; `ring` as a call's push does;
 * `ringtone` and `ringback` play the ringer for six seconds.
 */
class CallPreviewActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        val screen = intent.getStringExtra("screen").orEmpty()
        setContent { previews[screen]?.invoke() ?: Calling() }
        if (screen == "loopback") loopback()
        // A call to a chat that does not exist, as the page's call button would start it: the service, the call screen, an ending.
        if (screen == "dial") { CallService.dial(this, "00000000-0000-4000-8000-000000000000", "Thử", null); finish() }
        // A push saying a call rings, for a call the server does not know: the service starts, shows it, asks, and stops.
        if (screen == "ring") { CallService.ring(this, "00000000-0000-4000-8000-000000000001"); finish() }
        // The ringer alone, for six seconds: the ringtone and vibration, or the ringback tone.
        if (screen == "ringtone" || screen == "ringback") lifecycleScope.launch {
            val r = Ringer(this@CallPreviewActivity)
            if (screen == "ringtone") r.incoming() else r.ringback()
            delay(6_000)
            r.stop()
        }
    }

    private fun loopback() = lifecycleScope.launch {
        val log = { m: String -> android.util.Log.i("GomLoopback", m); Unit }
        val started = System.currentTimeMillis()
        val media = WebRtcMedia(this@CallPreviewActivity)
        val links = mutableListOf<String>()
        val a = media.open(emptyList()) { links += "a $it" } ?: return@launch log("no microphone")
        val b = media.open(emptyList()) { links += "b $it" } ?: return@launch log("no microphone")
        val offer = a.offer()
        log("offer ${offer.length} chars, ${offer.lines().count { it.startsWith("a=candidate") }} addresses")
        val answer = b.answer(offer)
        log("answer ${answer.length} chars, ${answer.lines().count { it.startsWith("a=candidate") }} addresses")
        a.accept(answer)
        for (i in 0 until 50) { if (links.count { it.endsWith("CONNECTED") } >= 2) break; delay(100) }
        log("links: $links after ${System.currentTimeMillis() - started} ms; stable ${a.stable} ${b.stable}")
        val again = a.offer(restart = true)
        a.accept(b.answer(again))
        delay(1500)
        log("after a restart: $links")
        a.close(); b.close()
        log("closed")
    }
}
