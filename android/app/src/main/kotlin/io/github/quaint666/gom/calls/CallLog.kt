package io.github.quaint666.gom.calls

/**
 * What a call writes to the phone's log (`adb logcat -s GomCall`) about how it connects: the kinds of address each side offered and
 * the pair WebRTC chose, never the addresses themselves. Enough to tell a network that needs the relay from one that blocks it.
 */
object CallLog {
    const val TAG = "GomCall"

    /** The kinds an offer or answer can hold: this device's own, as the internet sees it, and through the relay. */
    private val KINDS = listOf("host", "srflx", "relay")
    private val candidate = Regex("""^a=candidate:.* typ (\w+)""", RegexOption.MULTILINE)

    /** "host 2, srflx 1, relay 0": the addresses in an offer or answer, counted by kind. */
    fun kinds(sdp: String): String {
        val counts = candidate.findAll(sdp).groupingBy { it.groupValues[1] }.eachCount()
        return KINDS.joinToString { "$it ${counts[it] ?: 0}" }
    }

    /** One entry of WebRTC's statistics: its type ("transport", "candidate-pair", "local-candidate"…) and its values. */
    data class Stat(val type: String, val members: Map<String, Any?>)

    /**
     * "here relay/udp, there srflx/udp": the pair the connection uses, each end's kind and how it is reached (for a relay address, how
     * this device reaches the relay). Null when no pair is chosen yet.
     */
    fun chosen(stats: Map<String, Stat>): String? {
        val pairId = stats.values.firstOrNull { it.type == "transport" }?.members?.get("selectedCandidatePairId") as? String ?: return null
        val pair = stats[pairId] ?: return null
        fun end(key: String): String {
            val c = stats[pair.members[key] as? String]?.members ?: return "?"
            return "${c["candidateType"] ?: "?"}/${c["relayProtocol"] ?: c["protocol"] ?: "?"}"
        }
        return "here ${end("localCandidateId")}, there ${end("remoteCandidateId")}"
    }
}
