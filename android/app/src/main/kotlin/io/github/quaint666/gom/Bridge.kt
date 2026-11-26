package io.github.quaint666.gom

import android.net.Uri
import android.webkit.WebView
import androidx.webkit.JavaScriptReplyProxy
import androidx.webkit.WebMessageCompat
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature

/**
 * The page's `gomApp` object (androidx.webkit's web message listener; docs/voice-calls.md, decision 31). Android puts it only on
 * pages of Gom's own origin, and each message is checked again for that origin and the top frame: no other page can reach it.
 * What it carries is protocol 1 (`Scripts/app.ts`, [FromPage], [ToPage]).
 */
class Bridge(private val origin: String, private val handle: (FromPage, Bridge) -> Unit) : WebViewCompat.WebMessageListener {
    private var reply: JavaScriptReplyProxy? = null

    /** False on a web view too old for the listener: the page then runs as in a browser, without notifications. */
    fun attach(view: WebView): Boolean {
        if (origin.isEmpty() || !WebViewFeature.isFeatureSupported(WebViewFeature.WEB_MESSAGE_LISTENER)) return false
        WebViewCompat.addWebMessageListener(view, NAME, setOf(origin), this)
        return true
    }

    override fun onPostMessage(view: WebView, message: WebMessageCompat, sourceOrigin: Uri, isMainFrame: Boolean, replyProxy: JavaScriptReplyProxy) {
        if (!isMainFrame || sourceOrigin.toString().trimEnd('/') != origin) return
        val text = message.data ?: return
        val parsed = FromPage.parse(text) ?: return
        reply = replyProxy   // the newest page's: a reload gets a new one with its hello
        handle(parsed, this)
    }

    /** To the page that spoke last; dropped when there is none yet. */
    fun send(message: String) {
        if (!WebViewFeature.isFeatureSupported(WebViewFeature.WEB_MESSAGE_LISTENER)) return
        try { reply?.postMessage(message) } catch (_: IllegalStateException) { reply = null }
    }

    companion object {
        const val NAME = "gomApp"
    }
}
