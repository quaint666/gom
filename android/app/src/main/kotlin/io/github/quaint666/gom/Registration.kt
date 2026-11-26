package io.github.quaint666.gom

import android.content.Context
import android.os.Handler
import android.os.Looper
import androidx.core.content.edit
import com.google.firebase.messaging.FirebaseMessaging

/**
 * The app's FCM registration (docs/voice-calls.md, decision 43). Registered only when the person turns notifications on in
 * Settings, as on the web; the installation id FCM gives is kept, so the page can learn it at its next hello. FCM calls
 * [registered] again whenever the id changes; the open page is then told, and sends it to the server.
 */
object Registration {
    private const val PREFS = "gom.push"
    private const val FID = "fid"
    private const val ASKED = "asked"
    private val main = Handler(Looper.getMainLooper())

    /** Waiting for the id that answers the page's "enable". */
    private var waiting: ((String?) -> Unit)? = null

    /** The open page, to be told of a new id. */
    var listener: ((String) -> Unit)? = null

    fun fid(context: Context): String? = prefs(context).getString(FID, null)

    /** Whether Android's notification question was asked once already: a "no" then reads as the browser's "denied". */
    fun asked(context: Context): Boolean = prefs(context).getBoolean(ASKED, false)
    fun markAsked(context: Context) = prefs(context).edit { putBoolean(ASKED, true) }

    /** Asks FCM for an id; [done] gets it, or null when FCM refused or is not set up in this build. */
    fun register(context: Context, done: (String?) -> Unit) {
        if (!GomApp.push) { done(null); return }
        waiting = done
        // On success FCM calls onRegistered with the current id, even one it gave before, so the answer comes from there.
        FirebaseMessaging.getInstance().register().addOnFailureListener { answer(null) }
    }

    fun unregister(context: Context) {
        prefs(context).edit { remove(FID) }
        if (GomApp.push) FirebaseMessaging.getInstance().unregister()
    }

    /** From FCM, on a worker thread. */
    internal fun registered(context: Context, id: String) {
        val changed = fid(context) != id
        prefs(context).edit { putString(FID, id) }
        main.post {
            if (waiting != null) answer(id) else if (changed) listener?.invoke(id)
        }
    }

    private fun answer(id: String?) {
        main.post {
            val done = waiting ?: return@post
            waiting = null
            done(id)
        }
    }

    private fun prefs(context: Context) = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
}
