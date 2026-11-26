package io.github.quaint666.gom.calls

import android.Manifest
import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.net.Uri
import android.os.PowerManager
import android.telecom.DisconnectCause
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.app.Person
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import androidx.core.telecom.CallAttributesCompat
import androidx.core.telecom.CallControlScope
import androidx.core.telecom.CallEndpointCompat
import androidx.core.telecom.CallException
import androidx.core.telecom.CallsManager
import androidx.lifecycle.LifecycleService
import androidx.lifecycle.lifecycleScope
import io.github.quaint666.gom.GomApp
import io.github.quaint666.gom.Notice
import io.github.quaint666.gom.Notices
import io.github.quaint666.gom.R
import java.util.UUID
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch

/**
 * Where a call lives while it goes on (docs/voice-calls.md, decision 30): a foreground service of the `phoneCall` type that registers
 * the call with Android's call system (`core-telecom`), so Android treats it as a call (the microphone with the screen off, the
 * earpiece, a regular phone call arriving). It runs the [CallEngine], opens the call's socket, and shows the call as a notification:
 * ringing (full screen over the lock screen), and in progress. While ringing it is only `phoneCall`; the microphone is added once the
 * person answers or calls, since Android lets an app start the microphone only after a tap.
 */
class CallService : LifecycleService() {
    private lateinit var engine: CallEngine
    private lateinit var socket: CallSocket
    private lateinit var calls: CallsManager
    private var control: CallControlScope? = null
    private var telecom: Job? = null
    /** The words for the ringing notification (`GET /api/push/notice?call=`): no name at the "nothing" level. */
    private var words: Notice? = null
    private var foreground = 0
    /** Turns the screen off while the phone is at the ear (the earpiece in use, a call going on). */
    private var ear: PowerManager.WakeLock? = null

    override fun onCreate() {
        super.onCreate()
        running = true
        val origin = GomApp.origins.origin
        engine = CallEngine(HttpCallServer(origin), WebRtcMedia(this), lifecycleScope, System::currentTimeMillis) { UUID.randomUUID().toString() }
        socket = CallSocket(origin, lifecycleScope, engine::frame, engine::resync)
        calls = CallsManager(this)
        try { calls.registerAppWithTelecom(CallsManager.CAPABILITY_BASELINE) } catch (_: UnsupportedOperationException) { /* no Telecom: the call goes on without it */ }
        ear = getSystemService(PowerManager::class.java)
            .takeIf { it.isWakeLockLevelSupported(PowerManager.PROXIMITY_SCREEN_OFF_WAKE_LOCK) }
            ?.newWakeLock(PowerManager.PROXIMITY_SCREEN_OFF_WAKE_LOCK, "gom:call")
        lifecycleScope.launch {
            var last: CallView? = null
            engine.view.collect { v -> changed(last, v); last = v; shown.value = v; earLock() }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        super.onStartCommand(intent, flags, startId)
        val i = intent ?: return START_NOT_STICKY
        when (i.action) {
            // Checked before starting (dial, ring), so the service always shows the call it was started for.
            DIAL -> engine.start(i.getStringExtra(EXTRA_CONVERSATION).orEmpty(), Peer(i.getStringExtra(EXTRA_NAME).orEmpty(), i.getStringExtra(EXTRA_AVATAR)))
            RING -> ring(i.getStringExtra(EXTRA_ID).orEmpty())
            ANSWER -> answer(fromTelecom = false)
            HANG_UP -> engine.hangUp()
            MUTE -> engine.mute(i.getBooleanExtra(EXTRA_ON, false))
            ROUTE -> route(i.getStringExtra(EXTRA_ID).orEmpty())
            CLOSE -> engine.close()
            ENDED -> {
                val id = i.getStringExtra(EXTRA_ID).orEmpty()
                if (engine.view.value?.id == id) engine.ended(id, i.getStringExtra(EXTRA_OUTCOME).orEmpty())
                return stopIfIdle()
            }
            else -> return stopIfIdle()
        }
        return START_NOT_STICKY
    }

    /** A push says a call rings: shown at once (FCM's rule, and Android's for a foreground service), then worded and checked. */
    private fun ring(id: String) {
        if (engine.view.value != null) return   // in a call already: the server made this one busy
        words = null
        locked.value = getString(R.string.call_voice)   // nobody named until the server says the level allows it
        show(ringing(Peer(getString(R.string.call_voice))), ServiceInfo.FOREGROUND_SERVICE_TYPE_PHONE_CALL)
        lifecycleScope.launch {
            val server = HttpCallServer(GomApp.origins.origin)
            val info = (server.get(id) as? Reply.Ok)?.data
            if (info == null || info.state != "ringing") { if (engine.view.value == null) stop(); return@launch }
            words = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) { Notices.words("/api/push/notice?call=$id") }
            // At the "nothing" level the server's title names nobody: the lock screen then shows no name (decision 8).
            locked.value = words.let { if (it == null) getString(R.string.call_voice) else it.title.takeIf { t -> t != info.callerName } }
            engine.ringing(id, info.conversation, Peer(info.callerName))
        }
    }

