package io.github.quaint666.gom

import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.webkit.CookieManager
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.net.toUri
import io.github.quaint666.gom.calls.CallService
import java.net.HttpURLConnection
import java.net.URL

/**
 * The app's notifications, made as the page's service worker makes a browser's (`wwwroot/sw.js`): one per conversation (a new
 * message replaces it, with sound), the words from the server, and a tap that opens the conversation.
 */
object Notices {
    const val MESSAGES = "messages"
    const val OWNER = "owner"
    private const val ID = 1
    private const val MESSAGE_ID = "gom.m"
    private const val TIMEOUT_MS = 8_000

    /** A message or an owner's notice: worded by the server, or the app's own plain line when it cannot be (a 404, no network). */
    fun show(context: Context, push: Push) {
        val notice = words(push.noticePath) ?: when (push) {
            is Push.Message -> Notice("Gom", context.getString(R.string.new_message), "/c/${push.conversation}", push.conversation)
            is Push.Owner -> if (push.kind == "join") Notice("Gom", context.getString(R.string.new_notice), "/settings/requests", "join-requests")
                else Notice("Gom", context.getString(R.string.new_notice), "/settings/contact", "contact")
            is Push.Recall -> return
        }
        post(context, notice, if (push is Push.Owner) OWNER else MESSAGES, (push as? Push.Message)?.message, silent = false)
    }

    /**
     * A message was taken back. Its notification, if still shown, now says so, without a sound; a newer one of the conversation
     * stays as it is. Nothing is shown otherwise: this push is normal priority, so it need not show anything.
     */
    fun recall(context: Context, push: Push.Recall) {
        val shown = context.getSystemService(NotificationManager::class.java).activeNotifications
            .firstOrNull { it.tag == push.conversation && it.id == ID } ?: return
        if (shown.notification.extras.getString(MESSAGE_ID) != push.message) return
        val notice = words(push.noticePath) ?: return
        post(context, notice, MESSAGES, push.message, silent = true)
    }

    /** Closes a conversation's notification, when the page opens it. */
    fun clear(context: Context, conversation: String) = NotificationManagerCompat.from(context).cancel(conversation, ID)

    /** A call nobody answered here (a `call-end` push said "missed" or "busy"): worded by the server, a tap opening the chat. */
    fun missedCall(context: Context, call: String) {
        val notice = words("/api/push/notice?call=$call") ?: return
        post(context, notice, CallService.MISSED_CALLS, null, silent = false)
    }

    private fun post(context: Context, notice: Notice, channel: String, message: String?, silent: Boolean) {
        val manager = NotificationManagerCompat.from(context)
        if (!manager.areNotificationsEnabled()) return
        val open = Intent(context, MainActivity::class.java)
            .setAction(Intent.ACTION_VIEW).setData(GomApp.origins.page(notice.url).toUri())
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        val tap = PendingIntent.getActivity(context, notice.tag.hashCode(), open, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val built = NotificationCompat.Builder(context, channel)
            .setSmallIcon(R.drawable.ic_notification)
            .setColor(context.getColor(R.color.gom))
            .setContentTitle(notice.title)
            .setContentText(notice.body)
            .setStyle(NotificationCompat.BigTextStyle().bigText(notice.body))
            .setCategory(if (channel == MESSAGES) NotificationCompat.CATEGORY_MESSAGE else NotificationCompat.CATEGORY_SOCIAL)
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .setAutoCancel(true)
            .setSilent(silent)
            .setContentIntent(tap)
            .apply { if (message != null) addExtras(android.os.Bundle().apply { putString(MESSAGE_ID, message) }) }
            .build()
        notify(manager, notice.tag, built)
    }

    @Suppress("MissingPermission")   // checked just before: areNotificationsEnabled covers POST_NOTIFICATIONS
    private fun notify(manager: NotificationManagerCompat, tag: String, notification: Notification) =
        try { manager.notify(tag, ID, notification) } catch (_: SecurityException) { /* the permission went in between */ }

    /** The words, asked for with the web view's cookies (the session, and any sign-in in front of the server). Blocks: not on the main thread. */
    fun words(path: String): Notice? {
        val origins = GomApp.origins
        if (!origins.configured) return null
        return try {
            val connection = URL(origins.origin + path).openConnection() as HttpURLConnection
            try {
                connection.connectTimeout = TIMEOUT_MS
                connection.readTimeout = TIMEOUT_MS
                connection.instanceFollowRedirects = false
                connection.setRequestProperty("Accept", "application/json")
                CookieManager.getInstance().getCookie(origins.origin)?.let { connection.setRequestProperty("Cookie", it) }
                if (connection.responseCode != 200) null
                else Notice.parse(connection.inputStream.bufferedReader().use { it.readText() })
            } finally { connection.disconnect() }
        } catch (_: java.io.IOException) { null }
    }
}
