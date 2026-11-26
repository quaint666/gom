package io.github.quaint666.gom.calls

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The app's calls, as the page's are tested (`ScriptTests/calls.test.ts`): a fake server, a fake connection, and virtual time. */
@OptIn(ExperimentalCoroutinesApi::class)
class CallEngineTest {
    private val id = "0b6c7f6e-3c1a-4b8e-9d55-1f2a3b4c5d6e"
    private val chat = "6f1e2d3c-4b5a-4968-8776-655443322110"
    private val stun = listOf(IceServer(listOf("stun:s.example")))

    private fun info(state: String = "ringing", offer: String? = "v=0 offer", answer: String? = null, outcome: String? = null, sdp: Description? = null) =
        CallInfo(id, chat, state, outcome, "Minh", offer, answer, sdp, 45, 30)

    /** Records each step and answers as told. */
    private class FakeServer : CallServer {
        val asked = mutableListOf<String>()
        var servers: Reply<Servers> = Reply.Ok(Servers(listOf(IceServer(listOf("stun:s.example"))), relay = false, seconds = 10_800))
        var start: Reply<CallInfo>? = null
        /** Holds the start's answer until completed (a slow server). */
        var startGate: CompletableDeferred<Unit>? = null
        var get: Reply<CallInfo>? = null
        var accept: Reply<CallInfo>? = null
        var alive: Reply<Unit> = Reply.Ok(Unit)

        override suspend fun servers(conversation: String?, call: String?): Reply<Servers> { asked += "servers ${conversation ?: call}"; return servers }
        override suspend fun start(conversation: String, clientId: String, offer: String): Reply<CallInfo> { asked += "start $conversation $offer"; startGate?.await(); return start!! }
        override suspend fun get(id: String): Reply<CallInfo> { asked += "get"; return get!! }
        override suspend fun accept(id: String, answer: String): Reply<CallInfo> { asked += "accept $answer"; return accept!! }
        override suspend fun decline(id: String): Reply<Unit> { asked += "decline"; return Reply.Ok(Unit) }
        override suspend fun hangUp(id: String): Reply<Unit> { asked += "hangup $id"; return Reply.Ok(Unit) }
        override suspend fun sdp(id: String, type: String, sdp: String): Reply<Int> { asked += "sdp $type $sdp"; return Reply.Ok(1) }
        override suspend fun alive(id: String): Reply<Unit> { asked += "alive"; return alive }
    }

    private class FakeConnection : Connection {
        val did = mutableListOf<String>()
        var offers = 0
        override var waitingForAnswer = false
        override var stable = true
        override suspend fun offer(restart: Boolean): String { offers++; waitingForAnswer = true; did += "offer${if (restart) " restart" else ""}"; return "v=0 offer$offers" }
        override suspend fun answer(offer: String): String { did += "answer to $offer"; return "v=0 answer" }
        override suspend fun accept(answer: String) { waitingForAnswer = false; did += "took $answer" }
        override fun servers(list: List<IceServer>) { did += "servers ${list.size}" }
        override fun mute(on: Boolean) { did += "mute $on" }
        override fun close() { did += "close" }
    }

    private class Setup(val scope: TestScope, mic: Boolean = true) {
        val server = FakeServer()
        val connection = FakeConnection()
        var link: ((Link) -> Unit)? = null
        val engine = CallEngine(server, { _, l -> if (mic) { link = l; connection } else null }, scope.backgroundScope,
            { scope.testScheduler.currentTime }, { "client-1" })
        val view get() = engine.view.value
    }

    @Test
    fun calling_asks_for_servers_offers_every_address_and_rings_until_answered() = runTest {
        val s = Setup(this)
        s.server.start = Reply.Ok(info())
        s.engine.start(chat, Peer("Minh"))
        assertEquals(Phase.OUTGOING, s.view!!.phase)
        runCurrent()
        assertEquals(listOf("servers $chat", "start $chat v=0 offer1"), s.server.asked)
        assertEquals(id, s.view!!.id)
        assertTrue(s.view!!.ringing)

        s.engine.frame(Frame.Accepted(id, "v=0 answer"))
        runCurrent()
        assertEquals(Phase.CONNECTING, s.view!!.phase)
        assertEquals(listOf("offer", "took v=0 answer"), s.connection.did)

        advanceTimeBy(1_000)
        s.link!!(Link.CONNECTED)
        runCurrent()
        assertEquals(Phase.ACTIVE, s.view!!.phase)
        assertEquals(1_000L, s.view!!.connectedAt)
        advanceTimeBy(30_001)
        assertTrue("still here every 30 s", "alive" in s.server.asked)
    }

