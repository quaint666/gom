using System.Net;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>The owner trims messages sent before a date: they and everything they carry go for good; answers to them stay.</summary>
public class MessageTrimTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Put = HttpMethod.Put;

    private sealed record Setup(User A, string CA, User B, string CB, Guid Group, Guid Chat);

    private async Task<Setup> NewSetup()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        using var r = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { b.GomId } }, ca);
        var group = (await AppFixture.Json(r)).GetProperty("id").GetGuid();
        return new Setup(a, ca, b, cb, group, await app.OpenChat(ca, b.GomId));
    }

    private async Task<Guid> Say(Guid chat, string cookie, string body, Guid? replyTo = null, params Guid[] files)
    {
        using var r = await app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body, replyTo, attachments = files }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> Upload(Guid chat, string cookie)
    {
        using var r = await app.CallBytes(Post, $"/api/conversations/{chat}/attachments?name=a.jpg", TestImages.Jpeg(100, 80), cookie);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private async Task<HttpResponseMessage> Trim(object body, bool preview = false)
    {
        var (_, co) = await app.Owner();
        return await app.Call(Post, preview ? "/api/owner/trim/messages/preview" : "/api/owner/trim/messages", body, co);
    }

    private static object Rule(DateTimeOffset before, string where = "all", string? confirm = "Delete") => new { before, where, confirm };

    private async Task<List<JsonElement>> History(Guid chat, string cookie)
    {
        using var r = await app.Call(Get, $"/api/conversations/{chat}/messages", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return [.. (await AppFixture.Json(r)).GetProperty("messages").EnumerateArray().Select(m => m.Clone())];
    }

    private static string? NoticeType(JsonElement m) =>
        m.TryGetProperty("notice", out var n) && n.ValueKind == JsonValueKind.Object ? n.GetProperty("type").GetString() : null;

    private static long Long(JsonElement e, params string[] path) => path.Aggregate(e, (x, p) => x.GetProperty(p)).GetInt64();

    [Fact]
    public async Task Only_the_owner_may_preview_or_trim()
    {
        var (_, ca) = await app.Member();
        foreach (var path in new[] { "/api/owner/trim/messages/preview", "/api/owner/trim/messages" })
        {
            using var r = await app.Call(Post, path, Rule(app.Clock.Now), ca);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }
    }

    [Fact]
    public async Task A_rule_that_makes_no_sense_or_no_Delete_typed_changes_nothing()
    {
        var s = await NewSetup();
        var kept = await Say(s.Group, s.CA, "giữ lại");
        app.Clock.Advance(TimeSpan.FromDays(1));
        var now = app.Clock.Now;
        foreach (var bad in new object[] { new { where = "all", confirm = "Delete" }, Rule(now.AddDays(1)), Rule(now, where: "x"), Rule(now, where: s.Chat.ToString()) })
        {
            using var r = await Trim(bad);
            Assert.Equal("invalid_trim", (await AppFixture.Json(r)).GetProperty("error").GetString());
        }
        foreach (var typed in new[] { null, "delete", "Delete " })
        {
            using var r = await Trim(Rule(now, where: s.Group.ToString(), confirm: typed));
            Assert.Equal("trim_not_confirmed", (await AppFixture.Json(r)).GetProperty("error").GetString());
        }
        Assert.Contains(await History(s.Group, s.CA), m => m.GetProperty("id").GetGuid() == kept);
    }

    [Fact]
    public async Task The_preview_counts_what_the_trim_then_removes_and_changes_nothing()
    {
        var s = await NewSetup();
        var pic = await Upload(s.Group, s.CA);
        await Say(s.Group, s.CA, "ảnh", null, pic);
        await Say(s.Group, s.CB, "chữ");
        (await app.Call(HttpMethod.Patch, $"/api/conversations/{s.Group}", new { title = "Nhà mới" }, s.CA)).Dispose();   // a notice: it goes, uncounted
        app.Clock.Advance(TimeSpan.FromDays(1));
        var rule = Rule(app.Clock.Now, where: s.Group.ToString());

        using var p = await Trim(rule, preview: true);
        var preview = await AppFixture.Json(p);
        Assert.Equal(2, Long(preview, "messages"));   // what people wrote; the rename notice goes too, uncounted
        Assert.Equal(1, Long(preview, "pictures", "count"));
        Assert.Equal(1, Long(preview, "conversations"));
        Assert.Equal(2, (await History(s.Group, s.CA)).Count(m => NoticeType(m) is null));

        using var t = await Trim(rule);
        Assert.Equal(preview.GetRawText(), (await AppFixture.Json(t)).GetRawText());
    }

    [Fact]
    public async Task Old_messages_go_with_all_they_carry_newer_ones_and_answers_stay_and_a_notice_says_so()
    {
        var s = await NewSetup();
        var pic = await Upload(s.Group, s.CA);
        var old = await Say(s.Group, s.CA, "cũ", null, pic);
        var oldChat = await Say(s.Chat, s.CB, "cũ 1-1");
        (await app.Call(Put, $"/api/conversations/{s.Group}/messages/{old}/reaction", new { emoji = "❤️" }, s.CB)).Dispose();
        (await app.Call(Put, $"/api/conversations/{s.Group}/messages/{old}/pin", null, s.CA)).Dispose();
        (await app.Call(Put, $"/api/conversations/{s.Group}/messages/{old}/hidden", null, s.CB)).Dispose();
        app.Clock.Advance(TimeSpan.FromDays(2));
        var cutoff = app.Clock.Now;
        app.Clock.Advance(TimeSpan.FromDays(1));
        var answer = await Say(s.Group, s.CB, "trả lời", old);
        var fresh = await Say(s.Group, s.CA, "mới");

        using (var r = await Trim(Rule(cutoff)))
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        await using (var db = app.Db.NewContext())
        {
            Assert.False(await db.Messages.AnyAsync(m => m.Id == old || m.Id == oldChat));
            Assert.False(await db.Attachments.AnyAsync(a => a.Id == pic));
            Assert.False(await db.Reactions.AnyAsync(x => x.MessageId == old));
            Assert.False(await db.Pins.AnyAsync(x => x.MessageId == old));
            Assert.False(await db.Hides.AnyAsync(x => x.MessageId == old));
            Assert.False(await db.Events.AnyAsync(x => x.MessageId == old));
            // The group's "added" notice was older too: it went with the messages.
            Assert.False(await db.Messages.AnyAsync(m => m.ConversationId == s.Group && m.Notice == NoticeKind.Added));
        }
        var name = pic.ToString();
        Assert.False(File.Exists(Path.Combine(app.UploadsDir, name[..2], name)));
        Assert.False(File.Exists(Path.Combine(app.UploadsDir, name[..2], name + ".t")));

        // The answer stays, its quote now saying the message is not there; newer messages stay as they were.
        var group = await History(s.Group, s.CA);
        var kept = group.Single(m => m.GetProperty("id").GetGuid() == answer);
        Assert.Equal(old, kept.GetProperty("reply").GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, kept.GetProperty("reply").GetProperty("sender").ValueKind);
        Assert.Equal(JsonValueKind.Null, kept.GetProperty("reply").GetProperty("snippet").ValueKind);
        Assert.Contains(group, m => m.GetProperty("id").GetGuid() == fresh);

        // Both conversations, the one-to-one chat too, say so once, with the date and no name.
        foreach (var (chat, cookie) in new[] { (s.Group, s.CA), (s.Chat, s.CB) })
        {
            var notice = Assert.Single(await History(chat, cookie), m => NoticeType(m) == "messagesTrimmed");
            Assert.Equal(cutoff, notice.GetProperty("notice").GetProperty("before").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, notice.GetProperty("notice").GetProperty("target").ValueKind);
        }

        // The conversation carries on: a new message gets the next number and the pins are empty.
        var next = await Say(s.Group, s.CA, "tiếp");
        Assert.Contains(await History(s.Group, s.CB), m => m.GetProperty("id").GetGuid() == next);
        using var pins = await app.Call(Get, $"/api/conversations/{s.Group}/pins", cookie: s.CA);
        Assert.Empty((await AppFixture.Json(pins)).GetProperty("pins").EnumerateArray());
    }

    [Fact]
    public async Task One_group_or_one_to_one_chats_only_and_an_untouched_conversation_gets_no_notice()
    {
        var s = await NewSetup();
        var inGroup = await Say(s.Group, s.CA, "nhóm");
        var inChat = await Say(s.Chat, s.CA, "1-1");
        app.Clock.Advance(TimeSpan.FromDays(1));
        var now = app.Clock.Now;

        // One-to-one chats only: the group keeps its message and gets no notice.
        (await Trim(Rule(now, where: "direct"))).Dispose();
        Assert.DoesNotContain(await History(s.Chat, s.CA), m => m.GetProperty("id").GetGuid() == inChat);
        var group = await History(s.Group, s.CA);
        Assert.Contains(group, m => m.GetProperty("id").GetGuid() == inGroup);
        Assert.DoesNotContain(group, m => NoticeType(m) == "messagesTrimmed");

        // One group: only it.
        (await Trim(Rule(now, where: s.Group.ToString()))).Dispose();
        Assert.DoesNotContain(await History(s.Group, s.CA), m => m.GetProperty("id").GetGuid() == inGroup);
        Assert.Single(await History(s.Chat, s.CA), m => NoticeType(m) == "messagesTrimmed");
    }

    [Fact]
    public async Task A_group_whose_answers_point_at_trimmed_messages_can_still_be_ended()
    {
        var s = await NewSetup();
        var old = await Say(s.Group, s.CA, "cũ");
        app.Clock.Advance(TimeSpan.FromDays(1));
        var cutoff = app.Clock.Now;
        app.Clock.Advance(TimeSpan.FromSeconds(5));
        await Say(s.Group, s.CB, "trả lời", old);
        (await Trim(Rule(cutoff, where: s.Group.ToString()))).Dispose();

        using var r = await app.Call(HttpMethod.Delete, $"/api/conversations/{s.Group}", cookie: s.CA);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        await using var db = app.Db.NewContext();
        Assert.False(await db.Conversations.AnyAsync(c => c.Id == s.Group));
    }
}
