package io.github.quaint666.gom.calls

import android.content.Context
import android.media.AudioAttributes
import android.util.Log
import kotlin.coroutines.resume
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.delay
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withTimeoutOrNull
import org.webrtc.AudioSource
import org.webrtc.AudioTrack
import org.webrtc.DataChannel
import org.webrtc.IceCandidate
import org.webrtc.MediaConstraints
import org.webrtc.MediaStream
import org.webrtc.PeerConnection
import org.webrtc.PeerConnectionFactory
import org.webrtc.RtpReceiver
import org.webrtc.SdpObserver
import org.webrtc.SessionDescription
import org.webrtc.audio.JavaAudioDeviceModule

/**
 * The call's sound (decision 34): Google's WebRTC as `io.github.webrtc-sdk` builds it. The microphone with the phone's own echo and
 * noise removal, played as a call's voice (`USAGE_VOICE_COMMUNICATION`, the voice-call stream Android's call guide asks for), so
 * Android's call system can send it to the earpiece, the speaker or a headset. Where the sound goes is Telecom's; WebRTC does no
 * routing of its own.
 */
class WebRtcMedia(context: Context) : Media {
    private val app = context.applicationContext

    override fun open(servers: List<IceServer>, link: (Link) -> Unit): Connection? {
        if (!CallService.hasMic(app)) return null
        initialize(app)
        val voice = AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_VOICE_COMMUNICATION)
            .setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build()
        val adm = JavaAudioDeviceModule.builder(app)
            .setUseHardwareAcousticEchoCanceler(true)
            .setUseHardwareNoiseSuppressor(true)
            .setAudioAttributes(voice)
            .createAudioDeviceModule()
        val factory = PeerConnectionFactory.builder().setAudioDeviceModule(adm).createPeerConnectionFactory()
        val source = factory.createAudioSource(MediaConstraints())
        val track = factory.createAudioTrack("mic", source)
        return RtcConnection(factory, adm, source, track, servers, link).takeIf { it.ready }
            ?: run { track.dispose(); source.dispose(); factory.dispose(); adm.release(); null }
    }

    private companion object {
        @Volatile var initialized = false

        fun initialize(context: Context) {
            if (initialized) return
            synchronized(this) {
                if (initialized) return
                PeerConnectionFactory.initialize(PeerConnectionFactory.InitializationOptions.builder(context).createInitializationOptions())
                initialized = true
            }
        }
    }
}

