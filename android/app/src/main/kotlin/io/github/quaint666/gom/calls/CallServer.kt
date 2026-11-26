package io.github.quaint666.gom.calls

import android.webkit.CookieManager
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import org.json.JSONException
import org.json.JSONObject

/** A server's answer: what was asked for, or why not (its error code; null when there was no answer at all). */
sealed interface Reply<out T> {
    data class Ok<T>(val data: T) : Reply<T>
    data class Failed(val error: String?, val status: Int = 0) : Reply<Nothing>
}

/** The call steps, as the page's `api.ts` makes them (`/api/calls`). Tests put a fake in its place. */
interface CallServer {
    /** The servers for this device's connection: before calling ([conversation]), or for a call ringing or in progress ([call]). */
    suspend fun servers(conversation: String? = null, call: String? = null): Reply<Servers>
    suspend fun start(conversation: String, clientId: String, offer: String): Reply<CallInfo>
    suspend fun get(id: String): Reply<CallInfo>
    suspend fun accept(id: String, answer: String): Reply<CallInfo>
    suspend fun decline(id: String): Reply<Unit>
    suspend fun hangUp(id: String): Reply<Unit>
    /** A mid-call offer or answer; the server numbers it. */
    suspend fun sdp(id: String, type: String, sdp: String): Reply<Int>
    suspend fun alive(id: String): Reply<Unit>
}

/**
 * The call steps over HTTPS with the web view's cookies (the session, and any sign-in in front of the server), so there is no second
 * sign-in (decision 32). Each state-changing request carries Gom's origin, as the page's would, for the server's Origin check.
 */
class HttpCallServer(private val origin: String, private val client: OkHttpClient = shared) : CallServer {
    override suspend fun servers(conversation: String?, call: String?): Reply<Servers> =
        post("/api/calls/relay", JSONObject().apply { conversation?.let { put("conversationId", it) }; call?.let { put("callId", it) } }) {
            Servers.parse(it)
        }

    override suspend fun start(conversation: String, clientId: String, offer: String): Reply<CallInfo> = post("/api/calls",
        JSONObject().put("conversationId", conversation).put("clientId", clientId).put("sdp", offer).put("v", CallProtocol.VERSION)) { CallInfo.parse(it) }

    override suspend fun get(id: String): Reply<CallInfo> = send(request("/api/calls/$id").get().build()) { CallInfo.parse(it) }

    override suspend fun accept(id: String, answer: String): Reply<CallInfo> =
        post("/api/calls/$id/accept", JSONObject().put("sdp", answer).put("v", CallProtocol.VERSION)) { CallInfo.parse(it) }

    override suspend fun decline(id: String): Reply<Unit> = post("/api/calls/$id/decline", null) { }
    override suspend fun hangUp(id: String): Reply<Unit> = post("/api/calls/$id/hangup", null) { }

    override suspend fun sdp(id: String, type: String, sdp: String): Reply<Int> =
        post("/api/calls/$id/sdp", JSONObject().put("type", type).put("sdp", sdp)) { JSONObject(it).optInt("rev").takeIf { r -> r > 0 } }

    override suspend fun alive(id: String): Reply<Unit> = post("/api/calls/$id/alive", null) { }

    private fun request(path: String): Request.Builder = Request.Builder().url(origin + path)
        .header("Accept", "application/json")
        .apply { CookieManager.getInstance().getCookie(origin)?.let { header("Cookie", it) } }

    private suspend fun <T> post(path: String, body: JSONObject?, read: (String) -> T?): Reply<T> =
        send(request(path).header("Origin", origin).post((body?.toString() ?: "").toRequestBody(json)).build(), read)

    private suspend fun <T> send(request: Request, read: (String) -> T?): Reply<T> = withContext(Dispatchers.IO) {
        try {
            client.newCall(request).execute().use { r -> answer(r, read) }
        } catch (_: IOException) { Reply.Failed(null) }
    }

    private fun <T> answer(r: Response, read: (String) -> T?): Reply<T> {
        val text = r.body.string()
        if (!r.isSuccessful) return Reply.Failed(errorOf(text), r.code)
        val data = (if (r.code == 204) read("{}") else read(text)) ?: return Reply.Failed(null, r.code)
        return Reply.Ok(data)
    }

    companion object {
        private val json = "application/json".toMediaType()

        /** The server's error code (`{"error": "…"}`); null for anything else. */
        fun errorOf(text: String): String? = try { JSONObject(text).optString("error").ifEmpty { null } } catch (_: JSONException) { null }

        /** One client for the app's calls: its connections are reused, and nothing waits for ever on a bad mobile line. */
        val shared: OkHttpClient by lazy {
            OkHttpClient.Builder()
                .connectTimeout(10, TimeUnit.SECONDS).readTimeout(15, TimeUnit.SECONDS).writeTimeout(15, TimeUnit.SECONDS)
                .followRedirects(false)
                .pingInterval(20, TimeUnit.SECONDS)
                .build()
        }
    }
}
