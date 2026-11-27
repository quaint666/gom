using System.Net;
using System.Text.Json;
using Gom.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Gom.Tests;

/// <summary>Answering a message: what is checked on the way in and what a reader is shown of the quoted message.</summary>
public class ReplyTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly HttpMethod Post = HttpMethod.Post, Get = HttpMethod.Get, Delete = HttpMethod.Delete;

    /// <summary>A leads a group with B and C; D is A's friend, outside.</summary>
    private sealed record Team(User A, string CA, User B, string CB, User C, string CC, User D, string CD, Guid Chat);

    private async Task<Team> NewTeam()
    {
        var (a, ca) = await app.Member(); var (b, cb) = await app.Member(); var (c, cc) = await app.Member(); var (d, cd) = await app.Member();
        foreach (var x in new[] { b, c, d }) await app.Befriend(a, x);
        using var r = await app.Call(Post, "/api/conversations/group", new { title = "Nhà", members = new[] { b.GomId, c.GomId } }, ca);
        return new Team(a, ca, b, cb, c, cc, d, cd, (await AppFixture.Json(r)).GetProperty("id").GetGuid());
    }

    private async Task<JsonElement> Say(Guid chat, string cookie, string body, Guid? replyTo = null, Guid? client = null)
    {
        using var r = await app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = client ?? Guid.NewGuid(), body, replyTo }, cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await AppFixture.Json(r);
    }

    private Task<HttpResponseMessage> TrySay(Guid chat, string cookie, string body, Guid? replyTo) =>
        app.Call(Post, $"/api/conversations/{chat}/messages", new { clientId = Guid.NewGuid(), body, replyTo }, cookie);

    private async Task<JsonElement[]> History(Guid chat, string cookie)
    {
        using var r = await app.Call(Get, $"/api/conversations/{chat}/messages", cookie: cookie);
        return (await AppFixture.Json(r)).GetProperty("messages").EnumerateArray().OrderBy(m => m.GetProperty("seq").GetInt64()).ToArray();
    }

    private static Guid IdOf(JsonElement m) => m.GetProperty("id").GetGuid();
    private static async Task<string?> ErrorOf(HttpResponseMessage r) => (await AppFixture.Json(r)).GetProperty("error").GetString();

    [Fact]
    public async Task An_answer_carries_a_quote_of_the_message_it_answers_who_wrote_it_and_the_start_of_it()
    {
        var t = await NewTeam();
        var first = await Say(t.Chat, t.CB, "Tối nay ăn gì?");

        var answer = await Say(t.Chat, t.CC, "Ăn phở", IdOf(first));

        var reply = answer.GetProperty("reply");
        Assert.Equal(IdOf(first), reply.GetProperty("id").GetGuid());
        Assert.Equal(t.B.Id, reply.GetProperty("sender").GetProperty("id").GetGuid());
        Assert.Equal(t.B.DisplayName, reply.GetProperty("sender").GetProperty("displayName").GetString());
        Assert.Equal("Tối nay ăn gì?", reply.GetProperty("snippet").GetString());
        var inHistory = (await History(t.Chat, t.CA)).Single(m => IdOf(m) == IdOf(answer));
        Assert.Equal("Tối nay ăn gì?", inHistory.GetProperty("reply").GetProperty("snippet").GetString());
    }

    [Fact]
    public async Task A_quote_of_a_message_with_only_a_picture_or_a_file_says_what_its_first_one_is()
    {
        var t = await NewTeam();
        async Task<Guid> Upload(byte[] bytes, string name)
        {
            using var r = await app.CallBytes(Post, $"/api/conversations/{t.Chat}/attachments?name={Uri.EscapeDataString(name)}", bytes, t.CB);
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            return (await AppFixture.Json(r)).GetProperty("id").GetGuid();
        }
        var picture = await Upload(TestImages.Png(60, 40), "a.png");
        var file = await Upload([1, 2, 3], "b.bin");
        using var sent = await app.Call(Post, $"/api/conversations/{t.Chat}/messages", new { clientId = Guid.NewGuid(), body = "", attachments = new[] { picture, file } }, t.CB);
        var first = await AppFixture.Json(sent);
        var words = await Say(t.Chat, t.CB, "chữ thôi");

        var answer = await Say(t.Chat, t.CC, "đẹp quá", IdOf(first));
        var plain = await Say(t.Chat, t.CC, "ừ", IdOf(words));

        Assert.Equal("", answer.GetProperty("reply").GetProperty("snippet").GetString());
        Assert.Equal("image", answer.GetProperty("reply").GetProperty("attachment").GetString());
        Assert.Equal("image", (await History(t.Chat, t.CA)).Single(m => IdOf(m) == IdOf(answer)).GetProperty("reply").GetProperty("attachment").GetString());
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("reply").GetProperty("attachment").ValueKind);
    }

    [Fact]
    public async Task A_message_that_answers_nothing_has_no_quote()
    {
        var t = await NewTeam();
        var plain = await Say(t.Chat, t.CB, "xin chào");
        Assert.True(!plain.TryGetProperty("reply", out var r) || r.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task A_long_message_is_quoted_by_its_first_hundred_characters_only()
    {
        var t = await NewTeam();
        var long150 = string.Concat(Enumerable.Range(0, 150).Select(i => (char)('a' + i % 26)));
        var first = await Say(t.Chat, t.CB, long150);
        var answer = await Say(t.Chat, t.CC, "dài quá", IdOf(first));
        Assert.Equal(long150[..100], answer.GetProperty("reply").GetProperty("snippet").GetString());
    }

    [Fact]
    public async Task A_quote_never_cuts_an_emoji_in_half()
    {
        var t = await NewTeam();
        var first = await Say(t.Chat, t.CB, string.Concat(Enumerable.Repeat("👨‍👩‍👧", 150)));
        var answer = await Say(t.Chat, t.CC, "đông quá", IdOf(first));
        Assert.Equal(string.Concat(Enumerable.Repeat("👨‍👩‍👧", 100)), answer.GetProperty("reply").GetProperty("snippet").GetString());
    }

    // ---- what may be answered

    [Fact]
    public async Task Only_a_message_of_this_conversation_can_be_answered()
    {
        var t = await NewTeam();
        var other = await app.OpenChat(t.CA, t.D.GomId);
        var elsewhere = await Say(other, t.CA, "chuyện riêng");

        using var wrongChat = await TrySay(t.Chat, t.CB, "trả lời", IdOf(elsewhere));
        using var nothing = await TrySay(t.Chat, t.CB, "trả lời", Guid.NewGuid());

        Assert.Equal("invalid_reply", await ErrorOf(wrongChat));
        Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);
    }

    [Fact]
    public async Task A_message_taken_back_cannot_be_answered()
    {
        var t = await NewTeam();
        var first = await Say(t.Chat, t.CB, "sẽ thu hồi");
        using var recalled = await app.Call(Delete, $"/api/conversations/{t.Chat}/messages/{IdOf(first)}", cookie: t.CB);
        Assert.Equal(HttpStatusCode.NoContent, recalled.StatusCode);

        using var r = await TrySay(t.Chat, t.CC, "trả lời", IdOf(first));

        Assert.Equal("invalid_reply", await ErrorOf(r));
    }

    [Fact]
    public async Task What_was_said_before_you_joined_cannot_be_answered()
    {
        var t = await NewTeam();
        var before = await Say(t.Chat, t.CA, "trước khi D vào");
        using var add = await app.Call(Post, $"/api/conversations/{t.Chat}/members", new { gomId = t.D.GomId }, t.CA);
        Assert.Equal(HttpStatusCode.NoContent, add.StatusCode);

        using var r = await TrySay(t.Chat, t.CD, "trả lời", IdOf(before));

        Assert.Equal("invalid_reply", await ErrorOf(r));
    }

    [Fact]
    public async Task Sending_the_same_answer_twice_stores_one_and_gives_the_quote_both_times()
    {
        var t = await NewTeam();
        var first = await Say(t.Chat, t.CB, "gốc");
        var client = Guid.NewGuid();

        var one = await Say(t.Chat, t.CC, "đáp", IdOf(first), client);
        var two = await Say(t.Chat, t.CC, "đáp", IdOf(first), client);

        Assert.Equal(IdOf(one), IdOf(two));
        Assert.Equal("gốc", two.GetProperty("reply").GetProperty("snippet").GetString());
    }

    // ---- what a reader is shown

    [Fact]
    public async Task A_quote_whose_message_is_later_taken_back_shows_nothing_of_it()
    {
        var t = await NewTeam();
        var first = await Say(t.Chat, t.CB, "bí mật");
        var answer = await Say(t.Chat, t.CC, "đáp", IdOf(first));
        using var recalled = await app.Call(Delete, $"/api/conversations/{t.Chat}/messages/{IdOf(first)}", cookie: t.CB);
        Assert.Equal(HttpStatusCode.NoContent, recalled.StatusCode);

        var reply = (await History(t.Chat, t.CA)).Single(m => IdOf(m) == IdOf(answer)).GetProperty("reply");

        Assert.Equal(IdOf(first), reply.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("sender").ValueKind);
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("snippet").ValueKind);
    }

    [Fact]
    public async Task A_member_who_joined_after_the_quoted_message_sees_the_answer_but_not_the_quote()
    {
        var t = await NewTeam();
        var first = await Say(t.Chat, t.CA, "chuyện cũ");
        using var add = await app.Call(Post, $"/api/conversations/{t.Chat}/members", new { gomId = t.D.GomId }, t.CA);
        Assert.Equal(HttpStatusCode.NoContent, add.StatusCode);
        var answer = await Say(t.Chat, t.CB, "nhắc lại chuyện cũ", IdOf(first));

        var forD = (await History(t.Chat, t.CD)).Single(m => IdOf(m) == IdOf(answer));
        var forC = (await History(t.Chat, t.CC)).Single(m => IdOf(m) == IdOf(answer));

        Assert.Equal("nhắc lại chuyện cũ", forD.GetProperty("body").GetString());
        Assert.Equal(JsonValueKind.Null, forD.GetProperty("reply").GetProperty("snippet").ValueKind);
        Assert.Equal(JsonValueKind.Null, forD.GetProperty("reply").GetProperty("sender").ValueKind);
        Assert.DoesNotContain("chuyện cũ\"", forD.GetRawText().Replace("nhắc lại chuyện cũ", ""));
        Assert.Equal("chuyện cũ", forC.GetProperty("reply").GetProperty("snippet").GetString());
    }

    [Fact]
    public async Task Taking_back_an_answer_takes_its_quote_with_it()
    {
        var t = await NewTeam();
        var first = await Say(t.Chat, t.CB, "gốc");
        var answer = await Say(t.Chat, t.CC, "đáp", IdOf(first));
        using var recalled = await app.Call(Delete, $"/api/conversations/{t.Chat}/messages/{IdOf(answer)}", cookie: t.CC);
        Assert.Equal(HttpStatusCode.NoContent, recalled.StatusCode);

        var shown = (await History(t.Chat, t.CA)).Single(m => IdOf(m) == IdOf(answer));

        Assert.True(shown.GetProperty("deleted").GetBoolean());
        Assert.True(!shown.TryGetProperty("reply", out var r) || r.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Ending_a_group_works_when_it_holds_answers()
    {
        var t = await NewTeam();
        var first = await Say(t.Chat, t.CB, "gốc");
        await Say(t.Chat, t.CC, "đáp", IdOf(first));

        using var r = await app.Call(Delete, $"/api/conversations/{t.Chat}", cookie: t.CA);

        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        await using var db = app.Db.NewContext();
        Assert.False(await db.Messages.AnyAsync(m => m.ConversationId == t.Chat));
    }
}
