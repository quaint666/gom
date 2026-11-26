package io.github.quaint666.gom.calls

import android.Manifest
import android.app.KeyguardManager
import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.core.net.toUri
import androidx.lifecycle.lifecycleScope
import io.github.quaint666.gom.GomApp
import io.github.quaint666.gom.MainActivity
import kotlinx.coroutines.launch

/**
 * The call screen (mockups 21 to 28), over the lock screen when a call rings. It only shows the call the service runs and passes the
 * buttons to it; leaving it (the down arrow, Back) leaves the call going, with its notification to come back by. Answering asks for
 * the microphone first when Android has not been asked yet, as a browser would.
 */
class CallActivity : ComponentActivity() {
    private var locked by mutableStateOf(false)
    /** What waits for the microphone question's answer: answering, or calling back. */
    private var afterMic: (() -> Unit)? = null

    private val askMicrophone = registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        val then = afterMic
        afterMic = null
        if (granted) then?.invoke() else if (CallService.view.value?.phase == Phase.INCOMING) CallService.hangUp(this)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge()
        super.onCreate(savedInstanceState)
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() = minimise()
        })
        val act = CallActions(
            answer = { withMic { CallService.answer(this) } },
            hangUp = { CallService.hangUp(this) },
            mute = { CallService.mute(this, it) },
            route = { CallService.route(this, it.id) },
            minimise = ::minimise,
            close = { CallService.close(this); finish() },
            callBack = ::callBack,
            message = { openChat(); CallService.close(this) },
        )
        setContent {
            val view by CallService.view.collectAsState()
            val routes by CallService.sound.collectAsState()
            val hidden by CallService.lockedTitle.collectAsState()
            var choosing by remember { mutableStateOf(false) }
            CallScreen(view, routes, locked, hidden, act, choosing) { choosing = it }
        }
        // The screen closes once the call it showed is over and its ending has been shown.
        lifecycleScope.launch {
            var seen = false
            CallService.view.collect { v -> if (v != null) seen = true else if (seen) finish() }
        }
        handle(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handle(intent)
    }

    override fun onResume() {
        super.onResume()
        locked = getSystemService(KeyguardManager::class.java).isKeyguardLocked
        if (!CallService.running) finish()   // the call ended while the screen was away
    }

    private fun handle(intent: Intent?) {
        if (intent?.action == ANSWER) withMic { CallService.answer(this) }
    }

    private fun withMic(then: () -> Unit) {
        if (CallService.hasMic(this)) then()
        else { afterMic = then; askMicrophone.launch(Manifest.permission.RECORD_AUDIO) }
    }

    /** The ended call's "Call back": a new call in its place. */
    private fun callBack() {
        val v = CallService.view.value ?: return
        withMic { CallService.dial(this, v.conversation, v.peer.name, v.peer.avatar) }
    }

    /** Back to the app, on the chat with them; the call goes on. */
    private fun minimise() {
        openChat()
        finish()
    }

    private fun openChat() {
        val conversation = CallService.view.value?.conversation ?: return
        startActivity(Intent(this, MainActivity::class.java).setAction(Intent.ACTION_VIEW)
            .setData(GomApp.origins.page("/c/$conversation").toUri()).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP))
    }

    companion object {
        /** Open to answer (the ringing notification's button). */
        const val ANSWER = "io.github.quaint666.gom.call.SCREEN_ANSWER"
    }
}
