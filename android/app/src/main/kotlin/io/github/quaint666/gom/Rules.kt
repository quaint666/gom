package io.github.quaint666.gom

import java.net.URI
import org.json.JSONException
import org.json.JSONObject

/**
 * Which addresses the app shows inside, and which go to the browser. Gom's own origin (https only) always stays in; [alsoAllow]
 * is one more host whose pages may load in the web view too (a sign-in in front of a test server), never given the bridge.
 */
class Origins(baseUrl: String, alsoAllow: String = "") {
    private val base: URI? = runCatching { URI(baseUrl.trim()) }.getOrNull()?.takeIf { it.scheme == "https" && !it.host.isNullOrEmpty() }
    private val extra = alsoAllow.trim().lowercase()

    /** Whether the build was given a server at all. */
    val configured: Boolean get() = base != null

    /** "https://host" or "https://host:port": what the bridge answers, and where the app starts. */
    val origin: String get() = base?.let { "https://${it.host.lowercase()}${if (it.port == -1) "" else ":${it.port}"}" } ?: ""

    fun isGom(url: String): Boolean {
        val b = base ?: return false
        val u = parse(url) ?: return false
        return u.scheme == "https" && u.host.equals(b.host, ignoreCase = true) && u.port == b.port
    }

    /**
     * Whether a page may have the microphone (a voice message): only Gom's own pages, and only the microphone; never a page of
     * [alsoAllow]'s host, nor the camera. Android's own question still comes first.
     */
    fun mayUseMicrophone(origin: String, resources: List<String>): Boolean =
        isGom(origin) && resources == listOf(android.webkit.PermissionRequest.RESOURCE_AUDIO_CAPTURE)

    fun loadsInside(url: String): Boolean {
        if (isGom(url)) return true
        if (extra.isEmpty()) return false
        val u = parse(url) ?: return false
        return u.scheme == "https" && u.host.equals(extra, ignoreCase = true)
    }

    /** A path on Gom ("/c/…" from a notification) as a full address; anything else is the start page. */
    fun page(path: String?): String = if (path != null && path.startsWith("/") && !path.startsWith("//")) origin + path else "$origin/"

    private fun parse(url: String): URI? = runCatching { URI(url) }.getOrNull()?.takeIf { !it.host.isNullOrEmpty() }
}

/** What the page says to the app (protocol 1, `Scripts/app.ts`). */
sealed interface FromPage {
    /** [images]: the largest picture the server takes, in bytes; 0 (an older page) takes no keyboard pictures. */
    data class Hello(val version: Int, val images: Long = 0) : FromPage
    data object Enable : FromPage
    data object Disable : FromPage
    data class Clear(val conversation: String) : FromPage
    data class Theme(val bar: Int?, val page: Int?) : FromPage
    /** Call the friend of this chat (sent only once the app's hello said it takes calls). */
    data class Call(val conversation: String, val name: String, val avatar: String?) : FromPage

    companion object {
        /** Null for anything the protocol does not know: it is ignored, never guessed at. */
        fun parse(message: String): FromPage? {
            val m = try { JSONObject(message) } catch (_: JSONException) { return null }
            return when (m.optString("t")) {
                "hello" -> Hello(m.optInt("v", 0), m.optLong("images", 0).coerceAtLeast(0))
                "enable" -> Enable
                "disable" -> Disable
                "clear" -> m.optString("c").takeIf { it.isNotEmpty() }?.let { Clear(it) }
                "theme" -> Theme(Colors.parse(m.optString("bar")), Colors.parse(m.optString("page")))
                "call" -> m.optString("c").takeIf { io.github.quaint666.gom.calls.CallProtocol.isId(it) }?.let { c ->
                    Call(c, m.optString("name"), m.optString("avatar").takeIf { it.startsWith("/") && !it.startsWith("//") })
                }
                else -> null
            }
        }
    }
}

/** What the app says to the page (protocol 1). */
object ToPage {
    const val PROTOCOL = 1

    /** [calls]: this build makes and takes calls, so the page offers them (added after protocol 1, as `image` was). */
    fun hello(app: String, push: Boolean, permission: String, fid: String?, calls: Boolean = false): String = JSONObject()
        .put("t", "hello").put("v", PROTOCOL).put("app", app).put("push", push).put("permission", permission)
        .apply { if (!fid.isNullOrEmpty()) put("fid", fid) }.apply { if (calls) put("calls", true) }.toString()

