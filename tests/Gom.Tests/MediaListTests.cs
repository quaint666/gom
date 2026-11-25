using System.Net;
using System.Text.Json;
using Gom.Web.Data;

namespace Gom.Tests;

/// <summary>The side panel's list of a conversation's pictures and files, asked of the server (not just what the page has loaded).</summary>
public class MediaListTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Put = HttpMethod.Put, Delete = HttpMethod.Delete;

    private sealed record Team(User A, string CA, User B, string CB, User E, string CE, Guid Chat);

    private async Task<Team> NewTeam()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (e, ce) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(a, e);
        using var r = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { b.GomId } }, ca);
        return new Team(a, ca, b, cb, e, ce, (await AppFixture.Json(r)).GetProperty("id").GetGuid());
    }

    private async Task<Guid> Upload(Team t, string cookie, byte[] bytes, string name)
    {
        using var r = await app.CallBytes(Post, $"/api/conversations/{t.Chat}/attachments?name={Uri.EscapeDataString(name)}", bytes, cookie);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    /// <summary>Sends a message with these uploads; returns the message id.</summary>
    private async Task<Guid> Say(Team t, string cookie, params Guid[] files)
    {
        using var r = await app.Call(Post, $"/api/conversations/{t.Chat}/messages", new { clientId = Guid.NewGuid(), body = "x", attachments = files }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> Media(Team t, string cookie, string kind, long? before = null)
    {
        using var r = await app.Call(Get, $"/api/conversations/{t.Chat}/media?kind={kind}" + (before is null ? "" : $"&before={before}"), cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await AppFixture.Json(r)).Clone();
    }

    private static Guid[] Ids(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("attachment").GetProperty("id").GetGuid()).ToArray();

    [Fact]
    public async Task Pictures_and_files_are_listed_apart_newest_message_first_with_the_message_they_belong_to()
    {
        var t = await NewTeam();
        var oldPhoto = await Upload(t, t.CA, TestImages.Jpeg(120, 80), "a.jpg");
        var oldMessage = await Say(t, t.CA, oldPhoto);
        var doc = await Upload(t, t.CB, [1, 2, 3], "ghi chú.txt");
        var photoB = await Upload(t, t.CB, TestImages.Png(90, 90), "b.png");
        var photoC = await Upload(t, t.CB, TestImages.Png(80, 80), "c.png");
        var newMessage = await Say(t, t.CB, doc, photoB, photoC);

        var pictures = await Media(t, t.CA, "image");
        Assert.Equal([photoB, photoC, oldPhoto], Ids(pictures));   // newest message first; inside one message, the sender's order
        Assert.False(pictures.GetProperty("hasMore").GetBoolean());
        Assert.Equal(newMessage, pictures.GetProperty("items")[0].GetProperty("messageId").GetGuid());
        Assert.Equal(oldMessage, pictures.GetProperty("items")[2].GetProperty("messageId").GetGuid());
        Assert.Equal("image", pictures.GetProperty("items")[0].GetProperty("attachment").GetProperty("kind").GetString());
        // The viewer shows who sent each picture and the words that came with it.
        Assert.Equal(t.B.Id, pictures.GetProperty("items")[0].GetProperty("sender").GetProperty("id").GetGuid());
        Assert.Equal(t.B.DisplayName, pictures.GetProperty("items")[0].GetProperty("sender").GetProperty("displayName").GetString());
        Assert.Equal(t.A.Id, pictures.GetProperty("items")[2].GetProperty("sender").GetProperty("id").GetGuid());
        Assert.Equal("x", pictures.GetProperty("items")[0].GetProperty("body").GetString());

        var files = await Media(t, t.CA, "file");
        Assert.Equal([doc], Ids(files));
        Assert.Equal("ghi chú.txt", files.GetProperty("items")[0].GetProperty("attachment").GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_long_list_comes_in_pages_and_the_next_page_starts_where_the_last_ended()
    {
        var t = await NewTeam();
        var sent = new List<Guid>();
        // Two senders take turns: one person may only send 20 messages in a burst.
        for (var i = 0; i < 26; i++)
        {
            var cookie = i % 2 == 0 ? t.CA : t.CB;
            var file = await Upload(t, cookie, [(byte)(i + 1)], $"f{i}.bin");
            await Say(t, cookie, file);
            sent.Add(file);
        }
        sent.Reverse();

        var first = await Media(t, t.CA, "file");
        Assert.Equal(sent.Take(24).ToArray(), Ids(first));
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        var cursor = first.GetProperty("items").EnumerateArray().Last().GetProperty("seq").GetInt64();

        var second = await Media(t, t.CA, "file", cursor);
        Assert.Equal(sent.Skip(24).ToArray(), Ids(second));
        Assert.False(second.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task What_you_may_not_fetch_you_do_not_see_listed()
    {
        var t = await NewTeam();
        var before = await Upload(t, t.CA, [1], "before.txt");
        await Say(t, t.CA, before);

        // Someone who joins later sees nothing from before they joined.
        using var add = await app.Call(Post, $"/api/conversations/{t.Chat}/members", new { gomId = t.E.GomId }, t.CA);
        Assert.Equal(HttpStatusCode.NoContent, add.StatusCode);
        var after = await Upload(t, t.CA, [2], "after.txt");
        await Say(t, t.CA, after);
        Assert.Equal([after], Ids(await Media(t, t.CE, "file")));
        Assert.Equal([after, before], Ids(await Media(t, t.CB, "file")));

        // Hidden by me: gone for me, still there for the others.
        var hiddenFile = await Upload(t, t.CA, [3], "hidden.txt");
        var hiddenMessage = await Say(t, t.CA, hiddenFile);
        using var hide = await app.Call(Put, $"/api/conversations/{t.Chat}/messages/{hiddenMessage}/hidden", cookie: t.CB);
        Assert.Equal(HttpStatusCode.NoContent, hide.StatusCode);
        Assert.DoesNotContain(hiddenFile, Ids(await Media(t, t.CB, "file")));
        Assert.Contains(hiddenFile, Ids(await Media(t, t.CA, "file")));

        // Taken back: gone for everyone.
        using var recall = await app.Call(Delete, $"/api/conversations/{t.Chat}/messages/{hiddenMessage}", cookie: t.CA);
        Assert.Equal(HttpStatusCode.NoContent, recall.StatusCode);
        Assert.DoesNotContain(hiddenFile, Ids(await Media(t, t.CA, "file")));

        // An upload not yet in a message is not listed, not even to the one who made it.
        var loose = await Upload(t, t.CA, [4], "loose.txt");
        Assert.DoesNotContain(loose, Ids(await Media(t, t.CA, "file")));
    }

    [Fact]
    public async Task Somebody_outside_the_conversation_gets_a_404_and_a_wrong_kind_a_400()
    {
        var t = await NewTeam();
        var (_, outsider) = await app.Member();
        using var closed = await app.Call(Get, $"/api/conversations/{t.Chat}/media?kind=image", cookie: outsider);
        Assert.Equal(HttpStatusCode.NotFound, closed.StatusCode);
        using var unknown = await app.Call(Get, $"/api/conversations/{Guid.NewGuid()}/media?kind=image", cookie: t.CA);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var wrong = await app.Call(Get, $"/api/conversations/{t.Chat}/media?kind=video", cookie: t.CA);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using var none = await app.Call(Get, $"/api/conversations/{t.Chat}/media", cookie: t.CA);
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        using var anonymous = await app.Call(Get, $"/api/conversations/{t.Chat}/media?kind=image");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }
}
