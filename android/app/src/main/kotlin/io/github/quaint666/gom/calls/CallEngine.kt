package io.github.quaint666.gom.calls

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch

/** How the connection is doing, as WebRTC reports it. */
enum class Link { CONNECTED, DOWN, FAILED }

/** One call's connection with the microphone on it (6d: WebRTC). Its offers and answers carry every address (no trickle). */
interface Connection {
    /** A new offer, every address gathered or what there is after [CallProtocol.GATHER_MS]; [restart] gathers them all again. */
    suspend fun offer(restart: Boolean = false): String
    /** Takes the other side's offer and gives this side's answer. */
    suspend fun answer(offer: String): String
    /** Takes the other side's answer to this side's offer. */
    suspend fun accept(answer: String)
    /** Whether an offer of this side's waits for its answer, and whether nothing is in flight (only then may a restart begin). */
    val waitingForAnswer: Boolean
    val stable: Boolean
    /** Fresh relay logins for the connection (a long call). */
    fun servers(list: List<IceServer>)
    fun mute(on: Boolean)
    fun close()
}

/** Opens a call's connection, or null when the microphone cannot be had. [link] hears how the connection is doing. */
fun interface Media {
    fun open(servers: List<IceServer>, link: (Link) -> Unit): Connection?
}

enum class Phase { OUTGOING, INCOMING, CONNECTING, ACTIVE, ENDED }

/** How a call ended, for the one reading it; [problem]s stay until closed, the others close after [CallProtocol.ENDED_MS]. */
sealed interface Ending {
    val problem: Boolean get() = false

    data class Talked(val seconds: Long) : Ending
    data object Ended : Ending
    data object NoAnswer : Ending
    data object DeclinedByThem : Ending
    data object Busy : Ending
    data object AnsweredElsewhere : Ending
    data object Failed : Ending
    data object NeedMic : Ending { override val problem get() = true }
    /** The server refused (its error code; null when there was no answer at all). */
    data class Refused(val error: String?) : Ending { override val problem get() = true }
}

data class Peer(val name: String, val avatar: String? = null)

/** What the call screens and the notification show. Null when there is no call. */
data class CallView(
    val id: String?,
    val conversation: String,
    val peer: Peer,
    val mine: Boolean,
    val phase: Phase,
    /** The caller's phone rings out (the other side's phone is ringing). */
    val ringing: Boolean = false,
    val muted: Boolean = false,
    val connectedAt: Long? = null,
    /** When the connection went down, while it is down ("reconnecting"). */
    val downSince: Long? = null,
    val ending: Ending? = null,
)

/**
 * One call at a time, as the page's `createCalls` (`Scripts/calls/call.ts`) runs it, without its screen: the call steps with the
 * server, the connection, keeping it alive, restarting it after a network change (the caller only), and renewing relay logins.
 * Everything runs on [scope]'s thread; [now] is in milliseconds.
 */
