package io.github.quaint666.gom.calls

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class ProtocolTest {
    /** The page's scripts, beside the app in this repository (unit tests run in android/app). */
    private fun page(name: String): String = File("../../src/Gom.Web/Scripts/$name").readText()

    /** A number the page's script exports: `export const NAME = 1_000;`. */
    private fun constant(script: String, name: String): Long {
        val m = Regex("""export const $name = ([\d_]+);""").find(script) ?: error("$name is not in the page's script")
        return m.groupValues[1].replace("_", "").toLong()
    }

    @Test
    fun the_version_and_timings_are_the_pages() {
        val call = page("calls/call.ts")
        assertEquals(constant(page("api.ts"), "CALL_PROTOCOL"), CallProtocol.VERSION.toLong())
        assertEquals(constant(call, "GATHER_MS"), CallProtocol.GATHER_MS)
        assertEquals(constant(call, "ENDED_MS"), CallProtocol.ENDED_MS)
        assertEquals(constant(call, "RECONNECT_AFTER_MS"), CallProtocol.RECONNECT_AFTER_MS)
        assertEquals(constant(call, "RECONNECT_EVERY_MS"), CallProtocol.RECONNECT_EVERY_MS)
        assertEquals(constant(call, "GIVE_UP_MS"), CallProtocol.GIVE_UP_MS)
        assertEquals(constant(call, "CONNECT_MS"), CallProtocol.CONNECT_MS)
        assertEquals(constant(call, "RENEW_BEFORE_S"), CallProtocol.RENEW_BEFORE_S)
    }

    @Test
    fun a_login_is_renewed_ten_minutes_before_it_runs_out_or_at_half_its_life() {
        assertEquals((3 * 3600 - 600) * 1000L, CallProtocol.renewAfterMs(3 * 3600))
        assertEquals(300_000L, CallProtocol.renewAfterMs(600))
    }

    private val id = "0b6c7f6e-3c1a-4b8e-9d55-1f2a3b4c5d6e"
    private val chat = "6f1e2d3c-4b5a-4968-8776-655443322110"

    @Test
    fun reads_the_servers_with_one_address_or_several() {
        val s = Servers.parse("""{"iceServers":[{"urls":"stun:stun.example:3478"},{"urls":["turn:t.example:3478?transport=udp","turns:t.example:443"],"username":"u","credential":"p"}],"relay":true,"seconds":10800}""")!!
        assertEquals(listOf(IceServer(listOf("stun:stun.example:3478")), IceServer(listOf("turn:t.example:3478?transport=udp", "turns:t.example:443"), "u", "p")), s.iceServers)
        assertEquals(true, s.relay)
        assertEquals(10800L, s.seconds)
        assertNull(Servers.parse("{}"))
    }

    @Test
    fun reads_a_call_as_the_server_describes_it() {
        val c = CallInfo.parse("""{"id":"$id","conversationId":"$chat","state":"ringing","outcome":null,"caller":{"id":"x","displayName":"Minh"},"offer":"v=0","answer":null,"sdp":null,"ringSeconds":45,"aliveSeconds":30}""")!!
        assertEquals(CallInfo(id, chat, "ringing", null, "Minh", "v=0", null, null, 45, 30), c)
        val later = CallInfo.parse("""{"id":"$id","conversationId":"$chat","state":"active","caller":{"displayName":"Minh"},"sdp":{"type":"offer","sdp":"v=0 x","rev":2},"ringSeconds":45,"aliveSeconds":30}""")!!
        assertEquals(Description("offer", "v=0 x", 2), later.sdp)
        assertNull(CallInfo.parse("""{"id":"../x","conversationId":"$chat","state":"ringing","ringSeconds":45,"aliveSeconds":30}"""))
        assertNull(CallInfo.parse("""{"id":"$id","conversationId":"$chat","state":"ringing"}"""))   // the server's times are needed
    }

    @Test
    fun reads_the_call_frames_and_nothing_else() {
        assertEquals(Frame.Incoming(id, chat, "Minh"), Frame.parse("""{"t":"call","id":"$id","c":"$chat","kind":"incoming","from":{"id":"x","displayName":"Minh"}}"""))
        assertEquals(Frame.Accepted(id, "v=0"), Frame.parse("""{"t":"call","id":"$id","c":"$chat","kind":"accepted","sdp":"v=0"}"""))
        assertEquals(Frame.AnsweredElsewhere(id), Frame.parse("""{"t":"call","id":"$id","c":"$chat","kind":"answered_elsewhere"}"""))
        assertEquals(Frame.Ended(id, "missed"), Frame.parse("""{"t":"call","id":"$id","c":"$chat","kind":"ended","outcome":"missed"}"""))
        assertEquals(Frame.Sdp(id, Description("answer", "v=0", 3)), Frame.parse("""{"t":"call","id":"$id","c":"$chat","kind":"sdp","type":"answer","sdp":"v=0","rev":3}"""))
        assertNull(Frame.parse("""{"t":"call","id":"$id","kind":"sdp","type":"pranswer","sdp":"v=0","rev":3}"""))
        assertNull(Frame.parse("""{"t":"call","id":"$id","kind":"accepted"}"""))
        assertNull(Frame.parse("""{"t":"sync","id":"$id"}"""))
        assertNull(Frame.parse("""{"t":"call","id":"nope","kind":"ended"}"""))
        assertNull(Frame.parse("not json"))
    }

    @Test
    fun reads_the_call_pushes() {
        assertEquals(CallPush.Ring(id), CallPush.parse(mapOf("t" to "call", "id" to id)))
        assertEquals(CallPush.End(id, "missed"), CallPush.parse(mapOf("t" to "call-end", "id" to id, "outcome" to "missed")))
        assertNull(CallPush.parse(mapOf("t" to "call", "id" to "1/../x")))
        assertNull(CallPush.parse(mapOf("t" to "message", "id" to id)))
    }

    @Test
    fun a_calls_length_is_written_as_the_page_writes_it() {
        assertEquals("0:07", callLength(7))
        assertEquals("3:05", callLength(185))
        assertEquals("1:02:03", callLength(3723))
    }
}