    /** Answered here (the notification's button) or through Android (a headset's button, the system's call screen). */
    private fun answer(fromTelecom: Boolean) {
        val v = engine.view.value ?: return
        if (v.phase != Phase.INCOMING) return
        if (!hasMic()) { engine.hangUp(); return }   // the call screen asks for it first; a headset's button cannot
        show(ongoing(v), ServiceInfo.FOREGROUND_SERVICE_TYPE_PHONE_CALL or ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE)
        socket.open()
        engine.answer()
        if (!fromTelecom) lifecycleScope.launch { control?.answer(CallAttributesCompat.CALL_TYPE_AUDIO_CALL) }
    }

    /** Follows the call: the notification, the socket, Android's call system, and the service's own end. */
    private fun changed(before: CallView?, v: CallView?) {
        if (v == null) {
            socket.close()
            disconnect(before)
            stop()
            return
        }
        if (before == null || before.id != v.id && before.id != null) telecom(v)
        when (v.phase) {
            Phase.INCOMING -> show(ringing(v.peer), ServiceInfo.FOREGROUND_SERVICE_TYPE_PHONE_CALL)
            Phase.OUTGOING, Phase.CONNECTING, Phase.ACTIVE -> {
                if (v.mine) socket.open()
                show(ongoing(v), ServiceInfo.FOREGROUND_SERVICE_TYPE_PHONE_CALL or ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE)
                if (v.phase == Phase.ACTIVE && before?.phase != Phase.ACTIVE && v.mine) lifecycleScope.launch { control?.setActive() }
            }
            Phase.ENDED -> { socket.close(); disconnect(v) }
        }
    }

    /** The call, told to Android's call system. Refused (another app's call is being set up), the call goes on without it. */
    private fun telecom(v: CallView) {
        telecom?.cancel()
        control = null
        val attributes = CallAttributesCompat(v.peer.name, Uri.fromParts("gom", v.id ?: v.conversation, null),
            if (v.mine) CallAttributesCompat.DIRECTION_OUTGOING else CallAttributesCompat.DIRECTION_INCOMING,
            CallAttributesCompat.CALL_TYPE_AUDIO_CALL, 0)
        telecom = lifecycleScope.launch {
            try {
                calls.addCall(attributes,
                    onAnswer = { answer(fromTelecom = true) },
                    onDisconnect = { engine.hangUp() },
                    onSetActive = { },
                    // A regular phone call wants the line: Gom's call cannot be held, so it ends (decision 5).
                    onSetInactive = { engine.hangUp() },
                ) {
                    control = this
                    launch { isMuted.collect { muted -> if (engine.view.value?.muted != muted) engine.mute(muted) } }
                    launch { availableEndpoints.collect { list -> endpoints = list; routes.value = Routes(list.map(::routeOf), routes.value.current) } }
                    launch { currentCallEndpoint.collect { now -> routes.value = routes.value.copy(current = routeOf(now)); earLock() } }
                }
            } catch (_: CallException) { control = null }
        }
    }

    private var endpoints: List<CallEndpointCompat> = emptyList()

    /** Where the sound goes: through Android's call system, which Google asks of calling apps (not AudioManager directly). */
    private fun route(id: String) {
        val endpoint = endpoints.firstOrNull { it.identifier.toString() == id } ?: return
        lifecycleScope.launch { control?.requestEndpointChange(endpoint) }
    }

    /** The screen goes off at the ear only while the earpiece plays a call that goes on. */
    private fun earLock() {
        val lock = ear ?: return
        val v = engine.view.value
        val want = v != null && v.phase in setOf(Phase.OUTGOING, Phase.CONNECTING, Phase.ACTIVE) && routes.value.current?.kind == RouteKind.EARPIECE
        if (want && !lock.isHeld) lock.acquire(EAR_MAX_MS)
        else if (!want && lock.isHeld) lock.release(PowerManager.RELEASE_FLAG_WAIT_FOR_NO_PROXIMITY)
    }

