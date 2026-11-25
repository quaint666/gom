using System.Text.Json;
using Gom.Web.Auth;
using Gom.Web.Chat;
using Gom.Web.Social;
using Gom.Web.Uploads;

namespace Gom.Web;

/// <summary>
/// The pages. Every page is the same small HTML shell (wwwroot/pages/shell.html); the server fills in its title, which script
/// to run (<c>data-page</c>, read by /js/main.js) and the limits the server holds (<c>data-limits</c>), so a page never types a
/// number the server decides. The files a page loads are then named by their fingerprinted copies (see <see cref="Assets"/>).
/// </summary>
public static class Pages
{
    /// <summary>An address, the script for it and its title before " · Gom" (none: just "Gom").</summary>
    public sealed record Page(string Route, string Name, string? Title);

    public static readonly IReadOnlyList<Page> All =
    [
        new("/", "home", null),
        new("/c/{id}", "home", null),
        new("/sign-in", "signin", "Đăng nhập"),
        new("/join/{token}", "join", "Lời mời"),
        new("/reset/{token}", "reset", "Đặt lại mật khẩu"),
        new("/settings", "settings", "Cài đặt"),
        new("/friends", "friends", "Bạn bè"),
        new("/groups/new", "newgroup", "Nhóm mới"),
        new("/groups/{id}", "group", "Thành viên nhóm"),
        new("/add/{gomId}", "add", "Kết bạn"),
        new("/owner", "owner", "Lời mời và thành viên"),
        new("/owner/storage", "storage", "Dung lượng"),
    ];

    /// <summary>
    /// The limits a page shows or enforces before asking (a field's maxlength, "at most 60 characters"), as JSON. Some can be
    /// changed in the configuration, so they are read from it, never copied.
    /// </summary>
    public static string Limits(ChatOptions chat, UploadOptions uploads, AuthOptions auth) => JsonSerializer.Serialize(new
    {
        messageChars = chat.MaxChars,
        attachments = uploads.MaxPerMessage,
        imageBytes = uploads.MaxImageBytes,
        fileBytes = uploads.MaxFileBytes,
        pins = chat.MaxPins,
        nameChars = DisplayName.MaxLength,
        passwordMin = PasswordPolicy.MinLength,
        passwordMax = PasswordPolicy.MaxLength,
        gomIdMin = GomId.MinLength,
        gomIdMax = GomId.MaxLength,
        searchChars = MessageEndpoints.SearchMaxChars,
        quoteChars = Replies.SnippetChars,
        listPreviewChars = ConversationEndpoints.PreviewChars,
        inviteDays = LinkEndpoints.MaxInviteDays,
        inviteUses = LinkEndpoints.MaxInviteUses,
        inviteDefaultDays = auth.InviteDays,
        inviteDefaultUses = auth.InviteMaxUses,
    });

    /// <summary>The shell made into one page, each value escaped for HTML text or a quoted attribute.</summary>
    public static string Render(string shell, Page page, string limits) => shell
        .Replace("{title}", Escape(page.Title is null ? "Gom" : $"{page.Title} · Gom"), StringComparison.Ordinal)
        .Replace("{page}", Escape(page.Name), StringComparison.Ordinal)
        .Replace("{limits}", Escape(limits), StringComparison.Ordinal);

    // Only what HTML needs (WebUtility.HtmlEncode would also write "·" as "&#183;").
    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);
}