    @Test
    fun a_refused_call_or_no_microphone_stays_until_closed() = runTest {
        val s = Setup(this)
        s.server.start = Reply.Failed("cannot_take_calls", 409)
        s.engine.start(chat, Peer("Minh"))
        runCurrent()
        advanceTimeBy(10_000)
        assertEquals(Ending.Refused("cannot_take_calls"), s.view!!.ending)
        s.engine.close()
        assertNull(s.view)

        val m = Setup(this, mic = false)
        m.engine.start(chat, Peer("Minh"))
        runCurrent()
        assertEquals(Ending.NeedMic, m.view!!.ending)
    }

    @Test
    fun giving_up_before_the_server_answers_hangs_up_once_it_does() = runTest {
        val s = Setup(this)
        s.server.start = Reply.Ok(info())
        s.server.startGate = CompletableDeferred()
        s.engine.start(chat, Peer("Minh"))
        runCurrent()
        s.engine.hangUp()
        assertEquals(Ending.Ended, s.view!!.ending)
        s.server.startGate!!.complete(Unit)
        runCurrent()
        assertEquals("hangup $id", s.server.asked.last())
        advanceTimeBy(CallProtocol.ENDED_MS + 1)
        assertNull(s.view)

        val early = Setup(this)
        early.engine.start(chat, Peer("Minh"))
        early.engine.hangUp()
        runCurrent()
        assertEquals(listOf("servers $chat"), early.server.asked)   // given up before it was made: never sent
    }

    @Test
    fun an_end_heard_before_the_call_had_its_id_is_kept_for_it() = runTest {
        val s = Setup(this)
        s.server.start = Reply.Ok(info())
        s.engine.start(chat, Peer("Minh"))
        s.engine.frame(Frame.Ended(id, "declined"))
        runCurrent()
        assertEquals(Ending.DeclinedByThem, s.view!!.ending)
    }

    @Test
    fun answering_takes_the_offer_and_sends_every_address_back() = runTest {
        val s = Setup(this)
        s.server.get = Reply.Ok(info())
        s.server.accept = Reply.Ok(info(state = "active"))
        s.engine.ringing(id, chat, Peer("Minh"))
        s.engine.ringing("another", chat, Peer("Lan"))   // a second call while this one rings: the server made it busy
        assertEquals(Phase.INCOMING, s.view!!.phase)
        assertEquals(id, s.view!!.id)
        s.engine.answer()
        runCurrent()
        assertEquals(listOf("servers $id", "get", "accept v=0 answer"), s.server.asked)
        assertEquals(listOf("answer to v=0 offer"), s.connection.did)
        assertEquals(Phase.CONNECTING, s.view!!.phase)
    }

    @Test
    fun answered_on_another_device_first_or_declined_here() = runTest {
        val s = Setup(this)
        s.server.get = Reply.Ok(info())
        s.server.accept = Reply.Failed("call_answered", 409)
        s.engine.ringing(id, chat, Peer("Minh"))
        s.engine.answer()
        runCurrent()
        assertEquals(Ending.AnsweredElsewhere, s.view!!.ending)

        val d = Setup(this)
        d.engine.ringing(id, chat, Peer("Minh"))
        d.engine.hangUp()
        runCurrent()
        assertNull(d.view)   // the person called hears nothing more of a call they declined
        assertEquals(listOf("decline"), d.server.asked)
    }

    /** A call in progress: [mine] says who called. */
    private suspend fun TestScope.inCall(mine: Boolean): Setup {
        val s = Setup(this)
        if (mine) {
            s.server.start = Reply.Ok(info())
            s.engine.start(chat, Peer("Minh"))
            runCurrent()
            s.engine.frame(Frame.Accepted(id, "v=0 answer"))
        } else {
            s.server.get = Reply.Ok(info())
            s.server.accept = Reply.Ok(info(state = "active"))
            s.engine.ringing(id, chat, Peer("Minh"))
            s.engine.answer()
        }
        runCurrent()
        s.link!!(Link.CONNECTED)
        runCurrent()
        s.server.asked.clear()
        s.connection.did.clear()
        s.connection.waitingForAnswer = false
        return s
    }

