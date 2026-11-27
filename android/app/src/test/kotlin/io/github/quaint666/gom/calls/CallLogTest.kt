package io.github.quaint666.gom.calls

import io.github.quaint666.gom.calls.CallLog.Stat
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class CallLogTest {
    @Test
    fun counts_the_addresses_of_an_offer_by_kind_and_never_shows_them() {
        val sdp = "v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\n" +
            "a=candidate:1 1 udp 2122260223 192.0.2.10 50000 typ host generation 0\r\n" +
            "a=candidate:2 1 tcp 1518280447 192.0.2.10 9 typ host tcptype active\r\n" +
            "a=candidate:3 1 udp 1686052607 198.51.100.7 61000 typ srflx raddr 192.0.2.10 rport 50000\r\n" +
            "a=candidate:4 1 udp 41885439 203.0.113.5 30000 typ relay raddr 198.51.100.7 rport 61000\r\n"
        assertEquals("host 2, srflx 1, relay 1", CallLog.kinds(sdp))
        assertEquals("host 0, srflx 0, relay 0", CallLog.kinds("v=0\r\n"))
        assertTrue(CallLog.hasAddresses(sdp))
        assertFalse(CallLog.hasAddresses("v=0\r\nm=audio 9 UDP/TLS/RTP/SAVPF 111\r\na=ice-ufrag:x\r\n"))
    }

    @Test
    fun says_which_pair_the_connection_uses() {
        val stats = mapOf(
            "T" to Stat("transport", mapOf("selectedCandidatePairId" to "P")),
            "P" to Stat("candidate-pair", mapOf("localCandidateId" to "L", "remoteCandidateId" to "R")),
            "L" to Stat("local-candidate", mapOf("candidateType" to "relay", "protocol" to "udp", "relayProtocol" to "tls", "address" to "203.0.113.5")),
            "R" to Stat("remote-candidate", mapOf("candidateType" to "srflx", "protocol" to "udp", "address" to "198.51.100.7")),
        )
        assertEquals("here relay/tls, there srflx/udp", CallLog.chosen(stats))
        assertNull(CallLog.chosen(mapOf("T" to Stat("transport", emptyMap()))))
    }
}
