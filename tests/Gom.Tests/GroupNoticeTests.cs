using System.Net;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gom.Tests;

/// <summary>The lines in a group's thread that say who was added, taken out, who left, and what it was renamed to.</summary>
public class GroupNoticeTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Put = HttpMethod.Put, Delete = HttpMethod.Delete, Patch = HttpMethod.Patch;

    /// <summary>A leads a group with B and C. D and E are friends of A and B, outside the group.</summary>
    private sealed record Team(User A, string CA, User B, string CB, User C, string CC, User D, string CD, User E, string CE, Guid Chat);

    private async Task<Team> NewTeam(bool approval = false)
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, cc) = await app.Member();
        var (d, cd) = await app.Member(); var (e, ce) = await app.Member();
        foreach (var x in new[] { b, c, d, e }) await app.Befriend(a, x);
        foreach (var x in new[] { d, e }) await app.Befriend(b, x);
        using var r = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { b.GomId, c.GomId } }, ca);
        var chat = (await AppFixture.Json(r)).GetProperty("id").GetGuid();
        if (approval) (await app.Call(Put, $"/api/conversations/{chat}/settings", new { joinApproval = true }, ca)).Dispose();
        return new Team(a, ca, b, cb, c, cc, d, cd, e, ce, chat);
    }

    private Task<HttpResponseMessage> Add(Team t, string cookie, User who) => app.Call(Post, $"/api/conversations/{t.Chat}/members", new { gomId = who.GomId }, cookie);

    private async Task<List<JsonElement>> History(Team t, string cookie)
    {
        using var r = await app.Call(Get, $"/api/conversations/{t.Chat}/messages", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("messages").EnumerateArray().Select(m => m.Clone()).ToList();   // newest first
    }

    private static List<JsonElement> Notices(List<JsonElement> messages) =>
        messages.Where(m => m.TryGetProperty("notice", out var n) && n.ValueKind == JsonValueKind.Object).ToList();

    private async Task Say(Team t, string cookie, string body)
    {
        using var r = await app.Call(Post, $"/api/conversations/{t.Chat}/messages", new { clientId = Guid.NewGuid().ToString(), body }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Adding_someone_leaves_a_line_that_says_who_added_whom_and_everyone_sees_it_including_the_one_added()
    {
        var t = await NewTeam();
        using var added = await Add(t, t.CA, t.D);
        Assert.Equal(HttpStatusCode.NoContent, added.StatusCode);

        foreach (var cookie in new[] { t.CA, t.CB, t.CC, t.CD })
        {
            var notice = Notices(await History(t, cookie)).Single();
            Assert.Equal("added", notice.GetProperty("notice").GetProperty("type").GetString());
            Assert.Equal(t.A.Id, notice.GetProperty("sender").GetProperty("id").GetGuid());
            Assert.Equal(t.D.Id, notice.GetProperty("notice").GetProperty("target").GetProperty("id").GetGuid());
            Assert.Equal(t.D.DisplayName, notice.GetProperty("notice").GetProperty("target").GetProperty("displayName").GetString());
            Assert.Equal("", notice.GetProperty("body").GetString());
        }
    }

    [Fact]
    public async Task A_person_added_later_does_not_see_the_lines_from_before_they_came()
    {
        var t = await NewTeam();
        (await Add(t, t.CA, t.D)).Dispose();       // notice 1: D comes
        await Say(t, t.CA, "chào D");
        (await Add(t, t.CA, t.E)).Dispose();       // notice 2: E comes
        var seenByE = Notices(await History(t, t.CE));
        Assert.Single(seenByE);
        Assert.Equal(t.E.Id, seenByE[0].GetProperty("notice").GetProperty("target").GetProperty("id").GetGuid());
        Assert.Equal(2, Notices(await History(t, t.CA)).Count);
    }

    [Fact]
    public async Task Approving_a_request_says_who_approved_and_whom()
    {
        var t = await NewTeam(approval: true);
        using var asked = await Add(t, t.CB, t.D);   // a plain member's addition waits for approval
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        Assert.Empty(Notices(await History(t, t.CA)));
        using var approved = await app.Call(Post, $"/api/conversations/{t.Chat}/requests/{t.D.Id}/approve", cookie: t.CA);
        Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
        var notice = Notices(await History(t, t.CB)).Single();
        Assert.Equal("added", notice.GetProperty("notice").GetProperty("type").GetString());
        Assert.Equal(t.A.Id, notice.GetProperty("sender").GetProperty("id").GetGuid());   // the one who approved
        Assert.Equal(t.D.Id, notice.GetProperty("notice").GetProperty("target").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Taking_someone_out_says_so_to_those_who_stay_and_the_one_taken_out_can_no_longer_read_anything()
    {
        var t = await NewTeam();
        using var removed = await app.Call(Delete, $"/api/conversations/{t.Chat}/members/{t.C.Id}", cookie: t.CA);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        var notice = Notices(await History(t, t.CB)).Single();
        Assert.Equal("removed", notice.GetProperty("notice").GetProperty("type").GetString());
        Assert.Equal(t.A.Id, notice.GetProperty("sender").GetProperty("id").GetGuid());
        Assert.Equal(t.C.Id, notice.GetProperty("notice").GetProperty("target").GetProperty("id").GetGuid());
        using var gone = await app.Call(Get, $"/api/conversations/{t.Chat}/messages", cookie: t.CC);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Leaving_says_who_left()
    {
        var t = await NewTeam();
        using var left = await app.Call(Delete, $"/api/conversations/{t.Chat}/members/me", cookie: t.CB);
        Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        var notice = Notices(await History(t, t.CC)).Single();
        Assert.Equal("left", notice.GetProperty("notice").GetProperty("type").GetString());
        Assert.Equal(t.B.Id, notice.GetProperty("sender").GetProperty("id").GetGuid());
        Assert.True(!notice.GetProperty("notice").TryGetProperty("target", out var target) || target.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Renaming_says_the_new_name_and_naming_it_what_it_is_already_says_nothing()
    {
        var t = await NewTeam();
        using var renamed = await app.Call(Patch, $"/api/conversations/{t.Chat}", new { title = "Gia đình mình" }, t.CA);
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        (await app.Call(Patch, $"/api/conversations/{t.Chat}", new { title = "Gia đình mình" }, t.CA)).Dispose();
        var notice = Notices(await History(t, t.CB)).Single();
        Assert.Equal("renamed", notice.GetProperty("notice").GetProperty("type").GetString());
        Assert.Equal("Gia đình mình", notice.GetProperty("notice").GetProperty("title").GetString());
        Assert.Equal("", notice.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_notice_is_not_a_message_it_is_not_searched_answered_taken_back_edited_reacted_to_pinned_or_hidden()
    {
        var t = await NewTeam();
        (await Add(t, t.CA, t.D)).Dispose();
        var id = Notices(await History(t, t.CB)).Single().GetProperty("id").GetGuid();
        var by = $"/api/conversations/{t.Chat}/messages/{id}";

        using var recall = await app.Call(Delete, by, cookie: t.CA);
        using var edit = await app.Call(Patch, by, new { body = "x" }, t.CA);
        using var react = await app.Call(Put, by + "/reaction", new { emoji = "👍" }, t.CB);
        using var pin = await app.Call(Put, by + "/pin", cookie: t.CB);
        using var hide = await app.Call(Put, by + "/hidden", cookie: t.CB);
        using var reply = await app.Call(Post, $"/api/conversations/{t.Chat}/messages", new { clientId = Guid.NewGuid().ToString(), body = "trả lời", replyTo = id }, t.CB);
        foreach (var r in new[] { recall, edit, react, pin, hide }) Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reply.StatusCode);
        Assert.Equal("invalid_reply", (await AppFixture.Json(reply)).GetProperty("error").GetString());
        await Say(t, t.CB, "Đã thêm");   // a word that is not in the notice; the notice has no words to find anyway
        using var search = await app.Call(Get, $"/api/conversations/{t.Chat}/search?q=a", cookie: t.CB);
        Assert.DoesNotContain(id.ToString(), await search.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_notice_is_not_unread_and_is_not_the_last_message_in_the_list()
    {
        var t = await NewTeam();
        await Say(t, t.CA, "tin cuối cùng");
        using var before = await app.Call(Get, "/api/conversations", cookie: t.CB);
        var me = (await AppFixture.Json(before)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == t.Chat);
        var unreadBefore = me.GetProperty("unread").GetInt32();

        (await Add(t, t.CA, t.D)).Dispose();
        (await app.Call(Patch, $"/api/conversations/{t.Chat}", new { title = "Tên mới" }, t.CA)).Dispose();
        using var after = await app.Call(Get, "/api/conversations", cookie: t.CB);
        var row = (await AppFixture.Json(after)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == t.Chat);
        Assert.Equal(unreadBefore, row.GetProperty("unread").GetInt32());
        Assert.Equal("tin cuối cùng", row.GetProperty("lastMessage").GetProperty("body").GetString());
        Assert.True(row.GetProperty("lastSeq").GetInt64() > me.GetProperty("lastSeq").GetInt64());   // the stream moved on
    }

    [Fact]
    public async Task Notices_come_in_the_order_things_happened_among_the_messages()
    {
        var t = await NewTeam();
        await Say(t, t.CA, "một");
        (await Add(t, t.CA, t.D)).Dispose();
        await Say(t, t.CA, "hai");
        var oldestFirst = (await History(t, t.CA)).AsEnumerable().Reverse().ToList();
        Assert.Equal(["một", "", "hai"], oldestFirst.Select(m => m.GetProperty("body").GetString()!).ToArray());
        Assert.Equal(new[] { false, true, false }, oldestFirst.Select(m => m.TryGetProperty("notice", out var n) && n.ValueKind == JsonValueKind.Object).ToArray());
        var seqs = oldestFirst.Select(m => m.GetProperty("seq").GetInt64()).ToList();
        Assert.Equal(seqs.Order().ToList(), seqs);
        Assert.Equal(seqs.Distinct().Count(), seqs.Count);
    }

    [Fact]
    public async Task Disbanding_a_group_with_notices_works()
    {
        var t = await NewTeam();
        (await Add(t, t.CA, t.D)).Dispose();
        (await app.Call(Patch, $"/api/conversations/{t.Chat}", new { title = "Tên khác" }, t.CA)).Dispose();
        using var gone = await app.Call(Delete, $"/api/conversations/{t.Chat}", cookie: t.CA);
        Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);
    }

    [Fact]
    public async Task The_database_refuses_a_notice_that_says_who_it_is_about_wrongly()
    {
        var t = await NewTeam();
        await using var db = app.Db.NewContext();
        var id = Guid.NewGuid();
        // an "added" notice with nobody added, and a plain message that names somebody
        var noTarget = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO messages (id, conversation_id, seq, sender_id, client_id, body, created_at, notice) VALUES ({id}, {t.Chat}, 9000, {t.A.Id}, {Guid.NewGuid()}, '', now(), 1)"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, noTarget.SqlState);
        var plainWithTarget = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlAsync(
            $"INSERT INTO messages (id, conversation_id, seq, sender_id, client_id, body, created_at, notice_target_id) VALUES ({id}, {t.Chat}, 9001, {t.A.Id}, {Guid.NewGuid()}, 'x', now(), {t.B.Id})"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, plainWithTarget.SqlState);
    }
}
