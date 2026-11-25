using System.Net;
using System.Text.Json;
using Gom.Web.Data;

namespace Gom.Tests;

/// <summary>Forwarding a picture or a file: a real copy, made only of what the forwarder may fetch, into a conversation they may write in.</summary>
public class ForwardCopyTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Put = HttpMethod.Put, Delete = HttpMethod.Delete;

    /// <summary>A and B are friends and talk; A and C are friends and talk; B and C are not. D is a stranger.</summary>
    private sealed record Set(User A, string CA, User B, string CB, User C, string CC, string CD, Guid AB, Guid AC);

    private async Task<Set> NewSet()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, cc) = await app.Member(); var (_, cd) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(a, c);
        return new Set(a, ca, b, cb, c, cc, cd, await app.OpenChat(ca, b.GomId), await app.OpenChat(ca, c.GomId));
    }

    private async Task<Guid> Upload(Guid chat, string cookie, byte[] bytes, string name)
    {
        using var r = await app.CallBytes(Post, $"/api/conversations/{chat}/attachments?name={Uri.EscapeDataString(name)}", bytes, cookie);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    private async Task<(Guid Message, Guid Attachment)> SayWith(Guid chat, string cookie, byte[] bytes, string name)
    {
        var file = await Upload(chat, cookie, bytes, name);
        using var r = await app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body = "x", attachments = new[] { file } }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return ((await AppFixture.Json(r)).GetProperty("id").GetGuid(), file);
    }

    private Task<HttpResponseMessage> Copy(Guid target, string cookie, object? from) => app.Call(Post, $"/api/conversations/{target}/attachments/copy", new { from }, cookie);

    private async Task<byte[]> Bytes(string url, string cookie)
    {
        using var r = await app.Call(Get, url, cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await r.Content.ReadAsByteArrayAsync();
    }

    private async Task<Guid> CopyOk(Guid target, string cookie, Guid from)
    {
        using var r = await Copy(target, cookie, from);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_picture_is_copied_whole_with_its_small_version_and_goes_out_in_a_message_to_the_other_conversation()
    {
        var s = await NewSet();
        var (_, original) = await SayWith(s.AB, s.CA, TestImages.Jpeg(300, 200), "a.jpg");

        using var copied = await Copy(s.AC, s.CA, original);
        Assert.Equal(HttpStatusCode.Created, copied.StatusCode);
        var json = (await AppFixture.Json(copied)).Clone();
        var copy = json.GetProperty("id").GetGuid();
        Assert.NotEqual(original, copy);
        Assert.Equal("image", json.GetProperty("kind").GetString());
        Assert.Equal("a.webp", json.GetProperty("name").GetString());
        Assert.Equal(300, json.GetProperty("width").GetInt32());

        // Not in a message yet: only the one who copied it can fetch it.
        Assert.Equal(HttpStatusCode.NotFound, (await app.Call(Get, $"/api/attachments/{copy}", cookie: s.CC)).StatusCode);
        using var sent = await app.Call(Post, $"/api/conversations/{s.AC}/messages", new { clientId = Guid.NewGuid(), body = "", attachments = new[] { copy } }, s.CA);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);

        Assert.Equal(await Bytes($"/api/attachments/{original}", s.CA), await Bytes($"/api/attachments/{copy}", s.CC));
        Assert.Equal(await Bytes($"/api/attachments/{original}/thumb", s.CA), await Bytes($"/api/attachments/{copy}/thumb", s.CC));
    }

    [Fact]
    public async Task A_file_is_copied_under_its_name_and_the_copy_outlives_the_original()
    {
        var s = await NewSet();
        var (message, original) = await SayWith(s.AB, s.CB, [9, 8, 7, 6], "ghi chú.txt");
        var copy = await CopyOk(s.AC, s.CA, original);
        using var sent = await app.Call(Post, $"/api/conversations/{s.AC}/messages", new { clientId = Guid.NewGuid(), body = "", attachments = new[] { copy } }, s.CA);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var listed = (await AppFixture.Json(sent)).GetProperty("attachments")[0];
        Assert.Equal("ghi chú.txt", listed.GetProperty("name").GetString());
        Assert.Equal("file", listed.GetProperty("kind").GetString());

        // The sender takes the original back: it is gone for the first chat, and the forwarded copy is untouched.
        using var recall = await app.Call(Delete, $"/api/conversations/{s.AB}/messages/{message}", cookie: s.CB);
        Assert.Equal(HttpStatusCode.NoContent, recall.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Call(Get, $"/api/attachments/{original}", cookie: s.CA)).StatusCode);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, await Bytes($"/api/attachments/{copy}", s.CC));
    }

    [Fact]
    public async Task Only_what_you_may_fetch_can_be_copied()
    {
        var s = await NewSet();
        var (message, original) = await SayWith(s.AB, s.CA, [1, 2, 3], "a.bin");

        // Someone who is not in the first conversation (here, C and a stranger, each into a conversation of their own).
        using var byC = await Copy(s.AC, s.CC, original);
        Assert.Equal(HttpStatusCode.NotFound, byC.StatusCode);
        using var byStranger = await Copy(await app.OpenChat(s.CC, s.A.GomId), s.CC, original);
        Assert.Equal(HttpStatusCode.NotFound, byStranger.StatusCode);

        // Hidden by me.
        using var hide = await app.Call(Put, $"/api/conversations/{s.AB}/messages/{message}/hidden", cookie: s.CB);
        Assert.Equal(HttpStatusCode.NoContent, hide.StatusCode);
        using var hiddenCopy = await Copy(await app.OpenChat(s.CB, s.A.GomId), s.CB, original);
        Assert.Equal(HttpStatusCode.NotFound, hiddenCopy.StatusCode);

        // An upload that is not in a message yet, even your own, is not a thing to forward.
        var loose = await Upload(s.AB, s.CA, [4], "loose.bin");
        using var looseCopy = await Copy(s.AC, s.CA, loose);
        Assert.Equal(HttpStatusCode.NotFound, looseCopy.StatusCode);

        using var unknown = await Copy(s.AC, s.CA, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var nothing = await Copy(s.AC, s.CA, null);
        Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);
        Assert.Equal("invalid_attachment", (await AppFixture.Json(nothing)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Something_said_before_you_joined_cannot_be_copied()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (e, ce) = await app.Member();
        await app.Befriend(a, b); await app.Befriend(a, e);
        using var made = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { b.GomId } }, ca);
        var group = (await AppFixture.Json(made)).GetProperty("id").GetGuid();
        var (_, early) = await SayWith(group, ca, [1], "early.bin");
        using var add = await app.Call(Post, $"/api/conversations/{group}/members", new { gomId = e.GomId }, ca);
        Assert.Equal(HttpStatusCode.NoContent, add.StatusCode);
        var (_, late) = await SayWith(group, ca, [2], "late.bin");

        var mine = await app.OpenChat(ce, a.GomId);
        using var tooEarly = await Copy(mine, ce, early);
        Assert.Equal(HttpStatusCode.NotFound, tooEarly.StatusCode);
        using var fine = await Copy(mine, ce, late);
        Assert.Equal(HttpStatusCode.Created, fine.StatusCode);
    }

    [Fact]
    public async Task The_target_must_be_one_you_may_write_in()
    {
        var s = await NewSet();
        var (_, original) = await SayWith(s.AB, s.CA, [1, 2], "a.bin");

        using var notIn = await Copy(s.AC, s.CB, original);   // B may fetch it, but is not in the chat of A with C
        Assert.Equal(HttpStatusCode.NotFound, notIn.StatusCode);

        using var unfriend = await app.Call(Delete, $"/api/friends/{s.C.GomId}", cookie: s.CA);
        Assert.Equal(HttpStatusCode.NoContent, unfriend.StatusCode);
        using var noLonger = await Copy(s.AC, s.CA, original);
        Assert.Equal(HttpStatusCode.Forbidden, noLonger.StatusCode);
        Assert.Equal("not_friends", (await AppFixture.Json(noLonger)).GetProperty("error").GetString());

        using var anonymous = await app.Call(Post, $"/api/conversations/{s.AC}/attachments/copy", new { from = original });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Copying_is_paced_like_uploading()
    {
        var s = await NewSet();
        var (_, original) = await SayWith(s.AB, s.CA, [1], "a.bin");
        HttpStatusCode last = HttpStatusCode.Created;
        for (var i = 0; i < 40 && last == HttpStatusCode.Created; i++)
        {
            using var r = await Copy(s.AC, s.CA, original);
            last = r.StatusCode;
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
}