    private fun disconnect(v: CallView?) {
        val scope = control ?: return
        control = null
        val cause = when (v?.ending) {
            null, is Ending.Talked, Ending.Ended -> DisconnectCause.LOCAL
            Ending.NoAnswer, Ending.AnsweredElsewhere -> if (v.mine) DisconnectCause.REMOTE else DisconnectCause.MISSED
            Ending.DeclinedByThem, Ending.Busy -> DisconnectCause.REMOTE
            else -> DisconnectCause.ERROR
        }
        lifecycleScope.launch { try { scope.disconnect(DisconnectCause(cause)) } catch (_: IllegalStateException) { } }
    }

    private fun person(peer: Peer): Person = Person.Builder().setName(peer.name).setImportant(true).build()

    /**
     * Ringing: the name once unlocked; on the lock screen only what the person's notification level allows (decision 8), which is
     * the server's title ("Gom" at "nothing").
     */
    private fun ringing(peer: Peer): Notification {
        val hidden = locked.value
        val public = NotificationCompat.Builder(this, CALLS).setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(hidden ?: getString(R.string.call_voice)).setContentText(getString(R.string.call_incoming)).build()
        return NotificationCompat.Builder(this, CALLS)
            .setSmallIcon(R.drawable.ic_notification)
            .setColor(getColor(R.color.gom))
            .setContentTitle(peer.name)
            .setContentText(getString(R.string.call_incoming))
            .setStyle(NotificationCompat.CallStyle.forIncomingCall(person(peer), pending(HANG_UP), screen(CallActivity.ANSWER)))
            .setCategory(NotificationCompat.CATEGORY_CALL)
            .setPriority(NotificationCompat.PRIORITY_MAX)
            .setOngoing(true)
            .setVisibility(if (hidden == null) NotificationCompat.VISIBILITY_PUBLIC else NotificationCompat.VISIBILITY_PRIVATE)
            .setPublicVersion(public)
            .setFullScreenIntent(screen(null), true)
            .setContentIntent(screen(null))
            .build()
            .apply { flags = flags or Notification.FLAG_INSISTENT }   // the ringtone repeats until answered or stopped
    }

    private fun ongoing(v: CallView): Notification = NotificationCompat.Builder(this, CALLS)
        .setSmallIcon(R.drawable.ic_notification)
        .setColor(getColor(R.color.gom))
        .setContentTitle(v.peer.name)
        .setContentText(getString(when {
            v.downSince != null -> R.string.call_reconnecting
            v.phase == Phase.ACTIVE -> R.string.call_voice
            v.phase == Phase.CONNECTING -> R.string.call_connecting
            v.ringing -> R.string.call_ringing
            else -> R.string.call_calling
        }))
        .setStyle(NotificationCompat.CallStyle.forOngoingCall(person(v.peer), pending(HANG_UP)))
        .setCategory(NotificationCompat.CATEGORY_CALL)
        .setOngoing(true)
        .setSilent(true)
        .apply { v.connectedAt?.let { setUsesChronometer(true).setWhen(it).setShowWhen(true) } }
        .setContentIntent(screen(null))
        .build()

