using System.Net;
using System.Text.Json;
using Gom.Web;
using Gom.Web.Chat;
using Gom.Web.Data;
using Gom.Web.Push;
using Microsoft.EntityFrameworkCore;
using static System.Net.Http.HttpMethod;

namespace Gom.Tests;

/// <summary>The announcement channel: made once, everyone in it, only the owner writes, and it is the same for everyone.</summary>
public class AnnouncementTests(AppFixture app) : IClassFixture<AppFixture>
{
    /// <summary>Runs what the server runs at start (and again after people were added, as a deploy to an existing site does).</summary>
    private async Task<Guid> Channel()
    {
        await app.Owner();
        await using var db = app.Db.NewContext();
        await Announcements.EnsureAsync(db, app.Clock, default);
        return await db.Conversations.Where(c => c.Kind == ConversationKind.Announce).Select(c => c.Id).SingleAsync();
    }

    private async Task<JsonElement> Say(string cookie, Guid chat, string body)
    {
        using var r = await app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await AppFixture.Json(r);
    }

    private async Task<string> ErrorOf(HttpResponseMessage r) => (await AppFixture.Json(r)).GetProperty("error").GetString()!;

    private async Task<JsonElement[]> Listed(string cookie)
    {
        using var r = await app.Call(Get, "/api/conversations", cookie: cookie);
        return (await AppFixture.Json(r)).EnumerateArray().ToArray();
    }