    @Test
    fun a_lost_connection_is_restarted_by_the_caller_and_given_up_after_a_minute() = runTest {
        val s = inCall(mine = true)
        s.link!!(Link.DOWN)
        runCurrent()
        assertTrue(s.view!!.downSince != null)
        advanceTimeBy(CallProtocol.RECONNECT_AFTER_MS + 1)
        assertEquals(listOf("offer restart"), s.connection.did)
        assertEquals("sdp offer v=0 offer2", s.server.asked.last())
        advanceTimeBy(CallProtocol.RECONNECT_EVERY_MS)
        assertEquals(2, s.connection.did.count { it == "offer restart" })
        advanceTimeBy(CallProtocol.GIVE_UP_MS - CallProtocol.RECONNECT_EVERY_MS)   // the check at 62 s, the first past 60 s
        assertEquals(Ending.Failed, s.view!!.ending)
        assertTrue("hangup $id" in s.server.asked)
    }

    @Test
    fun the_person_called_never_restarts_but_answers_the_callers_newest_offer() = runTest {
        val s = inCall(mine = false)
        s.link!!(Link.DOWN)
        advanceTimeBy(CallProtocol.RECONNECT_AFTER_MS + CallProtocol.RECONNECT_EVERY_MS)
        assertTrue(s.connection.did.none { it.startsWith("offer") })
        s.engine.frame(Frame.Sdp(id, Description("offer", "v=0 again", 2)))
        s.engine.frame(Frame.Sdp(id, Description("offer", "v=0 older", 1)))
        runCurrent()
        assertEquals(listOf("answer to v=0 again"), s.connection.did)
        assertEquals(listOf("sdp answer v=0 answer"), s.server.asked.filter { it.startsWith("sdp") })
        s.link!!(Link.CONNECTED)
        runCurrent()
        assertNull(s.view!!.downSince)
    }

    @Test
    fun logins_are_renewed_before_they_run_out_and_the_caller_then_restarts() = runTest {
        val s = inCall(mine = true)
        advanceTimeBy(CallProtocol.renewAfterMs(10_800) + 1)
        assertTrue("servers $id" in s.server.asked)
        assertEquals("servers 1", s.connection.did.first())
        advanceTimeBy((45 + CallProtocol.RESTART_AFTER_RING_S) * 1000)
        assertTrue("offer restart" in s.connection.did)
    }

    @Test
    fun the_call_ends_for_both_with_how_long_it_lasted() = runTest {
        val s = inCall(mine = false)
        advanceTimeBy(65_000)
        s.engine.frame(Frame.Ended(id, "completed"))
        assertEquals(Ending.Talked(65), s.view!!.ending)
        assertTrue("close" in s.connection.did)
    }

    @Test
    fun the_server_saying_the_call_is_over_ends_it_here() = runTest {
        val s = inCall(mine = true)
        s.server.alive = Reply.Failed("call_ended", 409)
        advanceTimeBy(30_001)
        assertTrue(s.view!!.ending is Ending.Talked)
    }

    @Test
    fun back_on_the_socket_it_asks_what_it_missed() = runTest {
        val s = Setup(this)
        s.server.start = Reply.Ok(info())
        s.engine.start(chat, Peer("Minh"))
        runCurrent()
        s.server.get = Reply.Ok(info(state = "active", offer = null, answer = "v=0 missed answer"))
        s.engine.resync()
        runCurrent()
        assertEquals(Phase.CONNECTING, s.view!!.phase)
        assertTrue("took v=0 missed answer" in s.connection.did)

        s.server.get = Reply.Ok(info(state = "ended", outcome = "completed"))
        s.engine.resync()
        runCurrent()
        assertEquals(Phase.ENDED, s.view!!.phase)
    }

    @Test
    fun muting_mutes_the_microphone() = runTest {
        val s = inCall(mine = true)
        s.engine.mute(true)
        assertEquals(listOf("mute true"), s.connection.did)
        assertTrue(s.view!!.muted)
    }
}