class CallEngine(
    private val server: CallServer,
    private val media: Media,
    private val scope: CoroutineScope,
    private val now: () -> Long,
    private val newId: () -> String,
) {
    private val state = MutableStateFlow<CallView?>(null)
    val view: StateFlow<CallView?> = state

    private class Current(var view: CallView) {
        var connection: Connection? = null
        val jobs = mutableListOf<Job>()
        /** Hung up before the server gave the call its id: hung up as soon as it does. */
        var cancelled = false
        var rev = 0
        var ringSeconds = 0L
    }

    private var cur: Current? = null
    /** Calls the server said had ended before this side knew their id (a decline faster than the start's answer). */
    private val endedEarly = mutableMapOf<String, String>()

    private fun live(c: Current?): Boolean = c != null && c === cur && c.view.phase != Phase.ENDED

    private fun show(c: Current, change: (CallView) -> CallView) {
        c.view = change(c.view)
        if (c === cur) state.value = c.view
    }

    private fun later(c: Current, ms: Long, block: suspend () -> Unit) {
        c.jobs += scope.launch { delay(ms); if (c === cur) block() }
    }

    /** Lets go of the connection, the microphone and the timers. */
    private fun release(c: Current) {
        for (j in c.jobs) j.cancel()
        c.jobs.clear()
        c.connection?.close()
        c.connection = null
    }

    /** The call is over here: says how, then closes (by itself, or when closed if it needs reading). Null closes at once. */
    private fun finish(c: Current, ending: Ending?) {
        if (c !== cur || c.view.phase == Phase.ENDED) return
        release(c)
        if (ending == null) { cur = null; state.value = null; return }
        show(c) { it.copy(phase = Phase.ENDED, ending = ending, downSince = null, ringing = false) }
        if (!ending.problem) later(c, CallProtocol.ENDED_MS) { close() }
    }

    /** Closes the ended call's screen. */
    fun close() {
        val c = cur ?: return
        release(c)
        cur = null
        state.value = null
    }

    /** What the server's outcome means to this side. The person called hears nothing of a call that never rang through. */
    private fun endingOf(c: Current, outcome: String): Ending? {
        c.view.connectedAt?.let { return Ending.Talked((now() - it + 500) / 1000) }
        if (outcome == "failed") return Ending.Failed
        if (!c.view.mine) return null
        return when (outcome) {
            "missed" -> Ending.NoAnswer
            "declined" -> Ending.DeclinedByThem
            "busy" -> Ending.Busy
            else -> Ending.Ended
        }
    }

    private fun connect(c: Current, servers: List<IceServer>): Connection? {
        val connection = media.open(servers) { link -> scope.launch { onLink(c, link) } } ?: return null
        c.connection = connection
        return connection
    }

    private fun onLink(c: Current, link: Link) {
        if (!live(c)) return
        when {
            link == Link.CONNECTED -> {
                if (c.view.phase != Phase.ACTIVE) show(c) { it.copy(phase = Phase.ACTIVE, connectedAt = it.connectedAt ?: now(), downSince = null) }
                else show(c) { it.copy(downSince = null) }
            }
            c.view.phase == Phase.ACTIVE -> {
                if (c.view.downSince != null) return
                show(c) { it.copy(downSince = now()) }
                later(c, CallProtocol.RECONNECT_AFTER_MS) { down(c) }
            }
            link == Link.FAILED -> {
                c.view.id?.let { id -> scope.launch { server.hangUp(id) } }
                finish(c, Ending.Failed)
            }
        }
    }

    /** Still down: the caller restarts the connection (only the caller, so the two never offer at once), and either gives up. */
    private suspend fun down(c: Current) {
        val since = c.view.downSince
        if (!live(c) || since == null) return
        if (now() - since >= CallProtocol.GIVE_UP_MS) {
            c.view.id?.let { server.hangUp(it) }
            finish(c, Ending.Failed)
            return
        }
        if (c.view.mine) scope.launch { restart(c) }
        later(c, CallProtocol.RECONNECT_EVERY_MS) { down(c) }
    }

    /** A new offer that gathers every address again (an ICE restart), sent through the server; the answer comes as a frame. */
    private suspend fun restart(c: Current) {
        val connection = c.connection
        val id = c.view.id
        if (!live(c) || connection == null || id == null || !connection.stable) return
        val offer = connection.offer(restart = true)
        if (live(c)) server.sdp(id, "offer", offer)
    }

    /** A mid-call offer (the caller restarted) is answered; an answer (to my restart) is taken. Older ones are ignored. */
    private suspend fun renegotiate(c: Current, d: Description) {
        val connection = c.connection
        val id = c.view.id
        if (!live(c) || connection == null || id == null || d.rev <= c.rev) return
        c.rev = d.rev
        if (d.type == "answer") {
            if (c.view.mine && connection.waitingForAnswer) connection.accept(d.sdp)
            return
        }
        if (c.view.mine) return
        val answer = connection.answer(d.sdp)
        if (live(c)) server.sdp(id, "answer", answer)
    }

    /** Fresh relay logins before the old ones run out (decision 22); the caller restarts once the other side has surely renewed. */
    private fun renewLater(c: Current, seconds: Long) {
        if (seconds > 0) later(c, CallProtocol.renewAfterMs(seconds)) { renew(c) }
    }

    private suspend fun renew(c: Current) {
        val id = c.view.id
        if (!live(c) || id == null || c.connection == null) return
        val servers = server.servers(call = id)
        if (!live(c)) return
        if (servers !is Reply.Ok) { later(c, CallProtocol.RENEW_RETRY_MS) { renew(c) }; return }
        c.connection?.servers(servers.data.iceServers)
        renewLater(c, servers.data.seconds)
        if (c.view.mine) later(c, (c.ringSeconds + CallProtocol.RESTART_AFTER_RING_S) * 1000) { restart(c) }
    }

    private fun keepAlive(c: Current, seconds: Long) {
        val every = maxOf(1, seconds) * 1000
        c.jobs += scope.launch {
            while (true) {
                delay(every)
                if (!live(c)) return@launch
                val id = c.view.id ?: continue
                val r = server.alive(id)
                if (r is Reply.Failed && r.error == "call_ended") finish(c, endingOf(c, "completed"))
            }
        }
    }

    private fun begin(c: Current) {
        cur = c
        state.value = c.view
    }

    /** Calls the friend of this one-to-one chat. Does nothing while a call is on. */
    fun start(conversation: String, peer: Peer) {
        if (cur != null) return
        val c = Current(CallView(null, conversation, peer, mine = true, phase = Phase.OUTGOING))
        begin(c)
        scope.launch { dial(c) }
    }

    private suspend fun dial(c: Current) {
        val servers = server.servers(conversation = c.view.conversation)
        if (!live(c)) return
        if (servers !is Reply.Ok) { finish(c, Ending.Refused((servers as Reply.Failed).error)); return }
        val connection = connect(c, servers.data.iceServers) ?: run { finish(c, Ending.NeedMic); return }
        renewLater(c, servers.data.seconds)
        val offer = connection.offer()
        if (!live(c)) return
        val started = server.start(c.view.conversation, newId(), offer)
        if (started !is Reply.Ok) { if (live(c)) finish(c, Ending.Refused((started as Reply.Failed).error)); return }
        val call = started.data
        if (c.cancelled || !live(c)) { server.hangUp(call.id); return }
        show(c) { it.copy(id = call.id) }
        c.ringSeconds = call.ringSeconds
        val early = endedEarly.remove(call.id)
        if (call.state == "ended" || early != null) { finish(c, endingOf(c, call.outcome ?: early ?: "failed")); return }
        show(c) { it.copy(ringing = true) }
        keepAlive(c, call.aliveSeconds)
    }

    /** A call ringing for this person (from a push, or the socket). Ignored while another call is on: the server made it busy. */
    fun ringing(id: String, conversation: String, peer: Peer) {
        if (cur != null) return
        begin(Current(CallView(id, conversation, peer, mine = false, phase = Phase.INCOMING)))
    }

    /** Answers the call ringing here. */
    fun answer() {
        val c = cur ?: return
        if (!live(c) || c.view.phase != Phase.INCOMING) return
        show(c) { it.copy(phase = Phase.CONNECTING) }
        scope.launch { pickUp(c) }
    }

    private suspend fun pickUp(c: Current) {
        val id = c.view.id ?: return
        val servers = server.servers(call = id)
        val info = server.get(id)
        if (!live(c)) return
        if (servers !is Reply.Ok) { finish(c, Ending.Refused((servers as Reply.Failed).error)); return }
        if (info !is Reply.Ok) { finish(c, Ending.Refused((info as Reply.Failed).error)); return }
        val offer = info.data.offer ?: run { finish(c, if (info.data.state == "ended") null else Ending.Failed); return }
        c.ringSeconds = info.data.ringSeconds
        val connection = connect(c, servers.data.iceServers) ?: run { server.decline(id); finish(c, Ending.NeedMic); return }
        renewLater(c, servers.data.seconds)
        val answer = connection.answer(offer)
        if (!live(c)) return
        when (val accepted = server.accept(id, answer)) {
            is Reply.Ok -> keepAlive(c, accepted.data.aliveSeconds)
            is Reply.Failed -> if (live(c)) when (accepted.error) {
                "call_answered" -> finish(c, Ending.AnsweredElsewhere)
                "call_ended" -> finish(c, null)
                else -> finish(c, Ending.Refused(accepted.error))
            }
        }
    }

    /** Ends the call from this side: declines one ringing here, gives up one ringing out, hangs up one in progress. */
    fun hangUp() {
        val c = cur ?: return
        if (!live(c)) return
        val id = c.view.id
        if (c.view.phase == Phase.INCOMING) {
            if (id != null) scope.launch { server.decline(id) }
            finish(c, null)
            return
        }
        if (id == null) c.cancelled = true else scope.launch { server.hangUp(id) }
        finish(c, endingOf(c, "completed"))
    }

    fun mute(on: Boolean) {
        val c = cur ?: return
        if (!live(c)) return
        c.connection?.mute(on)
        show(c) { it.copy(muted = on) }
    }

    /** A call frame from the socket. */
    fun frame(f: Frame) {
        val c = cur
        if (f is Frame.Incoming) return   // a second call: the server makes it busy; a first one comes by push
        if (c == null || c.view.id != f.id) {
            if (f is Frame.Ended && c != null && c.view.mine && c.view.id == null) endedEarly[f.id] = f.outcome
            return
        }
        when (f) {
            is Frame.Accepted -> {
                val connection = c.connection
                if (!c.view.mine || !live(c) || connection == null || c.view.phase != Phase.OUTGOING) return
                show(c) { it.copy(phase = Phase.CONNECTING, ringing = false) }
                scope.launch { connection.accept(f.sdp) }
            }
            is Frame.Sdp -> scope.launch { renegotiate(c, f.description) }
            is Frame.AnsweredElsewhere -> if (c.view.phase == Phase.INCOMING) finish(c, null)
            is Frame.Ended -> finish(c, endingOf(c, f.outcome))
            is Frame.Incoming -> Unit
        }
    }

    /** The call ended on the server (a push said so; the socket may be down). */
    fun ended(id: String, outcome: String) = frame(Frame.Ended(id, outcome))

    /**
     * The socket came back: what happened while it was away is asked for (an answer, a mid-call offer, the end), as the server keeps
     * the newest of each for a device that missed its frame.
     */
    fun resync() {
        val c = cur ?: return
        val id = c.view.id ?: return
        if (!live(c)) return
        scope.launch {
            val info = (server.get(id) as? Reply.Ok)?.data ?: return@launch
            if (!live(c)) return@launch
            when {
                info.state == "ended" -> finish(c, endingOf(c, info.outcome ?: "completed"))
                c.view.mine && c.view.phase == Phase.OUTGOING && info.answer != null -> frame(Frame.Accepted(id, info.answer))
                info.sdp != null -> renegotiate(c, info.sdp)
            }
        }
    }
}