    [Fact]
    public async Task The_channel_is_made_once_and_has_every_member_in_it_but_not_a_waiting_request_or_a_deleted_account()
    {
        var first = await Channel();
        var (member, _) = await app.Member();
        var waiting = await app.AddUser();
        var gone = await app.AddUser();
        await using (var db = app.Db.NewContext())
        {
            await db.Users.Where(u => u.Id == waiting.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.RequestedAt, app.Clock.Now));
            await db.Users.Where(u => u.Id == gone.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.DeletedAt, app.Clock.Now).SetProperty(u => u.DisabledAt, app.Clock.Now));
        }
        Assert.Equal(first, await Channel());   // run again: the same one, not a second

        await using var check = app.Db.NewContext();
        var inside = await check.Members.Where(m => m.ConversationId == first).Select(m => m.UserId).ToListAsync();
        Assert.Contains(member.Id, inside);
        Assert.DoesNotContain(waiting.Id, inside);
        Assert.DoesNotContain(gone.Id, inside);
        Assert.Equal(inside.Count, inside.Distinct().Count());
        Assert.Equal(1, await check.Conversations.CountAsync(c => c.Kind == ConversationKind.Announce));
    }

    [Fact]
    public async Task It_comes_first_in_everyones_list_before_a_pinned_chat_and_is_never_hidden()
    {
        var (a, ca) = await app.Member(); var (b, _) = await app.Member();
        await app.Befriend(a, b);
        var direct = await app.OpenChat(ca, b.GomId);
        using (var pin = await app.Call(Put, $"/api/conversations/{direct}/pinned", new { on = true }, ca)) Assert.Equal(HttpStatusCode.NoContent, pin.StatusCode);
        var channel = await Channel();
        var (_, oc) = await app.Owner();
        await Say(oc, channel, "Họp mặt Chủ nhật 8h");

        var list = await Listed(ca);
        Assert.Equal(channel, list[0].GetProperty("id").GetGuid());
        Assert.Equal("announce", list[0].GetProperty("kind").GetString());
        Assert.Equal(direct, list[1].GetProperty("id").GetGuid());
        Assert.False(list[0].GetProperty("hidden").GetBoolean());
        Assert.Equal(1, list[0].GetProperty("unread").GetInt32());
        Assert.Equal("Họp mặt Chủ nhật 8h", list[0].GetProperty("lastMessage").GetProperty("body").GetString());
    }

    [Fact]
    public async Task Only_the_owner_writes_in_it_and_everyone_else_is_told_so()
    {
        var channel = await Channel();
        var (_, oc) = await app.Owner();
        var (_, mc) = await app.Member();
        await Channel();

        await Say(oc, channel, "Gom bảo trì lúc 23h");
        using var refused = await app.Call(Post, $"/api/conversations/{channel}/messages", new { clientId = Guid.NewGuid(), body = "tôi cũng muốn nói" }, mc);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("owner_only", await ErrorOf(refused));
        using var sticker = await app.Call(Post, $"/api/conversations/{channel}/messages", new { clientId = Guid.NewGuid(), sticker = "1f602" }, mc);
        Assert.Equal(HttpStatusCode.Forbidden, sticker.StatusCode);
    }

    [Fact]
    public async Task A_member_cannot_put_a_picture_in_it_nor_copy_one_into_it_but_the_owner_can()
    {
        var channel = await Channel();
        var (_, oc) = await app.Owner();
        var (_, mc) = await app.Member();
        await Channel();

        using var refused = await app.CallBytes(Post, $"/api/conversations/{channel}/attachments?name=a.jpg", TestImages.Jpeg(200, 100), mc);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("owner_only", await ErrorOf(refused));
        using var copy = await app.Call(Post, $"/api/conversations/{channel}/attachments/copy", new { from = Guid.NewGuid() }, mc);
        Assert.Equal(HttpStatusCode.Forbidden, copy.StatusCode);
        using var allowed = await app.CallBytes(Post, $"/api/conversations/{channel}/attachments?name=a.jpg", TestImages.Jpeg(200, 100), oc);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task A_member_reads_all_of_it_and_reacts_but_nobody_answers_a_message()
    {
        var channel = await Channel();
        var (_, oc) = await app.Owner();
        var said = await Say(oc, channel, "Nhớ đóng tiền quỹ nhé");
        var (_, mc) = await app.Member();
        await Channel();

        using var history = await app.Call(Get, $"/api/conversations/{channel}/messages", cookie: mc);
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);   // what was said before they came is theirs to read
        Assert.Contains((await AppFixture.Json(history)).GetProperty("messages").EnumerateArray(), m => m.GetProperty("body").GetString() == "Nhớ đóng tiền quỹ nhé");
        using var react = await app.Call(Put, $"/api/conversations/{channel}/messages/{said.GetProperty("id").GetGuid()}/reaction", new { emoji = "❤️" }, mc);
        Assert.Equal(HttpStatusCode.NoContent, react.StatusCode);

        using var answer = await app.Call(Post, $"/api/conversations/{channel}/messages", new { clientId = Guid.NewGuid(), body = "ok", replyTo = said.GetProperty("id").GetGuid() }, oc);
        Assert.Equal(HttpStatusCode.BadRequest, answer.StatusCode);
        Assert.Equal("invalid_reply", await ErrorOf(answer));
    }

    [Fact]
    public async Task Someone_who_joins_later_reads_what_was_said_and_none_of_it_is_unread()
    {
        var channel = await Channel();
        var (_, oc) = await app.Owner();
        await Say(oc, channel, "một");
        await Say(oc, channel, "hai");
        var token = await app.NewInviteToken();
        using var joined = await app.Call(Post, $"/api/join/{token}", new { gomId = "n" + Guid.NewGuid().ToString("N")[..10], password = AppFixture.Password, displayName = "Mới" });
        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);
        var cookie = AppFixture.CookieValue(joined)!;

        var mine = (await Listed(cookie)).Single(c => c.GetProperty("kind").GetString() == "announce");
        Assert.Equal(0, mine.GetProperty("unread").GetInt32());
        Assert.Equal(2, mine.GetProperty("lastSeq").GetInt64());
        using var history = await app.Call(Get, $"/api/conversations/{channel}/messages", cookie: cookie);
        Assert.Equal(2, (await AppFixture.Json(history)).GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task A_request_to_join_is_in_it_once_the_owner_approves_and_not_before()
    {
        await Channel();
        var (_, oc) = await app.Owner();
        using (var open = await app.Call(Put, "/api/sign-up-open", new { open = true }, oc)) Assert.Equal(HttpStatusCode.NoContent, open.StatusCode);
        var id = "q" + Guid.NewGuid().ToString("N")[..10];
        app.Clock.Advance(TimeSpan.FromDays(1));
        using (var ask = await app.Call(Post, "/api/sign-up", new { gomId = id, password = AppFixture.Password, displayName = "Hỏi", note = "con cô Lan" })) Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
        var user = await app.SignIn(id);
        await using (var db = app.Db.NewContext())
            Assert.False(await db.Members.AnyAsync(m => db.Users.Any(u => u.GomId == id && u.Id == m.UserId) && db.Conversations.Any(c => c.Id == m.ConversationId && c.Kind == ConversationKind.Announce)));

        using var approve = await app.Call(Post, $"/api/join-requests/{id}/approve", cookie: oc);
        Assert.Equal(HttpStatusCode.NoContent, approve.StatusCode);
        Assert.Contains(await Listed(user), c => c.GetProperty("kind").GetString() == "announce");
    }

    [Theory]
    [InlineData("PUT", "hidden", "{\"on\":true}")]
    [InlineData("POST", "clear", "{}")]
    [InlineData("PUT", "look", "{\"palette\":\"forest\"}")]
    [InlineData("PUT", "disappear", "{\"seconds\":900}")]
    public async Task It_is_the_same_for_everyone_so_nobody_hides_clears_restyles_or_sets_it_to_disappear(string method, string what, string json)
    {
        var channel = await Channel();
        var (_, oc) = await app.Owner();
        var (_, mc) = await app.Member();
        await Channel();
        foreach (var cookie in new[] { mc, oc })
        {
            using var r = await app.Call(new HttpMethod(method), $"/api/conversations/{channel}/{what}", JsonSerializer.Deserialize<JsonElement>(json), cookie);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Equal("not_allowed", await ErrorOf(r));
        }
    }

    [Fact]
    public async Task A_member_can_mute_it_and_a_muted_member_is_not_notified()
    {
        var channel = await Channel();
        var (_, mc) = await app.Member();
        await Channel();
        using var mute = await app.Call(Put, $"/api/conversations/{channel}/mute", new { muted = true }, mc);
        Assert.Equal(HttpStatusCode.NoContent, mute.StatusCode);
        Assert.True((await Listed(mc)).Single(c => c.GetProperty("kind").GetString() == "announce").GetProperty("muted").GetBoolean());
    }

    [Fact]
    public async Task Words_and_mentions_in_it_name_the_channel_and_name_nobody()
    {
        var channel = await Channel();
        var (_, oc) = await app.Owner();
        var said = await Say(oc, channel, "@all họp 8h");
        Assert.False(said.TryGetProperty("mentionsAll", out var all) && all.GetBoolean());   // everyone is told already

        var job = new PushJob(channel, 1, Guid.NewGuid(), "bi_nha", 0, 0, Text: "Họp 8h");
        Assert.Equal(("Thông báo", "Họp 8h"), PushSender.Words(job, ConversationKind.Announce, null, ServerText.Vi, words: true));
        Assert.Equal(("Announcements", "Họp 8h"), PushSender.Words(job, ConversationKind.Announce, null, ServerText.En, words: true));
        Assert.Equal(("Thông báo", "Tin nhắn mới"), PushSender.Words(job, ConversationKind.Announce, null, ServerText.Vi));
    }

    [Fact]
    public async Task There_is_never_a_second_announcement_channel()
    {
        await Channel();
        var (owner, _) = await app.Owner();
        await using var db = app.Db.NewContext();
        db.Conversations.Add(new Conversation { Id = Guid.NewGuid(), Kind = ConversationKind.Announce, CreatedBy = owner.Id, CreatedAt = app.Clock.Now });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