private class RtcConnection(
    private val factory: PeerConnectionFactory,
    private val adm: JavaAudioDeviceModule,
    private val source: AudioSource,
    private val track: AudioTrack,
    servers: List<IceServer>,
    private val link: (Link) -> Unit,
) : Connection {
    private val config = PeerConnection.RTCConfiguration(servers.map(::iceServer)).apply {
        sdpSemantics = PeerConnection.SdpSemantics.UNIFIED_PLAN
    }
    /** Completed when the addresses are all gathered; a new one for each gathering (a restart gathers again). */
    @Volatile private var gathered = CompletableDeferred<Unit>()
    /** Whether it ever connected, for the log when it closes. */
    @Volatile private var connected = false

    /** The connection; replaced only before anything is sent, when its first offer or answer had no addresses (see [withAddresses]). */
    @Volatile private var pc: PeerConnection? = newPc()

    /** A connection carrying the microphone, whose news counts only while it is the one in use. */
    private fun newPc(): PeerConnection? {
        val watch = Watch()
        return factory.createPeerConnection(config, watch)?.also { watch.of = it; it.addTrack(track, listOf("gom")) }
    }

    private inner class Watch : PeerConnection.Observer {
        var of: PeerConnection? = null
        private val current get() = of != null && of === pc
        override fun onConnectionChange(state: PeerConnection.PeerConnectionState) {
            if (!current) return
            when (state) {
                PeerConnection.PeerConnectionState.CONNECTED -> { connected = true; logChosen("connected"); link(Link.CONNECTED) }
                PeerConnection.PeerConnectionState.DISCONNECTED -> { Log.i(CallLog.TAG, "down"); link(Link.DOWN) }
                PeerConnection.PeerConnectionState.FAILED -> { logChosen("failed"); link(Link.FAILED) }
                else -> Unit
            }
        }
        override fun onIceGatheringChange(state: PeerConnection.IceGatheringState) {
            if (current && state == PeerConnection.IceGatheringState.COMPLETE) gathered.complete(Unit)
        }
        override fun onSignalingChange(state: PeerConnection.SignalingState) = Unit
        override fun onIceConnectionChange(state: PeerConnection.IceConnectionState) = Unit
        override fun onIceConnectionReceivingChange(receiving: Boolean) = Unit
        override fun onIceCandidate(candidate: IceCandidate) = Unit   // no trickle: every address goes in the offer or answer
        override fun onIceCandidatesRemoved(candidates: Array<out IceCandidate>) = Unit
        override fun onAddStream(stream: MediaStream) = Unit
        override fun onRemoveStream(stream: MediaStream) = Unit
        override fun onDataChannel(channel: DataChannel) = Unit
        override fun onRenegotiationNeeded() = Unit
        override fun onAddTrack(receiver: RtpReceiver, streams: Array<out MediaStream>) = Unit   // the other side's voice plays by itself
    }

    val ready: Boolean get() = pc != null

    override val waitingForAnswer: Boolean get() = pc?.signalingState() == PeerConnection.SignalingState.HAVE_LOCAL_OFFER
    override val stable: Boolean get() = pc?.signalingState() == PeerConnection.SignalingState.STABLE

    override suspend fun offer(restart: Boolean): String {
        if (restart) {   // mid-call: the connection in use restarts; with no network it has no addresses, and the call waits for one
            val pc = pc ?: return ""
            pc.restartIce()
            return local(pc, create { pc.createOffer(it, audioOnly) })
        }
        return withAddresses { pc -> local(pc, create { pc.createOffer(it, audioOnly) }) }
    }

    override suspend fun answer(offer: String): String {
        Log.i(CallLog.TAG, "offer there: ${CallLog.kinds(offer)}")
        return withAddresses { pc ->
            set { pc.setRemoteDescription(it, SessionDescription(SessionDescription.Type.OFFER, offer)) }
            local(pc, create { pc.createAnswer(it, audioOnly) })
        }
    }

    /**
     * A call's first offer or answer, made by [make], with addresses in it. WebRTC learns the phone's networks only once a
     * connection starts gathering, a few milliseconds late, and a connection that gathered before then has none and never gathers
     * again: such an offer or answer is never sent. A new connection made a moment later, while the first still holds WebRTC's
     * watch on the networks, makes it again; the first then goes.
     */
    private suspend fun withAddresses(make: suspend (PeerConnection) -> String): String {
        var sdp = make(pc ?: return "")
        for (attempt in 2..ATTEMPTS) {
            if (sdp.isEmpty() || CallLog.hasAddresses(sdp)) break
            Log.i(CallLog.TAG, "no addresses yet, trying again ($attempt of $ATTEMPTS)")
            delay(RETRY_MS)
            val old = pc
            pc = newPc() ?: break
            sdp = make(pc ?: break)
            old?.dispose()
        }
        return sdp
    }

    override suspend fun accept(answer: String) {
        val pc = pc ?: return
        Log.i(CallLog.TAG, "answer there: ${CallLog.kinds(answer)}")
        set { pc.setRemoteDescription(it, SessionDescription(SessionDescription.Type.ANSWER, answer)) }
    }

    override fun servers(list: List<IceServer>) {
        config.iceServers = list.map(::iceServer)
        pc?.setConfiguration(config)
    }

    override fun mute(on: Boolean) { track.setEnabled(!on) }

    override fun close() {
        if (!connected) Log.i(CallLog.TAG, "closed before it connected")
        pc?.dispose()
        track.dispose()
        source.dispose()
        factory.dispose()
        adm.release()
    }

    /** Sets this side's offer or answer, then waits for every address, or [CallProtocol.GATHER_MS]; a gathering left from before does not count. */
    private suspend fun local(pc: PeerConnection, description: SessionDescription?): String {
        if (description == null) return ""
        gathered = CompletableDeferred()
        set { pc.setLocalDescription(it, description) }
        val complete = withTimeoutOrNull(CallProtocol.GATHER_MS) { gathered.await() } != null
        val sdp = pc.localDescription?.description.orEmpty()
        Log.i(CallLog.TAG, "${description.type.canonicalForm()} here: ${CallLog.kinds(sdp)}${if (complete) "" else " (still gathering after ${CallProtocol.GATHER_MS} ms)"}")
        return sdp
    }

    /** Writes which pair of addresses the connection uses, or tried last. */
    private fun logChosen(what: String) {
        pc?.getStats { report ->
            val stats = report.statsMap.mapValues { (_, s) -> CallLog.Stat(s.type, s.members) }
            Log.i(CallLog.TAG, "$what: ${CallLog.chosen(stats) ?: "no pair chosen"}")
        }
    }

    private suspend fun create(make: (SdpObserver) -> Unit): SessionDescription? = suspendCancellableCoroutine { k ->
        make(object : SdpObserver {
            override fun onCreateSuccess(d: SessionDescription) { if (k.isActive) k.resume(d) }
            override fun onCreateFailure(error: String?) { if (k.isActive) k.resume(null) }
            override fun onSetSuccess() = Unit
            override fun onSetFailure(error: String?) = Unit
        })
    }

    private suspend fun set(apply: (SdpObserver) -> Unit): Boolean = suspendCancellableCoroutine { k ->
        apply(object : SdpObserver {
            override fun onCreateSuccess(d: SessionDescription) = Unit
            override fun onCreateFailure(error: String?) = Unit
            override fun onSetSuccess() { if (k.isActive) k.resume(true) }
            override fun onSetFailure(error: String?) { if (k.isActive) k.resume(false) }
        })
    }

    private companion object {
        /** Tries at an offer or answer with addresses, and the wait between them (see [withAddresses]). */
        const val ATTEMPTS = 3
        const val RETRY_MS = 200L

        val audioOnly = MediaConstraints().apply {
            mandatory.add(MediaConstraints.KeyValuePair("OfferToReceiveAudio", "true"))
            mandatory.add(MediaConstraints.KeyValuePair("OfferToReceiveVideo", "false"))
        }

        fun iceServer(s: IceServer): PeerConnection.IceServer = PeerConnection.IceServer.builder(s.urls)
            .apply { s.username?.let { setUsername(it) }; s.credential?.let { setPassword(it) } }
            .createIceServer()
    }
}
