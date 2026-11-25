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
    /// <summary>The owner's notification of a request to join: with the name, and without (the "nothing" level).</summary>
    public required Func<string, string> JoinRequest { get; init; }
    public required string JoinRequestUnnamed { get; init; }
    public required Func<int, string> SentPictures { get; init; }
    public required Func<int, string> SentFiles { get; init; }
    public required Func<int, int, string> SentPicturesAndFiles { get; init; }

    public static readonly ServerText Vi = new()
    {
        NewMessage = "Tin nhắn mới",
        Group = "Nhóm",
        Recalled = "Tin nhắn đã được thu hồi",
        SentSticker = "Đã gửi 1 nhãn dán",
        JoinRequest = name => $"{name} xin tham gia Gom",
        JoinRequestUnnamed = "Có người xin tham gia Gom",
        SentPictures = n => $"Đã gửi {n} ảnh",
        SentFiles = n => $"Đã gửi {n} file",
        SentPicturesAndFiles = (pictures, files) => $"Đã gửi {pictures} ảnh và {files} file",
    };

    public static readonly ServerText En = new()
    {
        NewMessage = "New message",
        Group = "Group",
        Recalled = "This message was taken back",
        SentSticker = "Sent a sticker",
        JoinRequest = name => $"{name} asks to join Gom",
        JoinRequestUnnamed = "Someone asks to join Gom",
        SentPictures = n => n == 1 ? "Sent a picture" : $"Sent {n} pictures",
        SentFiles = n => n == 1 ? "Sent a file" : $"Sent {n} files",
        SentPicturesAndFiles = (pictures, files) => $"Sent {Count(pictures, "picture", "pictures")} and {Count(files, "file", "files")}",
    };

    /// <summary>A person's language's words; anything unknown reads Vietnamese.</summary>
    public static ServerText For(string? language) => language == "en" ? En : Vi;

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";
}
