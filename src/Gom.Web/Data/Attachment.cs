namespace Gom.Web.Data;

public enum AttachmentKind { Image = 0, File = 1 }

/// <summary>
/// A picture or a file somebody uploaded to a conversation. It belongs to its uploader alone until it is put in a message
/// (<see cref="MessageId"/> is empty); after that everyone who may see that message may fetch it.
/// </summary>
public class Attachment
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public Guid UploaderId { get; set; }
    public Guid? MessageId { get; set; }
    /// <summary>Its place among the attachments of one message (0 first), in the order the sender chose.</summary>
    public int Position { get; set; }
    public AttachmentKind Kind { get; set; }
    /// <summary>The name to show and to save under: the uploaded one, cleaned.</summary>
    public string Name { get; set; } = "";
    /// <summary>What is served: image/webp for a picture, a download type for a file (never what the sender claimed).</summary>
    public string ContentType { get; set; } = "";
    /// <summary>Bytes on disk (for a picture, after it was made smaller).</summary>
    public long Size { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>
    /// Set when the owner trimmed it: its bytes are gone for good (backups included), the row stays so the message can say a picture
    /// or a file was here. Never fetched, forwarded or listed again.
    /// </summary>
    public DateTimeOffset? RemovedAt { get; set; }
}
