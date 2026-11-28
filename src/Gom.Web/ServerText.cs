namespace Gom.Web;

/// <summary>
/// The words the server itself writes: what a notification says. One place to change a word; each language is an instance of the
/// same shape. The pages' own words (titles included) are in Scripts/strings.
/// </summary>
public sealed class ServerText
{
    public const string DefaultLanguage = "vi";
    public static readonly IReadOnlySet<string> Languages = new HashSet<string> { "vi", "en" };

    public required string NewMessage { get; init; }
    public required string Group { get; init; }
    /// <summary>What replaces a notification once its message is taken back.</summary>
    public required string Recalled { get; init; }
    public required string SentSticker { get; init; }
    /// <summary>A group message that names the person told: who wrote and what it says.</summary>
    public required Func<string, string, string> MentionedYou { get; init; }
    /// <summary>A group message that says "@all": who wrote and what it says.</summary>
    public required Func<string, string, string> MentionedAll { get; init; }
    /// <summary>At the level that names nobody, a message that names the person told.</summary>
    public required string MentionedYouUnnamed { get; init; }
    /// <summary>The owner's notification of a request to join: with the name, and without (the "nothing" level).</summary>
    public required Func<string, string> JoinRequest { get; init; }
    public required string JoinRequestUnnamed { get; init; }
    /// <summary>A call nobody answered (or that found them busy): the caller's name is the title, at the levels that name people.</summary>
    public required string MissedCall { get; init; }
    /// <summary>A call ringing on the Android app: the caller's name is the title, at the levels that name people.</summary>
    public required string IncomingCall { get; init; }
    /// <summary>The owner's notification of a message from the privacy page's form.</summary>
    public required string ContactMessage { get; init; }
    public required Func<int, string> SentPictures { get; init; }
    public required Func<int, string> SentFiles { get; init; }
    public required Func<int, int, string> SentPicturesAndFiles { get; init; }
    public required Func<int, string> SentVideos { get; init; }
    /// <summary>Videos with pictures or files: only how many things in all.</summary>
    public required Func<int, string> SentAttachments { get; init; }

    public static readonly ServerText Vi = new()
    {
        NewMessage = "Tin nhắn mới",
        Group = "Nhóm",
        Recalled = "Tin nhắn đã được thu hồi",
        SentSticker = "Đã gửi 1 nhãn dán",
        MentionedYou = (name, what) => $"{name} đã nhắc đến bạn: {what}",
        MentionedAll = (name, what) => $"{name} nhắc mọi người: {what}",
        MentionedYouUnnamed = "Có người nhắc đến bạn",
        JoinRequest = name => $"{name} xin tham gia Gom",
        JoinRequestUnnamed = "Có người xin tham gia Gom",
        ContactMessage = "Có thư liên hệ mới",
        MissedCall = "Cuộc gọi nhỡ",
        IncomingCall = "Cuộc gọi đến",
        SentPictures = n => $"Đã gửi {n} ảnh",
        SentFiles = n => $"Đã gửi {n} file",
        SentPicturesAndFiles = (pictures, files) => $"Đã gửi {pictures} ảnh và {files} file",
        SentVideos = n => $"Đã gửi {n} video",
        SentAttachments = n => $"Đã gửi {n} tệp đính kèm",
    };

    public static readonly ServerText En = new()
    {
        NewMessage = "New message",
        Group = "Group",
        Recalled = "This message was taken back",
        SentSticker = "Sent a sticker",
        MentionedYou = (name, what) => $"{name} mentioned you: {what}",
        MentionedAll = (name, what) => $"{name} mentioned everyone: {what}",
        MentionedYouUnnamed = "Someone mentioned you",
        JoinRequest = name => $"{name} asks to join Gom",
        JoinRequestUnnamed = "Someone asks to join Gom",
        ContactMessage = "New message from the contact form",
        MissedCall = "Missed call",
        IncomingCall = "Incoming call",
        SentPictures = n => n == 1 ? "Sent a picture" : $"Sent {n} pictures",
        SentFiles = n => n == 1 ? "Sent a file" : $"Sent {n} files",
        SentPicturesAndFiles = (pictures, files) => $"Sent {Count(pictures, "picture", "pictures")} and {Count(files, "file", "files")}",
        SentVideos = n => n == 1 ? "Sent a video" : $"Sent {n} videos",
        SentAttachments = n => $"Sent {n} attachments",
    };

    /// <summary>A person's language's words; anything unknown reads Vietnamese.</summary>
    public static ServerText For(string? language) => language == "en" ? En : Vi;

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