    fun enabled(fid: String): String = JSONObject().put("t", "enabled").put("fid", fid).toString()
    fun fid(fid: String): String = JSONObject().put("t", "fid").put("fid", fid).toString()

    /** A picture the keyboard gave, for the page to take as a paste: only the kinds a message takes. */
    fun image(type: String, bytes: ByteArray): String? = if (type !in KEYBOARD_TYPES) null
        else JSONObject().put("t", "image").put("type", type).put("data", java.util.Base64.getEncoder().encodeToString(bytes)).toString()

    /** What the app tells keyboards it takes (Gboard's GIF button shows only when one of these is offered). */
    val KEYBOARD_TYPES = arrayOf("image/gif", "image/webp", "image/png", "image/jpeg")
    val denied: String = """{"t":"denied"}"""
    val failed: String = """{"t":"failed"}"""
}

/** The page's CSS colours, as the app needs them for the system bars. */
object Colors {
    private val hex = Regex("^#([0-9a-fA-F]{6})$")
    private val rgb = Regex("""^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*[\d.]+\s*)?\)$""")

    /** An opaque ARGB colour from "#rrggbb" or "rgb(r, g, b)"; null for anything else. */
    fun parse(css: String): Int? {
        hex.find(css.trim())?.let { return (0xFF shl 24) or it.groupValues[1].toInt(16) }
        rgb.find(css.trim())?.let { m ->
            val (r, g, b) = m.destructured.toList().map { it.toInt() }
            if (r > 255 || g > 255 || b > 255) return null
            return (0xFF shl 24) or (r shl 16) or (g shl 8) or b
        }
        return null
    }

    /** Whether dark icons read better on this colour (its relative brightness, as WCAG weighs it, is over half). */
    fun isLight(color: Int): Boolean {
        fun channel(c: Int): Double = (c / 255.0).let { if (it <= 0.03928) it / 12.92 else Math.pow((it + 0.055) / 1.055, 2.4) }
        val l = 0.2126 * channel((color shr 16) and 0xFF) + 0.7152 * channel((color shr 8) and 0xFF) + 0.0722 * channel(color and 0xFF)
        return l > 0.5
    }
}

/** A notification's words, as the server gives them (`GET /api/push/notice`). */
data class Notice(val title: String, val body: String, val url: String, val tag: String) {
    companion object {
        fun parse(json: String): Notice? = try {
            val o = JSONObject(json)
            Notice(o.getString("title"), o.getString("body"), o.getString("url"), o.getString("tag"))
                .takeIf { it.title.isNotEmpty() && it.body.isNotEmpty() && it.url.startsWith("/") && it.tag.isNotEmpty() }
        } catch (_: JSONException) { null }
    }
}

/** What FCM brought (data only, from the server's `PushSender` and `CallPusher`), and where its words are asked for. */
sealed interface Push {
    data class Message(val conversation: String, val message: String) : Push
    data class Recall(val conversation: String, val message: String) : Push
    data class Owner(val kind: String) : Push

    /** The address that words it. */
    val noticePath: String
        get() = when (this) {
            is Message -> "/api/push/notice?c=$conversation&m=$message"
            is Recall -> "/api/push/notice?c=$conversation&m=$message"
            is Owner -> "/api/push/notice?owner=$kind"
        }

    companion object {
        private val id = Regex("^[0-9a-fA-F-]{36}$")

        /** Null for calls ([io.github.quaint666.gom.calls.CallPush]) and anything unknown or malformed. Ids are checked, since they go into an address. */
        fun parse(data: Map<String, String>): Push? {
            val c = data["c"]?.takeIf { id.matches(it) }
            val m = data["m"]?.takeIf { id.matches(it) }
            return when (data["t"]) {
                "message" -> if (c != null && m != null) Message(c, m) else null
                "recall" -> if (c != null && m != null) Recall(c, m) else null
                "owner" -> data["kind"]?.takeIf { it == "join" || it == "contact" }?.let { Owner(it) }
                else -> null
            }
        }
    }
}

/** All of [input] when it is at most [limit] bytes; null when it is longer (read only one byte past the limit to know). */
fun readAtMost(input: java.io.InputStream, limit: Long): ByteArray? {
    val out = java.io.ByteArrayOutputStream()
    val buffer = ByteArray(64 * 1024)
    var total = 0L
    while (true) {
        val n = input.read(buffer)
        if (n < 0) return out.toByteArray()
        total += n
        if (total > limit) return null
        out.write(buffer, 0, n)
    }
}
