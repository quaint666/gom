package io.github.quaint666.gom

import android.annotation.SuppressLint
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage

/**
 * FCM's side: the installation id, and the data-only pushes the server sends (`PushSender`). Each brings only ids; the words are
 * fetched with the web view's session and shown at once, since a high-priority push that shows nothing lowers the app's priority.
 * Calls (`call`, `call-end`) are ignored until PR 6: nothing can start a call yet.
 */
// Lint still asks for onNewToken, which FCM's installation-id mode never calls (it is deprecated for onRegistered).
@SuppressLint("MissingFirebaseInstanceTokenRefresh")
class PushService : FirebaseMessagingService() {
    override fun onRegistered(installationId: String) = Registration.registered(this, installationId)

    override fun onMessageReceived(message: RemoteMessage) {
        when (val push = Push.parse(message.data)) {
            null -> Unit
            is Push.Recall -> Notices.recall(this, push)
            else -> Notices.show(this, push)
        }
    }
}
