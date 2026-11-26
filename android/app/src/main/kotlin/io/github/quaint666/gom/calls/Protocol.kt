package io.github.quaint666.gom.calls

import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject

/**
 * The call protocol as the server speaks it (`Chat/CallEndpoints.cs`) and as the page's calls use it (`Scripts/calls/call.ts`): the
 * same version and timings, which a unit test checks against the page's (`ProtocolTest`), so the app and a browser behave alike.
 */
object CallProtocol {
    /** `CALL_PROTOCOL` in `Scripts/api.ts`; the server takes this and the one before. */
    const val VERSION = 1
    /** How long an offer or answer waits for its addresses before it goes with what it has (`GATHER_MS`). */
    const val GATHER_MS = 3_000L
    /** How long the "ended" screen stays (`ENDED_MS`). */
    const val ENDED_MS = 2_500L
    /** A connection down this long is restarted by the caller, then again this often (`RECONNECT_AFTER_MS`, `RECONNECT_EVERY_MS`). */
    const val RECONNECT_AFTER_MS = 2_000L
    const val RECONNECT_EVERY_MS = 5_000L
    /** Down this long, the call is given up (`GIVE_UP_MS`). */
    const val GIVE_UP_MS = 60_000L
    /** Answered but never connected this long, the call is given up, never left "connecting" (`CONNECT_MS`). */
    const val CONNECT_MS = 30_000L
    /** A relay login is renewed this long before it runs out (`RENEW_BEFORE_S`). */
    const val RENEW_BEFORE_S = 600L
    /** A failed renewal is tried again after this long (the page's `60_000`). */
    const val RENEW_RETRY_MS = 60_000L
    /** The caller restarts the connection this long past the ringing time after renewing, when the other side has surely renewed too. */
    const val RESTART_AFTER_RING_S = 15L

    /** When to renew a login good for [seconds]: 10 minutes before it runs out, or at half its life when that is shorter. */
    fun renewAfterMs(seconds: Long): Long = maxOf(seconds / 2.0, (seconds - RENEW_BEFORE_S).toDouble()).times(1000).toLong()

    private val id = Regex("^[0-9a-fA-F-]{36}$")

    /** A call or conversation id, checked, since it goes into an address. */
    fun isId(value: String?): Boolean = value != null && id.matches(value)
}

/** A relay or STUN server for the connection, as the server hands them out. */
data class IceServer(val urls: List<String>, val username: String? = null, val credential: String? = null)

/** `POST /api/calls/relay`: the servers to use, and how long the login lasts. */
data class Servers(val iceServers: List<IceServer>, val relay: Boolean, val seconds: Long) {
    companion object {
        fun parse(json: String): Servers? = try {
            val o = JSONObject(json)
            val list = o.getJSONArray("iceServers")
            Servers((0 until list.length()).mapNotNull { server(list.getJSONObject(it)) }, o.optBoolean("relay"), o.optLong("seconds", 0))
        } catch (_: JSONException) { null }

        private fun server(o: JSONObject): IceServer? {
            val urls = when (val u = o.opt("urls")) {
                is String -> listOf(u)
                is JSONArray -> (0 until u.length()).map { u.getString(it) }
                else -> return null
            }
            return IceServer(urls, o.optString("username").ifEmpty { null }, o.optString("credential").ifEmpty { null })
        }
    }
}

/** A mid-call offer or answer and its number, newest wins. */
data class Description(val type: String, val sdp: String, val rev: Int)

/** A call as `GET /api/calls/{id}` (and start, accept) describe it to this device. */
data class CallInfo(
    val id: String,
    val conversation: String,
    val state: String,
    val outcome: String?,
    val callerName: String,
    val offer: String?,
    val answer: String?,
    val sdp: Description?,
    val ringSeconds: Long,
    val aliveSeconds: Long,
) {
    companion object {
        fun parse(json: String): CallInfo? = try {
            val o = JSONObject(json)
            val sdp = o.optJSONObject("sdp")?.let { Description(it.getString("type"), it.getString("sdp"), it.getInt("rev")) }
            CallInfo(o.getString("id"), o.getString("conversationId"), o.getString("state"), text(o, "outcome"),
                o.optJSONObject("caller")?.let { text(it, "displayName") }.orEmpty(), text(o, "offer"), text(o, "answer"), sdp,
                o.getLong("ringSeconds"), o.getLong("aliveSeconds"))
                .takeIf { CallProtocol.isId(it.id) && CallProtocol.isId(it.conversation) }
        } catch (_: JSONException) { null }

        private fun text(o: JSONObject, name: String): String? = if (o.isNull(name)) null else o.optString(name).ifEmpty { null }
    }
}

/** What the socket says of a call (`Frames.Call*` on the server). */
sealed interface Frame {
    val id: String

    data class Incoming(override val id: String, val conversation: String, val callerName: String) : Frame
    data class Accepted(override val id: String, val sdp: String) : Frame
    data class AnsweredElsewhere(override val id: String) : Frame
    data class Ended(override val id: String, val outcome: String) : Frame
    data class Sdp(override val id: String, val description: Description) : Frame

    companion object {
        /** Null for anything that is not a call frame (messages, heartbeats) or is malformed. */
        fun parse(text: String): Frame? {
            val o = try { JSONObject(text) } catch (_: JSONException) { return null }
            if (o.optString("t") != "call") return null
            val id = o.optString("id").takeIf { CallProtocol.isId(it) } ?: return null
            return when (o.optString("kind")) {
                "incoming" -> {
                    val c = o.optString("c").takeIf { CallProtocol.isId(it) } ?: return null
                    Incoming(id, c, o.optJSONObject("from")?.optString("displayName").orEmpty())
                }
                "accepted" -> o.optString("sdp").takeIf { it.isNotEmpty() }?.let { Accepted(id, it) }
                "answered_elsewhere" -> AnsweredElsewhere(id)
                "ended" -> Ended(id, o.optString("outcome"))
                "sdp" -> {
                    val type = o.optString("type").takeIf { it == "offer" || it == "answer" } ?: return null
                    val sdp = o.optString("sdp").takeIf { it.isNotEmpty() } ?: return null
                    Sdp(id, Description(type, sdp, o.optInt("rev", 0)))
                }
                else -> null
            }
        }
    }
}

/** A call's FCM push (`CallPusher`): ring, or stop ringing and say how it ended. */
sealed interface CallPush {
    val id: String

    data class Ring(override val id: String) : CallPush
    data class End(override val id: String, val outcome: String) : CallPush

    companion object {
        fun parse(data: Map<String, String>): CallPush? {
            val id = data["id"]?.takeIf { CallProtocol.isId(it) } ?: return null
            return when (data["t"]) {
                "call" -> Ring(id)
                "call-end" -> End(id, data["outcome"].orEmpty())
                else -> null
            }
        }
    }
}

/** A call's length as the page writes it (`callLength` in `chat/model.ts`): 3:05, or 1:02:03 past an hour. */
fun callLength(seconds: Long): String {
    val h = seconds / 3600
    val m = seconds / 60 % 60
    val s = seconds % 60
    return if (h > 0) String.format(java.util.Locale.ROOT, "%d:%02d:%02d", h, m, s) else String.format(java.util.Locale.ROOT, "%d:%02d", m, s)
}
