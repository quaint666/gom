namespace Gom.Web.Data;

/// <summary>
/// What the page behind the first address in a message said about itself, fetched by the server after the message was sent. Its
/// picture, if any, is an attachment of the kind <see cref="AttachmentKind.Preview"/> that belongs to the same message.
/// </summary>
public class MessagePreview
{
    public Guid MessageId { get; set; }
    /// <summary>The address as it was in the message (not where redirects led).</summary>
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Site { get; set; }
    public Guid? ImageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
