package io.github.quaint666.gom.calls

import android.webkit.CookieManager
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener

/**
 * The app's own socket, open only during a call (decision 33): the server's call frames for this device (an answer, a mid-call offer,
 * the end). Each time it connects, the call asks the server what it missed meanwhile. Frames reach [onFrame] on [scope]'s thread.
 */
class CallSocket(
    private val origin: String,
    private val scope: CoroutineScope,
    private val onFrame: (Frame) -> Unit,
    private val onOpen: () -> Unit,
    private val client: OkHttpClient = HttpCallServer.shared,
) {
    private var socket: WebSocket? = null
    private var retry: Job? = null
    private var wanted = false

    fun open() {
        if (wanted) return
        wanted = true
        connect()
    }

    fun close() {
        wanted = false
        retry?.cancel()
        socket?.close(1000, null)
        socket = null
    }

    private fun connect() {
        if (!wanted) return
        val address = origin.replaceFirst("https://", "wss://") + "/ws"
        val request = Request.Builder().url(address).header("Origin", origin)
            .apply { CookieManager.getInstance().getCookie(origin)?.let { header("Cookie", it) } }.build()
        socket = client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) { scope.launch { if (webSocket === socket) onOpen() } }
            override fun onMessage(webSocket: WebSocket, text: String) {
                val frame = Frame.parse(text) ?: return
                scope.launch { if (webSocket === socket) onFrame(frame) }
            }
            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = lost(webSocket)
            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) = lost(webSocket)
        })
    }

    /** Lost while the call goes on (a network change): tried again soon, as the page's socket is. */
    private fun lost(webSocket: WebSocket) {
        scope.launch {
            if (webSocket !== socket || !wanted) return@launch
            socket = null
            retry = scope.launch { delay(RETRY_MS); connect() }
        }
    }

    private companion object {
        const val RETRY_MS = 2_000L
    }
}
