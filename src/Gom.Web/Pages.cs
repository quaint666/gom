using System.Text.Json;
using Gom.Web.Auth;
using Gom.Web.Chat;
using Gom.Web.Social;
using Gom.Web.Uploads;

namespace Gom.Web;

/// <summary>
/// The pages. Every page is the same small HTML shell (wwwroot/pages/shell.html); the server fills in which script
/// to run (<c>data-page</c>, read by /js/main.js) and the limits the server holds (<c>data-limits</c>), so a page never types a
/// number the server decides. The files a page loads are then named by their fingerprinted copies (see <see cref="Assets"/>).
/// </summary>
public static class Pages
{
    /// <summary>An address and the script for it. Its title is set by the script, in the page's language ("Gom" until then).</summary>
    public sealed record Page(string Route, string Name);

    /// <summary>
    /// The parts of Settings, each at /settings/{code} (a phone shows one at a time, a wide screen the menu beside it); the owner's two
    /// are only for the owner, whose API checks it again. Scripts/pages/settings.ts lists the same codes; a test keeps them together.
    /// </summary>
    public static readonly IReadOnlyList<string> SettingsSections = ["profile", "notifications", "appearance", "privacy", "account", "owner", "storage"];

    /// <summary>Addresses that moved, kept so old links and bookmarks still land: the owner's pages are now parts of Settings.</summary>
    public static readonly IReadOnlyDictionary<string, string> Moved = new Dictionary<string, string>
    {
        ["/owner"] = "/settings/owner",
        ["/owner/storage"] = "/settings/storage",
    };

    public static readonly IReadOnlyList<Page> All =
    [
        new("/", "home"),
        new("/c/{id}", "home"),
        new("/sign-in", "signin"),
        new("/join/{token}", "join"),
        new("/sign-up", "signup"),
        new("/reset/{token}", "reset"),
        new("/settings", "settings"),
        // Only the known parts: any other /settings/... is not found.
        new($"/settings/{{section:regex(^({string.Join('|', SettingsSections)})$)}}", "settings"),
        new("/friends", "friends"),
        new("/groups/new", "newgroup"),
        new("/groups/{id}", "group"),
        new("/add/{gomId}", "add"),
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
        animatedBytes = uploads.MaxAnimatedBytes,
        videoBytes = uploads.MaxVideoBytes,
        posterBytes = uploads.MaxPosterBytes,
        uploadDailyBytes = uploads.DailyBytes,
        pins = chat.MaxPins,
        nameChars = DisplayName.MaxLength,
        passwordMin = PasswordPolicy.MinLength,
        passwordMax = PasswordPolicy.MaxLength,
        gomIdMin = GomId.MinLength,
        gomIdMax = GomId.MaxLength,
        searchChars = MessageEndpoints.SearchMaxChars,
        quoteChars = Replies.SnippetChars,
        listPreviewChars = ConversationEndpoints.PreviewChars,
        noteChars = SignUpEndpoints.NoteMaxLength,
        inviteDays = LinkEndpoints.MaxInviteDays,
        inviteUses = LinkEndpoints.MaxInviteUses,
        inviteDefaultDays = auth.InviteDays,
        inviteDefaultUses = auth.InviteMaxUses,
    });

    /// <summary>The shell made into one page, each value escaped for HTML text or a quoted attribute.</summary>
    public static string Render(string shell, Page page, string limits) => shell
        .Replace("{page}", Escape(page.Name), StringComparison.Ordinal)
        .Replace("{limits}", Escape(limits), StringComparison.Ordinal);

    // Only what HTML needs (WebUtility.HtmlEncode would also write "·" as "&#183;").
    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);
}
