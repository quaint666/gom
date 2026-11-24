using System.Net;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>Putting uploaded pictures and files in messages, and what happens to them afterwards.</summary>
public class MessageAttachmentTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Delete = HttpMethod.Delete, Patch = HttpMethod.Patch;

    private sealed record Pair(User A, string CA, User B, string CB, Guid Chat);

    private async Task<Pair> NewPair()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        return new Pair(a, ca, b, cb, await app.OpenChat(ca, b.GomId));
    }

    private async Task<Guid> Upload(Guid chat, string cookie, byte[] bytes, string name = "x")
    {
        using var r = await app.CallBytes(Post, $"/api/conversations/{chat}/attachments?name={Uri.EscapeDataString(name)}", bytes, cookie);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> Say(Guid chat, string cookie, string? body, IEnumerable<Guid>? files = null, Guid? clientId = null) =>
        app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = (clientId ?? Guid.NewGuid()).ToString(), body, attachments = files?.ToArray() }, cookie);

    private static async Task<string> ErrorOf(HttpResponseMessage r) => (await AppFixture.Json(r)).GetProperty("error").GetString()!;
    private int FilesOnDisk() => Directory.Exists(app.UploadsDir) ? Directory.EnumerateFiles(app.UploadsDir, "*", SearchOption.AllDirectories).Count() : 0;

    [Fact]
    public async Task A_message_carries_its_pictures_and_files_in_the_order_chosen_and_the_other_person_can_fetch_them()
    {
        var p = await NewPair();
        var photo = await Upload(p.Chat, p.CA, TestImages.Jpeg(300, 200), "a.jpg");
        var doc = await Upload(p.Chat, p.CA, new byte[] { 1, 2, 3, 4 }, "ghi chú.txt");
        var photo2 = await Upload(p.Chat, p.CA, TestImages.Png(200, 300), "b.png");

        using var sent = await Say(p.Chat, p.CA, "xem này", [doc, photo, photo2]);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var json = await AppFixture.Json(sent);
        Assert.Equal([doc, photo, photo2], json.GetProperty("attachments").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(new[] { "file", "image", "image" }, json.GetProperty("attachments").EnumerateArray().Select(a => a.GetProperty("kind").GetString()!).ToArray());

        using var history = await app.Call(Get, $"/api/conversations/{p.Chat}/messages", cookie: p.CB);
        var listed = (await AppFixture.Json(history)).GetProperty("messages")[0].GetProperty("attachments");
        Assert.Equal(3, listed.GetArrayLength());
        using var fetched = await app.Call(Get, $"/api/attachments/{photo}", cookie: p.CB);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
    }

    [Fact]
    public async Task A_message_may_be_only_pictures_but_not_nothing()
    {
        var p = await NewPair();
        var photo = await Upload(p.Chat, p.CA, TestImages.Jpeg(100, 100));
        using var only = await Say(p.Chat, p.CA, null, [photo]);
        Assert.Equal(HttpStatusCode.OK, only.StatusCode);
        Assert.Equal("", (await AppFixture.Json(only)).GetProperty("body").GetString());

        using var empty = await Say(p.Chat, p.CA, "   ");
        Assert.Equal("empty_message", await ErrorOf(empty));
        using var emptyList = await Say(p.Chat, p.CA, "", []);
        Assert.Equal("empty_message", await ErrorOf(emptyList));
    }

    [Fact]
    public async Task Only_your_own_unused_uploads_to_this_conversation_can_go_in_a_message()
    {
        var p = await NewPair();
        var other = await NewPair();
        var mine = await Upload(p.Chat, p.CA, TestImages.Jpeg(100, 100));
        var theirs = await Upload(p.Chat, p.CB, TestImages.Jpeg(100, 100));
        var elsewhere = await Upload(other.Chat, other.CA, TestImages.Jpeg(100, 100));

        foreach (var bad in new[] { theirs, elsewhere, Guid.NewGuid() })
        {
            using var r = await Say(p.Chat, p.CA, "x", [bad]);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal("invalid_attachment", await ErrorOf(r));
        }
        using var twice = await Say(p.Chat, p.CA, "x", [mine, mine]);
        Assert.Equal("invalid_attachment", await ErrorOf(twice));

        using var fine = await Say(p.Chat, p.CA, "x", [mine]);
        Assert.Equal(HttpStatusCode.OK, fine.StatusCode);
        using var used = await Say(p.Chat, p.CA, "again", [mine]);   // taken by the first message
        Assert.Equal("invalid_attachment", await ErrorOf(used));
    }

    [Fact]
    public async Task Of_two_messages_that_name_the_same_upload_at_once_exactly_one_takes_it()
    {
        var p = await NewPair();
        var photo = await Upload(p.Chat, p.CA, TestImages.Jpeg(100, 100));
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Say(p.Chat, p.CA, "x", [photo])));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.OK), r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        await using var db = app.Db.NewContext();
        Assert.Equal(1, await db.Messages.CountAsync(m => m.ConversationId == p.Chat));
    }

    [Fact]
    public async Task There_is_a_limit_on_how_many_go_in_one_message()
    {
        var p = await NewPair();
        var many = new List<Guid>();
        for (var i = 0; i < 11; i++) many.Add(await Upload(p.Chat, p.CA, TestImages.Jpeg(40, 40), $"{i}.jpg"));
        using var tooMany = await Say(p.Chat, p.CA, "x", many);
        Assert.Equal("too_many_attachments", await ErrorOf(tooMany));
        using var ten = await Say(p.Chat, p.CA, "x", many.Take(10));
        Assert.Equal(HttpStatusCode.OK, ten.StatusCode);
    }

    [Fact]
    public async Task Sending_again_with_the_same_client_id_gives_the_same_message_with_its_attachments()
    {
        var p = await NewPair();
        var photo = await Upload(p.Chat, p.CA, TestImages.Jpeg(100, 100));
        var client = Guid.NewGuid();
        using var first = await Say(p.Chat, p.CA, "x", [photo], client);
        using var second = await Say(p.Chat, p.CA, "x", [photo], client);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var a = await AppFixture.Json(first); var b = await AppFixture.Json(second);
        Assert.Equal(a.GetProperty("id").GetGuid(), b.GetProperty("id").GetGuid());
        Assert.Equal(1, b.GetProperty("attachments").GetArrayLength());
    }

    [Fact]
    public async Task Taking_a_message_back_removes_its_pictures_and_files_from_the_database_and_the_disk()
    {
        var p = await NewPair();
        var photo = await Upload(p.Chat, p.CA, TestImages.Jpeg(300, 200));
        var doc = await Upload(p.Chat, p.CA, new byte[] { 1, 2, 3 });
        var before = FilesOnDisk();
        using var sent = await Say(p.Chat, p.CA, "x", [photo, doc]);
        var messageId = (await AppFixture.Json(sent)).GetProperty("id").GetGuid();
        using var recalled = await app.Call(Delete, $"/api/conversations/{p.Chat}/messages/{messageId}", cookie: p.CA);
        Assert.Equal(HttpStatusCode.NoContent, recalled.StatusCode);

        foreach (var id in new[] { photo, doc })
        {
            using var gone = await app.Call(Get, $"/api/attachments/{id}", cookie: p.CB);
            Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        }
        Assert.Equal(before - 3, FilesOnDisk());   // the picture, its small version, and the file
        using var history = await app.Call(Get, $"/api/conversations/{p.Chat}/messages", cookie: p.CB);
        var message = (await AppFixture.Json(history)).GetProperty("messages")[0];
        Assert.True(message.GetProperty("deleted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, message.GetProperty("attachments").ValueKind);
    }

    [Fact]
    public async Task Disbanding_a_group_removes_everything_uploaded_to_it_including_what_no_message_took()
    {
        var (a, ca) = await app.Member(); var (b, _) = await app.Member();
        await app.Befriend(a, b);
        using var made = await app.Call(Post, "/api/conversations/group", new { title = "Nhóm", members = new[] { b.GomId } }, ca);
        var group = (await AppFixture.Json(made)).GetProperty("id").GetGuid();
        var used = await Upload(group, ca, TestImages.Jpeg(200, 200));
        await Upload(group, ca, new byte[] { 7, 7, 7 });   // never put in a message
        using var sent = await Say(group, ca, "x", [used]);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var before = FilesOnDisk();

        using var gone = await app.Call(Delete, $"/api/conversations/{group}", cookie: ca);
        Assert.Equal(HttpStatusCode.NoContent, gone.StatusCode);
        Assert.Equal(before - 3, FilesOnDisk());
        await using var db = app.Db.NewContext();
        Assert.Equal(0, await db.Attachments.CountAsync(x => x.ConversationId == group));
    }

    [Fact]
    public async Task What_someone_hid_or_said_before_you_joined_cannot_be_fetched_by_you()
    {
        var p = await NewPair();
        var photo = await Upload(p.Chat, p.CA, TestImages.Jpeg(100, 100));
        using var sent = await Say(p.Chat, p.CA, "x", [photo]);
        var messageId = (await AppFixture.Json(sent)).GetProperty("id").GetGuid();
        using var hidden = await app.Call(HttpMethod.Put, $"/api/conversations/{p.Chat}/messages/{messageId}/hidden", cookie: p.CB);
        Assert.Equal(HttpStatusCode.NoContent, hidden.StatusCode);
        using var hiddenFetch = await app.Call(Get, $"/api/attachments/{photo}", cookie: p.CB);
        Assert.Equal(HttpStatusCode.NotFound, hiddenFetch.StatusCode);
        using var senderFetch = await app.Call(Get, $"/api/attachments/{photo}", cookie: p.CA);
        Assert.Equal(HttpStatusCode.OK, senderFetch.StatusCode);
    }

    [Fact]
    public async Task The_conversation_list_says_when_the_last_message_is_a_picture_or_a_file()
    {
        var p = await NewPair();
        var photo = await Upload(p.Chat, p.CA, TestImages.Jpeg(100, 100));
        using var sent = await Say(p.Chat, p.CA, null, [photo]);
        using var list = await app.Call(Get, "/api/conversations", cookie: p.CB);
        var last = (await AppFixture.Json(list)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == p.Chat).GetProperty("lastMessage");
        Assert.Equal("image", last.GetProperty("attachment").GetString());
        Assert.Equal("", last.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Sync_over_the_socket_path_and_search_carry_attachments_too()
    {
        var p = await NewPair();
        var photo = await Upload(p.Chat, p.CA, TestImages.Jpeg(100, 100));
        using var sent = await Say(p.Chat, p.CA, "tìm cái này", [photo]);
        using var found = await app.Call(Get, $"/api/conversations/{p.Chat}/search?q=t%C3%ACm", cookie: p.CB);
        Assert.Equal(1, (await AppFixture.Json(found)).GetProperty("results")[0].GetProperty("attachments").GetArrayLength());
        var id = (await AppFixture.Json(sent)).GetProperty("id").GetGuid();
        using var pin = await app.Call(HttpMethod.Put, $"/api/conversations/{p.Chat}/messages/{id}/pin", cookie: p.CA);
        using var pins = await app.Call(Get, $"/api/conversations/{p.Chat}/pins", cookie: p.CB);
        Assert.Equal(1, (await AppFixture.Json(pins)).GetProperty("pins")[0].GetProperty("attachments").GetArrayLength());
    }
}
