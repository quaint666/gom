package io.github.quaint666.gom

import android.content.Context
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection
import android.webkit.WebView
import androidx.core.view.ContentInfoCompat
import androidx.core.view.ViewCompat
import androidx.core.view.inputmethod.EditorInfoCompat
import androidx.core.view.inputmethod.InputConnectionCompat

/**
 * The web view, telling keyboards it takes pictures. A plain WebView offers no picture types to the keyboard, so Gboard's GIF
 * button says the app "does not support images here"; this one offers [ToPage.KEYBOARD_TYPES] and hands what comes to
 * [onImage], which sends it to the page (Android's image keyboard support, through androidx's receive-content API).
 */
class GomWebView(context: Context) : WebView(context) {
    /** Takes one picture the keyboard committed; false when it was not taken (the keyboard then says so). */
    var onImage: ((ContentInfoCompat) -> Boolean)? = null

    init {
        ViewCompat.setOnReceiveContentListener(this, ToPage.KEYBOARD_TYPES) { _, payload ->
            val handle = onImage ?: return@setOnReceiveContentListener payload
            // Pictures come as items with an address; text (if any) is left to the web view as usual.
            val split = payload.partition { it.uri != null }   // android.util.Pair: (pictures, the rest), either may be null
            val pictures: ContentInfoCompat? = split.first
            if (pictures != null && !handle(pictures)) return@setOnReceiveContentListener payload
            split.second
        }
    }

    override fun onCreateInputConnection(outAttrs: EditorInfo): InputConnection? {
        val connection = super.onCreateInputConnection(outAttrs) ?: return null
        EditorInfoCompat.setContentMimeTypes(outAttrs, ToPage.KEYBOARD_TYPES)
        return InputConnectionCompat.createWrapper(this, connection, outAttrs)
    }
}
