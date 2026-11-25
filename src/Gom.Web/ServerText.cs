namespace Gom.Web;

/// <summary>
/// The words the server itself writes: page titles and what a notification says. One place to change a word; another language
/// is another instance of the same shape. The pages' own words are in Scripts/strings.
/// </summary>
public sealed class ServerText
{
    /// <summary>A page's title before " · Gom", by its script's name; a page not listed is just "Gom".</summary>
    public required IReadOnlyDictionary<string, string> PageTitles { get; init; }
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
        PageTitles = new Dictionary<string, string>
        {
            ["signin"] = "Đăng nhập",
            ["join"] = "Lời mời",
            ["signup"] = "Xin tham gia",
            ["reset"] = "Đặt lại mật khẩu",
            ["settings"] = "Cài đặt",
            ["friends"] = "Bạn bè",
            ["newgroup"] = "Nhóm mới",
            ["group"] = "Thành viên nhóm",
            ["add"] = "Kết bạn",
            ["owner"] = "Lời mời và thành viên",
            ["storage"] = "Dung lượng",
        },
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
}
