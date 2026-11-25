using System.Net;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>The owner trims pictures and files sent before a date: gone for good, a placeholder left, a notice in each conversation.</summary>
public class FileTrimTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post;

    private sealed record Setup(User A, string CA, User B, string CB, Guid Group, Guid Chat);

    /// <summary>A and B are friends, with a group of the two and their one-to-one chat.</summary>
    private async Task<Setup> NewSetup()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        using var r = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { b.GomId } }, ca);
        var group = (await AppFixture.Json(r)).GetProperty("id").GetGuid();
        return new Setup(a, ca, b, cb, group, await app.OpenChat(ca, b.GomId));
    }

    private async Task<(Guid Id, long Size)> Upload(Guid chat, string cookie, string name, byte[] bytes)
    {
        using var r = await app.CallBytes(Post, $"/api/conversations/{chat}/attachments?name={name}", bytes, cookie);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var j = await AppFixture.Json(r);
        return (j.GetProperty("id").GetGuid(), j.GetProperty("size").GetInt64());
    }

    private async Task Send(Guid chat, string cookie, params Guid[] files)
    {
        using var r = await app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body = "x", attachments = files }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    private async Task<HttpResponseMessage> Trim(object body, bool preview = false)
    {
        var (_, co) = await app.Owner();
        return await app.Call(Post, preview ? "/api/owner/trim/files/preview" : "/api/owner/trim/files", body, co);
    }

    private static object Rule(DateTimeOffset before, string kind = "all", string where = "all", long? minBytes = null, string? confirm = "Delete") =>
        new { before, kind, where, minBytes, confirm };

    private string BlobPath(string name) => Path.Combine(app.UploadsDir, name[..2], name);

    private async Task<DateTimeOffset?> RemovedAt(Guid id)
    {
        await using var db = app.Db.NewContext();
        return await db.Attachments.Where(a => a.Id == id).Select(a => a.RemovedAt).SingleAsync();
    }

    private async Task<List<JsonElement>> History(Guid chat, string cookie)
    {
        using var r = await app.Call(Get, $"/api/conversations/{chat}/messages", cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return [.. (await AppFixture.Json(r)).GetProperty("messages").EnumerateArray().Select(m => m.Clone())];
    }

    private static List<JsonElement> Notices(List<JsonElement> messages) =>
        [.. messages.Where(m => m.TryGetProperty("notice", out var n) && n.ValueKind == JsonValueKind.Object)];

    private static long Long(JsonElement e, params string[] path) => path.Aggregate(e, (x, p) => x.GetProperty(p)).GetInt64();

    [Fact]
    public async Task Only_the_owner_may_preview_or_trim()
    {
        var (_, ca) = await app.Member();
        foreach (var path in new[] { "/api/owner/trim/files/preview", "/api/owner/trim/files" })
        {
            using var r = await app.Call(Post, path, Rule(app.Clock.Now), ca);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }
    }

    [Fact]
    public async Task A_rule_that_makes_no_sense_is_refused()
    {
        var s = await NewSetup();
        var now = app.Clock.Now;
        foreach (var bad in new object[]
        {
            new { kind = "all", where = "all", confirm = "Delete" },   // no date
            Rule(now.AddDays(1)), Rule(now, kind: "music"), Rule(now, where: "somewhere"), Rule(now, where: Guid.NewGuid().ToString()),
            Rule(now, where: s.Chat.ToString()),   // a one-to-one chat is never picked on its own
            Rule(now, minBytes: -1),
        })
        {
            foreach (var preview in new[] { true, false })
            {
                using var r = await Trim(bad, preview);
                Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
                Assert.Equal("invalid_trim", (await AppFixture.Json(r)).GetProperty("error").GetString());
            }
        }
    }

    [Fact]
    public async Task Without_Delete_typed_exactly_nothing_is_trimmed()
    {
        var s = await NewSetup();
        var pic = await Upload(s.Group, s.CA, "a.jpg", TestImages.Jpeg(100, 100));
        await Send(s.Group, s.CA, pic.Id);
        app.Clock.Advance(TimeSpan.FromDays(1));
        foreach (var typed in new[] { null, "", "delete", "Xóa", "Delete " })
        {
            using var r = await Trim(Rule(app.Clock.Now, where: s.Group.ToString(), confirm: typed));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal("trim_not_confirmed", (await AppFixture.Json(r)).GetProperty("error").GetString());
        }
        Assert.Null(await RemovedAt(pic.Id));
        Assert.True(File.Exists(BlobPath(pic.Id.ToString())));
    }

    [Fact]
    public async Task The_preview_counts_what_the_trim_then_removes_and_changes_nothing()
    {
        var s = await NewSetup();
        var pic = await Upload(s.Group, s.CA, "a.jpg", TestImages.Jpeg(200, 100));
        var doc = await Upload(s.Group, s.CA, "notes.txt", "some notes"u8.ToArray());
        await Send(s.Group, s.CA, pic.Id, doc.Id);
        var rule = Rule(app.Clock.Now.AddSeconds(1), where: s.Group.ToString());
        app.Clock.Advance(TimeSpan.FromSeconds(2));

        using var p = await Trim(rule, preview: true);
        Assert.Equal(HttpStatusCode.OK, p.StatusCode);
        var preview = await AppFixture.Json(p);
        Assert.Equal(1, Long(preview, "pictures", "count"));
        Assert.Equal(pic.Size, Long(preview, "pictures", "bytes"));
        Assert.Equal(doc.Size, Long(preview, "files", "bytes"));
        Assert.Equal(1, Long(preview, "conversations"));
        Assert.Null(await RemovedAt(pic.Id));

        using var t = await Trim(rule);
        Assert.Equal(HttpStatusCode.OK, t.StatusCode);
        Assert.Equal(preview.GetRawText(), (await AppFixture.Json(t)).GetRawText());
    }

    [Fact]
    public async Task Trimmed_files_are_gone_for_good_with_a_placeholder_and_a_notice_and_newer_ones_stay()
    {
        var s = await NewSetup();
        var oldPic = await Upload(s.Group, s.CA, "old.jpg", TestImages.Jpeg(200, 100));
        var oldDoc = await Upload(s.Chat, s.CB, "old.txt", "old notes"u8.ToArray());
        await Send(s.Group, s.CA, oldPic.Id);
        await Send(s.Chat, s.CB, oldDoc.Id);
        app.Clock.Advance(TimeSpan.FromDays(2));
        var cutoff = app.Clock.Now;
        app.Clock.Advance(TimeSpan.FromDays(1));
        var newPic = await Upload(s.Group, s.CA, "new.jpg", TestImages.Jpeg(100, 100));
        await Send(s.Group, s.CA, newPic.Id);
        var unsent = await Upload(s.Group, s.CA, "unsent.jpg", TestImages.Jpeg(90, 90));

        using (var r = await Trim(Rule(cutoff)))
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var result = await AppFixture.Json(r);
            // Everywhere: other tests' old files in this database go too, so only "at least these" can be said.
            Assert.True(Long(result, "pictures", "count") >= 1);
            Assert.True(Long(result, "files", "count") >= 1);
            Assert.True(Long(result, "conversations") >= 2);
        }

        // The bytes and the small version are gone; the row stays, marked.
        Assert.NotNull(await RemovedAt(oldPic.Id));
        Assert.NotNull(await RemovedAt(oldDoc.Id));
        Assert.False(File.Exists(BlobPath(oldPic.Id.ToString())));
        Assert.False(File.Exists(BlobPath(oldPic.Id + ".t")));
        Assert.False(File.Exists(BlobPath(oldDoc.Id.ToString())));
        // Newer, and never sent, are untouched.
        Assert.Null(await RemovedAt(newPic.Id));
        Assert.Null(await RemovedAt(unsent.Id));
        Assert.True(File.Exists(BlobPath(newPic.Id.ToString())));

        // The message keeps a placeholder; nobody can fetch, forward or list it any more.
        var history = await History(s.Group, s.CB);
        var placeholder = history.SelectMany(m => m.TryGetProperty("attachments", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : [])
            .Single(a => a.GetProperty("id").GetGuid() == oldPic.Id);
        Assert.True(placeholder.GetProperty("removed").GetBoolean());
        foreach (var path in new[] { $"/api/attachments/{oldPic.Id}", $"/api/attachments/{oldPic.Id}/thumb" })
        {
            using var f = await app.Call(Get, path, cookie: s.CB);
            Assert.Equal(HttpStatusCode.NotFound, f.StatusCode);
        }
        using (var copy = await app.Call(Post, $"/api/conversations/{s.Chat}/attachments/copy", new { from = oldPic.Id }, s.CA))
            Assert.Equal(HttpStatusCode.NotFound, copy.StatusCode);
        using (var media = await app.Call(Get, $"/api/conversations/{s.Group}/media?kind=image", cookie: s.CA))
        {
            var ids = (await AppFixture.Json(media)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("attachment").GetProperty("id").GetGuid()).ToList();
            Assert.Equal([newPic.Id], ids);
        }

        // Both conversations, the one-to-one chat too, say so once, naming nobody, with the date.
        foreach (var (chat, cookie) in new[] { (s.Group, s.CA), (s.Chat, s.CB) })
        {
            var notice = Assert.Single(Notices(await History(chat, cookie)), n => n.GetProperty("notice").GetProperty("type").GetString() == "filesTrimmed");
            Assert.Equal(cutoff, notice.GetProperty("notice").GetProperty("before").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, notice.GetProperty("notice").GetProperty("target").ValueKind);
        }

        // The storage view no longer counts them.
        var (_, co) = await app.Owner();
        using var st = await app.Call(Get, "/api/owner/storage", cookie: co);
        var group = (await AppFixture.Json(st)).GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == s.Group);
        Assert.Equal(1, group.GetProperty("files").GetInt32());
        Assert.Equal(newPic.Size, group.GetProperty("bytes").GetInt64());
    }

    [Fact]
    public async Task Kind_place_and_size_narrow_the_trim_and_an_untouched_conversation_gets_no_notice()
    {
        var s = await NewSetup();
        var smallPic = await Upload(s.Group, s.CA, "small.jpg", TestImages.Jpeg(40, 40));
        var bigPic = await Upload(s.Group, s.CA, "big.jpg", TestImages.Jpeg(600, 500));
        var groupDoc = await Upload(s.Group, s.CA, "g.txt", "group notes"u8.ToArray());
        var chatPic = await Upload(s.Chat, s.CA, "c.jpg", TestImages.Jpeg(300, 300));
        await Send(s.Group, s.CA, smallPic.Id, bigPic.Id, groupDoc.Id);
        await Send(s.Chat, s.CA, chatPic.Id);
        app.Clock.Advance(TimeSpan.FromDays(1));
        var now = app.Clock.Now;

        // Pictures only, in this group, at least as big as the big one: only the big picture.
        (await Trim(Rule(now, kind: "image", where: s.Group.ToString(), minBytes: bigPic.Size))).Dispose();
        Assert.NotNull(await RemovedAt(bigPic.Id));
        Assert.Null(await RemovedAt(smallPic.Id));
        Assert.Null(await RemovedAt(groupDoc.Id));
        Assert.Null(await RemovedAt(chatPic.Id));
        Assert.Empty(Notices(await History(s.Chat, s.CA)));

        // Files only, one-to-one chats only: the chat has no file, so nothing at all happens.
        using (var r = await Trim(Rule(now, kind: "file", where: "direct")))
            Assert.Equal(0, Long(await AppFixture.Json(r), "conversations"));
        Assert.Null(await RemovedAt(groupDoc.Id));

        // One-to-one chats only, any kind: the chat's picture, not the group's.
        (await Trim(Rule(now, where: "direct"))).Dispose();
        Assert.NotNull(await RemovedAt(chatPic.Id));
        Assert.Null(await RemovedAt(smallPic.Id));
        Assert.Single(Notices(await History(s.Chat, s.CA)));
        Assert.Single(Notices(await History(s.Group, s.CA)), n => n.GetProperty("notice").GetProperty("type").GetString() == "filesTrimmed");
    }

    [Fact]
    public async Task A_second_trim_of_the_same_rule_finds_nothing_and_writes_no_notice()
    {
        var s = await NewSetup();
        var pic = await Upload(s.Group, s.CA, "a.jpg", TestImages.Jpeg(100, 100));
        await Send(s.Group, s.CA, pic.Id);
        app.Clock.Advance(TimeSpan.FromDays(1));
        var rule = Rule(app.Clock.Now, where: s.Group.ToString());
        (await Trim(rule)).Dispose();
        using var again = await Trim(rule);
        Assert.Equal(0, Long(await AppFixture.Json(again), "conversations"));
        Assert.Single(Notices(await History(s.Group, s.CA)), n => n.GetProperty("notice").GetProperty("type").GetString() == "filesTrimmed");
    }
}
