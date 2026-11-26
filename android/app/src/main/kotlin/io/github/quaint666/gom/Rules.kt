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
    data class Hello(val version: Int) : FromPage
    data object Enable : FromPage
    data object Disable : FromPage
    data class Clear(val conversation: String) : FromPage
    data class Theme(val bar: Int?, val page: Int?) : FromPage

    companion object {
        /** Null for anything the protocol does not know: it is ignored, never guessed at. */
        fun parse(message: String): FromPage? {
            val m = try { JSONObject(message) } catch (_: JSONException) { return null }
            return when (m.optString("t")) {
                "hello" -> Hello(m.optInt("v", 0))
                "enable" -> Enable
                "disable" -> Disable
                "clear" -> m.optString("c").takeIf { it.isNotEmpty() }?.let { Clear(it) }
                "theme" -> Theme(Colors.parse(m.optString("bar")), Colors.parse(m.optString("page")))
                else -> null
            }
        }
    }
}

/** What the app says to the page (protocol 1). */
object ToPage {
    const val PROTOCOL = 1

    fun hello(app: String, push: Boolean, permission: String, fid: String?): String = JSONObject()
        .put("t", "hello").put("v", PROTOCOL).put("app", app).put("push", push).put("permission", permission)
        .apply { if (!fid.isNullOrEmpty()) put("fid", fid) }.toString()

    fun enabled(fid: String): String = JSONObject().put("t", "enabled").put("fid", fid).toString()
    fun fid(fid: String): String = JSONObject().put("t", "fid").put("fid", fid).toString()
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

        /** Null for calls (PR 6) and anything unknown or malformed. Ids are checked, since they go into an address. */
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
