using System.Text.Json;

namespace Gom.Web.Chat.Live;

public sealed record EventDto(long Seq, string Kind, MessageEndpoints.MessageDto? Message);

/// <summary>The JSON the socket speaks. Every frame has a "t" (type); a conversation's frames also have "c".</summary>
public static class Frames
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // These bytes go straight to a socket and are read by JSON.parse, never pasted into HTML, so accented letters need no escaping.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static byte[] Json(object value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    /// <summary>"Your list of conversations changed" (you were added to a group, someone joined or left): reload it.</summary>
    public static byte[] ConversationsChanged() => Json(new { t = "conversations" });
    /// <summary>"This person has now read up to here": for the thread's "seen" line.</summary>
    public static byte[] Read(Guid conversation, Guid reader, long seq) => Json(new { t = "read", c = conversation, u = reader, seq });
    /// <summary>"This person deleted this message for themself": their other devices drop it.</summary>
    public static byte[] Hidden(Guid conversation, Guid message) => Json(new { t = "hidden", c = conversation, m = message });
    /// <summary>"This person deleted the conversation for themself up to here": their other devices drop what they hold of it.</summary>
    public static byte[] Cleared(Guid conversation, long seq) => Json(new { t = "cleared", c = conversation, seq });
    /// <summary>"Still here", sent every few seconds. The page closes and reconnects if nothing at all arrives for a minute.</summary>
    public static byte[] Heartbeat() => Json(new { t = "hb" });
    /// <summary>"The pinned messages of this conversation changed": ask for them again.</summary>
    public static byte[] Pins(Guid conversation) => Json(new { t = "pins", c = conversation });
    /// <summary>"Connected", with the version of the pages this server hands out (null without one): an older page knows to reload.</summary>
    public static byte[] Ready(string? version) => Json(new { t = "ready", v = version });
    public static byte[] Live(Guid conversation, EventDto e) => Json(new { t = "event", c = conversation, @event = e });
    public static byte[] Events(Guid conversation, IReadOnlyList<EventDto> events, bool hasMore) => Json(new { t = "events", c = conversation, events, hasMore });
    public static byte[] Error(string code, Guid? conversation = null) => Json(new { t = "error", c = conversation, error = code });

    // ---- calls (docs/voice-calls.md): every call frame is {"t": "call", "id", "c", "kind", ...}. A page that does not take calls ignores them.
    /// <summary>"Someone is calling you": to every device of the person called.</summary>
    public static byte[] CallIncoming(Guid call, Guid conversation, MessageEndpoints.PersonDto from) => Json(new { t = "call", id = call, c = conversation, kind = "incoming", from });
    /// <summary>"They answered", with their answer: to the caller's device only.</summary>
    public static byte[] CallAccepted(Guid call, Guid conversation, string sdp) => Json(new { t = "call", id = call, c = conversation, kind = "accepted", sdp });
    /// <summary>"You answered on another device": to the person's other devices, which stop ringing.</summary>
    public static byte[] CallAnsweredElsewhere(Guid call, Guid conversation) => Json(new { t = "call", id = call, c = conversation, kind = "answered_elsewhere" });
    /// <summary>"The call is over", and how: to every device of both.</summary>
    public static byte[] CallEnded(Guid call, Guid conversation, string outcome) => Json(new { t = "call", id = call, c = conversation, kind = "ended", outcome });
    /// <summary>A new offer or answer mid-call (a network change): to the other side's device only.</summary>
    public static byte[] CallSdp(Guid call, Guid conversation, string type, string sdp, int rev) => Json(new { t = "call", id = call, c = conversation, kind = "sdp", type, sdp, rev });
}
