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
    public static byte[] Ready() => Json(new { t = "ready" });
    public static byte[] Live(Guid conversation, EventDto e) => Json(new { t = "event", c = conversation, @event = e });
    public static byte[] Events(Guid conversation, IReadOnlyList<EventDto> events, bool hasMore) => Json(new { t = "events", c = conversation, events, hasMore });
    public static byte[] Error(string code, Guid? conversation = null) => Json(new { t = "error", c = conversation, error = code });
}