    /** The call screen, over the lock screen; [action] [CallActivity.ANSWER] answers once the microphone is allowed. */
    private fun screen(action: String?): PendingIntent {
        val intent = Intent(this, CallActivity::class.java).setAction(action ?: Intent.ACTION_MAIN)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        return PendingIntent.getActivity(this, if (action == null) OPEN else ANSWERING, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
    }

    private fun pending(action: String): PendingIntent =
        PendingIntent.getService(this, action.hashCode(), Intent(this, CallService::class.java).setAction(action), PendingIntent.FLAG_IMMUTABLE)

    private fun show(notification: Notification, type: Int) {
        val allowed = if (hasMic()) type else type and ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE.inv()
        if (foreground == allowed) { notify(notification); return }
        ServiceCompat.startForeground(this, CALL_ID, notification, allowed)
        foreground = allowed
    }

    @Suppress("MissingPermission")   // a call's notification is the foreground service's: Android shows it without the permission
    private fun notify(notification: Notification) = NotificationManagerCompat.from(this).let {
        try { it.notify(CALL_ID, notification) } catch (_: SecurityException) { }
    }

    private fun hasMic() = hasMic(this)

    override fun onDestroy() {
        running = false
        socket.close()
        if (ear?.isHeld == true) ear?.release()
        shown.value = null
        locked.value = null
        routes.value = Routes()
        super.onDestroy()
    }

    private fun stop() {
        ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        foreground = 0
        stopSelf()
    }

    private fun stopIfIdle(): Int { if (engine.view.value == null && foreground == 0) stopSelf(); return START_NOT_STICKY }

    companion object {
        const val CALLS = "calls"
        const val MISSED_CALLS = "missed_calls"
        private const val CALL_ID = 2
        private const val OPEN = 3
        private const val ANSWERING = 4
        /** A call longer than this keeps the screen-off lock only until then (a lock is never held for ever). */
        private const val EAR_MAX_MS = 6 * 60 * 60 * 1000L

        private const val DIAL = "io.github.quaint666.gom.call.DIAL"
        private const val RING = "io.github.quaint666.gom.call.RING"
        private const val ANSWER = "io.github.quaint666.gom.call.ANSWER"
        private const val HANG_UP = "io.github.quaint666.gom.call.HANG_UP"
        private const val MUTE = "io.github.quaint666.gom.call.MUTE"
        private const val ROUTE = "io.github.quaint666.gom.call.ROUTE"
        private const val CLOSE = "io.github.quaint666.gom.call.CLOSE"
        private const val ENDED = "io.github.quaint666.gom.call.ENDED"
        private const val EXTRA_ID = "id"
        private const val EXTRA_CONVERSATION = "conversation"
        private const val EXTRA_NAME = "name"
        private const val EXTRA_AVATAR = "avatar"
        private const val EXTRA_OUTCOME = "outcome"
        private const val EXTRA_ON = "on"

        /** Whether this build makes and takes calls; the page offers calls only then (its hello says so). */
        const val AVAILABLE = true

        private val shown = MutableStateFlow<CallView?>(null)
        private val locked = MutableStateFlow<String?>(null)
        private val routes = MutableStateFlow(Routes())

        /** The call here, for the call screen; null when there is none. */
        val view: StateFlow<CallView?> = shown
        /** What the lock screen may say instead of the caller's name ("Gom" at the "nothing" level); null when it may name them. */
        val lockedTitle: StateFlow<String?> = locked
        /** Where the sound can go, and where it goes now. */
        val sound: StateFlow<Routes> = routes

        private fun command(context: Context, action: String, extra: Intent.() -> Unit = {}) {
            if (running) context.startService(Intent(context, CallService::class.java).setAction(action).apply(extra))
        }

        fun answer(context: Context) = command(context, ANSWER)
        fun hangUp(context: Context) = command(context, HANG_UP)
        fun mute(context: Context, on: Boolean) = command(context, MUTE) { putExtra(EXTRA_ON, on) }
        fun route(context: Context, id: String) = command(context, ROUTE) { putExtra(EXTRA_ID, id) }
        fun close(context: Context) = command(context, CLOSE)

        /** Whether the service runs (a call is on here, ringing or not). */
        @Volatile var running = false
            private set

        /**
         * The page asked to call. Only with the microphone allowed, since the service starts as soon as it is asked and must then
         * show the call at once; the app is on screen, so the microphone may start.
         */
        fun dial(context: Context, conversation: String, name: String, avatar: String?) {
            if (!CallProtocol.isId(conversation) || !hasMic(context)) return
            ContextCompat.startForegroundService(context, Intent(context, CallService::class.java).setAction(DIAL)
                .putExtra(EXTRA_CONVERSATION, conversation).putExtra(EXTRA_NAME, name).putExtra(EXTRA_AVATAR, avatar))
        }

        /** FCM says a call rings (a high-priority push lets the app start a foreground service from the background). */
        fun ring(context: Context, id: String) {
            if (!CallProtocol.isId(id)) return
            ContextCompat.startForegroundService(context, Intent(context, CallService::class.java).setAction(RING).putExtra(EXTRA_ID, id))
        }

        /** FCM says a call stopped ringing here, and how it ended: a call ringing or in progress here hears it. */
        fun ended(context: Context, id: String, outcome: String) {
            if (!running) return
            context.startService(Intent(context, CallService::class.java).setAction(ENDED).putExtra(EXTRA_ID, id).putExtra(EXTRA_OUTCOME, outcome))
        }

        fun hasMic(context: Context) =
            ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED

        /** The two call channels: ringing (the ringtone, over everything) and missed calls. */
        fun channels(context: Context) {
            val manager = context.getSystemService(NotificationManager::class.java)
            val ring = android.media.AudioAttributes.Builder().setUsage(android.media.AudioAttributes.USAGE_NOTIFICATION_RINGTONE)
                .setContentType(android.media.AudioAttributes.CONTENT_TYPE_SONIFICATION).build()
            manager.createNotificationChannel(android.app.NotificationChannel(CALLS, context.getString(R.string.channel_calls),
                NotificationManager.IMPORTANCE_HIGH).apply {
                setSound(android.provider.Settings.System.DEFAULT_RINGTONE_URI, ring)
                enableVibration(true)
                lockscreenVisibility = Notification.VISIBILITY_PRIVATE
            })
            manager.createNotificationChannel(android.app.NotificationChannel(MISSED_CALLS, context.getString(R.string.channel_missed_calls),
                NotificationManager.IMPORTANCE_HIGH))
        }
    }
}
