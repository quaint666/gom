package io.github.quaint666.gom

import android.annotation.SuppressLint
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
import io.github.quaint666.gom.calls.CallPush
import io.github.quaint666.gom.calls.CallService

/**
 * FCM's side: the installation id, and the data-only pushes the server sends (`PushSender`). Each brings only ids; the words are
 * fetched with the web view's session and shown at once, since a high-priority push that shows nothing lowers the app's priority.
 * A call (`call`) starts the call service, which rings; `call-end` stops it, and a missed call is then shown like a message.
 */
// Lint still asks for onNewToken, which FCM's installation-id mode never calls (it is deprecated for onRegistered).
@SuppressLint("MissingFirebaseInstanceTokenRefresh")
class PushService : FirebaseMessagingService() {
    override fun onRegistered(installationId: String) = Registration.registered(this, installationId)

    override fun onMessageReceived(message: RemoteMessage) {
        when (val call = CallPush.parse(message.data)) {
            is CallPush.Ring -> {
                // Android lets the call service start only from a push FCM delivered as high priority; a lowered one rings nowhere.
                if (message.priority == RemoteMessage.PRIORITY_HIGH) CallService.ring(this, call.id)
                return
            }
            is CallPush.End -> {
                CallService.ended(this, call.id, call.outcome)
                if (call.outcome == "missed" || call.outcome == "busy") Notices.missedCall(this, call.id)
                return
            }
            null -> Unit
        }
        when (val push = Push.parse(message.data)) {
            null -> Unit
            is Push.Recall -> Notices.recall(this, push)
            else -> Notices.show(this, push)
        }
    }
}
