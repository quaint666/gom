using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>The owner's storage view, and deleting a group nobody is in.</summary>
public class StorageTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Delete = HttpMethod.Delete;

    private async Task<JsonElement> Storage()
    {
        var (_, co) = await app.Owner();
        using var r = await app.Call(Get, "/api/owner/storage", cookie: co);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await AppFixture.Json(r);
    }

    private async Task<Guid> Group(string cookie, string title, params string[] members)
    {
        using var r = await app.Call(Post, "/api/conversations/group", new { title, members }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    /// <summary>Uploads one picture or file; returns its id and its size on disk.</summary>
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

    private static JsonElement GroupRow(JsonElement storage, Guid id) => storage.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("id").GetGuid() == id);
    private static long Long(JsonElement e, params string[] path) => path.Aggregate(e, (x, p) => x.GetProperty(p)).GetInt64();

    [Fact]
    public async Task Only_the_owner_sees_it_or_deletes_a_group()
    {
        var (_, ca) = await app.Member();
        using var get = await app.Call(Get, "/api/owner/storage", cookie: ca);
        Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        using var del = await app.Call(Delete, $"/api/owner/groups/{Guid.NewGuid()}", cookie: ca);
        Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);
    }

    [Fact]
    public async Task Counts_what_was_sent_by_group_one_to_one_together_and_by_person()
    {
        var (a, ca) = await app.Member(); var (b, _) = await app.Member();
        await app.Befriend(a, b);
        var group = await Group(ca, "Nhà mình", b.GomId);
        var chat = await app.OpenChat(ca, b.GomId);
        var before = await Storage();

        var picture = await Upload(group, ca, "a.jpg", TestImages.Jpeg(300, 200));
        var file = await Upload(group, ca, "notes.txt", "hello storage"u8.ToArray());
        await Send(group, ca, picture.Id, file.Id);
        var direct = await Upload(chat, ca, "b.jpg", TestImages.Jpeg(200, 200));
        await Send(chat, ca, direct.Id);
        await Upload(chat, ca, "never-sent.jpg", TestImages.Jpeg(100, 100));   // not in a message: not counted

        var after = await Storage();
        var row = GroupRow(after, group);
        Assert.Equal("Nhà mình", row.GetProperty("title").GetString());
        Assert.Equal(2, row.GetProperty("members").GetInt32());
        Assert.Equal(1, row.GetProperty("messages").GetInt32());
        Assert.Equal(2, row.GetProperty("files").GetInt32());
        Assert.Equal(picture.Size + file.Size, row.GetProperty("bytes").GetInt64());

        Assert.Equal(1, Long(after, "direct", "messages") - Long(before, "direct", "messages"));
        Assert.Equal(direct.Size, Long(after, "direct", "bytes") - Long(before, "direct", "bytes"));
        Assert.Equal(picture.Size + direct.Size, Long(after, "pictures", "bytes") - Long(before, "pictures", "bytes"));
        Assert.Equal(file.Size, Long(after, "files", "bytes") - Long(before, "files", "bytes"));

        var person = after.GetProperty("people").EnumerateArray().Single(p => p.GetProperty("gomId").GetString() == a.GomId);
        Assert.Equal(3, person.GetProperty("files").GetInt32());
        Assert.Equal(picture.Size + file.Size + direct.Size, person.GetProperty("bytes").GetInt64());
        Assert.DoesNotContain(after.GetProperty("people").EnumerateArray(), p => p.GetProperty("gomId").GetString() == b.GomId);

        Assert.True(Long(after, "database") > 0);
        Assert.True(Long(after, "uploadsFolder") >= Long(after, "pictures", "bytes") + Long(after, "files", "bytes"));
        Assert.True(Long(after, "disk", "total") >= Long(after, "disk", "free"));
        Assert.True(Long(after, "disk", "free") > 0);
    }

    [Fact]
    public async Task A_one_to_one_chat_is_never_named_or_listed_on_its_own()
    {
        var (a, ca) = await app.Member(); var (b, _) = await app.Member();
        await app.Befriend(a, b);
        var chat = await app.OpenChat(ca, b.GomId);
        var pic = await Upload(chat, ca, "c.jpg", TestImages.Jpeg(100, 100));
        await Send(chat, ca, pic.Id);

        var raw = (await Storage()).GetRawText();
        Assert.DoesNotContain(chat.ToString(), raw);
        Assert.DoesNotContain(GroupRowIds(await Storage()), id => id == chat);
    }

    private static IEnumerable<Guid> GroupRowIds(JsonElement storage) => storage.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("id").GetGuid());

    [Fact]
    public async Task A_group_nobody_is_in_shows_with_no_members_and_the_owner_can_delete_it_with_its_files()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        var group = await Group(ca, "Bỏ trống", b.GomId);
        var pic = await Upload(group, ca, "d.jpg", TestImages.Jpeg(200, 100));
        await Send(group, ca, pic.Id);
        foreach (var (user, cookie) in new[] { (a, ca), (b, cb) })
            (await app.Call(Delete, "/api/me", new { gomId = user.GomId }, cookie)).Dispose();
        Assert.Equal(0, GroupRow(await Storage(), group).GetProperty("members").GetInt32());

        var (_, co) = await app.Owner();
        using var r = await app.Call(Delete, $"/api/owner/groups/{group}", cookie: co);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);

        Assert.DoesNotContain(GroupRowIds(await Storage()), id => id == group);
        var name = pic.Id.ToString();
        Assert.False(File.Exists(Path.Combine(app.UploadsDir, name[..2], name)));
        await using var db = app.Db.NewContext();
        Assert.False(await db.Conversations.AnyAsync(c => c.Id == group));
        Assert.False(await db.Messages.AnyAsync(m => m.ConversationId == group));
    }

    [Fact]
    public async Task A_group_someone_is_in_or_a_one_to_one_chat_is_not_the_owners_to_delete()
    {
        var (a, ca) = await app.Member(); var (b, _) = await app.Member();
        await app.Befriend(a, b);
        var group = await Group(ca, "Còn người", b.GomId);
        var chat = await app.OpenChat(ca, b.GomId);
        var (_, co) = await app.Owner();

        using var busy = await app.Call(Delete, $"/api/owner/groups/{group}", cookie: co);
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal("group_not_empty", (await AppFixture.Json(busy)).GetProperty("error").GetString());
        using var direct = await app.Call(Delete, $"/api/owner/groups/{chat}", cookie: co);
        Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
        using var none = await app.Call(Delete, $"/api/owner/groups/{Guid.NewGuid()}", cookie: co);
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);

        await using var db = app.Db.NewContext();
        Assert.True(await db.Conversations.AnyAsync(c => c.Id == group));
        Assert.Equal(2, await db.Members.CountAsync(m => m.ConversationId == group));
    }
}
