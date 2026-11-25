using System.Net;
using System.Text.Json;
using Gom.Web.Chat;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Gom.Tests;

/// <summary>A sticker is a whole message: one picture from the fixed list, with no words and no files.</summary>
public class StickerTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Delete = HttpMethod.Delete, Patch = HttpMethod.Patch;

    private sealed record Pair(User A, string CA, User B, string CB, Guid Chat);

    private async Task<Pair> NewPair()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member();
        await app.Befriend(a, b);
        using var r = await app.Call(Post, "/api/conversations/direct", new { gomId = b.GomId }, ca);
        return new Pair(a, ca, b, cb, (await AppFixture.Json(r)).GetProperty("id").GetGuid());
    }

    private Task<HttpResponseMessage> Send(Pair p, string cookie, object body) => app.Call(Post, $"/api/conversations/{p.Chat}/messages", body, cookie);

    private async Task<JsonElement> Sticker(Pair p, string cookie, string code, Guid? replyTo = null)
    {
        using var r = await Send(p, cookie, new { clientId = Guid.NewGuid(), sticker = code, replyTo });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await AppFixture.Json(r);
    }

    private async Task<JsonElement> Seen(Pair p, string cookie, Guid m)
    {
        using var r = await app.Call(Get, $"/api/conversations/{p.Chat}/messages", cookie: cookie);
        return (await AppFixture.Json(r)).GetProperty("messages").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == m);
    }

    [Fact]
    public async Task A_sticker_is_sent_as_its_code_with_no_words_and_the_other_sees_it_in_the_history_and_the_list()
    {
        var p = await NewPair();
        var sent = await Sticker(p, p.CA, "1f602");
        Assert.Equal("1f602", sent.GetProperty("sticker").GetString());
        Assert.Equal("", sent.GetProperty("body").GetString());

        var seen = await Seen(p, p.CB, sent.GetProperty("id").GetGuid());
        Assert.Equal("1f602", seen.GetProperty("sticker").GetString());

        using var list = await app.Call(Get, "/api/conversations", cookie: p.CB);
        var last = (await AppFixture.Json(list)).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == p.Chat).GetProperty("lastMessage");
        Assert.Equal("1f602", last.GetProperty("sticker").GetString());
        Assert.Equal("", last.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_message_of_words_has_no_sticker()
    {
        var p = await NewPair();
        using var r = await Send(p, p.CA, new { clientId = Guid.NewGuid(), body = "chào" });
        var json = await AppFixture.Json(r);
        Assert.True(!json.TryGetProperty("sticker", out var s) || s.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task A_sticker_not_on_the_list_or_with_words_or_files_is_refused_and_nothing_is_stored()
    {
        var p = await NewPair();
        foreach (var body in new object[]
        {
            new { clientId = Guid.NewGuid(), sticker = "1f4a9" },
            new { clientId = Guid.NewGuid(), sticker = "" },
            new { clientId = Guid.NewGuid(), sticker = "../../etc/passwd" },
            new { clientId = Guid.NewGuid(), sticker = "1f602", body = "haha" },
            new { clientId = Guid.NewGuid(), sticker = "1f602", attachments = new[] { Guid.NewGuid() } },
        })
        {
            using var r = await Send(p, p.CA, body);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal("invalid_sticker", (await AppFixture.Json(r)).GetProperty("error").GetString());
        }
        await using var db = app.Db.NewContext();
        Assert.False(await db.Messages.AnyAsync(m => m.ConversationId == p.Chat));
    }

    [Fact]
    public async Task Every_listed_sticker_is_accepted()
    {
        var p = await NewPair();
        app.Clock.Advance(TimeSpan.FromMinutes(1));
        foreach (var code in Stickers.Allowed)
        {
            app.Clock.Advance(TimeSpan.FromSeconds(2));   // past the send limit
            await Sticker(p, p.CA, code);
        }
    }

    [Fact]
    public async Task A_sticker_cannot_be_edited_into_words()
    {
        var p = await NewPair();
        var m = (await Sticker(p, p.CA, "1f44d")).GetProperty("id").GetGuid();
        using var r = await app.Call(Patch, $"/api/conversations/{p.Chat}/messages/{m}", new { body = "ok" }, p.CA);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("not_editable", (await AppFixture.Json(r)).GetProperty("error").GetString());
        Assert.Equal("1f44d", (await Seen(p, p.CB, m)).GetProperty("sticker").GetString());
    }

    [Fact]
    public async Task A_sticker_taken_back_is_gone_for_everyone()
    {
        var p = await NewPair();
        var m = (await Sticker(p, p.CA, "2764")).GetProperty("id").GetGuid();
        using var r = await app.Call(Delete, $"/api/conversations/{p.Chat}/messages/{m}", cookie: p.CA);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var seen = await Seen(p, p.CB, m);
        Assert.True(seen.GetProperty("deleted").GetBoolean());
        Assert.True(!seen.TryGetProperty("sticker", out var s) || s.ValueKind == JsonValueKind.Null);
        await using var db = app.Db.NewContext();
        Assert.Null(await db.Messages.Where(x => x.Id == m).Select(x => x.Sticker).SingleAsync());
    }

    [Fact]
    public async Task An_answer_to_a_sticker_quotes_the_sticker()
    {
        var p = await NewPair();
        var m = (await Sticker(p, p.CA, "1f382")).GetProperty("id").GetGuid();
        using var r = await Send(p, p.CB, new { clientId = Guid.NewGuid(), body = "cảm ơn", replyTo = m });
        var reply = (await AppFixture.Json(r)).GetProperty("reply");
        Assert.Equal("1f382", reply.GetProperty("sticker").GetString());
        Assert.Equal(p.A.Id, reply.GetProperty("sender").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task The_database_refuses_a_sticker_with_words()
    {
        var p = await NewPair();
        var m = (await Sticker(p, p.CA, "1f600")).GetProperty("id").GetGuid();
        await using var db = app.Db.NewContext();
        var e = await Assert.ThrowsAsync<PostgresException>(() => db.Messages.Where(x => x.Id == m).ExecuteUpdateAsync(s => s.SetProperty(x => x.Body, "chữ")));
        Assert.Equal("ck_messages_sticker", e.ConstraintName);
    }
}
